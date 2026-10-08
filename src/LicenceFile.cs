using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace Supervertaler.Core
{
    /// <summary>
    /// Reads and writes the licence file that every Supervertaler product
    /// shares, at <c>&lt;data folder&gt;/licence/licence.json</c>.
    ///
    /// Two products can have it open at once - Trados and memoQ side by side is
    /// a normal working day - so it is written as two-process state, with no
    /// lock of any kind (the mechanics are in <see cref="AtomicFile"/>):
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
    ///
    /// A licence counts only for the computer and Windows account that
    /// activated it, so a data folder that several people use - or that one
    /// person uses from more than one computer - holds one file per person.
    /// <c>licence.json</c> belongs to whoever's record it holds; everyone else
    /// has a file of their own beside it (<see cref="PersonalPath"/>). Each
    /// person writes only their own file, so they cannot overwrite each other,
    /// and every build that reads only <c>licence.json</c> still finds its
    /// owner's licence exactly where it was. The one exception is clearing an
    /// activation the licence server has just confirmed released, which is
    /// written back to the file that recorded it.
    /// </summary>
    internal static class LicenceFile
    {
        /// <summary>The shared licence file.</summary>
        internal static string SharedPath =>
            Path.Combine(SupervertalerPaths.Root, "licence", "licence.json");

        private const string PersonalPrefix = "licence-";

        /// <summary>
        /// The licence file of the person with <paramref name="fingerprint"/>,
        /// when <c>licence.json</c> beside <paramref name="sharedPath"/> is
        /// someone else's. Named by the start of the fingerprint, which is
        /// already a hash: it says nothing about the person.
        /// </summary>
        internal static string PersonalPath(string sharedPath, string fingerprint)
        {
            var name = new StringBuilder();
            foreach (var c in fingerprint ?? "")
            {
                if (name.Length == 16) break;
                if (char.IsLetterOrDigit(c) || c == '-') name.Append(char.ToLowerInvariant(c));
            }
            return Path.Combine(Path.GetDirectoryName(sharedPath), PersonalPrefix + name + ".json");
        }

        /// <summary>
        /// Every licence file in the folder: the shared one and each person's.
        /// Read only to find an activation of a key that has run out of them.
        /// </summary>
        internal static string[] AllRecordPaths(string sharedPath)
        {
            var paths = new System.Collections.Generic.List<string> { sharedPath };
            try
            {
                var dir = Path.GetDirectoryName(sharedPath);
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, PersonalPrefix + "*.json"))
                    {
                        // The pattern also matches longer extensions on some
                        // file systems; temporary and set-aside files are not records.
                        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            paths.Add(file);
                    }
                }
            }
            catch { }
            return paths.ToArray();
        }

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
                    if (attempt >= AtomicFile.RetryDelaysMs.Length) return ReadStatus.Unreadable;
                    Thread.Sleep(AtomicFile.RetryDelaysMs[attempt]);
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
        /// step (see <see cref="AtomicFile"/>). With <paramref name="replaceExisting"/>
        /// false it only creates the file, and reports
        /// <see cref="WriteStatus.AlreadyExists"/> if another writer got there first.
        /// </summary>
        internal static WriteStatus Write(string path, byte[] bytes, bool replaceExisting)
        {
            switch (AtomicFile.Write(path, bytes, replaceExisting, SupervertalerLicence.WriteLog))
            {
                case AtomicFile.Outcome.Written: return WriteStatus.Written;
                case AtomicFile.Outcome.AlreadyExists: return WriteStatus.AlreadyExists;
                default: return WriteStatus.Failed;
            }
        }

        /// <summary>
        /// Removes temporary files a process left behind by being killed in the
        /// middle of a write. Only ones old enough that no live write can still
        /// own them.
        /// </summary>
        internal static void SweepAbandonedTemporaryFiles(string path, TimeSpan olderThan) =>
            AtomicFile.SweepAbandoned(path, olderThan);
    }
}
