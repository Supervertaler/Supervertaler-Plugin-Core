using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Supervertaler.Core
{
    /// <summary>
    /// One description run: a list of images shown to the AI one at a time, and
    /// the decision whether what came back may be written.
    ///
    /// <para>The decision is the reason this is its own class. figures.md is read
    /// into every prompt, and a user corrects it by hand, so a run that was
    /// stopped, or in which every request failed (wrong key, no network), must
    /// leave an existing file exactly as it was. Before this, such a run replaced
    /// a good file with a table of "not analysed".</para>
    ///
    /// <para>Scale: sequential, one request in flight, one image in memory at a
    /// time. Tested at 200 images; a host refuses more than
    /// <see cref="ReferenceImages.MaxImages"/> before it gets here.</para>
    /// </summary>
    public static class FigureRun
    {
        /// <summary>One image to describe.</summary>
        public sealed class Job
        {
            /// <summary>The image file.</summary>
            public string Path;
            /// <summary>The figure it is, or null when nobody knows.</summary>
            public string Label;
            /// <summary>What the document's text says about it; may be empty.</summary>
            public List<string> Descriptions = new List<string>();
        }

        /// <summary>What a run produced.</summary>
        public sealed class Result
        {
            /// <summary>One per image attempted, in order. Shorter than the job list when the run was stopped.</summary>
            public List<FigureVision> Visions = new List<FigureVision>();
            /// <summary>True when the run was stopped before the last image.</summary>
            public bool Stopped;
            /// <summary>The file written, or null when nothing was.</summary>
            public string WrittenTo;
            /// <summary>Reference signs read in the images that the text never cites.</summary>
            public List<string> SignsNotInText = new List<string>();

            public int Failed => Visions.Count(v => !string.IsNullOrEmpty(v.Error));
            /// <summary>True when there is nothing worth keeping: no image was described.</summary>
            public bool NothingDescribed => Visions.Count == 0 || Failed == Visions.Count;
            /// <summary>Whether the result may replace (or become) figures.md.</summary>
            public bool MayWrite => !Stopped && !NothingDescribed;
            /// <summary>The first failure's reason, for a message that says why nothing was written.</summary>
            public string FirstError => Visions.Select(v => v.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e));
        }

        /// <summary>
        /// Describe each image in turn. A failure is kept as that image's row and
        /// the run goes on; a stop ends the run with what was done so far and
        /// <see cref="Result.Stopped"/> set. Does not throw for either.
        /// </summary>
        /// <param name="describe">Shows one image to the AI. Passed in so the run
        /// can be tested without a paid request; a host passes
        /// <see cref="FigureAnalyzer.AnalyseAsync"/>.</param>
        /// <param name="progress">Called before each image with its number (from 1), the total and the job.</param>
        public static async Task<Result> DescribeAsync(
            IList<Job> jobs,
            Func<Job, CancellationToken, Task<FigureVision>> describe,
            Action<int, int, Job> progress,
            CancellationToken cancellationToken)
        {
            var result = new Result();
            if (jobs == null || describe == null) return result;

            for (int i = 0; i < jobs.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) { result.Stopped = true; break; }
                var job = jobs[i];
                try { progress?.Invoke(i + 1, jobs.Count, job); } catch { }

                FigureVision vision;
                try
                {
                    vision = await describe(job, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result.Stopped = true;
                    break;
                }
                catch (Exception ex)
                {
                    vision = new FigureVision { Label = job.Label, FileName = Path.GetFileName(job.Path ?? ""), Error = ex.Message };
                }
                if (vision == null)
                    vision = new FigureVision { Label = job.Label, FileName = Path.GetFileName(job.Path ?? ""), Error = "no answer" };
                result.Visions.Add(vision);
            }
            return result;
        }
    }

    /// <summary>One image file of a chosen folder, with what is known about it before the AI looks.</summary>
    public sealed class FolderFigure
    {
        public ReferenceImage Image;
        /// <summary>The figure label from the file name ("FIG. 3", "FIG. 3, FIG. 4"), or null when the name gives none.</summary>
        public string Label;
        /// <summary>What the documents' text says about the figure(s) the file name gives.</summary>
        public List<string> Descriptions = new List<string>();
        /// <summary>Something the reader should know about the match; null when there is nothing to say.</summary>
        public string Note;
    }

    /// <summary>What a description run over a folder's image files would do, worked out before anything is sent.</summary>
    public sealed class FolderFigurePlan
    {
        public string Folder;
        /// <summary>False when the folder is not there (any more).</summary>
        public bool FolderExists;
        /// <summary>True when the folder holds more image files than one run takes.</summary>
        public bool TooMany;
        /// <summary>The image files, in the order they will be described.</summary>
        public List<FolderFigure> Files = new List<FolderFigure>();
        /// <summary>Files in the folder that are not images and are not used.</summary>
        public int OtherFiles;
        /// <summary>What the documents' text says, by figure number.</summary>
        public SortedDictionary<int, List<string>> TextFigures = new SortedDictionary<int, List<string>>();
        /// <summary>Figures the text describes that no file name gives.</summary>
        public List<int> FiguresWithoutFile = new List<int>();

        /// <summary>Files whose name gives a figure number.</summary>
        public int Matched => Files.Count(f => f.Label != null);
        /// <summary>Files whose name gives none; described all the same, without a figure label.</summary>
        public int Unmatched => Files.Count - Matched;
    }

    /// <summary>
    /// FigureLens for the documents that hold no images: the drawings arrived as
    /// separate files, the user pointed at their folder, and each file is matched
    /// to a figure by its NAME ("Figure 3.png", "fig3.jpg") against what the
    /// documents' text says about that figure ("Figuur 3 toont ...").
    ///
    /// <para>Nothing is guessed. A file whose name gives no figure number is
    /// described without one and its row says so; two files that give the same
    /// number are both described and both flagged; a figure the text describes
    /// with no file is listed as missing.</para>
    /// </summary>
    public static class FolderFigures
    {
        /// <summary>Above this many image files a host asks before spending: it is
        /// more than a patent's drawings usually come to, and each is a paid request.</summary>
        public const int AskAbove = 25;

        /// <summary>The label a row carries when the file name gives no figure number.</summary>
        public const string NoNumberLabel = "no figure number in the file name";

        /// <summary>What the text of several documents says, by figure number, merged.</summary>
        public static SortedDictionary<int, List<string>> MergeDescriptions(IEnumerable<DocxImageSet> documents)
        {
            var merged = new SortedDictionary<int, List<string>>();
            foreach (var set in documents ?? Enumerable.Empty<DocxImageSet>())
            {
                if (set == null || set.FigureDescriptions == null) continue;
                foreach (var pair in set.FigureDescriptions)
                {
                    List<string> list;
                    if (!merged.TryGetValue(pair.Key, out list)) { list = new List<string>(); merged[pair.Key] = list; }
                    foreach (var sentence in pair.Value ?? new List<string>())
                        if (!list.Contains(sentence)) list.Add(sentence);
                }
            }
            return merged;
        }

        /// <summary>
        /// Look at the folder and match its image files to the figures the text
        /// describes. Reads only the directory listing, no file contents, so it
        /// is cheap enough for a window to call on every refresh; never throws.
        /// </summary>
        public static FolderFigurePlan Plan(string folder, IDictionary<int, List<string>> textFigures)
        {
            var plan = new FolderFigurePlan { Folder = folder };
            if (textFigures != null)
                foreach (var pair in textFigures)
                    if (pair.Value != null && pair.Value.Count > 0) plan.TextFigures[pair.Key] = pair.Value;

            var scan = ReferenceImages.Scan(folder);
            plan.FolderExists = scan.Exists;
            plan.TooMany = scan.TooMany;
            plan.OtherFiles = scan.OtherFiles;

            var claimed = new Dictionary<string, FolderFigure>(StringComparer.Ordinal);   // "3A" -> first file to give it
            var numbersWithFile = new HashSet<int>();
            foreach (var image in scan.Images)
            {
                var file = new FolderFigure { Image = image };
                plan.Files.Add(file);
                var figures = image.Figures ?? new List<FigureRef>();
                if (figures.Count == 0)
                {
                    file.Note = "no figure number could be read from the file name";
                    continue;
                }

                file.Label = string.Join(", ", figures.Select(f => f.Label));
                var notes = new List<string>();
                foreach (var number in figures.Select(f => f.Number).Distinct())
                {
                    numbersWithFile.Add(number);
                    List<string> said;
                    if (plan.TextFigures.TryGetValue(number, out said))
                    {
                        foreach (var sentence in said)
                            if (!file.Descriptions.Contains(sentence)) file.Descriptions.Add(sentence);
                    }
                    else if (plan.TextFigures.Count > 0)
                    {
                        notes.Add("the text does not describe figure " + number);
                    }
                }
                foreach (var f in figures)
                {
                    var key = f.Number + f.Letter;
                    FolderFigure first;
                    if (claimed.TryGetValue(key, out first))
                    {
                        if (first != file)
                        {
                            notes.Add("same figure number as " + first.Image.DisplayName);
                            var back = "same figure number as " + image.DisplayName;
                            first.Note = string.IsNullOrEmpty(first.Note) ? back : first.Note + "; " + back;
                        }
                    }
                    else claimed[key] = file;
                }
                if (notes.Count > 0) file.Note = string.Join("; ", notes);
            }

            foreach (var number in plan.TextFigures.Keys)
                if (!numbersWithFile.Contains(number)) plan.FiguresWithoutFile.Add(number);
            return plan;
        }

        /// <summary>The run's jobs, one per file, in the plan's order.</summary>
        public static List<FigureRun.Job> Jobs(FolderFigurePlan plan)
        {
            return (plan?.Files ?? new List<FolderFigure>())
                .Select(f => new FigureRun.Job { Path = f.Image.Path, Label = f.Label, Descriptions = f.Descriptions })
                .ToList();
        }

        /// <summary>
        /// Describe the folder's files and, only if the run finished and at least
        /// one image was described, write figures.md to <paramref name="figuresPath"/>.
        /// A stopped run, and a run in which every image failed, write nothing:
        /// a file that was there is left byte for byte as it was, and none is
        /// created. <see cref="FigureRun.Result.WrittenTo"/> says which happened.
        /// </summary>
        public static async Task<FigureRun.Result> DescribeAndSaveAsync(
            FolderFigurePlan plan,
            Func<FigureRun.Job, CancellationToken, Task<FigureVision>> describe,
            string figuresPath,
            ISet<string> textSigns,
            string rawSourceText,
            string regenerateHint,
            Action<int, int, FigureRun.Job> progress,
            CancellationToken cancellationToken)
        {
            var result = await FigureRun.DescribeAsync(Jobs(plan), describe, progress, cancellationToken).ConfigureAwait(false);
            if (!result.MayWrite) return result;

            result.SignsNotInText = FiguresFile.SignsNotInText(result.Visions, textSigns, rawSourceText);
            var markdown = Render(plan, result.Visions, result.SignsNotInText, regenerateHint);
            FiguresFile.Save(figuresPath, markdown);
            result.WrittenTo = figuresPath;
            return result;
        }

        /// <summary>figures.md for a finished folder run: the same file a document run writes, with a line saying where the figure numbers came from.</summary>
        public static string Render(FolderFigurePlan plan, IList<FigureVision> visions, IList<string> signsNotInText, string regenerateHint)
        {
            var folderName = "";
            try { folderName = Path.GetFileName((plan.Folder ?? "").TrimEnd('\\', '/')); } catch { }
            var doc = new FiguresFile.FigureDocument
            {
                Name = "the image files in folder “" + folderName + "”",
                Intro = "The documents hold no images themselves; these are the image files in the folder chosen in FigureLens. "
                      + "A figure number comes from the file's name, never from the image.",
            };

            for (int i = 0; i < visions.Count && i < plan.Files.Count; i++)
            {
                var file = plan.Files[i];
                var v = visions[i];
                // The row shows the file as the user knows it and, where the name
                // gave no number, says so in the place the number would be.
                v.FileName = file.Image.DisplayName;
                v.Label = file.Label ?? NoNumberLabel;
                if (file.Label != null && !string.IsNullOrEmpty(file.Note)) v.Label += " (" + file.Note + ")";
                doc.Images.Add(new ExtractedImage { Label = file.Label, Descriptions = file.Descriptions });
                doc.Visions.Add(v);
            }
            foreach (var number in plan.FiguresWithoutFile)
                doc.WithoutImage.Add(new KeyValuePair<string, string>("FIG. " + number, string.Join(" ", plan.TextFigures[number])));

            return FiguresFile.RenderWithVision(new List<FiguresFile.FigureDocument> { doc }, signsNotInText, regenerateHint);
        }
    }
}
