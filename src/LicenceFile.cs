using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Supervertaler.Core
{
    /// <summary>
    /// Reads and writes the licence file that every Supervertaler product
    /// shares, at <c>&lt;data folder&gt;/licence/licence.json</c>.
    ///
    /// Two products can have it open at once - Trados and memoQ side by side is
    /// a normal working day - so it is written as two-process state, with no
    /// lock of any kind:
    ///
    ///   - A write goes to a temporary file in the same folder, which then
    ///     replaces the real one in a single step. A reader sees the old file
    ///     or the new one, never half of either.
    ///   - Readers open with delete sharing, so a read never blocks a write.
    ///   - A write that cannot complete retries briefly, then gives up cleanly:
    ///     the temporary file is removed and the old file stays as it was. It
    ///     never falls back to writing the real file in place, because that
    ///     fallback would only run when something is already contended - which
    ///     is exactly when a half-written file is most likely to be read.
    ///   - A first write can be "only if there is no file yet", so two products
    ///     starting on a new computer do not overwrite each other's first
    ///     record: the loser reads the winner's.
    ///
    /// The Trados plugin's own licence file is only ever read, as the source of
    /// a one-time copy. Nothing here writes to a product-named location.
    /// </summary>
    internal static class LicenceFile
    {
        /// <summary>The shared licence file.</summary>
        internal static string SharedPath =>
            Path.Combine(SupervertalerPaths.Root, "licence", "licence.json");

        /// <summary>
        /// Where the Trados plugin kept its licence before it was shared. Read
        /// only, as the source of the copy, and kept in place so a rollback to
        /// an older Trados build still finds its activation.
        /// </summary>
        internal static string LegacyTradosPath =>
            Path.Combine(SupervertalerPaths.Root, "trados", "settings", "license.json");

        internal enum ReadStatus
        {
            /// <summary>Read and understood.</summary>
            Ok,
            /// <summary>There is no file.</summary>
            Missing,
            /// <summary>There is a file, but it could not be opened, even after retrying.</summary>
            Unreadable,
            /// <summary>The file was read but is not a licence record.</summary>
            Damaged,
        }

        internal enum WriteStatus
        {
            Written,
            /// <summary>An only-if-absent write found a file already there. Nothing was changed.</summary>
            AlreadyExists,
            /// <summary>Gave up. The old file, if any, is untouched and no temporary file is left.</summary>
            Failed,
        }

        // Retry schedule for a file the other product has in use. About a third
        // of a second in all: long enough to outlast a read or a rename, short
        // enough not to be felt if it is paid on the UI thread.
        private static readonly int[] RetryDelaysMs = { 10, 20, 40, 80, 160 };

        // ─── Reading ────────────────────────────────────────────────

        internal static ReadStatus TryRead(string path, out LicenceRecord record, out byte[] bytes)
        {
            record = null;
            bytes = null;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // Delete sharing, so the other product's rename is never
                    // refused because this product happens to be reading.
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    break;
                }
                catch (FileNotFoundException) { return ReadStatus.Missing; }
                catch (DirectoryNotFoundException) { return ReadStatus.Missing; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= RetryDelaysMs.Length) return ReadStatus.Unreadable;
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
                catch
                {
                    // A path Windows rejects outright. Waiting will not help.
                    return ReadStatus.Unreadable;
                }
            }

            return TryParse(bytes, out record) ? ReadStatus.Ok : ReadStatus.Damaged;
        }

        internal static bool TryParse(byte[] bytes, out LicenceRecord record)
        {
            record = null;
            if (bytes == null) return false;

            // The Trados plugin wrote its file with a byte-order mark.
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            if (bytes.Length - offset == 0) return false;

            try
            {
                using (var ms = new MemoryStream(bytes, offset, bytes.Length - offset))
                {
                    record = new DataContractJsonSerializer(typeof(LicenceRecord)).ReadObject(ms) as LicenceRecord;
                }
                return record != null;
            }
            catch
            {
                record = null;
                return false;
            }
        }

        internal static byte[] Serialise(LicenceRecord record)
        {
            using (var ms = new MemoryStream())
            {
                var settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
                new DataContractJsonSerializer(typeof(LicenceRecord), settings).WriteObject(ms, record);
                return ms.ToArray();
            }
        }

        // ─── Writing ────────────────────────────────────────────────

        /// <summary>
        /// Writes <paramref name="bytes"/> to <paramref name="path"/> in one
        /// step. With <paramref name="replaceExisting"/> false it only creates
        /// the file, and reports <see cref="WriteStatus.AlreadyExists"/> if
        /// another writer got there first.
        /// </summary>
        internal static WriteStatus Write(string path, byte[] bytes, bool replaceExisting)
        {
            // Unique per write, so two writers never share a temporary file.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                for (int attempt = 0; ; attempt++)
                {
                    int error;
                    if (replaceExisting)
                    {
                        error = RenameReplacing(temp, path);
                    }
                    else
                    {
                        // Move refuses to overwrite, which is what makes the
                        // first write safe for two products to race.
                        error = MoveFileEx(temp, path, MOVEFILE_WRITE_THROUGH) ? 0 : Marshal.GetLastWin32Error();
                        if (error == ERROR_ALREADY_EXISTS || error == ERROR_FILE_EXISTS)
                            return WriteStatus.AlreadyExists;
                    }

                    if (error == 0)
                        return WriteStatus.Written;

                    if (!IsInUse(error) || attempt >= RetryDelaysMs.Length)
                    {
                        SupervertalerLicence.WriteLog(
                            "Licence file not written (Windows error " + error + "); the previous file is unchanged.");
                        return WriteStatus.Failed;
                    }
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
            catch (Exception ex)
            {
                SupervertalerLicence.WriteLog(
                    "Licence file not written: " + ex.GetType().Name + ": " + ex.Message);
                return WriteStatus.Failed;
            }
            finally
            {
                // On success the temporary file has become the licence file and
                // this does nothing. On every other path it is the cleanup.
                TryDelete(temp);
            }
        }

        // Why a rename by handle, and not File.Replace or a plain MoveFileEx -
        // both were tried against a second process reading the file:
        //
        //   - File.Replace moves the old file aside before moving the new one
        //     in, so for a moment there is no licence file at all. A product
        //     starting in that moment reads "no licence" and would begin a
        //     trial. It also left its own ~RF*.TMP files behind.
        //   - MoveFileEx will not replace a file another process has open, even
        //     with delete sharing, so the other product merely reading would
        //     block every write.
        //
        // A rename with POSIX semantics (Windows 10 1709 and later, on NTFS)
        // replaces the name in one step while readers keep the old file, which
        // is exactly the behaviour wanted. Where the volume does not support it,
        // MoveFileEx is the fallback: still one step, but it has to wait for
        // readers, which the retry covers.

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
            FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, int bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        private const uint DELETE = 0x00010000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const int FileRenameInfoEx = 22;
        private const int FILE_RENAME_FLAG_REPLACE_IF_EXISTS = 0x1;
        private const int FILE_RENAME_FLAG_POSIX_SEMANTICS = 0x2;
        private const int MOVEFILE_REPLACE_EXISTING = 0x1;
        private const int MOVEFILE_WRITE_THROUGH = 0x8;

        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int ERROR_FILE_EXISTS = 80;
        private const int ERROR_ALREADY_EXISTS = 183;

        private static int _fallbackLogged;

        private static bool IsInUse(int error) =>
            error == ERROR_SHARING_VIOLATION || error == ERROR_LOCK_VIOLATION || error == ERROR_ACCESS_DENIED;

        /// <summary>
        /// Renames <paramref name="source"/> to <paramref name="target"/>,
        /// replacing any file there, in one step. Returns 0 or a Windows error.
        /// </summary>
        private static int RenameReplacing(string source, string target)
        {
            int error;
            using (var handle = CreateFile(source, DELETE | SYNCHRONIZE,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return Marshal.GetLastWin32Error();

                // FILE_RENAME_INFO: Flags, then RootDirectory (a handle, so
                // pointer-aligned), then FileNameLength in bytes, then the name.
                var name = Encoding.Unicode.GetBytes(target);
                int nameOffset = 2 * IntPtr.Size + 4;
                int size = nameOffset + name.Length + 2;
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                    Marshal.WriteInt32(buffer, 0, FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS);
                    Marshal.WriteInt32(buffer, 2 * IntPtr.Size, name.Length);
                    Marshal.Copy(name, 0, buffer + nameOffset, name.Length);

                    if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, size))
                        return 0;
                    error = Marshal.GetLastWin32Error();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            if (IsInUse(error))
                return error;

            // Not supported here (an older Windows, or a volume without POSIX
            // renames). The handle is closed, so the fallback can open the file.
            // Said once per process: a machine on the fallback otherwise looks
            // exactly like one that is not, until two products are open at once.
            if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
                SupervertalerLicence.WriteLog(
                    "Rename by handle unavailable (Windows error " + error + "); " +
                    "using MoveFileEx, which has to wait for readers.");

            return MoveFileEx(source, target, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)
                ? 0
                : Marshal.GetLastWin32Error();
        }

        /// <summary>
        /// Removes temporary files a process left behind by being killed in the
        /// middle of a write. Only ones old enough that no live write can still
        /// own them.
        /// </summary>
        internal static void SweepAbandonedTemporaryFiles(string path, TimeSpan olderThan)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) return;

                var cutoff = DateTime.UtcNow - olderThan;
                foreach (var file in Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
