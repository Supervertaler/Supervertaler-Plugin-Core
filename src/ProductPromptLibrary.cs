using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// Each product's own prompt library, and the one-time copy that fills it.
    ///
    /// Until 21 Nov 2026 every product read and wrote one shared folder,
    /// <c>prompt_library</c>. The sharing cost more than it gave: twins
    /// ("Define.md" from Workbench beside "Define [Trados].md"), hidden
    /// <c>app:</c> tags, one product renaming files another had stored paths to.
    /// Michael decided each product gets its own folder beside it:
    /// <c>prompt_library_Trados</c>, <c>prompt_library_memoQ</c>,
    /// <c>prompt_library_Workbench</c> (plan: separate-prompt-libraries-plan.md).
    /// Beside, not inside (22 Nov 2026): every released Workbench, memoQ and
    /// Trados reads <c>prompt_library</c> with all its subfolders, so a folder
    /// inside it showed them each product's copies as duplicates, ready to be
    /// "tidied away". No released version reads a sibling folder.
    ///
    /// <para>The pre-release builds of 21 Nov put the libraries inside, as
    /// <c>prompt_library\Trados</c> and <c>prompt_library\memoQ</c>. Such a folder
    /// (recognised by its marker, never by its name alone) is carried over
    /// first: every file copied to the new folder, the result checked file by
    /// file, and only then the old files removed, so nothing is left for an
    /// old Workbench to show. Until the new folder is complete, the old one
    /// stays in use. See <see cref="CarryOver"/>.</para>
    ///
    /// <para>The first time a product that has said which it is
    /// (<see cref="SupervertalerPaths.SetProduct"/>) asks for its library, the
    /// prompts meant for it are COPIED from the shared folder into its own:
    /// those tagged for it and every untagged one (Michael: "yes, into every
    /// product"). Prompts tagged for another product stay behind. An untagged
    /// prompt with a tagged twin of the same name in the same folder stays
    /// behind too, so "Define" is copied once. Names and subfolders are kept,
    /// so every path stored relative to the library (selected prompts, a
    /// project's prompt, QuickLauncher slots, menu sections) still points at
    /// the same prompt.</para>
    ///
    /// <para>Nothing in the shared folder is ever deleted, moved or changed:
    /// a product that has not moved yet keeps reading it as before.</para>
    ///
    /// <para>Failure paths. A copy that stops halfway (Studio closed, disk
    /// full) leaves no marker, so the next start finishes it. Each file goes to
    /// a temporary name first and is then renamed, so a half-written prompt
    /// never appears under a real name. A file already in the product's folder
    /// is never overwritten: that makes a second run, or two programs running
    /// it at once (two Studios, two colleagues on a team folder), harmless. A
    /// file that cannot be read is counted and the marker is not written, so
    /// it is tried again next session (once per session, not on every menu
    /// open). Read-only source files are copied without the read-only flag, or
    /// the product could never save its own copy.</para>
    ///
    /// <para>Scale: one pass over the shared folder, once per product and
    /// data folder; after that one File.Exists for the marker per session.
    /// Tested at a few dozen prompts two folders deep; designed for thousands
    /// (a team library with years of client prompts) on a network share. The
    /// copy runs on whichever thread first asks, which is the UI thread in
    /// Studio: about a second for hundreds of files on a local disk, longer on
    /// a slow share, once.</para>
    /// </summary>
    public static class ProductPromptLibrary
    {
        /// <summary>The products with a library of their own.</summary>
        public static readonly string[] ProductFolders = { "Trados", "memoQ", "Workbench" };

        /// <summary>A product's library is this plus its name, beside <c>prompt_library</c>.</summary>
        public const string FolderPrefix = "prompt_library_";

        /// <summary>Where <paramref name="product"/>'s library is, for a shared folder:
        /// <c>...\prompt_library</c> gives <c>...\prompt_library_Trados</c>.</summary>
        public static string ProductDirFor(string sharedDir, string product) =>
            Path.Combine(Path.GetDirectoryName(sharedDir.TrimEnd('\\', '/')) ?? "", FolderPrefix + product);

        /// <summary>A pre-release library inside the shared folder: a product's
        /// name AND our marker. A user's own folder that happens to be called
        /// "Trados" has no marker, and is an ordinary category.</summary>
        internal static bool IsLegacyProductFolder(string dir) =>
            IsProductFolder(Path.GetFileName(dir.TrimEnd('\\', '/'))) && File.Exists(Path.Combine(dir, MarkerFileName));

        /// <summary>Written in a product's folder when the copy has finished
        /// without a failure. Its presence is the only state on disk.</summary>
        public const string MarkerFileName = ".copied-from-shared-library";

        private const string TempSuffix = ".svcopy.tmp";

        /// <summary>A temporary file younger than this may belong to a copy running
        /// right now in another program; only older ones are cleared away.</summary>
        internal static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

        private static readonly object _lock = new object();

        /// <summary>Product folder already settled this session, per shared folder:
        /// so a failed file is retried once per session rather than on every
        /// menu open. Cleared when the data folder changes.</summary>
        private static readonly Dictionary<string, string> _settled =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the last copy did, for the host to log. Null until one ran.</summary>
        public static CopyResult LastResult { get; private set; }

        public sealed class CopyResult
        {
            public int Copied;
            /// <summary>Already in the product's folder: never overwritten.</summary>
            public int AlreadyThere;
            /// <summary>Tagged for another product, or an untagged twin of a tagged one.</summary>
            public int LeftBehind;
            public readonly List<string> Failed = new List<string>();
            public bool MarkerWritten;
            /// <summary>Files carried over from a pre-release library inside prompt_library.</summary>
            public int CarriedOver;
            /// <summary>What happened to that old folder, or null when there was none.</summary>
            public string CarryOverNote;
            public override string ToString() =>
                (CarryOverNote != null ? $"carried over {CarriedOver} ({CarryOverNote}); " : "") +
                $"copied {Copied}, already there {AlreadyThere}, left behind {LeftBehind}, failed {Failed.Count}" +
                (MarkerWritten ? ", done" : (CarryOverNote != null && Copied == 0 && AlreadyThere == 0 ? "" : ", not done (tried again next session)"));
        }

        /// <summary>True for a name in <see cref="ProductFolders"/>, ignoring case.</summary>
        public static bool IsProductFolder(string name)
        {
            foreach (var p in ProductFolders)
                if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The folder <paramref name="product"/> keeps its prompts in, beside
        /// <paramref name="sharedDir"/>: first carrying over a pre-release library
        /// from inside it, then copying the shared prompts in the first time.
        /// While a carry-over is incomplete, the old folder is returned, so the
        /// user works on the complete set. When the product's folder cannot be
        /// created at all (a read-only share), the shared folder is returned, so
        /// the user still has their prompts rather than none.
        /// </summary>
        internal static string Resolve(string sharedDir, string product)
        {
            var key = sharedDir + "|" + product;
            lock (_lock)
            {
                if (_settled.TryGetValue(key, out var known)) return known;

                var productDir = ProductDirFor(sharedDir, product);
                var legacy = Path.Combine(sharedDir, product);
                string use = null;
                var r = new CopyResult();
                try
                {
                    if (IsLegacyProductFolder(legacy) && !CarryOver(legacy, productDir, r))
                        use = legacy;   // not complete yet: keep working on the old folder
                    else if (!File.Exists(Path.Combine(productDir, MarkerFileName)))
                        CopyFromShared(sharedDir, productDir, product, r);
                }
                catch (Exception ex)
                {
                    // Belt and braces: both steps catch per file. Whatever got
                    // through, the library must still open.
                    r.Failed.Add(ex.GetType().Name + ": " + ex.Message);
                }
                LastResult = r;

                if (use == null)
                    use = Directory.Exists(productDir) ? productDir
                        : IsLegacyProductFolder(legacy) ? legacy : sharedDir;
                _settled[key] = use;
                return use;
            }
        }

        /// <summary>
        /// Moves a pre-release library (<paramref name="legacy"/>, inside
        /// prompt_library) to <paramref name="productDir"/>, safely: every file is
        /// copied (temporary name, then renamed; never over an existing file),
        /// then every file still in the old folder is checked against the new
        /// one, byte for byte. Only when every file is there are the old ones
        /// removed, and only those identical to their new copy. Returns true when
        /// the new folder is complete. A file that differs (changed in the new
        /// folder since an earlier, interrupted carry-over) stays in the old
        /// folder rather than being lost; so does one that cannot be deleted. Both
        /// are tried again next session. Two programs doing this at once is
        /// harmless: neither overwrites, and a file the other has already removed
        /// counts as carried over when its copy is there.
        /// </summary>
        internal static bool CarryOver(string legacy, string productDir, CopyResult result)
        {
            Directory.CreateDirectory(productDir);
            ClearStaleTemps(productDir);

            List<string> files;
            try { files = LegacyFiles(legacy); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Failed.Add(legacy + ": " + ex.Message);
                result.CarryOverNote = "old folder could not be read; still in use";
                return false;
            }

            // Copy. Counted apart from the shared copy's numbers.
            var scratch = new CopyResult();
            foreach (var f in files)
                CopyOne(f, Path.Combine(productDir, f.Substring(legacy.Length).TrimStart('\\', '/')), scratch);
            result.CarriedOver += scratch.Copied;

            // Check, file by file, what is still in the old folder.
            var removable = new List<string>();
            int missing = 0, differ = 0;
            foreach (var f in files)
            {
                if (!File.Exists(f)) continue;   // another program has already moved it
                var dest = Path.Combine(productDir, f.Substring(legacy.Length).TrimStart('\\', '/'));
                try
                {
                    if (!File.Exists(dest)) missing++;
                    else if (SameBytes(f, dest)) removable.Add(f);
                    else differ++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    missing++;
                    scratch.Failed.Add(f + ": " + ex.Message);
                }
            }
            if (missing > 0)
            {
                result.Failed.AddRange(scratch.Failed);
                result.CarryOverNote = missing + " file(s) not copied yet; the old folder stays in use";
                return false;
            }

            // The marker goes last, and only when nothing else is left: it is what
            // marks the folder as ours, so a file that cannot be removed now is
            // still recognised, and removed, next session.
            var marker = Path.Combine(legacy, MarkerFileName);
            int kept = differ;
            foreach (var f in removable)
            {
                if (string.Equals(f, marker, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryRemove(f)) kept++;
            }
            if (kept == 0 && removable.Exists(f => string.Equals(f, marker, StringComparison.OrdinalIgnoreCase)) && !TryRemove(marker))
                kept++;
            foreach (var t in SafeTemps(legacy)) TryDelete(t);
            RemoveEmptyFolders(legacy);

            result.CarryOverNote = Directory.Exists(legacy)
                ? "complete; " + kept + " file(s) left in the old folder (" + differ +
                  " changed since and kept rather than lost), tried again next session"
                : "complete; old folder removed";
            return true;
        }

        private static bool TryRemove(string f)
        {
            try
            {
                var attrs = File.GetAttributes(f);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
                File.Delete(f);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return true;   // another program carrying over at the same moment removed it
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private static List<string> LegacyFiles(string legacy)
        {
            var list = new List<string>();
            foreach (var f in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories))
                if (!f.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) list.Add(f);
            return list;
        }

        private static IEnumerable<string> SafeTemps(string dir)
        {
            try { return Directory.GetFiles(dir, "*" + TempSuffix, SearchOption.AllDirectories); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new string[0]; }
        }

        private static bool SameBytes(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var ba = new byte[65536];
                var bb = new byte[65536];
                int na;
                while ((na = sa.Read(ba, 0, ba.Length)) > 0)
                {
                    int nb = 0;
                    while (nb < na)
                    {
                        int n = sb.Read(bb, nb, na - nb);
                        if (n == 0) return false;
                        nb += n;
                    }
                    for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
                }
                return sb.Read(bb, 0, 1) == 0;
            }
        }

        /// <summary>Removes empty folders under and including <paramref name="dir"/>, deepest first.</summary>
        private static void RemoveEmptyFolders(string dir)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir)) RemoveEmptyFolders(sub);
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Forgets what was settled this session. Called when the data folder changes.</summary>
        internal static void Forget()
        {
            lock (_lock) { _settled.Clear(); }
        }

        /// <summary>
        /// The copy itself, on explicit folders so it can be tested without
        /// touching the real data folder. See the class summary for the rules.
        /// </summary>
        public static CopyResult CopyFromShared(string sharedDir, string productDir, string product) =>
            CopyFromShared(sharedDir, productDir, product, new CopyResult());

        internal static CopyResult CopyFromShared(string sharedDir, string productDir, string product, CopyResult result)
        {
            Directory.CreateDirectory(productDir);
            ClearStaleTemps(productDir);

            if (Directory.Exists(sharedDir))
            {
                foreach (var folder in SourceFolders(sharedDir))
                    CopyFolder(sharedDir, folder, productDir, product, result);
            }

            if (result.Failed.Count == 0)
            {
                var marker = Path.Combine(productDir, MarkerFileName);
                var text = "Prompts copied from the shared library on " + DateTime.UtcNow.ToString("u") +
                           // The counts only: the result's own wording ends "not done"
                           // until the marker exists, which is what is being written.
                           $": copied {result.Copied}, already there {result.AlreadyThere}, left behind {result.LeftBehind}." +
                           Environment.NewLine +
                           "Delete this file to copy again; nothing already here is overwritten." + Environment.NewLine;
                try
                {
                    var tmp = marker + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + TempSuffix;
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    try { File.Move(tmp, marker); }
                    catch (IOException) { TryDelete(tmp); }   // another program wrote it first: fine
                    result.MarkerWritten = File.Exists(marker);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Failed.Add(MarkerFileName + ": " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>The shared folder and its subfolders, minus the product folders
        /// at the top, the bin, and any hidden or archived folder.</summary>
        private static IEnumerable<string> SourceFolders(string sharedDir)
        {
            var pending = new Stack<string>();
            pending.Push(sharedDir);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                yield return dir;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var sub in subs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || name.StartsWith("_old shared library", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (dir == sharedDir && IsLegacyProductFolder(sub)) continue;   // a pre-release product library
                    pending.Push(sub);
                }
            }
        }

        private static void CopyFolder(string sharedDir, string folder, string productDir, string product, CopyResult result)
        {
            string[] files;
            try
            {
                var md = Directory.GetFiles(folder, "*.md");
                var legacy = Directory.GetFiles(folder, "*.svprompt");
                files = new string[md.Length + legacy.Length];
                md.CopyTo(files, 0);
                legacy.CopyTo(files, md.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Failed.Add(folder + ": " + ex.Message);
                return;
            }

            // Which names this product has its own tagged copy of, in this folder.
            var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var taggedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string app;
                try { app = ReadApp(f); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Failed.Add(f + ": " + ex.Message);
                    continue;
                }
                apps[f] = app;
                if (string.Equals(app, product, StringComparison.OrdinalIgnoreCase))
                    taggedNames.Add(Label(f));
            }

            foreach (var f in files)
            {
                if (!apps.TryGetValue(f, out var app)) continue;   // unreadable, already counted
                bool untagged = string.IsNullOrEmpty(app) || app.Equals("both", StringComparison.OrdinalIgnoreCase);
                bool mine = string.Equals(app, product, StringComparison.OrdinalIgnoreCase);
                if (!mine && !(untagged && !taggedNames.Contains(Label(f))))
                {
                    result.LeftBehind++;
                    continue;
                }
                var rel = f.Substring(sharedDir.Length).TrimStart('\\', '/');
                if (rel.EndsWith(".svprompt", StringComparison.OrdinalIgnoreCase))
                {
                    // Copied under its .md name, the format it would be renamed to
                    // anyway. Released Workbench (1.9.364 to 1.10.381) renames or
                    // DELETES every .svprompt anywhere under prompt_library, the
                    // product folders included; with none there, it touches nothing
                    // (checked by running those releases' code against a copied
                    // library). Beside an .md of the same name, the .md is the prompt.
                    if (File.Exists(Path.ChangeExtension(f, ".md"))) { result.LeftBehind++; continue; }
                    rel = Path.ChangeExtension(rel, ".md");
                }
                CopyOne(f, Path.Combine(productDir, rel), result);
            }
        }

        private static void CopyOne(string source, string dest, CopyResult result)
        {
            if (File.Exists(dest)) { result.AlreadyThere++; return; }
            var tmp = dest + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + TempSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(source, tmp, false);
                var attrs = File.GetAttributes(tmp);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(tmp, attrs & ~FileAttributes.ReadOnly);
                try
                {
                    File.Move(tmp, dest);
                    result.Copied++;
                }
                catch (IOException) when (File.Exists(dest))
                {
                    // Another program put it there between our check and now.
                    TryDelete(tmp);
                    result.AlreadyThere++;
                }
            }
            catch (Exception ex) when ((ex is FileNotFoundException || ex is DirectoryNotFoundException) && File.Exists(dest))
            {
                // Moved by another program carrying over at the same moment.
                TryDelete(tmp);
                result.AlreadyThere++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                result.Failed.Add(source + ": " + ex.Message);
            }
        }

        /// <summary>The prompt's name as menus show it: the file name without its
        /// product marker, so "Define.md" and "Define [Trados].md" are one name.</summary>
        private static string Label(string file) =>
            PromptLibrary.StripAppTag(Path.GetFileNameWithoutExtension(file));

        /// <summary>The <c>app:</c> value in a prompt's front matter, lower case, or "" when none.</summary>
        internal static string ReadApp(string file)
        {
            using (var reader = new StreamReader(file, Encoding.UTF8, true))
            {
                var first = reader.ReadLine();
                if (first == null || first.Trim() != "---") return "";
                string line;
                int guard = 0;
                while ((line = reader.ReadLine()) != null && guard++ < 200)
                {
                    var t = line.Trim();
                    if (t == "---") break;
                    if (t.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
                        return t.Substring(4).Trim().Trim('"', '\'').ToLowerInvariant();
                }
            }
            return "";
        }

        private static void ClearStaleTemps(string productDir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(productDir, "*" + TempSuffix, SearchOption.AllDirectories))
                {
                    try
                    {
                        // Creation time, not last-write: File.Copy keeps the source's
                        // last-write time, so a copy made a second ago can look years old.
                        if (DateTime.UtcNow - File.GetCreationTimeUtc(f) > StaleTempAge) File.Delete(f);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
