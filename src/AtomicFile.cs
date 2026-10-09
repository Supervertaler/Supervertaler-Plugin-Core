using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Supervertaler.Core
{
    /// <summary>
    /// Writes a small file in one step, safely while other processes - the
    /// other Supervertaler product, a second Trados Studio, a virus scanner or
    /// a sync tool - may be reading it, with no lock of any kind:
    ///
    ///   - A write goes to a temporary file in the same folder, which then
    ///     replaces the real one in a single step. A reader sees the old file
    ///     or the new one, never half of either, and never no file at all.
    ///   - A write that finds the file in use retries for about a third of a
    ///     second, then gives up cleanly: the temporary file is removed and the
    ///     old file stays exactly as it was. It never falls back to writing the
    ///     real file in place, because that fallback would run only when the
    ///     file is contended - exactly when a half-written file is most likely
    ///     to be read.
    ///   - A first write can be "only if there is no file yet", so two writers
    ///     starting at once do not overwrite each other: the loser reads the
    ///     winner's.
    ///
    /// Readers should open with delete sharing (<see cref="ReadAllText"/>), so
    /// a read never stands in the way of a write.
    ///
    /// Written for the shared licence file (<see cref="LicenceFile"/>), where
    /// two products write the same file; used wherever a settings file must not
    /// be lost to someone else reading it at the wrong moment.
    /// </summary>
    internal static class AtomicFile
    {
        internal enum Outcome
        {
            Written,
            /// <summary>An only-if-absent write found a file already there. Nothing was changed.</summary>
            AlreadyExists,
            /// <summary>Gave up. The old file, if any, is untouched and no temporary file is left.</summary>
            Failed,
        }

        // Retry schedule for a file someone else has in use. About a third of a
        // second in all: long enough to outlast a read or a rename, short enough
        // not to be felt if it is paid on the UI thread.
        internal static readonly int[] RetryDelaysMs = { 10, 20, 40, 80, 160 };

        /// <summary>
        /// Writes <paramref name="bytes"/> to <paramref name="path"/> in one
        /// step. With <paramref name="replaceExisting"/> false it only creates
        /// the file, and reports <see cref="Outcome.AlreadyExists"/> if another
        /// writer got there first. Why it failed, if it did, goes to
        /// <paramref name="log"/>.
        /// </summary>
        internal static Outcome Write(string path, byte[] bytes, bool replaceExisting, Action<string> log)
        {
            // Unique per write, so two writers never share a temporary file.
            // The name is "<file>.<guid>.tmp", which SweepAbandoned looks for.
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
                        error = RenameReplacing(temp, path, log);
                    }
                    else
                    {
                        // Move refuses to overwrite, which is what makes the
                        // first write safe for two writers to race.
                        error = MoveFileEx(temp, path, MOVEFILE_WRITE_THROUGH) ? 0 : Marshal.GetLastWin32Error();
                        if (error == ERROR_ALREADY_EXISTS || error == ERROR_FILE_EXISTS)
                            return Outcome.AlreadyExists;
                    }

                    if (error == 0)
                        return Outcome.Written;

                    if (!IsInUse(error) || attempt >= RetryDelaysMs.Length)
                    {
                        Say(log, Path.GetFileName(path) + " not written (Windows error " + error +
                            "); the previous file is unchanged.");
                        return Outcome.Failed;
                    }
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
            catch (Exception ex)
            {
                Say(log, Path.GetFileName(path) + " not written: " + ex.GetType().Name + ": " + ex.Message);
                return Outcome.Failed;
            }
            finally
            {
                // On success the temporary file has become the real one and this
                // does nothing. On every other path it is the cleanup.
                try { File.Delete(temp); } catch { }
            }
        }

        /// <summary>
        /// Reads a text file with delete sharing, so this read never makes
        /// someone else's <see cref="Write"/> fail. A byte-order mark, if any,
        /// decides the encoding; otherwise UTF-8. A file someone holds without
        /// sharing - a scanner, a sync tool - is waited out on the same schedule
        /// as a write. Throws as File.ReadAllText does: at once for a missing
        /// file, otherwise once the wait runs out.
        /// </summary>
        internal static string ReadAllText(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (FileNotFoundException) { throw; }
                catch (DirectoryNotFoundException) { throw; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                           && attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
        }

        /// <summary>
        /// <see cref="ReadAllText"/> without the decoding: the file's exact bytes,
        /// read with the same sharing and the same wait. For a caller that has to
        /// know whether the file changed at all - a byte-order mark or a line
        /// ending included - which decoded text cannot tell it.
        /// </summary>
        internal static byte[] ReadAllBytes(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (FileNotFoundException) { throw; }
                catch (DirectoryNotFoundException) { throw; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                           && attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
        }

        /// <summary>
        /// Removes temporary files a process left behind by being killed in the
        /// middle of a write to <paramref name="path"/>. Only ones old enough
        /// that no live write can still own them.
        /// </summary>
        internal static void SweepAbandoned(string path, TimeSpan olderThan)
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

        private static void Say(Action<string> log, string message)
        {
            try { log?.Invoke(message); }
            catch { /* logging must never break a write */ }
        }

        // Why a rename by handle, and not File.Replace or a plain MoveFileEx -
        // both were tried against a second process reading the file:
        //
        //   - File.Replace moves the old file aside before moving the new one
        //     in, so for a moment there is no file at all. A product starting in
        //     that moment reads "no licence" and would begin a trial. It also
        //     left its own ~RF*.TMP files behind, and it fails outright ("Unable
        //     to remove the file to be replaced") whenever anyone has the file
        //     open, which lost project settings in October 2026.
        //   - MoveFileEx will not replace a file another process has open, even
        //     with delete sharing, so another process merely reading would block
        //     every write.
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
        private static int RenameReplacing(string source, string target, Action<string> log)
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
            // exactly like one that is not, until two writers meet.
            if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
                Say(log, "Rename by handle unavailable (Windows error " + error + "); " +
                    "using MoveFileEx, which has to wait for readers.");

            return MoveFileEx(source, target, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)
                ? 0
                : Marshal.GetLastWin32Error();
        }
    }
}
