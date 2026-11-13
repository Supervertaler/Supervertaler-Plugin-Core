using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// FigureLens when the documents hold no images and the drawings are files
    /// in a folder the user chose (13 Nov 2026: a patent whose four drawings
    /// arrived as Figure 1.png ... Figure 4.png; the window counted them and
    /// then said "No images to describe").
    ///
    /// <para>Everything here works in its own temporary folder and makes no AI
    /// request: the "AI" is a function in the test. No memory bank of the
    /// user's is read or written.</para>
    /// </summary>
    [Tests]
    internal static class FolderFiguresTests
    {
        private sealed class Folder : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "sv-figures-" + Guid.NewGuid().ToString("N"));
            public Folder() { Directory.CreateDirectory(Dir); }
            public string File(string name) => Path.Combine(Dir, name);
            public string Sub(string name) { var d = Path.Combine(Dir, name); Directory.CreateDirectory(d); return d; }
            public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        }

        private static void Png(string path, int width = 40, int height = 30)
        {
            using (var bmp = new Bitmap(width, height))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static SortedDictionary<int, List<string>> Text(params int[] figures)
        {
            var d = new SortedDictionary<int, List<string>>();
            foreach (var n in figures) d[n] = new List<string> { "Figuur " + n + " toont onderdeel " + n + "." };
            return d;
        }

        /// <summary>An "AI" that answers every image.</summary>
        private static Task<FigureVision> Answers(FigureRun.Job job, CancellationToken ct)
        {
            return Task.FromResult(new FigureVision
            {
                Label = job.Label,
                FileName = Path.GetFileName(job.Path),
                Caption = "A drawing of " + Path.GetFileNameWithoutExtension(job.Path) + ".",
                SignsInDrawing = new List<string> { "12" },
            });
        }

        private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

        private static string Labels(string name) =>
            string.Join("|", ReferenceImages.ParseFigures(name).Select(f => f.Label));

        // ---- file name -> figure ----------------------------------------------------------

        public static void FileName_GivesTheFigure_InTheUsualSpellings()
        {
            Assert.Equal("FIG. 3", Labels("Figure 3"), "Figure 3");
            Assert.Equal("FIG. 3", Labels("fig3"), "fig3");
            Assert.Equal("FIG. 1", Labels("FIG-1"), "FIG-1");
            Assert.Equal("FIG. 2", Labels("Figuur 2"), "Dutch");
            Assert.Equal("FIG. 4", Labels("Figure 04"), "zero-padded, as step 1 writes them");
            Assert.Equal("FIG. 3A", Labels("PROJ-001 Fig. 3A (amended)"), "inside a longer name, with a letter");
            Assert.Equal("FIG. 3", Labels("03"), "a name that is only a number");
            Assert.Equal("FIG. 3", Labels("fig3_v2"), "a version after it is not part of the number");
        }

        public static void FileName_ThatNamesNoFigure_GivesNone()
        {
            Assert.Equal("", Labels("scan0042"), "a scanner's name");
            Assert.Equal("", Labels("page 2"), "a page is not a figure");
            Assert.Equal("", Labels("config3"), "fig inside another word");
            Assert.Equal("", Labels("Figure 2026"), "a year is not figure 202");
            Assert.Equal("", Labels("fig3abc"), "letters run on after the number");
            Assert.Equal("", Labels("drawings"), "no number at all");
        }

        public static void FileName_WithSeveralFigures_GivesEachOfThem()
        {
            Assert.Equal("FIG. 1|FIG. 2", Labels("Fig 1-2"), "a range that ends the name");
            Assert.Equal("FIG. 1|FIG. 2|FIG. 3|FIG. 4", Labels("Figures 1-4"), "a longer range");
            Assert.Equal("FIG. 3|FIG. 4", Labels("Figs 3 and 4 amended"), "plural, so the list counts although more follows");
            Assert.Equal("FIG. 3|FIG. 4", Labels("Figuur 3 en 4"), "Dutch");
            Assert.Equal("FIG. 5A|FIG. 5B|FIG. 5C", Labels("Figures 5A-5C"), "letters");
        }

        public static void FileName_SecondNumberThatMayBeSomethingElse_IsNotTakenAsAFigure()
        {
            Assert.Equal("FIG. 1", Labels("Figure 1 - 13 Nov"), "a date after the figure");
            Assert.Equal("FIG. 1", Labels("Figure 01 - 2026-11-13"), "an ISO date after the figure");
            Assert.Equal("FIG. 3", Labels("Figure 3 (2)"), "Windows' name for a second copy");
            Assert.Equal("FIG. 3", Labels("Figure 3-2"), "a backwards range is not a range");
        }

        // ---- the folder -------------------------------------------------------------------

        public static void Scan_AFolderThatIsGone_SaysSo_AndAnEmptyOneHoldsNothing()
        {
            using (var f = new Folder())
            {
                var gone = ReferenceImages.Scan(f.File("not-there"));
                Assert.True(!gone.Exists, "a folder that is gone is reported as gone");
                Assert.Equal(0, gone.Images.Count, "and holds nothing");

                var empty = ReferenceImages.Scan(f.Dir);
                Assert.True(empty.Exists, "an empty folder exists");
                Assert.Equal(0, empty.Images.Count, "and holds no images");
                Assert.True(!empty.TooMany, "and is not too many");

                Assert.True(!ReferenceImages.Scan(null).Exists, "no folder chosen");
            }
        }

        public static void Scan_CountsImageFilesOnly_OneLevelOfSubFolders_InFigureOrder()
        {
            using (var f = new Folder())
            {
                Png(f.File("Figure 10.png"));
                Png(f.File("Figure 2.png"));
                Png(f.File("cover.png"));
                File.WriteAllText(f.File("drawings.pdf"), "%PDF");
                File.WriteAllText(f.File("notes.docx"), "x");
                File.WriteAllText(f.File("Thumbs.db"), "x");
                var sub = f.Sub("sheet scans");
                Png(Path.Combine(sub, "Figure 1.png"));
                var deeper = Path.Combine(sub, "old"); Directory.CreateDirectory(deeper);
                Png(Path.Combine(deeper, "Figure 9.png"));

                var scan = ReferenceImages.Scan(f.Dir);
                Assert.Equal("Figure 2.png|Figure 10.png|cover.png|sheet scans\\Figure 1.png",
                    string.Join("|", scan.Images.Select(i => i.DisplayName)),
                    "numbered in number order, then the rest, then each sub-folder; two levels down is not looked at");
                Assert.Equal(2, scan.OtherFiles, "the PDF and the Word file are counted as not used; Thumbs.db is nobody's file");
                Assert.Equal(ReferenceImages.CountImages(f.Dir), scan.Images.Count, "the number shown in the window is the number described");
            }
        }

        public static void Scan_TwoHundredFilesAreTaken_OneMoreIsTooMany()
        {
            using (var f = new Folder())
            {
                for (int i = 1; i <= 200; i++) File.WriteAllBytes(f.File("Figure " + i + ".png"), new byte[] { 1 });
                var ok = ReferenceImages.Scan(f.Dir);
                Assert.Equal(200, ok.Images.Count, "200 image files");
                Assert.True(!ok.TooMany, "200 is within the limit");

                File.WriteAllBytes(f.File("Figure 201.png"), new byte[] { 1 });
                Assert.True(ReferenceImages.Scan(f.Dir).TooMany, "201 is refused");
            }
        }

        public static void Scan_AWronglyChosenFolderOfThousandsOfFiles_StopsLooking()
        {
            using (var f = new Folder())
            {
                for (int i = 0; i < ReferenceImages.MaxEntries + 50; i++) File.WriteAllBytes(f.File("file" + i + ".txt"), new byte[0]);
                var scan = ReferenceImages.Scan(f.Dir);
                Assert.True(scan.TooMany, "more entries than the scan looks through");
                Assert.True(scan.OtherFiles <= ReferenceImages.MaxEntries, "it stopped at the limit instead of listing everything");
            }
        }

        // ---- matching files to the text ------------------------------------------------------

        public static void Plan_FourDrawingFilesAndATextThatDescribesFourFigures_AllMatch()
        {
            using (var f = new Folder())
            {
                for (int i = 1; i <= 4; i++) Png(f.File("Figure " + i + ".png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2, 3, 4));

                Assert.True(plan.FolderExists && !plan.TooMany, "the folder is usable");
                Assert.Equal(4, plan.Files.Count, "four files");
                Assert.Equal(4, plan.Matched, "all matched by name");
                Assert.Equal(0, plan.FiguresWithoutFile.Count, "no figure is missing a file");
                Assert.Equal("FIG. 3", plan.Files[2].Label, "third file is figure 3");
                Assert.Equal("Figuur 3 toont onderdeel 3.", plan.Files[2].Descriptions.Single(), "and carries what the text says about figure 3");
                Assert.True(plan.Files.All(x => x.Note == null), "nothing to remark");
            }
        }

        public static void Plan_AFileThatMatchesNoFigure_IsKept_WithoutALabel_AndSaysWhy()
        {
            using (var f = new Folder())
            {
                Png(f.File("Figure 1.png"));
                Png(f.File("scan0042.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2));

                Assert.Equal(2, plan.Files.Count, "both files will be described");
                Assert.Equal(1, plan.Unmatched, "one has no figure number");
                var odd = plan.Files.Single(x => x.Image.FileName == "scan0042.png");
                Assert.True(odd.Label == null, "it is given no figure label");
                Assert.Equal(0, odd.Descriptions.Count, "and no text that belongs to some other figure");
                Assert.True(odd.Note.Contains("no figure number"), "its note says why");
                Assert.Equal("2", string.Join(",", plan.FiguresWithoutFile), "figure 2 is described in the text and has no file");
            }
        }

        public static void Plan_TwoFilesForOneFigure_AFigureTheTextDoesNotHave_AndOneFileForTwoFigures()
        {
            using (var f = new Folder())
            {
                Png(f.File("Figure 3.png"));
                Png(f.File("fig3.jpg"));
                Png(f.File("Figure 7.png"));
                Png(f.File("Fig 1-2.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2, 3));

                var both = plan.Files.Where(x => x.Label == "FIG. 3").ToList();
                Assert.Equal(2, both.Count, "both files for figure 3 are described, neither is picked over the other");
                Assert.True(both.All(x => x.Note != null && x.Note.Contains("same figure number as")), "and each names the other");

                var seven = plan.Files.Single(x => x.Label == "FIG. 7");
                Assert.True(seven.Note.Contains("does not describe figure 7"), "a figure the text never describes is said to be so");
                Assert.Equal(0, seven.Descriptions.Count, "and gets no text");

                var sheet = plan.Files.Single(x => x.Image.FileName == "Fig 1-2.png");
                Assert.Equal("FIG. 1, FIG. 2", sheet.Label, "one file, two figures");
                Assert.Equal(2, sheet.Descriptions.Count, "with the text for both");
                Assert.Equal(0, plan.FiguresWithoutFile.Count, "so figures 1 and 2 have a file");
            }
        }

        // ---- one image, before it is sent --------------------------------------------------

        public static void Prepare_AnOrdinaryPng_IsSentAsItIs()
        {
            using (var f = new Folder())
            {
                var path = f.File("Figure 1.png"); Png(path);
                string error;
                var image = ImageConversion.PrepareForVision(path, out error);
                Assert.True(image != null && error == null, "usable: " + error);
                Assert.Equal("image/png", image.MimeType, "as PNG");
                Assert.Equal(new FileInfo(path).Length, (long)image.Data.Length, "byte for byte");
                Assert.True(image.Note == null, "nothing done to it");
            }
        }

        public static void Prepare_AFileThatIsNotAnImage_IsRefusedBeforeAnythingIsSent()
        {
            using (var f = new Folder())
            {
                string error;

                var renamed = f.File("Figure 1.png"); File.WriteAllText(renamed, "This is a text file, not a drawing.");
                Assert.True(ImageConversion.PrepareForVision(renamed, out error) == null, "text renamed to .png");
                Assert.True(error.Contains("not a readable image"), "says so: " + error);

                var webp = f.File("Figure 2.webp"); File.WriteAllText(webp, "also text");
                Assert.True(ImageConversion.PrepareForVision(webp, out error) == null, "text renamed to .webp");
                Assert.True(error.Contains("not a readable image"), "says so: " + error);

                var pdf = f.File("drawings.pdf"); File.WriteAllText(pdf, "%PDF-1.7");
                Assert.True(ImageConversion.PrepareForVision(pdf, out error) == null, "a PDF");
                Assert.True(error.Contains("not an image format"), "says so: " + error);

                var empty = f.File("Figure 3.png"); File.WriteAllBytes(empty, new byte[0]);
                Assert.True(ImageConversion.PrepareForVision(empty, out error) == null, "an empty file");
                Assert.True(error.Contains("empty"), "says so: " + error);

                Assert.True(ImageConversion.PrepareForVision(f.File("gone.png"), out error) == null, "a file that is gone");
                Assert.True(error.Contains("no longer there"), "says so: " + error);
            }
        }

        public static void Prepare_AFileSomethingElseHasLocked_IsReportedNotThrown()
        {
            using (var f = new Folder())
            {
                var path = f.File("Figure 1.png"); Png(path);
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    string error;
                    Assert.True(ImageConversion.PrepareForVision(path, out error) == null, "a file another program holds");
                    Assert.True(error.Contains("could not read"), "says so: " + error);
                }
            }
        }

        public static void Prepare_AHugeFile_IsRefusedWithoutBeingRead()
        {
            using (var f = new Folder())
            {
                var path = f.File("Figure 1.png");
                using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(ImageConversion.MaxFileBytes + 1);
                string error;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Assert.True(ImageConversion.PrepareForVision(path, out error) == null, "over the limit");
                Assert.True(error.Contains("too large"), "says so: " + error);
                Assert.True(clock.ElapsedMilliseconds < 2000, "decided from the size, not by reading 50 MB");
            }
        }

        public static void Prepare_AnImageWiderThanAProviderTakes_IsScaledDown_AndTheFileIsLeftAlone()
        {
            using (var f = new Folder())
            {
                var path = f.File("Figure 1.png"); Png(path, 9000, 300);
                var before = File.ReadAllBytes(path);
                string error;
                var image = ImageConversion.PrepareForVision(path, out error);
                Assert.True(image != null, "usable: " + error);
                Assert.Equal("scaled down before sending", image.Note, "and the result says it was scaled");
                using (var ms = new MemoryStream(image.Data))
                using (var sent = Image.FromStream(ms))
                    Assert.True(sent.Width <= 2200 && sent.Width > 2000, "long edge brought to 2200, was 9000; got " + sent.Width);
                Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "the user's file is untouched");
            }
        }

        public static void Prepare_ABmpIsConverted_AndATwoPageTiffSaysOnlyItsFirstPageWasUsed()
        {
            using (var f = new Folder())
            {
                string error;
                var bmpPath = f.File("Figure 1.bmp");
                using (var bmp = new Bitmap(60, 40)) bmp.Save(bmpPath, ImageFormat.Bmp);
                var fromBmp = ImageConversion.PrepareForVision(bmpPath, out error);
                Assert.True(fromBmp != null, "a BMP is usable: " + error);
                Assert.Equal("image/png", fromBmp.MimeType, "sent as PNG");

                var tifPath = f.File("Figure 2.tif");
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid);
                using (var first = new Bitmap(60, 40))
                using (var second = new Bitmap(60, 40))
                using (var p = new EncoderParameters(1))
                {
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
                    first.Save(tifPath, codec, p);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
                    first.SaveAdd(second, p);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
                    first.SaveAdd(p);
                }
                var fromTif = ImageConversion.PrepareForVision(tifPath, out error);
                Assert.True(fromTif != null, "a TIFF is usable: " + error);
                Assert.Equal("first page of 2 only", fromTif.Note, "and the second page is not silently dropped");
            }
        }

        public static void Analyse_AFileThatCannotBeUsed_MakesNoRequest()
        {
            using (var f = new Folder())
            {
                var path = f.File("Figure 1.png"); File.WriteAllText(path, "not an image");
                // No client at all: reaching the request would fail differently.
                var v = Wait(FigureAnalyzer.AnalyseAsync(null, path, "FIG. 1", new[] { "Figuur 1 toont iets." }));
                Assert.True(v.Error != null && v.Error.Contains("not a readable image"), "refused before the request: " + v.Error);
            }
        }

        // ---- the run, and what it may write ------------------------------------------------

        private const string HandWritten = "# Figures\r\n\r\nWritten by hand. FIG. 1 shows the housing (12).\r\n";

        public static void Run_DescribesEveryFile_AndWritesFiguresMd()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            {
                Png(f.File("Figure 1.png")); Png(f.File("Figure 2.png")); Png(f.File("scan0042.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2, 3));
                var figures = bank.File("figures.md");
                var seen = new List<string>();

                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, Answers, figures, new HashSet<string>(), "", "FigureLens",
                    (n, of, job) => seen.Add(n + "/" + of), CancellationToken.None));

                Assert.Equal(figures, result.WrittenTo, "written");
                Assert.Equal("1/3,2/3,3/3", string.Join(",", seen), "progress for each file");
                var text = File.ReadAllText(figures);
                Assert.True(text.Contains("| FIG. 1 | Figure 1.png | Figuur 1 toont onderdeel 1. | A drawing of Figure 1. | 12 |"), "a matched row: label, file, text, what the AI saw\n" + text);
                Assert.True(text.Contains("| " + FolderFigures.NoNumberLabel + " | scan0042.png | – |"), "the unmatched file is described and its row says no figure number");
                Assert.True(text.Contains("| FIG. 3 | – | Figuur 3 toont onderdeel 3. | *no image file for this figure* |"), "a figure with no file is listed, not dropped");
                Assert.True(text.Contains("# Figures"), "headed Figures, since some are labelled");
                Assert.True(text.Contains("**12**"), "a sign read in the drawings and absent from the text is reported");
                Assert.True(!FiguresFile.IsTextOnly(text), "it is the with-AI shape");
            }
        }

        public static void Run_OneImageFails_TheRestAreKept_AndItsRowSaysWhy()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            {
                Png(f.File("Figure 1.png")); Png(f.File("Figure 2.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2));
                var figures = bank.File("figures.md");

                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, (job, ct) =>
                {
                    if (job.Label == "FIG. 2") throw new InvalidOperationException("the provider refused the image");
                    return Answers(job, ct);
                }, figures, null, "", null, null, CancellationToken.None));

                Assert.Equal(1, result.Failed, "one failed");
                Assert.True(result.MayWrite && result.WrittenTo != null, "the run still counts");
                Assert.True(File.ReadAllText(figures).Contains("*not analysed: the provider refused the image*"), "the failed row says why");
            }
        }

        public static void Run_EveryRequestFails_AnExistingFiguresMdIsLeftAsItWas_AndNoneIsCreated()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            {
                Png(f.File("Figure 1.png")); Png(f.File("Figure 2.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2));
                Func<FigureRun.Job, CancellationToken, Task<FigureVision>> noKey = (job, ct) =>
                    Task.FromResult(new FigureVision { Label = job.Label, FileName = "x", Error = "401: invalid API key" });

                var figures = bank.File("figures.md");
                File.WriteAllText(figures, HandWritten);
                var stamp = File.GetLastWriteTimeUtc(figures);
                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, noKey, figures, null, "", null, null, CancellationToken.None));

                Assert.True(result.NothingDescribed && !result.MayWrite, "nothing worth writing");
                Assert.True(result.WrittenTo == null, "nothing written");
                Assert.Equal("401: invalid API key", result.FirstError, "the reason is kept for the message");
                Assert.Equal(HandWritten, File.ReadAllText(figures), "the hand-written file is untouched");
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(figures), "not even rewritten with the same text");

                var fresh = bank.File("other\\figures.md");
                Wait(FolderFigures.DescribeAndSaveAsync(plan, noKey, fresh, null, "", null, null, CancellationToken.None));
                Assert.True(!File.Exists(fresh), "and where there was none, a file of failures is not created");
            }
        }

        public static void Run_StoppedHalfWay_LeavesFiguresMdAsItWas()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            using (var cts = new CancellationTokenSource())
            {
                for (int i = 1; i <= 5; i++) Png(f.File("Figure " + i + ".png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2, 3, 4, 5));
                var figures = bank.File("figures.md");
                File.WriteAllText(figures, HandWritten);
                var asked = 0;

                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, async (job, ct) =>
                {
                    asked++;
                    if (asked == 3)
                    {
                        // Stop arrives while the third request is on its way.
                        cts.Cancel();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    return await Answers(job, ct);
                }, figures, null, "", null, null, cts.Token));

                Assert.True(result.Stopped, "the run reports it was stopped");
                Assert.Equal(3, asked, "no request after the stop");
                Assert.Equal(2, result.Visions.Count, "the two that finished are known");
                Assert.True(result.WrittenTo == null && !result.MayWrite, "but nothing is written");
                Assert.Equal(HandWritten, File.ReadAllText(figures), "figures.md is as it was");
            }
        }

        public static void Run_StoppedBeforeItStarts_AsksNothing_AndWritesNothing()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            using (var cts = new CancellationTokenSource())
            {
                Png(f.File("Figure 1.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1));
                cts.Cancel();
                var asked = 0;
                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, (job, ct) => { asked++; return Answers(job, ct); },
                    bank.File("figures.md"), null, "", null, null, cts.Token));
                Assert.True(result.Stopped && asked == 0, "stopped with no request made");
                Assert.True(!File.Exists(bank.File("figures.md")), "and no file");
            }
        }

        public static void Run_ARequestThatTimesOut_IsAFailedImage_NotAStoppedRun()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            {
                Png(f.File("Figure 1.png")); Png(f.File("Figure 2.png"));
                var plan = FolderFigures.Plan(f.Dir, Text(1, 2));
                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, (job, ct) =>
                {
                    // What HttpClient throws on a timeout: a cancellation nobody asked for.
                    if (job.Label == "FIG. 1") throw new TaskCanceledException("The request timed out.");
                    return Answers(job, ct);
                }, bank.File("figures.md"), null, "", null, null, CancellationToken.None));

                Assert.True(!result.Stopped, "nobody pressed Stop");
                Assert.Equal(2, result.Visions.Count, "the run went on to the second image");
                Assert.Equal(1, result.Failed, "the timed-out one is a failed row");
                Assert.True(result.WrittenTo != null, "and the file is written");
            }
        }

        public static void Run_AFolderThatIsGoneOrEmpty_HasNothingToDo_AndWritesNothing()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            {
                var figures = bank.File("figures.md");
                File.WriteAllText(figures, HandWritten);

                var gone = FolderFigures.Plan(f.File("not-there"), Text(1));
                Assert.True(!gone.FolderExists && gone.Files.Count == 0, "a folder that is gone gives an empty plan");
                Assert.Equal("1", string.Join(",", gone.FiguresWithoutFile), "the text's figures are still known");

                var empty = FolderFigures.Plan(f.Dir, Text(1));
                Assert.True(empty.FolderExists && empty.Files.Count == 0, "an empty folder gives an empty plan");

                foreach (var plan in new[] { gone, empty })
                {
                    var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, Answers, figures, null, "", null, null, CancellationToken.None));
                    Assert.True(result.NothingDescribed && result.WrittenTo == null, "nothing described, nothing written");
                }
                Assert.Equal(HandWritten, File.ReadAllText(figures), "figures.md is as it was");
            }
        }

        public static void Run_TwoHundredFiles_AllDescribed_InFigureOrder()
        {
            using (var f = new Folder())
            using (var bank = new Folder())
            {
                for (int i = 1; i <= 200; i++) File.WriteAllBytes(f.File("Figure " + i + ".png"), new byte[] { 1 });
                var plan = FolderFigures.Plan(f.Dir, Text(Enumerable.Range(1, 200).ToArray()));
                Assert.True(!plan.TooMany, "200 is within the limit");
                var order = new List<string>();

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = Wait(FolderFigures.DescribeAndSaveAsync(plan, (job, ct) => { order.Add(job.Label); return Answers(job, ct); },
                    bank.File("figures.md"), null, "", null, null, CancellationToken.None));

                Assert.Equal(200, result.Visions.Count, "200 described");
                Assert.Equal("FIG. 1,FIG. 2,FIG. 3", string.Join(",", order.Take(3)), "from figure 1");
                Assert.Equal("FIG. 200", order.Last(), "to figure 200, not 1, 10, 100, 101 ...");
                var rows = File.ReadAllLines(bank.File("figures.md")).Count(l => l.StartsWith("| FIG. "));
                Assert.Equal(200, rows, "200 rows");
                Assert.True(clock.ElapsedMilliseconds < 5000, "the bookkeeping for 200 takes no time worth mentioning: " + clock.ElapsedMilliseconds + " ms");
            }
        }

        // ---- from the text only ------------------------------------------------------------

        public static void TextOnly_OneRowPerFigureTheTextDescribes_WithItsFileWhereThereIsOne()
        {
            using (var f = new Folder())
            {
                Png(f.File("Figure 1.png")); Png(f.File("scan0042.png"));
                var scan = ReferenceImages.Scan(f.Dir);
                int wrote;
                var text = FiguresFile.RenderFromTextFigures(Text(1, 2), scan.Images, "FigureLens", out wrote);

                Assert.Equal(2, wrote, "two figures");
                Assert.True(text.Contains("| FIG. 1 | Figure 1.png | Figuur 1 toont onderdeel 1. |"), "figure 1 with its file\n" + text);
                Assert.True(text.Contains("| FIG. 2 | – | Figuur 2 toont onderdeel 2. |"), "figure 2 without one");
                Assert.True(!text.Contains("scan0042"), "a file that matches no figure has nothing to say here");
                Assert.True(FiguresFile.IsTextOnly(text), "marked as written without the AI");

                Assert.True(FiguresFile.RenderFromTextFigures(Text(1), null, null, out wrote) != null && wrote == 1, "needs no image files at all");
                Assert.True(FiguresFile.RenderFromTextFigures(Text(), scan.Images, null, out wrote) == null && wrote == 0,
                    "a text that describes no figure gives nothing to write");
            }
        }

        public static void ADocumentWithoutImages_StillSaysWhatItsTextTellsAboutEachFigure()
        {
            using (var f = new Folder())
            {
                var docx = f.File("PROJ-001 description.docx");
                using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
                {
                    Write(zip, "[Content_Types].xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                        + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                        + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                    Write(zip, "word/document.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                        + P("De uitvinding wordt toegelicht aan de hand van de tekening.")
                        + P("Figuur 1 toont een zijaanzicht van de inrichting.")
                        + P("Figuur 2 toont een doorsnede. Daarin is de as (4) zichtbaar.")
                        + P("In figuur 1 is de behuizing (2) weergegeven.")
                        + "</w:body></w:document>");
                }

                var set = DocxImageExtractor.Extract(docx);
                Assert.Equal(0, set.Images.Count, "no images in the document");
                var merged = FolderFigures.MergeDescriptions(new[] { set, null, set });
                Assert.Equal("1,2", string.Join(",", merged.Keys), "but its text describes figures 1 and 2");
                Assert.Equal("Figuur 1 toont een zijaanzicht van de inrichting.", merged[1].Single(), "the sentence that opens with the figure, once");
                Assert.Equal("Figuur 2 toont een doorsnede.", merged[2].Single(), "one sentence, not the paragraph");
            }
        }

        private static string P(string text) => "<w:p><w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r></w:p>";

        private static void Write(ZipArchive zip, string name, string content)
        {
            using (var s = zip.CreateEntry(name).Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
