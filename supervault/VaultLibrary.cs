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
        public string BuildProgress;       // "Indexing: 412,000 of 1,554,005 translation units" while building
        public string LastBuildError;      // the last build's failure in this process, if it failed
        public VaultLearnCounts Learned = new VaultLearnCounts();   // what storing this process's confirmed segments did
        public string LearnError;          // why storing confirmed segments last failed (they are kept and retried)
    }

    /// <summary>
    /// What storing confirmed segments did, since the process started. Identical units handed over together (Studio
    /// sends a repetition it auto-propagated and confirmed as well as the segment you confirmed) count once.
    /// </summary>
    public sealed class VaultLearnCounts
    {
        public long Added;       // new units
        public long Replaced;    // a corrected translation replaced the one SuperVault learned before
        public long Already;     // the unit was already in SuperVault: nothing changed
        public long Skipped;     // not stored: no text, the same language twice, or junk

        public long Total => Added + Replaced + Already + Skipped;

        internal void Count(VaultLearnOutcome o)
        {
            switch (o)
            {
                case VaultLearnOutcome.Added: Added++; break;
                case VaultLearnOutcome.Updated: case VaultLearnOutcome.Replaced: Replaced++; break;
                case VaultLearnOutcome.Duplicate: Already++; break;
                default: Skipped++; break;
            }
        }

        internal void Add(VaultLearnCounts c) { Added += c.Added; Replaced += c.Replaced; Already += c.Already; Skipped += c.Skipped; }
        internal VaultLearnCounts Copy() { var c = new VaultLearnCounts(); c.Add(this); return c; }

        /// <summary>"1 confirmed: 0 added, 1 already in SuperVault".</summary>
        public override string ToString()
        {
            var f = CultureInfo.CurrentCulture;
            var s = Total.ToString("N0", f) + " confirmed: " + Added.ToString("N0", f) + " added";
            if (Replaced > 0) s += ", " + Replaced.ToString("N0", f) + " replaced";
            s += ", " + Already.ToString("N0", f) + " already in EverTM";
            if (Skipped > 0) s += ", " + Skipped.ToString("N0", f) + " skipped (empty or not text)";
            return s;
        }
    }

    /// <summary>
    /// SuperVault on disk: its own memory (<see cref="VaultStore"/>), and the search index built from it.
    ///
    /// Layout of <see cref="Root"/> (by default <c>&lt;data folder&gt;\evertm\</c>; <c>supervault\</c> before the rename,
    /// moved by <see cref="MoveToNewName"/>):
    ///   evertm.db           the memory itself (SQLite), filled by imports (supervault.db before the rename)
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
            lock (Libraries) foreach (var lib in Libraries.Values.Concat(TestOnly)) { lock (lib._gate) { lib.Swap(null, null); lib._checkedAt = long.MaxValue / 2; } lock (lib._learnGate) { lib._waiting.Clear(); lib._recent.Clear(); lib._recentCount = 0; lib._recentOldest = 0; } }
        }

        /// <summary>Tests only: forget every unit this library still has to store (its queue, its inbox, its recent units).</summary>
        internal void DropWaitingForTests()
        {
            lock (_learnGate) { _waiting.Clear(); _recent.Clear(); _recentCount = 0; _recentOldest = 0; }
            try { if (File.Exists(InboxPath)) VaultInbox.Delete(InboxPath, long.MaxValue, 5); } catch { }
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

        /// <summary>
        /// Where EverTM lives for this user: <c>&lt;Supervertaler data folder&gt;\evertm</c>. Only the path: it moves
        /// nothing (tests compare against it). A product opens <see cref="DefaultRootInUse"/>.
        /// </summary>
        public static string DefaultRoot => Path.Combine(SupervertalerPaths.Root, FolderName);

        /// <summary>The folder before EverTM was renamed (it was called SuperVault): <c>&lt;data folder&gt;\supervault</c>.</summary>
        public static string OldDefaultRoot => Path.Combine(SupervertalerPaths.Root, OldFolderName);

        public const string FolderName = "evertm";
        public const string OldFolderName = "supervault";

        static FileStream _oldFolderPin;   // held until the process ends while it uses the old folder: see ResolveInUse
        static readonly Lazy<string> InUse = new Lazy<string>(() => ResolveInUse(SupervertalerPaths.Root, out _oldFolderPin));

        /// <summary>
        /// The folder a product opens: <see cref="DefaultRoot"/>, after moving the old <c>supervault</c> folder there
        /// the first time it is asked for in this process (<see cref="ResolveInUse"/>). Set <see cref="Log"/> first.
        /// </summary>
        public static string DefaultRootInUse => InUse.Value;

        /// <summary>
        /// <see cref="MoveToNewName"/>, and if the old folder has to be used for now, a file in it held open (shared)
        /// until this process ends. That keeps any other Studio from renaming the folder while this one still uses it
        /// under the old name: an open index alone doesn't (in 64-bit it is mapped into memory and holds no file open),
        /// and this process would otherwise go on to recreate an empty <c>supervault</c> folder beside the moved one.
        /// </summary>
        internal static string ResolveInUse(string dataFolder, out FileStream pin)
        {
            pin = null;
            var root = MoveToNewName(dataFolder);
            if (!string.Equals(Path.GetFileName(root), OldFolderName, StringComparison.OrdinalIgnoreCase)) return root;
            try
            {
                pin = new FileStream(Path.Combine(root, PinFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                     FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
                return root;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!System.IO.Directory.Exists(root)) return MoveToNewName(dataFolder);   // another Studio moved it a moment ago
                TryLog("Could not mark " + root + " as in use (" + ex.Message + ").");
                return root;
            }
        }

        internal const string PinFileName = "in-use-before-rename.lock";

        /// <summary>
        /// Renames <c>&lt;dataFolder&gt;\supervault</c> to <c>&lt;dataFolder&gt;\evertm</c> and returns the folder to use.
        ///
        /// One rename of the folder, on the same disk: Windows does it in one step, whatever the store's size (it is
        /// never copied), and refuses it while any file inside is open by anyone (another Studio looking up,
        /// importing or holding build.lock, or Explorer). That refusal is the lock: if the rename succeeds, nobody had
        /// the store open. If it fails, nothing has changed: the old folder is used, the reason is logged, and the
        /// next start tries again. There is no half-moved state, so there is nothing to repair.
        ///
        /// Both folders already there (an older Supervertaler in another Studio recreated the old one): the new one is
        /// used and the old one is left as it is (deleted only if completely empty), with a line in the log. A new
        /// folder that is completely empty is removed first, so it can't hide the store.
        /// </summary>
        public static string MoveToNewName(string dataFolder)
        {
            var old = Path.Combine(dataFolder, OldFolderName);
            var now = Path.Combine(dataFolder, FolderName);
            try
            {
                if (!System.IO.Directory.Exists(old)) return now;
                if (System.IO.Directory.Exists(now))
                {
                    if (System.IO.Directory.EnumerateFileSystemEntries(now).Any())
                    {
                        if (System.IO.Directory.EnumerateFileSystemEntries(old).Any())
                            TryLog("Both " + old + " and " + now + " exist. Using " + now + "; " + old + " is left as it is (nothing in it is deleted).");
                        else try { System.IO.Directory.Delete(old); } catch { }   // an empty leftover: Delete refuses a folder that isn't empty
                        return now;
                    }
                    System.IO.Directory.Delete(now);   // empty: refused (and logged below) if anything appeared in it meanwhile
                }
                System.IO.Directory.Move(old, now);
                TryLog("Moved " + old + " to " + now + ".");
                return now;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                bool movedByOther = !System.IO.Directory.Exists(old) && System.IO.Directory.Exists(now);   // another Studio starting at the same moment
                if (!movedByOther) TryLog("Could not move " + old + " to " + now + " (" + ex.Message + "). Using " + old + " for now; trying again next time.");
                return movedByOther ? now : old;
            }
        }

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
                    int fmt = info.TryGetValue("format", out var format) && int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 0;
                    if (fmt < VaultIndex.OldestReadable || fmt > VaultIndex.Format)
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
                        if (fmt < VaultIndex.Format) UpgradeSoon();
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

        bool _upgradeStarted;   // under _gate

        /// <summary>
        /// The index in use is of an older format (before metadata): it is still searched as it is, so Studio keeps its
        /// matches and never sees EverTM as unavailable, and a rebuild in the new format is started in the background,
        /// once per process. If another Studio holds the build lock, that Studio's next build or this one's next start
        /// does it. Caller holds _gate.
        /// </summary>
        void UpgradeSoon()
        {
            if (_upgradeStarted || _buildThread != null || NoAutoUpgradeForTests) return;
            _upgradeStarted = true;
            TryLog("The EverTM index is of an older format; rebuilding it in the background (lookups go on meanwhile).");
            ThreadPool.QueueUserWorkItem(_ => { try { StartBuild(e => { if (e != null) TryLog("Rebuilding the index in the new format: " + e); }); } catch (Exception ex) { TryLog("Could not start the rebuild: " + ex.Message); } });
        }

        internal static bool NoAutoUpgradeForTests;

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
                var s = new VaultStatus { Building = IsBuilding, BuildProgress = _progress, LastBuildError = _lastError, Learned = LearnedThisSession, LearnError = _learnError };
                lock (_gate)
                {
                    if (idx != null)
                    {
                        s.State = VaultState.Ready;
                        s.Units = idx.Count;
                        s.BuiltAt = ReadBuiltAt(idx.Folder);
                        s.Message = Units(idx.Count, CultureInfo.InvariantCulture);
                    }
                    else if (_broken != null) { s.State = VaultState.Broken; s.Message = "The EverTM index could not be opened (" + _broken + "). Rebuild it."; }
                    else
                    {
                        // Empty is a normal state, not an error: SuperVault fills as you confirm segments.
                        s.State = VaultState.Missing;
                        s.Message = File.Exists(StorePath)
                            ? "EverTM has no index yet: it is built when you next confirm a segment, or click Rebuild index."
                            : "EverTM is empty: it fills as you confirm segments (with its Update box ticked), or import a TMX file or a translation memory.";
                    }
                }
                return s;
            }
        }

        /// <summary>"1 translation unit", "0 translation units", "1,234 translation units". Michael: "translation units"
        /// is sure not to be misunderstood, so every count a user sees says it in full (not "units", not "TUs").</summary>
        public static string Units(long n, IFormatProvider culture = null) =>
            n.ToString("N0", culture ?? CultureInfo.CurrentCulture) + (n == 1 ? " translation unit" : " translation units");

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

        /// <summary>
        /// EverTM's own memory, which the index is built from: <c>evertm.db</c> in <see cref="Root"/>, or the
        /// <c>supervault.db</c> of a store from before the rename until <see cref="RenameOldStore"/> has renamed it.
        /// </summary>
        public string StorePath => File.Exists(OldStorePath) && !File.Exists(NewStorePath) ? OldStorePath : NewStorePath;
        string NewStorePath => Path.Combine(Root, "evertm.db");
        string OldStorePath => Path.Combine(Root, "supervault.db");

        bool _storeNameChecked;   // RenameOldStore has been tried in this process (only under the build lock)

        /// <summary>
        /// Renames a <c>supervault.db</c> from before the rename to <c>evertm.db</c>, once the folder has its new name
        /// (never inside a folder still called <c>supervault</c>). Caller holds the build lock, so
        /// no writer can be using the old name; a reader that has it open makes the rename fail, harmlessly. Skipped
        /// while its <c>-wal</c> file holds changes not yet written into it (renaming only the main file would lose
        /// them); SQLite folds them in when the last connection closes, so a later start renames it. A failure
        /// changes nothing: <see cref="StorePath"/> keeps finding the old name.
        /// </summary>
        void RenameOldStore()
        {
            if (_storeNameChecked) return;
            _storeNameChecked = true;
            try
            {
                if (!File.Exists(OldStorePath) || File.Exists(NewStorePath)) return;
                // Never in the old folder: it is still there because something had it open, maybe an older Supervertaler
                // in another Studio, which knows only supervault.db and would start a new, empty one beside ours.
                if (string.Equals(Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)), OldFolderName, StringComparison.OrdinalIgnoreCase)) return;
                var wal = new FileInfo(OldStorePath + "-wal");
                if (wal.Exists && wal.Length > 0) { TryLog(OldStorePath + " was not renamed: its -wal file still holds changes. Trying again next time."); return; }
                File.Move(OldStorePath, NewStorePath);
                TryLog("Renamed " + OldStorePath + " to " + NewStorePath + ".");
                foreach (var leftover in new[] { OldStorePath + "-wal", OldStorePath + "-shm" })
                    try { File.Delete(leftover); } catch { }   // empty or SQLite's scratch index: nothing in them is needed
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryLog("Could not rename " + OldStorePath + " to " + NewStorePath + " (" + ex.Message + "). Using it as it is; trying again next time.");
            }
        }

        /// <summary>Units in the store. Opens the file: for a settings dialog, not for every lookup.</summary>
        public long StoreCount() => VaultStore.Count(StorePath);

        /// <summary>
        /// "Metadata: 285 projects, 55 clients, 15 subjects", or null when EverTM holds none. Opens the file: for a
        /// settings dialog.
        /// </summary>
        public string MetadataSummary()
        {
            var counts = VaultStore.FieldCounts(StorePath);
            var parts = new List<string>();
            foreach (var (field, one, many) in new[] { (VaultFields.Project, "project", "projects"), (VaultFields.Client, "client", "clients"), (VaultFields.EndClient, "end client", "end clients"), (VaultFields.Subject, "subject", "subjects") })
                if (counts.TryGetValue(field, out var n) && n > 0) parts.Add(n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? one : many));
            return parts.Count == 0 ? null : "Metadata: " + string.Join(", ", parts);
        }

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
                VaultImportResult result;
                // In a 32-bit process, an import that would take SuperVault past the size it can index safely stops
                // as soon as it gets there, and its transaction is rolled back: nothing is added (VaultStore.Writer).
                VaultStore.ImportLimit = Is32Bit ? Max32BitUnits : 0;
                try { result = importer(StorePath, progress, cancel); }
                finally { VaultStore.ImportLimit = 0; }
                // Units that only gained metadata are changes too: a re-import that fills in most of EverTM rebuilds the
                // index (the 25% rule) rather than leaving it to learning to apply them one by one.
                if (result.Added > 0 || result.Enriched > 0 || Current == null) SyncIndexLocked(progress, cancel, rebuildIfLarge: true);
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
            StartJob("build", cancel => Units(Build(p => _progress = p, cancel)) + " indexed", done);

        /// <summary>
        /// Starts <see cref="VaultExport.Export"/> on a background thread (the same one-job-at-a-time slot as imports and
        /// builds, so Stop and the progress line work alike). Takes no lock: it reads a snapshot of the store, so
        /// lookups, learning and imports in other Studios carry on meanwhile. False if a job is already running here.
        /// </summary>
        public bool StartExport(string tmxPath, string pair, ICollection<string> origins, Action<string> done = null) =>
            StartJob("export", cancel => VaultExport.Export(StorePath, tmxPath, pair, origins, p => _progress = p, cancel).ToString(), done);

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
                _progress = "Starting";   // progress texts are shown as they are, so each starts with a capital (CAT tools show them in a status line)
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
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "EverTM " + kind };
                _buildThread.Start();
                return true;
            }
        }

        public void CancelBuild() { lock (_gate) _buildCancel?.Cancel(); }

        /// <summary>
        /// The build lock for an import or rebuild, waiting up to <see cref="LockWaitMs"/> for it. Without the wait, an
        /// import refused when the lock was held for a moment only: after a rebuild, the old index is deleted under the
        /// lock (<see cref="ScheduleCleanup"/>), and confirmed segments are stored under it. Those take seconds at most;
        /// another Studio's import or rebuild takes minutes, so that still gets the message. Holds nothing while waiting,
        /// so the locking scheme's rule 3 (no waiting while holding anything) is kept.
        /// </summary>
        IDisposable TakeLock()
        {
            var sw = Stopwatch.StartNew();
            var l = TryTakeLock();
            while (l == null && sw.ElapsedMilliseconds < LockWaitMs) { Thread.Sleep(100); l = TryTakeLock(); }
            if (l == null) throw new IOException("Another program (a second Trados Studio, or memoQ) is importing into EverTM, building its index or storing confirmed segments right now. Try again when it has finished.");
            return l;
        }

        /// <summary>The build lock, or null if someone holds it. Never waits.</summary>
        FileStream TryTakeLock()
        {
            System.IO.Directory.CreateDirectory(Root);
            FileStream l;
            try { l = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }   // the file is being deleted by its last holder this instant
            RenameOldStore();   // once per process, under the lock; never throws
            return l;
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
                if (!File.Exists(StorePath)) throw new FileNotFoundException("EverTM is empty: import a TMX file or a translation memory first.", StorePath);
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
                    var tooLarge = TooLargeFor32Bit(total);
                    if (tooLarge != null) throw new VaultTooLargeException(tooLarge);
                    using (var dir = FSDirectory.Open(genFolder))
                    using (var w = new IndexWriter(dir, new IndexWriterConfig(VaultIndex.V, new VaultIndex.NoAnalyzer()) { OpenMode = OpenMode.CREATE, RAMBufferSizeMB = 48 }))
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "SELECT " + VaultStore.IndexColumns + " FROM units u ORDER BY u.id";
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                if ((++read & 1023) == 0) cancel.ThrowIfCancellationRequested();
                                if ((read % 10000) == 0) progress?.Invoke("Indexing: " + read.ToString("N0", CultureInfo.InvariantCulture) + " of " + Units(total, CultureInfo.InvariantCulture));
                                string a = r.GetString(1), ta = r.GetString(2), b = r.GetString(3), tb = r.GetString(4);
                                w.AddDocument(VaultIndex.MakeDocument(r.GetInt64(0).ToString(CultureInfo.InvariantCulture), a, ta, VaultMarkup.Plain(ta), b, tb, VaultMarkup.Plain(tb), r.IsDBNull(5) ? "" : r.GetString(5), VaultStore.ReadIndexMeta(r, 6)));
                                added++;
                            }
                        }
                        cancel.ThrowIfCancellationRequested();
                        progress?.Invoke("Saving the index");
                        w.SetCommitData(new Dictionary<string, string> { [VaultIndex.SeqKey] = lastChange.ToString(CultureInfo.InvariantCulture) });
                        w.Commit();
                    }
                }

                // Verify before switching: a generation that can't be opened never becomes current.
                using (var check = VaultIndex.Open(genFolder))
                    if (check.Count != added) throw new InvalidDataException($"The new index holds {Units(check.Count, CultureInfo.InvariantCulture)}, expected {added:N0}.");

                var info = new StringBuilder();
                info.AppendLine("format=" + VaultIndex.Format);
                info.AppendLine("built=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                info.AppendLine("store_version=" + version.ToString(CultureInfo.InvariantCulture));
                info.AppendLine("units=" + added);
                info.AppendLine("seconds=" + (int)sw.Elapsed.TotalSeconds);
                File.WriteAllText(Path.Combine(genFolder, "info.txt"), info.ToString(), new UTF8Encoding(false));

                if (AtomicFile.Write(PointerFile, new UTF8Encoding(false).GetBytes(name), true, null) != AtomicFile.Outcome.Written)
                    throw new IOException("Could not switch to the new EverTM index (" + PointerFile + " is in use). The previous index stays in use.");
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

        internal static int LockWaitMs = 10000;   // settable for tests

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

        // ---- the size limit in a 32-bit process (Studio 2024) ----

        /// <summary>
        /// The most units a 32-bit process (Studio 2024) may import into SuperVault or build an index for: the largest size
        /// measured. Measured 9 Nov 2026 in a 32-bit process, on copies of the real store padded to size:
        ///   full build   1M: 279 s, peak 109 MB private / 280 MB address space; 5M: 1,411 s, 118 / 293 MB;
        ///                10M: 2,650 s, 122 / 309 MB (index 2.3 GB).
        ///   lookups      a few tens of MB at any size.
        /// Memory hardly grows with size: in a 32-bit process Lucene reads the index with SimpleFSDirectory (no memory
        /// mapping), so even a 2+ GB index takes no address space, and building is bounded by the 48 MB buffer and
        /// streaming merges. The limit is where the measurements stop, not where trouble was seen. Confirmed segments
        /// are never refused: the index is updated a piece at a time, with merges capped at 512 MB segments.
        /// </summary>
        public static long Max32BitUnits { get; internal set; } = 10000000;   // settable for tests only

        /// <summary>Tests only: decide as a 32-bit (true) or 64-bit (false) process would; null for this process.</summary>
        internal static bool? Is32BitForTests;
        internal static bool Is32Bit => Is32BitForTests ?? !Environment.Is64BitProcess;

        /// <summary>
        /// Null when a SuperVault of <paramref name="units"/> units is safe to index here; otherwise the message that says
        /// why not and what to do instead. Always null in a 64-bit process. <paramref name="importing"/>: the message is
        /// for an import that was stopped on the way (its final size is unknown), not for a rebuild.
        /// </summary>
        public static string TooLargeFor32Bit(long units, bool importing = false)
        {
            if (!Is32Bit || units <= Max32BitUnits) return null;
            var f = CultureInfo.CurrentCulture;
            return (importing
                       ? "This import would take EverTM past " + Units(Max32BitUnits, f) + ", the most it has been tested with in Studio 2024. "
                       : "EverTM holds " + Units(units, f) + ", more than it has been tested with in Studio 2024 (" + Units(Max32BitUnits, f) + "). ") +
                   "Studio 2024 is a 32-bit program, with far less memory to work with than Studio 2026. " +
                   "Do this " + (importing ? "import" : "rebuild") + " in Studio 2026 (64-bit) instead; Studio 2024 can still look things up in the result " +
                   "and learn from the segments you confirm. Nothing was changed.";
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
                        cmd.CommandText = "SELECT c.seq, c.unit_id, " + VaultStore.IndexColumns + " FROM changes c LEFT JOIN units u ON u.id = c.unit_id WHERE c.seq > $from ORDER BY c.seq LIMIT 2000";
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
                                    page[id] = VaultIndex.MakeDocument(id.ToString(CultureInfo.InvariantCulture), a, ta, VaultMarkup.Plain(ta), b, tb, VaultMarkup.Plain(tb), r.IsDBNull(7) ? "" : r.GetString(7), VaultStore.ReadIndexMeta(r, 8));
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
                            progress?.Invoke("Updating the index: " + Units(applied, CultureInfo.InvariantCulture));
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

        // ---- the choice of Workbench TMs ----

        string WorkbenchChoiceFile => Path.Combine(Root, "workbench-unticked.txt");

        /// <summary>
        /// The Workbench TMs (tm_ids) the user left unticked at the last Import from Workbench; empty when there is no
        /// such choice. Unticked, not ticked, is remembered: a TM added to Workbench since then shows up ticked, and the
        /// list is always shown before anything is imported.
        /// </summary>
        public HashSet<string> WorkbenchUnticked()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var text = File.Exists(WorkbenchChoiceFile) ? AtomicFile.ReadAllText(WorkbenchChoiceFile) : null;
                if (text != null)
                    foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) set.Add(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return set;
        }

        /// <summary>Remembers the unticked Workbench TMs (see <see cref="WorkbenchUnticked"/>). Best effort: never throws.</summary>
        public void RememberWorkbenchUnticked(IEnumerable<string> tmIds)
        {
            try
            {
                var lines = tmIds.Where(t => !string.IsNullOrEmpty(t) && t.IndexOfAny(new[] { '\r', '\n' }) < 0).Distinct(StringComparer.Ordinal);
                System.IO.Directory.CreateDirectory(Root);
                AtomicFile.Write(WorkbenchChoiceFile, new UTF8Encoding(false).GetBytes(string.Join("\n", lines)), true, null);
            }
            catch (Exception ex) { TryLog("Could not remember the choice of Workbench TMs: " + ex.Message); }
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
        readonly VaultLearnCounts _learned = new VaultLearnCounts();   // under _learnGate
        volatile string _learnError;

        // ---- read your own writes: units handed over, not yet searchable ----
        //
        // Studio assumes a TM has the unit once its update call returns: right after a confirm it looks the segment up
        // again, and keeps that result in its own cache (TranslatableDocument.TranslationsCache, cleared only when the
        // project's TM settings change). SuperVault stores the unit a moment later, in the background, so that lookup
        // would miss it, and the miss would stay in Studio's cache. So lookups in this process also see the units it
        // was handed in the last minute, as exact matches. A minute is plenty: a unit is normally searchable within a
        // second or two, and from then on the index serves it (Search keeps one match per translation).

        sealed class RecentUnit
        {
            public string OtherLang, Raw, OtherRaw, Variant;
            public long AddedAt;
            public VaultUnitInfo Info;   // shown on the match until the index has the unit
        }

        readonly Dictionary<string, List<RecentUnit>> _recent = new Dictionary<string, List<RecentUnit>>(StringComparer.Ordinal);   // "lang|plain text"; under _learnGate
        int _recentCount;
        long _recentOldest;
        internal static long RecentKeepTicks = Stopwatch.Frequency * 60;
        internal const int MaxRecent = 5000;        // units kept at most; beyond this a unit is simply found once stored
        internal const int MaxRecentPerCall = 50;   // larger calls are batches, not confirms

        static string RecentKey(string lang, string plain) => lang + "|" + plain;

        /// <summary>
        /// Prepares the units for <see cref="AddRecentLocked"/>: the parsing happens here, outside the lock, and for
        /// at most <see cref="MaxRecent"/> units, so a big batch costs the caller a bounded few milliseconds.
        /// </summary>
        static List<(string key, RecentUnit unit, bool replace)> PrepareRecent(List<VaultLearnUnit> units)
        {
            var prepared = new List<(string, RecentUnit, bool)>();
            long now = Stopwatch.GetTimestamp();
            foreach (var u in units.Take(MaxRecent))
            {
                string a = VaultMarkup.BaseLanguage(u.SourceCulture), b = VaultMarkup.BaseLanguage(u.TargetCulture);
                string src = u.Source?.Trim() ?? "", trg = u.Target?.Trim() ?? "";
                if (a.Length == 0 || b.Length == 0 || a == b) continue;
                string srcPlain = VaultLookup.PlainOf(VaultMarkup.Parse(src)), trgPlain = VaultLookup.PlainOf(VaultMarkup.Parse(trg));
                if (srcPlain.Length == 0 || trgPlain.Length == 0) continue;
                var variant = (u.SourceCulture ?? a) + ">" + (u.TargetCulture ?? b);
                var info = u.Info(DateTime.UtcNow);
                // A corrected translation replaces the one handed over before for the same source.
                prepared.Add((RecentKey(a, srcPlain), new RecentUnit { OtherLang = b, Raw = src, OtherRaw = trg, Variant = variant, AddedAt = now, Info = info }, true));
                prepared.Add((RecentKey(b, trgPlain), new RecentUnit { OtherLang = a, Raw = trg, OtherRaw = src, Variant = variant, AddedAt = now, Info = info }, false));
            }
            return prepared;
        }

        /// <summary>Caller holds _learnGate.</summary>
        void AddRecentLocked(List<(string key, RecentUnit unit, bool replace)> prepared)
        {
            PruneRecent(Stopwatch.GetTimestamp());
            foreach (var (key, unit, replace) in prepared)
            {
                if (_recentCount >= 2 * MaxRecent) break;   // two entries per unit: one per side
                AddRecent(key, unit, replace);
            }
        }

        void AddRecent(string key, RecentUnit r, bool replaceSameSource)
        {
            if (!_recent.TryGetValue(key, out var list)) _recent[key] = list = new List<RecentUnit>(1);
            int removed = list.RemoveAll(x => x.OtherLang == r.OtherLang && (replaceSameSource || x.OtherRaw == r.OtherRaw));
            list.Add(r);
            _recentCount += 1 - removed;
            if (_recentOldest == 0 || r.AddedAt < _recentOldest) _recentOldest = r.AddedAt;
        }

        /// <summary>Drops units handed over more than a minute ago. Scans only when one has expired. Caller holds _learnGate.</summary>
        void PruneRecent(long now)
        {
            if (_recentOldest == 0 || now - _recentOldest < RecentKeepTicks) return;
            long oldest = 0;
            foreach (var key in _recent.Keys.ToList())
            {
                var list = _recent[key];
                _recentCount -= list.RemoveAll(x => now - x.AddedAt >= RecentKeepTicks);
                if (list.Count == 0) _recent.Remove(key);
                else foreach (var x in list) if (oldest == 0 || x.AddedAt < oldest) oldest = x.AddedAt;
            }
            _recentOldest = oldest;
        }

        /// <summary>Units this process was handed in the last minute whose source is exactly <paramref name="docPlain"/>.</summary>
        List<VaultCandidate> RecentCandidates(string docPlain, string sourceLang, string targetLang)
        {
            var found = new List<VaultCandidate>();
            if (string.IsNullOrEmpty(docPlain)) return found;
            lock (_learnGate)
            {
                if (_recentCount == 0) return found;
                PruneRecent(Stopwatch.GetTimestamp());
                if (_recent.TryGetValue(RecentKey(sourceLang, docPlain), out var list))
                    foreach (var r in list)
                        if (r.OtherLang == targetLang)
                            found.Add(new VaultCandidate
                            {
                                Id = "0", SourceRaw = r.Raw, TargetRaw = r.OtherRaw, Variant = r.Variant, Exact = true,
                                Fields = r.Info?.Fields.ToList() ?? new List<(string, string)>(),
                                Created = r.Info?.Created, Changed = r.Info?.Changed, CreatedBy = r.Info?.CreatedBy, ChangedBy = r.Info?.ChangedBy,
                            });
            }
            return found;
        }

        /// <summary>
        /// A lookup: the index in use, plus (as exact matches) the units this process was handed in the last minute and
        /// may not have stored yet. This is what a CAT tool should call; never throws for an empty SuperVault.
        /// </summary>
        /// <param name="targetCulture">The project's target language with its variant ("en-GB"): units in that exact variant win first.</param>
        public List<VaultMatch> Lookup(IList<VaultPiece> document, string sourceLang, string targetLang, int minScore, int maxResults, string targetCulture = null)
        {
            var recent = document == null ? null : RecentCandidates(VaultLookup.PlainOf(document), sourceLang, targetLang);
            return VaultLookup.Search(Current, document, sourceLang, targetLang, minScore, maxResults, recent, targetCulture);
        }

        /// <summary>
        /// Stores confirmed segments in SuperVault. Returns at once and never throws: the work happens on a background
        /// thread, so a CAT tool's confirm action can never fail or hang because of SuperVault.
        /// </summary>
        public void Learn(IEnumerable<VaultLearnUnit> units)
        {
            try
            {
                var list = units?.Where(u => u != null).ToList() ?? new List<VaultLearnUnit>();
                var now = DateTime.UtcNow;
                foreach (var u in list) if (u.ConfirmedAt == null) u.ConfirmedAt = now;   // confirmed now, whenever it is stored
                // Only for interactive confirms (a unit or two per call): a batch such as Update Main TMs doesn't look
                // its segments up meanwhile, and parsing thousands of units would slow its hand-over.
                var recent = list.Count <= MaxRecentPerCall ? PrepareRecent(list) : null;
                lock (_learnGate)
                {
                    _waiting.AddRange(list);
                    if (recent != null) AddRecentLocked(recent);
                    if (_waiting.Count > MaxWaiting)
                    {
                        int drop = _waiting.Count - MaxWaiting;
                        _waiting.RemoveRange(0, drop);
                        TryLog(drop + " confirmed segments were not stored: EverTM could not save them (" + _learnError + ").");
                    }
                    if (_learner == null)
                    {
                        _learner = new Thread(LearnLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "EverTM learning" };
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

        /// <summary>What storing confirmed segments has done in this process (a copy).</summary>
        public VaultLearnCounts LearnedThisSession { get { lock (_learnGate) return _learned.Copy(); } }

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
        /// <summary>
        /// The CAT tool's way to find a document's project name and client (Trados: the .sdlproj above it). Called on
        /// the learning thread, never on the UI thread, once per unit with a <see cref="VaultLearnUnit.DocumentPath"/> and
        /// no project yet; it should cache. Null, or a failure: the unit is stored without them.
        /// </summary>
        public static Func<string, (string Project, string Client)> ProjectOf { get; set; }

        static void DescribeProjects(List<VaultLearnUnit> batch)
        {
            var find = ProjectOf;
            if (find == null) return;
            foreach (var u in batch)
            {
                if (u.Project != null || string.IsNullOrEmpty(u.DocumentPath)) continue;
                try { var p = find(u.DocumentPath); u.Project = p.Project; if (string.IsNullOrWhiteSpace(u.Client)) u.Client = p.Client; }
                catch (Exception ex) { TryLog("Could not read the project of " + u.DocumentPath + ": " + ex.Message); }
                u.DocumentPath = null;
            }
        }

        internal bool LearnOnce()
        {
            List<VaultLearnUnit> batch;
            lock (_learnGate) { batch = _waiting; _waiting = new List<VaultLearnUnit>(); }
            DescribeProjects(batch);
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
                var counts = new VaultLearnCounts();
                // Identical units in one batch are one unit: Studio also sends a repetition it auto-propagated and
                // confirmed, a moment after the segment you confirmed. Order is kept, so a correction still wins.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                using (var w = new VaultStore.Writer(StorePath, StoreBusySeconds))
                {
                    foreach (var u in units)
                    {
                        if (!seen.Add(u.SourceCulture + "" + u.Source + "" + u.TargetCulture + "" + u.Target)) continue;
                        try { counts.Count(w.Learn(u)); }
                        catch (SqliteException ex) when (ex.SqliteErrorCode != 5 && ex.SqliteErrorCode != 6)   // not BUSY/LOCKED: this unit only
                        { counts.Skipped++; TryLog("Learning: one unit could not be stored and was skipped: " + ex.Message); }
                    }
                    FailPoint?.Invoke("store-commit");
                    w.Commit();
                }
                FailPoint?.Invoke("inbox-delete");
                VaultInbox.Delete(InboxPath, lastId, InboxBusySeconds);
                lock (_learnGate) _learned.Add(counts);   // only once committed: a failed batch is retried, not counted twice
                TryLog("Stored confirmed segments: " + counts + (units.Count > seen.Count ? " (" + units.Count + " received; identical ones counted once)" : "") + ".");
            }
            SyncIndexLocked(null, CancellationToken.None, rebuildIfLarge: false);
        }
    }
}
