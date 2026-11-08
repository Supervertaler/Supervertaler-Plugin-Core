using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lucene.Net.Documents;
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
        public long LearnedThisSession;    // confirmed segments this process has stored
        public string LearnError;          // why storing confirmed segments last failed (they are kept and retried)
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
    ///
    /// Learning as you work (<see cref="Learn"/>): a confirmed segment goes to an in-memory queue (the CAT tool's
    /// call returns at once), then to <c>inbox.db</c> (<see cref="VaultInbox"/>), then, under the build lock, into
    /// the store and the current generation, which is updated in place (Lucene adds a small segment; nothing is
    /// rebuilt). Lookups pick the new units up within about 2 s (<see cref="Current"/> reopens the generation).
    ///
    /// The locking scheme, all of it:
    ///   1. build.lock is the only lock. Whoever holds it is the only writer of the store and of the index, in any
    ///      process: imports, rebuilds and learning all take it. Lucene's one-writer rule is therefore met by
    ///      construction, and a store transaction never waits for another writer.
    ///   2. Learning never waits for build.lock: it tries it, and if another Studio holds it (a 6-minute import, or
    ///      that Studio's own learning) tries again a few seconds later. Meanwhile the units are safe in inbox.db,
    ///      which only ever sees transactions of a few milliseconds. Whoever next holds the lock moves every waiting
    ///      unit, whichever Studio confirmed it.
    ///   3. Nobody waits for build.lock while holding anything (an inbox transaction is finished before the lock is
    ///      tried), and the holder of build.lock only ever waits, with a time limit, for an inbox transaction of a few
    ///      milliseconds. So no two writers can wait on each other: no deadlock. The OS drops the lock if its holder dies.
    /// </summary>
    public sealed class VaultLibrary
    {
        static readonly Dictionary<string, VaultLibrary> Libraries = new Dictionary<string, VaultLibrary>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tests only: a second library object on the same folder, as another process would have.</summary>
        internal static VaultLibrary ForTests(string root) { var lib = new VaultLibrary(Path.GetFullPath(root)); lock (Libraries) TestOnly.Add(lib); return lib; }
        static readonly List<VaultLibrary> TestOnly = new List<VaultLibrary>();

        /// <summary>Tests only: closes every open index in this process, so its scratch folders can be deleted.</summary>
        internal static void CloseAllForTests()
        {
            lock (Libraries) foreach (var lib in Libraries.Values.Concat(TestOnly)) { lock (lib._gate) { lib.Swap(null, null); lib._checkedAt = long.MaxValue / 2; } lock (lib._learnGate) lib._waiting.Clear(); }
        }

        /// <summary>Tests only: look at the pointer and the index's commits at the next <see cref="Current"/>.</summary>
        internal void Refresh() { lock (_gate) _checkedAt = 0; }

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
                    if (name == _openName && _open != null)
                    {
                        // Same generation: pick up units committed to it since (learned, or a small import).
                        try { var newer = _open.Reopen(); if (newer != null) Swap(newer, name); }
                        catch (Exception ex) { TryLog("Could not reopen the index: " + ex.Message); }   // keep searching the version we have
                        return _open;
                    }
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
                var s = new VaultStatus { Building = IsBuilding, BuildProgress = _progress, LastBuildError = _lastError, LearnedThisSession = LearnedThisSession, LearnError = _learnError };
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

        /// <summary>True when the store holds changes the index in use doesn't have yet.</summary>
        public bool IndexOutOfDate()
        {
            var idx = Current;
            long version = VaultStore.Version(StorePath);
            if (idx == null) return version > 0;
            if (idx.HasStoreSeq) return VaultStore.ChangesAfter(StorePath, idx.StoreSeq) > 0;
            // An index built by stage 1: compare the import count it was built from.
            return !ReadInfo(idx.Folder).TryGetValue("store_version", out var v) || v != version.ToString(CultureInfo.InvariantCulture);
        }

        public bool IsBuilding { get { lock (_gate) return _buildThread != null; } }

        /// <summary>
        /// Imports into the store with <paramref name="importer"/> (VaultTmx.Import, VaultStore.ImportWorkbench, a CAT
        /// tool's own TM reader), then brings the index up to date: a piece at a time when the import is small next to
        /// the store (<see cref="SyncIndexLocked"/>), with a full rebuild when it is large or there is no usable index.
        /// Synchronous; one job at a time across processes (the build lock is held throughout). The import is one
        /// transaction: if it fails or is cancelled, the store is as it was and the index is not touched.
        /// </summary>
        public VaultImportResult Import(Func<string, Action<string>, CancellationToken, VaultImportResult> importer, Action<string> progress, CancellationToken cancel)
        {
            using (TakeLock())
            {
                VaultStore.Create(StorePath);   // upgrade an older store first, so the import's units are logged as changes
                var result = importer(StorePath, progress, cancel);
                if (result.Added > 0 || Current == null) SyncIndexLocked(progress, cancel, rebuildIfLarge: true);
                FlushWhileLocked();
                return result;
            }
        }

        /// <summary>After an import or rebuild, while we still hold the lock: store the confirmations that waited for it.</summary>
        void FlushWhileLocked()
        {
            try { FlushLocked(); }
            catch (Exception ex) { TryLog("Learning after an import or rebuild: " + ex.Message); }   // the learning thread tries again
        }

        /// <summary>
        /// A full rebuild beats applying changes one by one once they are more than this share of the store. Measured
        /// at 1.5M units (8 Nov 2026): a rebuild takes 373 s (about 4,000 units a second, whatever was imported); a
        /// 50,000-unit import applied piece at a time took 33-34 s, import included (about 1,500 a second). So a quarter
        /// of 1.5M (375,000) applied piece at a time is roughly 250 s, still under a rebuild; above that, rebuild.
        /// </summary>
        internal const double RebuildShare = 0.25;

        /// <summary>Rebuilds the index from the store. Synchronous; throws on failure, leaving the index in use as it was.</summary>
        public int Build(Action<string> progress, CancellationToken cancel)
        {
            using (TakeLock())
            {
                int n = BuildLocked(progress, cancel);
                FlushWhileLocked();
                return n;
            }
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
            var l = TryTakeLock();
            if (l == null) throw new IOException("Another program (a second Trados Studio, or memoQ) is importing into SuperVault, building its index or storing confirmed segments right now. Try again when it has finished.");
            return l;
        }

        /// <summary>The build lock, or null if someone holds it. Never waits.</summary>
        FileStream TryTakeLock()
        {
            System.IO.Directory.CreateDirectory(Root);
            try { return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }   // the file is being deleted by its last holder this instant
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
                long total, version, lastChange;
                VaultStore.Create(StorePath);   // an older store gains its change log before we record a position in it

                using (var conn = VaultStore.Open(StorePath, readOnly: true))
                using (var tx = conn.BeginTransaction())   // one snapshot for the count, the version, the change position and the rows
                {
                    using (var q = conn.CreateCommand())
                    {
                        q.Transaction = tx;
                        q.CommandText = "SELECT count(*) FROM units";
                        total = Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture);
                        q.CommandText = "SELECT value FROM meta WHERE name = 'version'";
                        long.TryParse(Convert.ToString(q.ExecuteScalar(), CultureInfo.InvariantCulture), out version);
                    }
                    lastChange = VaultStore.LastChange(conn, tx);
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
                        w.SetCommitData(new Dictionary<string, string> { [VaultIndex.SeqKey] = lastChange.ToString(CultureInfo.InvariantCulture) });
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
                try { VaultStore.PruneChanges(StorePath, lastChange, StoreBusySeconds); }
                catch (Exception ex) { TryLog("Could not tidy the change log (harmless, retried later): " + ex.Message); }
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

        // ---- the index a piece at a time ----

        /// <summary>
        /// Applies the store's changes since the index's last one to the current generation, in place, and commits.
        /// The caller holds the build lock. Lucene's commit is all-or-nothing: if anything fails before it, the
        /// generation is rolled back to its last commit (still readable, the changes simply not in it yet) and the
        /// changes stay in the log for next time.
        ///
        /// With <paramref name="rebuildIfLarge"/> (after an import) a missing, damaged or other-format index is rebuilt,
        /// as in stage 1, and so is one that would need more than <see cref="RebuildShare"/> of the store applied.
        /// Without it (learning), a damaged index is left alone: the units are safe in the store, and the dialog says
        /// to rebuild. A missing one is built either way: that is the first confirmed segment of an empty SuperVault.
        ///
        /// Cost: proportional to the number of changes, not to the size of the store. Memory: Lucene's 16 MB buffer.
        /// </summary>
        void SyncIndexLocked(Action<string> progress, CancellationToken cancel, bool rebuildIfLarge)
        {
            if (!File.Exists(StorePath)) return;
            var name = ReadPointer();
            if (name == null || (rebuildIfLarge && Current == null)) { BuildLocked(progress, cancel); return; }
            lock (_gate) if (name == _brokenName) return;   // damaged or another format: wait for "Rebuild index"
            var folder = Path.Combine(Root, name);
            if (!ReadInfo(folder).TryGetValue("format", out var format) || format != VaultIndex.Format.ToString(CultureInfo.InvariantCulture)) return;

            long applied = 0, from;
            using (var dir = FSDirectory.Open(folder))
            {
                var config = new IndexWriterConfig(VaultIndex.V, new VaultIndex.NoAnalyzer())
                {
                    OpenMode = OpenMode.APPEND,
                    RAMBufferSizeMB = 16,                        // Studio 2024 is a 32-bit process
                    MergeScheduler = new SerialMergeScheduler(), // merges run here, below normal priority, before the lock is released
                    // Designed for 10M units (a 2+ GB index): never merge into a segment over 512 MB here, so one tidy-up
                    // holds the lock for a minute at most, not the many minutes a multi-GB merge would take.
                    MergePolicy = new TieredMergePolicy { MaxMergedSegmentMB = 512 },
                };
                var w = new IndexWriter(dir, config);
                try
                {
                    from = VaultIndex.ReadSeq(w.CommitData, out _);
                    if (rebuildIfLarge)
                    {
                        long pending = VaultStore.ChangesAfter(StorePath, from);
                        if (pending > StoreCount() * RebuildShare)
                        {
                            w.Rollback();
                            BuildLocked(progress, cancel);
                            return;
                        }
                    }
                    using (var conn = VaultStore.Open(StorePath, readOnly: true))
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT c.seq, c.unit_id, u.id, u.lang_a, u.text_a, u.lang_b, u.text_b, u.variant FROM changes c LEFT JOIN units u ON u.id = c.unit_id WHERE c.seq > $from ORDER BY c.seq LIMIT 2000";
                        var p = cmd.Parameters.AddWithValue("$from", from);
                        while (true)
                        {
                            // The latest state of each unit in this page: a unit changed twice is indexed once.
                            var page = new Dictionary<long, Document>();
                            long last = from;
                            using (var r = cmd.ExecuteReader())
                                while (r.Read())
                                {
                                    last = r.GetInt64(0);
                                    long id = r.GetInt64(1);
                                    if (r.IsDBNull(2)) { page[id] = null; continue; }   // removed
                                    string a = r.GetString(3), ta = r.GetString(4), b = r.GetString(5), tb = r.GetString(6);
                                    page[id] = VaultIndex.MakeDocument(id.ToString(CultureInfo.InvariantCulture), a, ta, VaultMarkup.Plain(ta), b, tb, VaultMarkup.Plain(tb), r.IsDBNull(7) ? "" : r.GetString(7));
                                }
                            if (last == from) break;
                            foreach (var kv in page)
                            {
                                var term = new Term("id", kv.Key.ToString(CultureInfo.InvariantCulture));
                                if (kv.Value == null) w.DeleteDocuments(term); else w.UpdateDocument(term, kv.Value);
                            }
                            applied += page.Count;
                            from = last;
                            p.Value = from;
                            cancel.ThrowIfCancellationRequested();
                            progress?.Invoke("updating the index: " + applied.ToString("N0", CultureInfo.InvariantCulture) + " units");
                        }
                    }
                    if (applied > 0)
                    {
                        w.SetCommitData(new Dictionary<string, string> { [VaultIndex.SeqKey] = from.ToString(CultureInfo.InvariantCulture) });
                        FailPoint?.Invoke("index-commit");
                        w.Commit();
                        // The units are searchable from here. Lucene may now tidy small segments into bigger ones
                        // (in Dispose, measured up to 30 s at 1.5M in a 32-bit process): lookups needn't wait for that.
                        lock (_gate) _checkedAt = 0;
                    }
                    w.Dispose();
                }
                catch
                {
                    try { w.Rollback(); } catch { }   // back to the last commit; never a half-applied index
                    throw;
                }
            }
            if (applied == 0) return;
            lock (_gate) _checkedAt = 0;   // this process sees the new units at its next lookup
            try { VaultStore.PruneChanges(StorePath, from, StoreBusySeconds); }
            catch (Exception ex) { TryLog("Could not tidy the change log (harmless, retried later): " + ex.Message); }
        }

        // ---- learning as you work ----

        /// <summary>Where diagnostics go (the CAT tool's log). Never required.</summary>
        public static Action<string> Log { get; set; }
        static void TryLog(string message) { try { Log?.Invoke(message); } catch { } }

        /// <summary>Tests only: called at named points of a write ("store-commit", "inbox-delete", "index-commit"); throw to simulate a failure there.</summary>
        internal static Action<string> FailPoint = null;

        internal static int StoreBusySeconds = 10;   // under the build lock nobody else writes the store; this only covers a stray reader-writer
        internal static int InboxBusySeconds = 10;   // inbox transactions take milliseconds
        internal static int LearnDelayMs = 500;      // gather confirmations for this long, then store them together
        internal static int RetryDelayMs = 3000;     // first retry when the build lock is busy; doubles up to MaxRetryDelayMs
        internal const int MaxRetryDelayMs = 15000;
        internal const int MaxWaiting = 20000;       // in memory only while inbox.db can't be written (a missing drive); small for 32-bit Studio

        string InboxPath => Path.Combine(Root, "inbox.db");

        readonly object _learnGate = new object();
        List<VaultLearnUnit> _waiting = new List<VaultLearnUnit>();
        Thread _learner;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        long _learned;
        volatile string _learnError;

        /// <summary>
        /// Stores confirmed segments in SuperVault. Returns at once and never throws: the work happens on a background
        /// thread, so a CAT tool's confirm action can never fail or hang because of SuperVault.
        /// </summary>
        public void Learn(IEnumerable<VaultLearnUnit> units)
        {
            try
            {
                lock (_learnGate)
                {
                    foreach (var u in units) if (u != null) _waiting.Add(u);
                    if (_waiting.Count > MaxWaiting)
                    {
                        int drop = _waiting.Count - MaxWaiting;
                        _waiting.RemoveRange(0, drop);
                        TryLog(drop + " confirmed segments were not stored: SuperVault could not save them (" + _learnError + ").");
                    }
                    if (_learner == null)
                    {
                        _learner = new Thread(LearnLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SuperVault learning" };
                        _learner.Start();
                        AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveWaiting();
                    }
                }
                _wake.Set();
            }
            catch (Exception ex) { TryLog("Could not queue confirmed segments: " + ex); }
        }

        /// <summary>Confirmed segments not yet in the store: this process's queue plus inbox.db (every process's). Opens a file: for a dialog.</summary>
        public long LearnWaiting()
        {
            long n;
            lock (_learnGate) n = _waiting.Count;
            try { n += VaultInbox.Count(InboxPath, 2); } catch { }
            return n;
        }

        /// <summary>Confirmed segments this process has moved into the store.</summary>
        public long LearnedThisSession => Interlocked.Read(ref _learned);

        /// <summary>Why the last attempt to store confirmed segments failed, or null. They are kept and retried.</summary>
        public string LearnError => _learnError;

        /// <summary>Studio is closing: anything still in memory goes to inbox.db, for the next Studio to store.</summary>
        void SaveWaiting()
        {
            List<VaultLearnUnit> batch;
            lock (_learnGate) { batch = _waiting; _waiting = new List<VaultLearnUnit>(); }
            try { VaultInbox.Append(InboxPath, batch, 1); } catch { }
        }

        void LearnLoop()
        {
            bool again = File.Exists(InboxPath);   // units left by a Studio that closed before storing them
            int delay = RetryDelayMs;
            while (true)
            {
                try
                {
                    if (again) _wake.WaitOne(delay); else _wake.WaitOne();
                    Thread.Sleep(LearnDelayMs);
                    again = !LearnOnce();
                    delay = again ? Math.Min(delay * 2, MaxRetryDelayMs) : RetryDelayMs;
                }
                catch (Exception ex) { TryLog("Learning: " + ex); again = true; }
            }
        }

        /// <summary>
        /// One round: this process's queue to inbox.db; then, if the build lock is free, every waiting unit into the
        /// store and the index. True when everything is done; false to try again later.
        /// </summary>
        internal bool LearnOnce()
        {
            List<VaultLearnUnit> batch;
            lock (_learnGate) { batch = _waiting; _waiting = new List<VaultLearnUnit>(); }
            try { VaultInbox.Append(InboxPath, batch, InboxBusySeconds); }
            catch (Exception ex)
            {
                lock (_learnGate) _waiting.InsertRange(0, batch);
                _learnError = "the confirmed segments could not be saved: " + ex.Message;
                TryLog("Learning: " + _learnError);
                return false;
            }
            try
            {
                using (var l = TryTakeLock())
                {
                    if (l == null) return false;   // another Studio is writing; it stores our units too, or we do next time
                    FlushLocked();
                }
                _learnError = null;
            }
            catch (Exception ex)
            {
                _learnError = ex.Message;
                TryLog("Learning: " + ex);
                return false;
            }
            lock (_learnGate) return _waiting.Count == 0;
        }

        /// <summary>Moves every unit waiting in inbox.db into the store, then brings the index up to date. Caller holds the build lock.</summary>
        void FlushLocked()
        {
            while (true)
            {
                var units = VaultInbox.Read(InboxPath, InboxBusySeconds, out long lastId);
                if (units.Count == 0) break;
                using (var w = new VaultStore.Writer(StorePath, StoreBusySeconds))
                {
                    foreach (var u in units)
                    {
                        try { w.Learn(u.SourceCulture, u.Source, u.TargetCulture, u.Target, u.Origin); }
                        catch (SqliteException ex) when (ex.SqliteErrorCode != 5 && ex.SqliteErrorCode != 6)   // not BUSY/LOCKED: this unit only
                        { TryLog("Learning: one unit could not be stored and was skipped: " + ex.Message); }
                    }
                    FailPoint?.Invoke("store-commit");
                    w.Commit();
                }
                FailPoint?.Invoke("inbox-delete");
                VaultInbox.Delete(InboxPath, lastId, InboxBusySeconds);
                Interlocked.Add(ref _learned, units.Count);
            }
            SyncIndexLocked(null, CancellationToken.None, rebuildIfLarge: false);
        }
    }
}
