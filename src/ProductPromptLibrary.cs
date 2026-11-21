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
    /// Michael decided each product gets its own folder inside it:
    /// <c>prompt_library\Trados</c>, <c>prompt_library\memoQ</c>,
    /// <c>prompt_library\Workbench</c> (plan: separate-prompt-libraries-plan.md).
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
        /// <summary>The product folders inside <c>prompt_library</c>. The shared
        /// folder's scan skips them, and the copy never copies one into another.</summary>
        public static readonly string[] ProductFolders = { "Trados", "memoQ", "Workbench" };

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
            public override string ToString() =>
                $"copied {Copied}, already there {AlreadyThere}, left behind {LeftBehind}, failed {Failed.Count}" +
                (MarkerWritten ? ", done" : ", not done (tried again next session)");
        }

        /// <summary>True for a name in <see cref="ProductFolders"/>, ignoring case.</summary>
        public static bool IsProductFolder(string name)
        {
            foreach (var p in ProductFolders)
                if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The folder <paramref name="product"/> keeps its prompts in, inside
        /// <paramref name="sharedDir"/>, copying the shared prompts into it the
        /// first time. When the product's folder cannot be created at all (a
        /// read-only share), the shared folder is returned, so the user still has
        /// their prompts rather than none.
        /// </summary>
        internal static string Resolve(string sharedDir, string product)
        {
            var key = sharedDir + "|" + product;
            lock (_lock)
            {
                if (_settled.TryGetValue(key, out var known)) return known;

                var productDir = Path.Combine(sharedDir, product);
                try
                {
                    if (!File.Exists(Path.Combine(productDir, MarkerFileName)))
                        LastResult = CopyFromShared(sharedDir, productDir, product);
                }
                catch (Exception ex)
                {
                    // Belt and braces: CopyFromShared catches per file. Whatever
                    // got through, the library must still open.
                    var r = new CopyResult();
                    r.Failed.Add(ex.GetType().Name + ": " + ex.Message);
                    LastResult = r;
                }

                var use = Directory.Exists(productDir) ? productDir : sharedDir;
                _settled[key] = use;
                return use;
            }
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
        public static CopyResult CopyFromShared(string sharedDir, string productDir, string product)
        {
            var result = new CopyResult();
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
                           ": " + result + Environment.NewLine +
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
                    if (dir == sharedDir && IsProductFolder(name)) continue;
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
                CopyOne(f, Path.Combine(productDir, f.Substring(sharedDir.Length).TrimStart('\\', '/')), result);
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
