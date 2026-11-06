using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault
{
    public enum VaultState { Missing, Ready, Broken }

    /// <summary>What a product shows about SuperVault: is there an index, how big, and is a build running.</summary>
    public sealed class VaultStatus
    {
        public VaultState State;
        public int Units;
        public DateTime? BuiltAt;          // UTC
        public string Message;             // for Broken: what went wrong; otherwise a one-line summary
        public bool Building;              // a build is running in this process
        public string BuildProgress;       // "412,000 of 1,554,005 units" while building
        public string LastBuildError;      // the last build's failure in this process, if it failed
    }

    /// <summary>
    /// The SuperVault index on disk, and building it from the Workbench database.
    ///
    /// Layout of <see cref="Root"/> (by default <c>&lt;data folder&gt;\supervault\</c>):
    ///   current.txt         name of the generation in use, replaced in one step (AtomicFile)
    ///   gen-&lt;utc&gt;-&lt;n&gt;\    one complete index per build, never changed once current
    ///   build.lock          held open, unshared, by the process building; the OS drops it if that process dies
    ///
    /// A rebuild writes a NEW generation beside the one in use; lookups carry on against the old one until the new
    /// one is complete, verified and made current. A failed or cancelled build deletes its half-written folder and
    /// leaves the current generation untouched. Older generations are deleted once nothing has them open; a folder
    /// still open (here or in another Studio) is left and retried after the next build.
    ///
    /// One instance per root per process (<see cref="For"/>), shared by every provider object a CAT tool creates.
    /// </summary>
    public sealed class VaultLibrary
    {
        static readonly Dictionary<string, VaultLibrary> Libraries = new Dictionary<string, VaultLibrary>(StringComparer.OrdinalIgnoreCase);

        public static VaultLibrary For(string root)
        {
            root = Path.GetFullPath(root);
            lock (Libraries)
            {
                if (!Libraries.TryGetValue(root, out var lib)) Libraries[root] = lib = new VaultLibrary(root);
                return lib;
            }
        }

        /// <summary>Where SuperVault lives for this user: <c>&lt;Supervertaler data folder&gt;\supervault</c>.</summary>
        public static string DefaultRoot => Path.Combine(SupervertalerPaths.Root, "supervault");

        /// <summary>The Workbench database SuperVault is built from: <c>&lt;data folder&gt;\resources\supervertaler.db</c>.</summary>
        public static string DefaultDatabase => Path.Combine(SupervertalerPaths.ResourcesDir, "supervertaler.db");

        public string Root { get; }
        string PointerFile => Path.Combine(Root, "current.txt");
        string LockFile => Path.Combine(Root, "build.lock");

        readonly object _gate = new object();
        VaultIndex _open;              // the generation lookups use; null if none or broken
        string _openName;              // which generation _open is
        string _broken;                // why the current generation could not be opened (cleared by a new pointer)
        string _brokenName;
        long _checkedAt;               // Stopwatch ticks of the last pointer check
        static readonly long CheckEvery = Stopwatch.Frequency * 2;   // the pointer is looked at no more than every 2 s

        Thread _buildThread;
        CancellationTokenSource _buildCancel;
        string _progress, _lastError;

        VaultLibrary(string root) { Root = root; }

        /// <summary>
        /// The index to look things up in, or null if there is none yet or it is damaged (see <see cref="Status"/>).
        /// Picks up a newly built generation within 2 s. Cheap: one file read every 2 s at most.
        /// </summary>
        public VaultIndex Current
        {
            get
            {
                lock (_gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (_checkedAt != 0 && now - _checkedAt < CheckEvery) return _open;
                    _checkedAt = now;
                    var name = ReadPointer();
                    if (name == _openName && _open != null) return _open;
                    if (name == null) { Swap(null, null); return null; }
                    if (name == _brokenName) return null;      // already failed to open; wait for a new build
                    var info = ReadInfo(Path.Combine(Root, name));
                    if (!info.TryGetValue("format", out var format) || format != VaultIndex.Format.ToString(CultureInfo.InvariantCulture))
                    {
                        Swap(null, null);
                        _broken = "it was built by another version of Supervertaler";
                        _brokenName = name;
                        return null;
                    }
                    try
                    {
                        var idx = VaultIndex.Open(Path.Combine(Root, name));
                        bool replaced = _openName != null;
                        Swap(idx, name);
                        _broken = _brokenName = null;
                        if (replaced) ScheduleCleanup();
                    }
                    catch (Exception ex)
                    {
                        Swap(null, null);
                        _broken = ex.GetType().Name + ": " + ex.Message;
                        _brokenName = name;
                    }
                    return _open;
                }
            }
        }

        void Swap(VaultIndex idx, string name)
        {
            var old = _open;
            _open = idx; _openName = name;
            old?.Dispose();   // lookups still running on it keep their own reference; it closes after the last one
        }

        string ReadPointer()
        {
            try
            {
                if (!File.Exists(PointerFile)) return null;
                var name = AtomicFile.ReadAllText(PointerFile)?.Trim();
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                return System.IO.Directory.Exists(Path.Combine(Root, name)) ? name : null;
            }
            catch (IOException) { return _openName; }   // being replaced this instant: keep what we have
        }

        public VaultStatus Status
        {
            get
            {
                var idx = Current;
                var s = new VaultStatus { Building = IsBuilding, BuildProgress = _progress, LastBuildError = _lastError };
                lock (_gate)
                {
                    if (idx != null)
                    {
                        s.State = VaultState.Ready;
                        s.Units = idx.Count;
                        s.BuiltAt = ReadBuiltAt(idx.Folder);
                        s.Message = idx.Count.ToString("N0", CultureInfo.InvariantCulture) + " units";
                    }
                    else if (_broken != null) { s.State = VaultState.Broken; s.Message = "The SuperVault index could not be opened (" + _broken + "). Rebuild it."; }
                    else { s.State = VaultState.Missing; s.Message = "No SuperVault index has been built yet."; }
                }
                return s;
            }
        }

        static DateTime? ReadBuiltAt(string folder)
        {
            var info = ReadInfo(folder);
            return info.TryGetValue("built", out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null;
        }

        internal static Dictionary<string, string> ReadInfo(string folder)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var line in File.ReadAllLines(Path.Combine(folder, "info.txt")))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return d;
        }

        // ---- building ----

        public bool IsBuilding { get { lock (_gate) return _buildThread != null; } }

        /// <summary>
        /// Starts building a new generation from <paramref name="database"/> on a background thread (below-normal
        /// priority, so Studio's UI is never held up). Returns false if a build is already running in this process.
        /// <paramref name="done"/> is called on that thread with null on success, or the error message.
        /// </summary>
        public bool StartBuild(string database, Action<string> done = null)
        {
            lock (_gate)
            {
                if (_buildThread != null) return false;
                _buildCancel = new CancellationTokenSource();
                _progress = "starting";
                _lastError = null;
                var cancel = _buildCancel.Token;
                _buildThread = new Thread(() =>
                {
                    string error = null;
                    try { Build(database, p => _progress = p, cancel); }
                    catch (OperationCanceledException) { error = "The build was cancelled."; }
                    catch (Exception ex) { error = ex.Message; }
                    lock (_gate)
                    {
                        _buildThread = null;
                        _lastError = error;
                        _progress = null;
                        _checkedAt = 0;   // look at the pointer at once
                    }
                    try { done?.Invoke(error); } catch { }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SuperVault index build" };
                _buildThread.Start();
                return true;
            }
        }

        public void CancelBuild() { lock (_gate) _buildCancel?.Cancel(); }

        /// <summary>
        /// Builds a new generation from the Workbench database and makes it current. Synchronous; throws on failure,
        /// with the current generation untouched and no half-written folder left behind.
        ///
        /// The database is opened read-only and read in one pass, ordered by id. Memory use is bounded by Lucene's
        /// 48 MB buffer however large the database is (it matters in Studio 2024, a 32-bit process). Measured: about
        /// 5 minutes for the 1.55M-unit Workbench TM; linear, so about 35 minutes at 10M.
        /// </summary>
        public int Build(string database, Action<string> progress, CancellationToken cancel, int busyTimeoutSeconds = 30)
        {
            System.IO.Directory.CreateDirectory(Root);
            FileStream lockStream;
            try { lockStream = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { throw new IOException("Another program (a second Trados Studio, or memoQ) is building the SuperVault index right now. Try again when it has finished."); }

            string genFolder = null;
            try
            {
                if (!File.Exists(database)) throw new FileNotFoundException("The Workbench database was not found at " + database + ".", database);
                var current = ReadPointer();
                DeleteOldGenerations(current, noneIsCurrent: current == null && !File.Exists(PointerFile));   // e.g. a build that died half-way

                var name = "gen-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                genFolder = Path.Combine(Root, name);
                var sw = Stopwatch.StartNew();
                int added = 0, junk = 0, skipped = 0, read = 0; long total = 0;

                var cs = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, DefaultTimeout = busyTimeoutSeconds, Pooling = false }.ToString();
                using (var conn = new SqliteConnection(cs))
                {
                    conn.Open();
                    using (var count = conn.CreateCommand())
                    {
                        count.CommandText = "SELECT count(*) FROM translation_units";
                        total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }
                    using (var dir = FSDirectory.Open(genFolder))
                    using (var w = new IndexWriter(dir, new IndexWriterConfig(VaultIndex.V, new VaultIndex.NoAnalyzer()) { OpenMode = OpenMode.CREATE, RAMBufferSizeMB = 48 }))
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id, source_text, target_text, source_lang, target_lang FROM translation_units ORDER BY id";
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                if ((++read & 1023) == 0) cancel.ThrowIfCancellationRequested();
                                if ((read % 10000) == 0) progress?.Invoke(read.ToString("N0", CultureInfo.InvariantCulture) + " of " + total.ToString("N0", CultureInfo.InvariantCulture) + " units");
                                var srcRaw = r.IsDBNull(1) ? "" : r.GetString(1);
                                var trgRaw = r.IsDBNull(2) ? "" : r.GetString(2);
                                var srcLangFull = r.IsDBNull(3) ? "" : r.GetString(3);
                                var trgLangFull = r.IsDBNull(4) ? "" : r.GetString(4);
                                string a = VaultMarkup.BaseLanguage(srcLangFull), b = VaultMarkup.BaseLanguage(trgLangFull);
                                if (a.Length == 0 || b.Length == 0 || a == b) { skipped++; continue; }
                                string pa = VaultMarkup.Plain(srcRaw), pb = VaultMarkup.Plain(trgRaw);
                                if (VaultMarkup.IsJunk(pa, pb)) { junk++; continue; }
                                w.AddDocument(VaultIndex.MakeDocument(r.GetInt64(0).ToString(CultureInfo.InvariantCulture), a, srcRaw, pa, b, trgRaw, pb, srcLangFull + ">" + trgLangFull));
                                added++;
                            }
                        }
                        cancel.ThrowIfCancellationRequested();
                        progress?.Invoke("saving");
                        w.Commit();
                    }
                }

                // Verify before switching: a generation that can't be opened never becomes current.
                using (var check = VaultIndex.Open(genFolder))
                    if (check.Count != added) throw new InvalidDataException($"The new index holds {check.Count} units, expected {added}.");

                var info = new StringBuilder();
                info.AppendLine("format=" + VaultIndex.Format);
                info.AppendLine("built=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                info.AppendLine("database=" + database);
                info.AppendLine("rows=" + read);
                info.AppendLine("units=" + added);
                info.AppendLine("junk=" + junk);
                info.AppendLine("skipped=" + skipped);
                info.AppendLine("seconds=" + (int)sw.Elapsed.TotalSeconds);
                File.WriteAllText(Path.Combine(genFolder, "info.txt"), info.ToString(), new UTF8Encoding(false));

                if (AtomicFile.Write(PointerFile, new UTF8Encoding(false).GetBytes(name), true, null) != AtomicFile.Outcome.Written)
                    throw new IOException("Could not switch to the new SuperVault index (" + PointerFile + " is in use). The previous index stays in use.");
                genFolder = null;   // it is current now: never delete it below
                lock (_gate) _checkedAt = 0;
                DeleteOldGenerations(keep: name);
                return added;
            }
            finally
            {
                if (genFolder != null) TryDelete(genFolder);   // failed or cancelled: leave nothing half-written
                lockStream.Dispose();
            }
        }

        /// <summary>
        /// After switching to a new generation, deletes the old one once lookups on it have had time to finish.
        /// Only while holding the build lock: without it, the folder being deleted could be a build in progress
        /// in another process.
        /// </summary>
        void ScheduleCleanup()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(CleanupDelayMs);
                FileStream lockStream;
                try { lockStream = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { return; }   // a build is running; it cleans up when it ends
                catch (UnauthorizedAccessException) { return; }
                using (lockStream) DeleteOldGenerations(ReadPointer());
            });
        }

        internal static int CleanupDelayMs = 30000;

        /// <summary>Deletes every generation but <paramref name="keep"/>. Call only while holding the build lock.</summary>
        void DeleteOldGenerations(string keep, bool noneIsCurrent = false)
        {
            if (keep == null && !noneIsCurrent) return;   // never sweep everything because the pointer couldn't be read
            if (!System.IO.Directory.Exists(Root)) return;
            foreach (var d in System.IO.Directory.GetDirectories(Root, "gen-*"))
            {
                var n = Path.GetFileName(d);
                if (string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)) continue;
                lock (_gate) if (string.Equals(n, _openName, StringComparison.OrdinalIgnoreCase)) continue;   // a lookup may still use it
                TryDelete(d);
            }
        }

        static void TryDelete(string folder)
        {
            try { System.IO.Directory.Delete(folder, true); }
            catch (IOException) { }                    // open in another process: next build tries again
            catch (UnauthorizedAccessException) { }
        }
    }
}
