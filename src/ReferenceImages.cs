using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// The folder of drawings a user has pointed this project at.
    ///
    /// <para>The premise: you put a job's figures in a folder, tell the plugin
    /// where, and they become reference material for its memory bank. Nothing
    /// here is specific to patents — patents are simply where drawings carry the
    /// most translation-relevant information — so avoid narrowing it to figures
    /// with reference numerals.</para>
    ///
    /// <para><b>Where the folder lives is the user's business, not ours.</b>
    /// Studio projects can be saved anywhere under any name, so there is no
    /// layout to infer from. <see cref="Resolve"/> returns only what the user
    /// set; <see cref="Suggest"/> offers nearby candidates for a dialog to
    /// propose. The distinction is load-bearing: a folder guessed by walking up
    /// the tree can belong to a different job, and drawings from the wrong matter
    /// are worse than no drawings, because the output still looks reasonable.</para>
    ///
    /// <para>Deliberately has no dependency on Studio or on any AI provider, so it
    /// can be exercised without either.</para>
    /// </summary>
    public static class ReferenceImages
    {
        /// <summary>Folder names looked for by convention, in preference order.</summary>
        private static readonly string[] ConventionalNames = { "Images", "Figures", "images", "figures" };

        /// <summary>
        /// Extensions we treat as drawings. Deliberately narrow: a reference
        /// folder full of PDFs and DOCX would otherwise be reported as usable and
        /// then fail in the vision pass, which is a worse failure than saying
        /// "no images found" up front.
        /// </summary>
        private static readonly HashSet<string> ImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            // .emf/.wmf included deliberately: they are image files, and a folder of
            // drawings that could not be rasterised must not be reported as empty.
            // Whether a vision model can read one is the analyser's business.
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".emf", ".wmf" };

        /// <summary>
        /// The reference-images folder for a project: whatever the user chose,
        /// and nothing else.
        ///
        /// <para><b>There is deliberately no fallback.</b> An earlier version fell
        /// back to a convention search when the setting was unset, which sounds
        /// helpful and is dangerous: project folders have no fixed shape, so a
        /// search that walks upwards can leave the job entirely. A project stored
        /// directly in its job folder, with no <c>Images\</c> of its own, would
        /// have found the one belonging to a SIBLING FILING — or to the client
        /// folder above it — and fed another matter's drawings into this one's
        /// context, silently and plausibly.</para>
        ///
        /// <para>Convention now only ever produces a <see cref="Suggest"/>ion for
        /// the user to accept. Getting drawings from a folder nobody pointed at is
        /// not a feature.</para>
        ///
        /// <para>Returns null when unset or missing. Callers should treat that as
        /// "this project has no drawings", not as an error — most do not.</para>
        /// </summary>
        /// <param name="explicitFolder">The per-project setting; may be null or blank.</param>
        public static string Resolve(string explicitFolder)
        {
            if (string.IsNullOrWhiteSpace(explicitFolder)) return null;
            try
            {
                return Directory.Exists(explicitFolder) ? Path.GetFullPath(explicitFolder) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Folders near the project that look like they hold drawings, for the UI
        /// to offer as a starting point. Never applied automatically.
        ///
        /// <para>Searches the project's own folder and its immediate parent only.
        /// One level up, because a project is commonly kept in a subfolder of the
        /// job folder while the drawings sit beside it — but that is a habit, not
        /// a rule, and going further reaches other jobs. Empty folders are skipped:
        /// suggesting one would waste the user's click.</para>
        ///
        /// <para>Ordered nearest-first, so the most likely candidate leads.</para>
        /// </summary>
        /// <param name="projectFilePath">Path to the .sdlproj file, or its folder.</param>
        public static List<string> Suggest(string projectFilePath)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(projectFilePath)) return found;

            try
            {
                var dir = Directory.Exists(projectFilePath)
                    ? projectFilePath
                    : Path.GetDirectoryName(projectFilePath);

                for (int level = 0; level < 2 && !string.IsNullOrEmpty(dir); level++)
                {
                    foreach (var name in ConventionalNames)
                    {
                        var candidate = Path.Combine(dir, name);
                        if (!Directory.Exists(candidate) || !HasAnyImage(candidate)) continue;

                        var full = Path.GetFullPath(candidate);
                        if (!found.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
                            found.Add(full);
                    }

                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            catch { /* permissions, malformed path — offer nothing */ }

            return found;
        }

        private static bool HasAnyImage(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder)
                    .Any(f => ImageExtensions.Contains(Path.GetExtension(f)));
            }
            catch { return false; }
        }

        /// <summary>
        /// The image files in a folder, ordered the way a reader would expect.
        ///
        /// <para>Ordering is the whole point of this method. The convention is one
        /// file per figure named for its label — <c>FIG. 1.png</c> …
        /// <c>FIG. 9.png</c> — and an ordinary string sort puts <c>FIG. 10</c>
        /// between <c>FIG. 1</c> and <c>FIG. 2</c>. A vision pass handed the
        /// figures out of order will caption them out of order, and the error is
        /// invisible in the output.</para>
        /// </summary>
        public static List<ReferenceImage> List(string folder)
        {
            var result = new List<ReferenceImage>();
            if (string.IsNullOrWhiteSpace(folder)) return result;

            try
            {
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    if (!ImageExtensions.Contains(Path.GetExtension(path))) continue;
                    result.Add(Describe(path, null));
                }
            }
            catch { return result; }

            result.Sort(CompareInFolder);
            return result;
        }

        private static ReferenceImage Describe(string path, string subFolder)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var figures = ParseFigures(stem);
            return new ReferenceImage
            {
                Path = path,
                FileName = Path.GetFileName(path),
                SubFolder = subFolder,
                Figures = figures,
                FigureLabel = figures.Count > 0 ? string.Join(", ", figures.Select(f => f.Label)) : stem.Trim(),
                FigureNumber = figures.Count > 0 ? figures[0].Number : (int?)null,
            };
        }

        // Numbered figures first in numeric order, then anything unnumbered
        // alphabetically, so an odd file never displaces the sequence.
        private static int CompareInFolder(ReferenceImage a, ReferenceImage b)
        {
            if (a.FigureNumber.HasValue && b.FigureNumber.HasValue)
            {
                var c = a.FigureNumber.Value.CompareTo(b.FigureNumber.Value);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
            }
            if (a.FigureNumber.HasValue) return -1;
            if (b.FigureNumber.HasValue) return 1;
            return StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
        }

        /// <summary>The most image files one description run takes. Above it the
        /// folder is refused: two hundred paid requests is a wrongly chosen folder
        /// far more often than a document with two hundred drawings.</summary>
        public const int MaxImages = 200;

        /// <summary>The most directory entries <see cref="Scan"/> looks at before it
        /// gives up. It runs on the UI thread every time the FigureLens window
        /// refreshes, and someone will point it at a drive root.</summary>
        public const int MaxEntries = 5000;

        // Files Windows and macOS drop into every folder; not "other files" a user put there.
        private static readonly HashSet<string> Litter =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thumbs.db", "desktop.ini", ".ds_store" };

        /// <summary>
        /// What a chosen folder holds, for a run that describes its files: the
        /// image files in it and in its sub-folders one level down (as deep as
        /// extraction writes, and as deep as <see cref="CountImages"/> counts, so
        /// the number shown is the number described), in figure order per folder.
        /// Never throws.
        ///
        /// <para>Scale: stops at <see cref="MaxImages"/> + 1 image files or
        /// <see cref="MaxEntries"/> entries, whichever comes first, and says so
        /// in <see cref="FolderScan.TooMany"/>. Designed for a folder of up to
        /// 200 drawings and for a wrongly chosen one of 100,000 files; the latter
        /// costs one partial directory listing, not a sort of everything.</para>
        /// </summary>
        public static FolderScan Scan(string folder)
        {
            var scan = new FolderScan();
            if (string.IsNullOrWhiteSpace(folder)) return scan;
            var groups = new List<List<ReferenceImage>>();
            try
            {
                if (!Directory.Exists(folder)) return scan;
                scan.Exists = true;

                var entries = 0;
                var images = 0;
                if (ScanOne(folder, null, scan, groups, ref entries, ref images))
                {
                    var subs = new List<string>();
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(folder))
                        {
                            if (++entries > MaxEntries) { scan.TooMany = true; break; }
                            subs.Add(d);
                        }
                    }
                    catch { /* no sub-folders we may list */ }
                    subs.Sort(StringComparer.OrdinalIgnoreCase);

                    if (!scan.TooMany)
                        foreach (var sub in subs)
                            if (!ScanOne(sub, Path.GetFileName(sub), scan, groups, ref entries, ref images)) break;
                }
            }
            catch { /* whatever was found so far stands */ }

            foreach (var g in groups)
            {
                g.Sort(CompareInFolder);
                scan.Images.AddRange(g);
            }
            return scan;
        }

        /// <summary>False when a limit was reached and the scan must stop.</summary>
        private static bool ScanOne(string dir, string subFolder, FolderScan scan, List<List<ReferenceImage>> groups,
            ref int entries, ref int images)
        {
            var here = new List<ReferenceImage>();
            groups.Add(here);
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    if (++entries > MaxEntries) { scan.TooMany = true; return false; }
                    if (ImageExtensions.Contains(Path.GetExtension(path)))
                    {
                        here.Add(Describe(path, subFolder));
                        if (++images > MaxImages) { scan.TooMany = true; return false; }
                    }
                    else if (!Litter.Contains(Path.GetFileName(path)))
                    {
                        scan.OtherFiles++;
                    }
                }
            }
            catch { /* a sub-folder we may not read holds nothing we can use */ }
            return true;
        }

        /// <summary>
        /// How many image files a chosen folder holds, counting one level of
        /// subfolders as well as the folder itself. Never throws; 0 for a folder
        /// that does not exist.
        ///
        /// <para>One level, and no deeper, because that is exactly as deep as
        /// extraction ever writes: a project with several documents holding images
        /// gets one subfolder per document, so two documents' "Figure 01.png"
        /// cannot overwrite each other. A flat count reads 0 straight after a
        /// successful extraction, which tells the user their images are not there
        /// when they are. Use <see cref="List"/> when the files themselves are
        /// wanted in figure order; that stays per folder, where the ordering
        /// means something.</para>
        /// </summary>
        public static int CountImages(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return 0;
            var count = 0;
            try
            {
                count = List(folder).Count;
                foreach (var sub in Directory.EnumerateDirectories(folder))
                {
                    try { count += List(sub).Count; }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        // The word for a figure, then its number, anywhere in the name:
        // "Figure 3", "fig3", "FIG-1", "Figuur 2", "PROJ-001 Fig. 3A (amended)".
        // Not inside another word ("config3"), and the number must end there
        // ("fig3abc" and "Figure 2026" are not figures 3 and 202).
        private static readonly Regex KeywordRe = new Regex(
            @"(?<![A-Za-z])(figuren|figures|figuur|figure|figs|fig)\.?[\s_\-]*(\d{1,3})([A-Za-z])?(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // What may follow: "-2", " to 4", " and 4", ", 5", "_en_6", "-5C".
        private static readonly Regex MoreRe = new Regex(
            @"\G[\s_]*(-|–|—|to|tot|and|en|&|,|\+)[\s_]*(?:fig(?:uur|ure)?\.?\s*)?(\d{1,3})([A-Za-z])?(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A name that is nothing but a number: "3", "03", "12b".
        private static readonly Regex BareNumberRe = new Regex(
            @"^[\s_]*(\d{1,3})([A-Za-z])?[\s_]*$", RegexOptions.Compiled);

        /// <summary>
        /// The figures a file name says the file holds; empty when it names none.
        ///
        /// <para>Deliberately cautious, because a wrong figure number is the worst
        /// thing this feature can produce: it reads fine and is wrong everywhere
        /// downstream. A second number counts only where the name leaves no
        /// doubt - the word is plural ("Figs 3 and 4 amended") or the numbers end
        /// the name ("Fig 1-2"). "Figure 1 - 13 Nov" is figure 1; "Figure 3 (2)"
        /// and "Figure 3-2" are figure 3. A file that names no figure is still
        /// described; it is just not given a number nobody wrote.</para>
        /// </summary>
        public static List<FigureRef> ParseFigures(string fileNameWithoutExtension)
        {
            var found = new List<FigureRef>();
            var name = fileNameWithoutExtension ?? "";
            if (name.Trim().Length == 0) return found;

            foreach (Match m in KeywordRe.Matches(name))
            {
                var first = new FigureRef(int.Parse(m.Groups[2].Value), m.Groups[3].Value);
                Add(found, first);

                // Whatever follows the first number, read as a chain.
                var more = new List<KeyValuePair<string, FigureRef>>();
                var at = m.Index + m.Length;
                while (true)
                {
                    var n = MoreRe.Match(name, at);
                    if (!n.Success) break;
                    more.Add(new KeyValuePair<string, FigureRef>(n.Groups[1].Value.ToLowerInvariant(),
                        new FigureRef(int.Parse(n.Groups[2].Value), n.Groups[3].Value)));
                    at = n.Index + n.Length;
                }
                if (more.Count == 0) continue;

                var word = m.Groups[1].Value;
                var plural = word.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                          || word.EndsWith("en", StringComparison.OrdinalIgnoreCase);
                var endsName = name.Substring(at).Trim(' ', '_', ')', '.').Length == 0;
                if (!plural && !endsName) continue;

                var previous = first;
                foreach (var step in more)
                {
                    var isRange = step.Key == "-" || step.Key == "–" || step.Key == "—"
                               || step.Key == "to" || step.Key == "tot";
                    if (isRange) AddRange(found, previous, step.Value);
                    else Add(found, step.Value);
                    previous = step.Value;
                }
            }

            if (found.Count == 0)
            {
                var bare = BareNumberRe.Match(name);
                if (bare.Success) Add(found, new FigureRef(int.Parse(bare.Groups[1].Value), bare.Groups[2].Value));
            }
            return found;
        }

        private static void Add(List<FigureRef> list, FigureRef f)
        {
            if (!list.Any(x => x.Number == f.Number && x.Letter == f.Letter)) list.Add(f);
        }

        /// <summary>"1-4" is 1, 2, 3, 4; "5A-5C" is 5A, 5B, 5C. Anything else that
        /// looks like a range (3-2, 3A-4B, 1-80) is not expanded: only what was
        /// written is taken, and a backwards one not even that.</summary>
        private static void AddRange(List<FigureRef> list, FigureRef from, FigureRef to)
        {
            if (from.Letter.Length == 0 && to.Letter.Length == 0)
            {
                if (to.Number <= from.Number) return;
                if (to.Number - from.Number > 30) { Add(list, to); return; }
                for (int n = from.Number + 1; n <= to.Number; n++) Add(list, new FigureRef(n, ""));
                return;
            }
            if (from.Number == to.Number && from.Letter.Length == 1 && to.Letter.Length == 1 && to.Letter[0] > from.Letter[0])
            {
                for (char c = (char)(from.Letter[0] + 1); c <= to.Letter[0]; c++) Add(list, new FigureRef(from.Number, c.ToString()));
                return;
            }
            if (to.Number > from.Number) Add(list, to);
        }

        /// <summary>The figure label, normalised to "FIG. n" (or "FIG. nA"), or
        /// the bare filename when it does not look like a figure at all.</summary>
        public static string ParseFigureLabel(string fileNameWithoutExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) return "";
            var figures = ParseFigures(fileNameWithoutExtension);
            return figures.Count > 0 ? figures[0].Label : fileNameWithoutExtension.Trim();
        }

        /// <summary>The figure's number, or null when the name is not a figure.</summary>
        public static int? ParseFigureNumber(string fileNameWithoutExtension)
        {
            var figures = ParseFigures(fileNameWithoutExtension);
            return figures.Count > 0 ? figures[0].Number : (int?)null;
        }
    }

    /// <summary>One figure as a file name gives it: a number and, sometimes, a letter.</summary>
    public sealed class FigureRef
    {
        public FigureRef(int number, string letter)
        {
            Number = number;
            Letter = (letter ?? "").ToUpperInvariant();
        }

        public int Number { get; }
        /// <summary>"A" in "3A"; empty when there is none.</summary>
        public string Letter { get; }
        public string Label => "FIG. " + Number + Letter;
        public override string ToString() => Label;
    }

    /// <summary>What <see cref="ReferenceImages.Scan"/> found in a folder.</summary>
    public sealed class FolderScan
    {
        /// <summary>False when the folder is not there (any more).</summary>
        public bool Exists { get; set; }
        /// <summary>The image files, top folder first, then each sub-folder; figure order within each.
        /// Incomplete when <see cref="TooMany"/>.</summary>
        public List<ReferenceImage> Images { get; } = new List<ReferenceImage>();
        /// <summary>Files that are not images (PDF, Word, ...) and so are not used.</summary>
        public int OtherFiles { get; set; }
        /// <summary>True when the folder holds more than a run takes, or more than the scan looks through.</summary>
        public bool TooMany { get; set; }
    }

    /// <summary>One drawing on disk.</summary>
    public class ReferenceImage
    {
        public string Path { get; set; }
        public string FileName { get; set; }

        /// <summary>Normalised label, e.g. "FIG. 3". Falls back to the filename
        /// when the name does not follow the convention.</summary>
        public string FigureLabel { get; set; }

        /// <summary>Figure number when the filename yields one; null otherwise.
        /// The first, when the name gives several.</summary>
        public int? FigureNumber { get; set; }

        /// <summary>Every figure the file name gives; empty when it gives none.</summary>
        public List<FigureRef> Figures { get; set; } = new List<FigureRef>();

        /// <summary>The sub-folder the file is in, one level below the chosen folder; null in the folder itself.</summary>
        public string SubFolder { get; set; }

        /// <summary>The name as shown to the user: with its sub-folder when it is in one.</summary>
        public string DisplayName => string.IsNullOrEmpty(SubFolder) ? FileName : SubFolder + "\\" + FileName;

        public override string ToString() => FigureLabel + " (" + FileName + ")";
    }
}
