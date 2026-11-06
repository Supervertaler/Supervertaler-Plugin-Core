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
    /// SuperVault on disk: its own memory (<see cref="VaultStore"/>), and the search index built from it.
    ///
    /// Layout of <see cref="Root"/> (by default <c>&lt;data folder&gt;\supervault\</c>):
    ///   supervault.db       the memory itself (SQLite), filled by imports
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

        /// <summary>
        /// Where a Supervertaler Workbench database would be, for the optional "Import from Workbench":
        /// <c>&lt;data folder&gt;\resources\supervertaler.db</c>. SuperVault never needs it.
        /// </summary>
        public static string WorkbenchDatabase => Path.Combine(SupervertalerPaths.ResourcesDir, "supervertaler.db");

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
                    else { s.State = VaultState.Missing; s.Message = "SuperVault is empty: import a TMX file or a translation memory."; }
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

        // ---- importing and building ----

        /// <summary>SuperVault's own memory, which the index is built from: <c>supervault.db</c> in <see cref="Root"/>.</summary>
        public string StorePath => Path.Combine(Root, "supervault.db");

        /// <summary>Units in the store. Opens the file: for a settings dialog, not for every lookup.</summary>
        public long StoreCount() => VaultStore.Count(StorePath);

        /// <summary>True when units were added to the store after the index in use was built.</summary>
        public bool IndexOutOfDate()
        {
            var idx = Current;
            long version = VaultStore.Version(StorePath);
            if (idx == null) return version > 0;
            return !ReadInfo(idx.Folder).TryGetValue("store_version", out var v) || v != version.ToString(CultureInfo.InvariantCulture);
        }

        public bool IsBuilding { get { lock (_gate) return _buildThread != null; } }

        /// <summary>
        /// Imports into the store with <paramref name="importer"/> (VaultTmx.Import, VaultStore.ImportWorkbench, a CAT
        /// tool's own TM reader), then rebuilds the index if anything was added. Synchronous; one job at a time across
        /// processes (the build lock is held throughout). The import is one transaction: if it fails or is cancelled,
        /// the store is as it was and the index is not touched.
        /// </summary>
        public VaultImportResult Import(Func<string, Action<string>, CancellationToken, VaultImportResult> importer, Action<string> progress, CancellationToken cancel)
        {
            using (TakeLock())
            {
                var result = importer(StorePath, progress, cancel);
                if (result.Added > 0 || (Current == null && File.Exists(StorePath))) BuildLocked(progress, cancel);
                return result;
            }
        }

        /// <summary>Rebuilds the index from the store. Synchronous; throws on failure, leaving the index in use as it was.</summary>
        public int Build(Action<string> progress, CancellationToken cancel)
        {
            using (TakeLock()) return BuildLocked(progress, cancel);
        }

        /// <summary>Starts <see cref="Import"/> on a background thread. False if a job is already running in this process.</summary>
        public bool StartImport(Func<string, Action<string>, CancellationToken, VaultImportResult> importer, Action<string> done = null) =>
            StartJob("import", cancel => Import(importer, p => _progress = p, cancel).ToString(), done);

        /// <summary>Starts <see cref="Build"/> on a background thread. False if a job is already running in this process.</summary>
        public bool StartBuild(Action<string> done = null) =>
            StartJob("build", cancel => Build(p => _progress = p, cancel).ToString("N0", CultureInfo.InvariantCulture) + " units indexed", done);

        /// <summary>The last background job's outcome in this process: its summary, or the error.</summary>
        public string LastJobResult { get { lock (_gate) return _lastResult; } }
        string _lastResult;

        /// <summary>
        /// Runs a job on a background thread at below-normal priority, so Studio's UI is never held up.
        /// <paramref name="done"/> gets null on success, or the error message.
        /// </summary>
        bool StartJob(string kind, Func<CancellationToken, string> job, Action<string> done)
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
                    string error = null, result = null;
                    try { result = job(cancel); }
                    catch (OperationCanceledException) { error = "Stopped. Nothing was changed."; }
                    catch (Exception ex) { error = ex.Message; }
                    lock (_gate)
                    {
                        _buildThread = null;
                        _lastError = error;
                        _lastResult = error ?? result;
                        _progress = null;
                        _checkedAt = 0;   // look at the pointer at once
                    }
                    try { done?.Invoke(error); } catch { }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SuperVault " + kind };
                _buildThread.Start();
                return true;
            }
        }

        public void CancelBuild() { lock (_gate) _buildCancel?.Cancel(); }

        IDisposable TakeLock()
        {
            System.IO.Directory.CreateDirectory(Root);
            try { return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { throw new IOException("Another program (a second Trados Studio, or memoQ) is importing into SuperVault or building its index right now. Try again when it has finished."); }
        }

        /// <summary>
        /// Builds a new generation from the store and makes it current; the caller holds the build lock. Throws on
        /// failure, with the current generation untouched and no half-written folder left behind.
        ///
        /// The store is read in one pass, ordered by id, inside one read transaction, so an import can't change it
        /// half-way. Memory use is bounded by Lucene's 48 MB buffer however large the store is (it matters in Studio
        /// 2024, a 32-bit process). Linear in the number of units.
        /// </summary>
        int BuildLocked(Action<string> progress, CancellationToken cancel)
        {
            string genFolder = null;
            try
            {
                if (!File.Exists(StorePath)) throw new FileNotFoundException("SuperVault is empty: import a TMX file or a translation memory first.", StorePath);
                var current = ReadPointer();
                DeleteOldGenerations(current, noneIsCurrent: current == null && !File.Exists(PointerFile));   // e.g. a build that died half-way

                var name = "gen-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                genFolder = Path.Combine(Root, name);
                var sw = Stopwatch.StartNew();
                int added = 0, read = 0;
                long total, version;

                using (var conn = VaultStore.Open(StorePath, readOnly: true))
                using (var tx = conn.BeginTransaction())   // one snapshot for the count, the version and the rows
                {
                    using (var q = conn.CreateCommand())
                    {
                        q.Transaction = tx;
                        q.CommandText = "SELECT count(*) FROM units";
                        total = Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture);
                        q.CommandText = "SELECT value FROM meta WHERE name = 'version'";
                        long.TryParse(Convert.ToString(q.ExecuteScalar(), CultureInfo.InvariantCulture), out version);
                    }
                    using (var dir = FSDirectory.Open(genFolder))
                    using (var w = new IndexWriter(dir, new IndexWriterConfig(VaultIndex.V, new VaultIndex.NoAnalyzer()) { OpenMode = OpenMode.CREATE, RAMBufferSizeMB = 48 }))
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "SELECT id, lang_a, text_a, lang_b, text_b, variant FROM units ORDER BY id";
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                if ((++read & 1023) == 0) cancel.ThrowIfCancellationRequested();
                                if ((read % 10000) == 0) progress?.Invoke("indexing: " + read.ToString("N0", CultureInfo.InvariantCulture) + " of " + total.ToString("N0", CultureInfo.InvariantCulture) + " units");
                                string a = r.GetString(1), ta = r.GetString(2), b = r.GetString(3), tb = r.GetString(4);
                                w.AddDocument(VaultIndex.MakeDocument(r.GetInt64(0).ToString(CultureInfo.InvariantCulture), a, ta, VaultMarkup.Plain(ta), b, tb, VaultMarkup.Plain(tb), r.IsDBNull(5) ? "" : r.GetString(5)));
                                added++;
                            }
                        }
                        cancel.ThrowIfCancellationRequested();
                        progress?.Invoke("saving the index");
                        w.Commit();
                    }
                }

                // Verify before switching: a generation that can't be opened never becomes current.
                using (var check = VaultIndex.Open(genFolder))
                    if (check.Count != added) throw new InvalidDataException($"The new index holds {check.Count} units, expected {added}.");

                var info = new StringBuilder();
                info.AppendLine("format=" + VaultIndex.Format);
                info.AppendLine("built=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                info.AppendLine("store_version=" + version.ToString(CultureInfo.InvariantCulture));
                info.AppendLine("units=" + added);
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
