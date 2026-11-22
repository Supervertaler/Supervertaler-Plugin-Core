using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Each product's own prompt library (21 Nov 2026): what the one-time copy
    /// from the shared folder takes and leaves, that it never changes the shared
    /// folder, and what it does when it is interrupted, finds a file already
    /// there, meets a read-only or locked file, or runs in two programs at once.
    /// Everything happens in a temporary folder; the tests that go through
    /// SupervertalerPaths check that the data root is that folder first.
    /// </summary>
    [Tests]
    internal static class ProductPromptLibraryTests
    {
        private sealed class Scene : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "sv-prompts-" + Guid.NewGuid().ToString("N"));
            public string Shared => Path.Combine(Root, "prompt_library");
            public string Trados => Path.Combine(Root, "prompt_library_Trados");
            public string GoFile => Path.Combine(Root, "go");

            public Scene() { Directory.CreateDirectory(Shared); }

            public string Write(string rel, string app, string body = "Do the thing.")
            {
                var path = Path.Combine(Shared, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var fm = "---\ntype: prompt\n" + (app == null ? "" : "app: \"" + app + "\"\n") + "---\n\n" + body + "\n";
                File.WriteAllText(path, fm, new UTF8Encoding(true));
                return path;
            }

            public string[] ProductFiles() => Directory.Exists(Trados)
                ? Directory.GetFiles(Trados, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(Trados.Length + 1)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : new string[0];

            public string[] Prompts() => ProductFiles().Where(f => !f.StartsWith(".")).ToArray();

            /// <summary>Every file under the shared folder outside the product
            /// folders, with its bytes, attributes and time: to prove nothing changed.</summary>
            public string Snapshot() => string.Join("\n", Directory.GetFiles(Shared, "*", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(Trados + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => f + "|" + Convert.ToBase64String(File.ReadAllBytes(f)) + "|" +
                             File.GetAttributes(f) + "|" + File.GetLastWriteTimeUtc(f).Ticks));

            public void Dispose()
            {
                try
                {
                    foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Root, true);
                }
                catch { }
            }
        }

        private static string Join(IEnumerable<string> items) => string.Join(", ", items);

        /// <summary>A small library like Michael's: tagged, untagged, twins, another product's.</summary>
        private static void Library(Scene s)
        {
            s.Write(@"Translate\Client A.md", null);
            s.Write(@"Translate\Client B [Trados].md", "trados");
            s.Write(@"Translate\Client C [memoQ].md", "memoq");
            s.Write(@"Translate\Old Workbench one.md", "workbench");
            s.Write(@"Translate\Said both.md", "both");
            s.Write(@"QuickLauncher\Default\Define.md", null);
            s.Write(@"QuickLauncher\Default\Define [Trados].md", "trados");
            s.Write(@"QuickLauncher\Default\Make it sound better.md", null);
            s.Write(@"QuickLauncher\Default\Explain\Explain selection (in general).md", null);
            s.Write(@"QuickLauncher\Default\Explain\Explain selection (in general) [Trados].md", "trados");
            s.Write(@"test-glossary.md", null);
            s.Write(@".trash\Deleted one.md", null);
            File.WriteAllText(Path.Combine(s.Shared, "system_prompts_layer1.json"), "{}");
        }

        private static readonly string[] Expected =
        {
            @"QuickLauncher\Default\Define [Trados].md",
            @"QuickLauncher\Default\Explain\Explain selection (in general) [Trados].md",
            @"QuickLauncher\Default\Make it sound better.md",
            @"test-glossary.md",
            @"Translate\Client A.md",
            @"Translate\Client B [Trados].md",
            @"Translate\Said both.md",
        };

        public static void Copy_TakesThisProductsAndUntagged_LeavesOthersAndTwins()
        {
            using (var s = new Scene())
            {
                Library(s);
                var r = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");

                Assert.Equal(Join(Expected), Join(s.Prompts()), "what the Trados library holds");
                Assert.Equal(7, r.Copied, "copied");
                Assert.Equal(4, r.LeftBehind, "left behind: memoQ, Workbench, and two untagged twins");
                Assert.Equal(0, r.Failed.Count, "failed");
                Assert.True(r.MarkerWritten && File.Exists(Path.Combine(s.Trados, ProductPromptLibrary.MarkerFileName)), "marker written");
                var note = File.ReadAllText(Path.Combine(s.Trados, ProductPromptLibrary.MarkerFileName));
                Assert.True(note.Contains("copied 7,") && !note.Contains("not done"), "the marker's note gives the counts, not a stale status: " + note);
                Assert.Equal(File.ReadAllText(Path.Combine(s.Shared, @"Translate\Client A.md")),
                             File.ReadAllText(Path.Combine(s.Trados, @"Translate\Client A.md")), "content copied as it was");
            }
        }

        public static void Copy_NeverChangesTheSharedFolder()
        {
            using (var s = new Scene())
            {
                Library(s);
                var ro = s.Write(@"Translate\Read-only one.md", null);
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                var before = s.Snapshot();

                ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");
                ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");

                Assert.True(before == s.Snapshot(), "every shared file has the same bytes, attributes and time, and none is gone");
            }
        }

        public static void Copy_NeverOverwritesAFileAlreadyInTheProductFolder()
        {
            using (var s = new Scene())
            {
                Library(s);
                var mine = Path.Combine(s.Trados, @"Translate\Client A.md");
                Directory.CreateDirectory(Path.GetDirectoryName(mine));
                File.WriteAllText(mine, "my own edit");

                var r = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");

                Assert.Equal("my own edit", File.ReadAllText(mine), "the product's own file is kept");
                Assert.Equal(1, r.AlreadyThere, "counted as already there");
                Assert.Equal(6, r.Copied, "the rest copied");
            }
        }

        public static void Copy_ReadOnlySource_GivesAWritableCopy()
        {
            using (var s = new Scene())
            {
                var ro = s.Write(@"Translate\Locked down.md", null);
                File.SetAttributes(ro, FileAttributes.ReadOnly);

                ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");

                var copy = Path.Combine(s.Trados, @"Translate\Locked down.md");
                Assert.True(File.Exists(copy), "copied");
                Assert.True((File.GetAttributes(copy) & FileAttributes.ReadOnly) == 0, "the copy can be saved");
                Assert.True((File.GetAttributes(ro) & FileAttributes.ReadOnly) != 0, "the source is still read-only");
            }
        }

        /// <summary>A file that cannot be read stops the marker, so the next session
        /// finishes the job; everything else is copied the first time.</summary>
        public static void Copy_UnreadableFile_NoMarker_NextRunFinishes()
        {
            using (var s = new Scene())
            {
                Library(s);
                var locked = Path.Combine(s.Shared, @"Translate\Client A.md");
                CopyResult1 first;
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    first = new CopyResult1(ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados"));

                Assert.Equal(1, first.Failed, "the locked file failed");
                Assert.True(!first.Marker, "no marker after a failure");
                Assert.Equal(Expected.Length - 1, s.Prompts().Length, "everything else copied");

                var second = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");
                Assert.Equal(1, second.Copied, "the second run copies only what was missing");
                Assert.Equal(Expected.Length - 1, second.AlreadyThere, "and leaves the rest");
                Assert.True(second.MarkerWritten, "and finishes");
                Assert.Equal(Join(Expected), Join(s.Prompts()), "complete");
            }
        }

        private sealed class CopyResult1
        {
            public readonly int Failed; public readonly bool Marker;
            public CopyResult1(ProductPromptLibrary.CopyResult r) { Failed = r.Failed.Count; Marker = r.MarkerWritten; }
        }

        /// <summary>A copy stopped halfway (Studio closed) leaves some files, a
        /// temporary file and no marker. The next run clears the old temporary
        /// file, keeps a young one (another program may be writing it), and finishes.</summary>
        public static void Copy_InterruptedHalfway_IsFinishedNextTime()
        {
            using (var s = new Scene())
            {
                Library(s);
                var done = Path.Combine(s.Trados, @"Translate\Client A.md");
                Directory.CreateDirectory(Path.GetDirectoryName(done));
                File.Copy(Path.Combine(s.Shared, @"Translate\Client A.md"), done);
                var stale = Path.Combine(s.Trados, @"Translate\Client B [Trados].md.1a2b3c4d.svcopy.tmp");
                File.WriteAllText(stale, "half a pro");
                File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
                var young = Path.Combine(s.Trados, @"Translate\Someone else.md.9f8e7d6c.svcopy.tmp");
                File.WriteAllText(young, "being written right now");

                var r = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");

                Assert.True(!File.Exists(stale), "the stale temporary file is cleared");
                Assert.True(File.Exists(young), "a young temporary file is left alone");
                Assert.Equal(Expected.Length - 1, r.Copied, "the rest is copied");
                Assert.True(r.MarkerWritten, "and the copy is finished");
                Assert.Equal("half a pro".Length < new FileInfo(Path.Combine(s.Trados, @"Translate\Client B [Trados].md")).Length, true,
                             "the real file is whole, not the half-written one");
            }
        }

        /// <summary>Four programs copy at the same moment (two Studios, or a team
        /// folder opened by several colleagues). Each file is copied exactly once,
        /// whole, and no temporary file is left.</summary>
        public static void Copy_FourProgramsAtOnce_EachFileOnceAndWhole()
        {
            using (var s = new Scene())
            {
                for (int i = 0; i < 150; i++)
                    s.Write($@"Translate\Client {i % 7}\Prompt {i}.md", null, new string('x', 2000 + i));
                var results = Program.RunChildren(4,
                    i => $"--copy-library \"{s.Shared}\" \"{s.Trados}\" \"{s.GoFile}\"", s.GoFile);

                var copied = results.Select(r => int.Parse(r.Split(' ')[1])).Sum();
                Assert.Equal(150, copied, "copies across all four [" + Join(results) + "]");
                Assert.True(results.All(r => r.EndsWith("failed 0")), "no failures [" + Join(results) + "]");
                Assert.Equal(150, s.Prompts().Count(f => f.EndsWith(".md")), "prompts in the product folder");
                Assert.Equal(0, s.ProductFiles().Count(f => f.EndsWith(".tmp")), "temporary files left");
                foreach (var f in s.Prompts().Where(f => f.EndsWith(".md")))
                    Assert.Equal(new FileInfo(Path.Combine(s.Shared, f)).Length, new FileInfo(Path.Combine(s.Trados, f)).Length, "whole: " + f);
                Assert.True(File.Exists(Path.Combine(s.Trados, ProductPromptLibrary.MarkerFileName)), "marker");
            }
        }

        /// <summary>No .svprompt ever lands in a product folder: released Workbench
        /// (1.9.364 on) renames every .svprompt under prompt_library, and deletes one
        /// that has an .md beside it. A legacy file is copied under its .md name; one
        /// beside an .md of the same name is left, as the .md is the prompt.</summary>
        public static void Copy_LegacySvprompt_ArrivesAsMd_NoneLeftForOldWorkbench()
        {
            using (var s = new Scene())
            {
                s.Write(@"Translate\Legacy one.svprompt", null, "old format");
                s.Write(@"Translate\Both.svprompt", null, "old");
                s.Write(@"Translate\Both.md", null, "new");

                var r = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");

                Assert.Equal(@"Translate\Both.md, Translate\Legacy one.md", Join(s.Prompts()), "what arrives");
                Assert.True(File.ReadAllText(Path.Combine(s.Trados, @"Translate\Legacy one.md")).Contains("old format"), "the legacy prompt's text");
                Assert.True(File.ReadAllText(Path.Combine(s.Trados, @"Translate\Both.md")).Contains("new"), "the .md wins");
                Assert.Equal(0, s.ProductFiles().Count(f => f.EndsWith(".svprompt", StringComparison.OrdinalIgnoreCase)), ".svprompt files in the product folder");
                Assert.True(File.Exists(Path.Combine(s.Shared, @"Translate\Legacy one.svprompt")), "the shared original stays as it was");
                Assert.True(r.MarkerWritten, "done");
            }
        }

        // ---- Carry-over of a pre-release library inside prompt_library (22 Nov 2026) ----

        /// <summary>A pre-release library as the 21 Nov builds left it: inside
        /// prompt_library, complete, with its marker and a prompt made in Trados
        /// since (so not in the shared folder).</summary>
        private static string Legacy(Scene s, string product = "Trados")
        {
            // Built outside and moved in: copying the shared folder into a folder
            // inside itself would copy the copy.
            var staging = Path.Combine(s.Root, "staging-" + product);
            ProductPromptLibrary.CopyFromShared(s.Shared, staging, product);
            var legacy = Path.Combine(s.Shared, product);
            Directory.Move(staging, legacy);
            var made = Path.Combine(legacy, @"Translate\Made in Trados since.md");
            File.WriteAllText(made, "---\ntype: prompt\n---\n\nmine\n");
            return legacy;
        }

        private static string[] FilesUnder(string dir) => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => f.Substring(dir.Length + 1))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : new string[0];

        public static void CarryOver_MovesTheOldFolder_ChecksIt_ThenRemovesIt()
        {
            using (var s = new Scene())
            {
                Library(s);
                var legacy = Legacy(s);
                var oldFiles = FilesUnder(legacy);
                ProductPromptLibrary.Forget();

                var used = ProductPromptLibrary.Resolve(s.Shared, "Trados");
                var r = ProductPromptLibrary.LastResult;

                Assert.Equal(s.Trados, used, "the new folder is used");
                Assert.Equal(Join(oldFiles), Join(FilesUnder(s.Trados)), "every file, marker included, is in the new folder");
                Assert.True(File.ReadAllText(Path.Combine(s.Trados, @"Translate\Made in Trados since.md")).Contains("mine"), "a prompt made since arrives");
                Assert.True(!Directory.Exists(legacy), "the old folder is gone, so an old Workbench has nothing to show");
                Assert.Equal(0, r.Copied, "no second copy from the shared folder: the old folder's marker came along");
                Assert.True(r.CarryOverNote.Contains("old folder removed"), r.ToString());
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>Stopped halfway: some files already in the new folder, a stale
        /// temporary file. The next start finishes and removes the old folder.</summary>
        public static void CarryOver_InterruptedHalfway_IsFinishedNextTime()
        {
            using (var s = new Scene())
            {
                Library(s);
                var legacy = Legacy(s);
                var half = FilesUnder(legacy).Take(3).ToList();
                foreach (var f in half)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(s.Trados, f)));
                    File.Copy(Path.Combine(legacy, f), Path.Combine(s.Trados, f));
                }
                var stale = Path.Combine(s.Trados, @"Translate\Client A.md.0badf00d.svcopy.tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(stale));
                File.WriteAllText(stale, "half");
                File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
                var all = FilesUnder(legacy);
                ProductPromptLibrary.Forget();

                Assert.Equal(s.Trados, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "the new folder is used");
                Assert.Equal(Join(all), Join(FilesUnder(s.Trados)), "complete, without the temporary file");
                Assert.True(!Directory.Exists(legacy), "old folder removed");
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>A file in the new folder differs from the old one (changed in
        /// the new folder after an earlier, interrupted carry-over): the new one is
        /// kept, the old one is not deleted, everything else is removed.</summary>
        public static void CarryOver_TargetPartlyThereAndChanged_KeepsBoth_LosesNothing()
        {
            using (var s = new Scene())
            {
                Library(s);
                var legacy = Legacy(s);
                var changed = Path.Combine(s.Trados, @"Translate\Client A.md");
                Directory.CreateDirectory(Path.GetDirectoryName(changed));
                File.WriteAllText(changed, "edited in the new folder");
                ProductPromptLibrary.Forget();

                Assert.Equal(s.Trados, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "the new folder is used");
                Assert.Equal("edited in the new folder", File.ReadAllText(changed), "the newer edit is kept");
                Assert.Equal(@".copied-from-shared-library, Translate\Client A.md", Join(FilesUnder(legacy)),
                             "only the differing old file is left, not deleted, with the marker that keeps the folder recognised");
                Assert.True(ProductPromptLibrary.LastResult.CarryOverNote.Contains("changed since"), ProductPromptLibrary.LastResult.ToString());
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>A file Windows will not let us read: nothing is removed, and the
        /// old folder stays in use, so the user works on the complete set. Next
        /// time, unlocked, it finishes.</summary>
        public static void CarryOver_LockedFile_OldFolderStaysInUse_ThenFinishes()
        {
            using (var s = new Scene())
            {
                Library(s);
                var legacy = Legacy(s);
                var before = FilesUnder(legacy);
                var locked = Path.Combine(legacy, @"Translate\Made in Trados since.md");
                ProductPromptLibrary.Forget();
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(legacy, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "the old folder stays in use");
                    Assert.Equal(Join(before), Join(FilesUnder(legacy)), "nothing removed from it");
                }
                ProductPromptLibrary.Forget();
                Assert.Equal(s.Trados, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "next session: the new folder");
                Assert.True(!Directory.Exists(legacy), "and the old one removed");
                Assert.Equal(Join(before), Join(FilesUnder(s.Trados)), "complete");
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>A file that can be read but not deleted (open elsewhere): the new
        /// folder is complete and used; that one old file stays until next time.</summary>
        public static void CarryOver_FileThatCannotBeDeleted_StaysUntilNextTime()
        {
            using (var s = new Scene())
            {
                Library(s);
                var legacy = Legacy(s);
                var open = Path.Combine(legacy, @"Translate\Client A.md");
                ProductPromptLibrary.Forget();
                using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.Equal(s.Trados, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "the new folder is complete and used");
                    Assert.True(File.Exists(open), "the open file is left");
                }
                ProductPromptLibrary.Forget();
                ProductPromptLibrary.Resolve(s.Shared, "Trados");
                Assert.True(!Directory.Exists(legacy), "removed next time");
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>A user's own top-level folder called "Trados", without our marker,
        /// is an ordinary category: copied as one, never moved or removed.</summary>
        public static void CarryOver_AUsersOwnTradosFolder_IsLeftAlone()
        {
            using (var s = new Scene())
            {
                Library(s);
                s.Write(@"Trados\My notes.md", null);
                ProductPromptLibrary.Forget();

                ProductPromptLibrary.Resolve(s.Shared, "Trados");

                Assert.True(File.Exists(Path.Combine(s.Shared, @"Trados\My notes.md")), "the user's folder is untouched");
                Assert.True(File.Exists(Path.Combine(s.Trados, @"Trados\My notes.md")), "and copied as a category");
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>Trados and memoQ start at the same moment, each twice (two
        /// Studios, two memoQs): both new folders complete, both old ones gone,
        /// nothing lost, no temporary files.</summary>
        public static void CarryOver_TradosAndMemoQAtOnce_EachTwice_LosesNothing()
        {
            using (var s = new Scene())
            {
                for (int i = 0; i < 120; i++) s.Write($@"Translate\Client {i % 6}\Prompt {i}.md", null, new string('z', 1500 + i));
                var tLegacy = Legacy(s, "Trados");
                var mLegacy = Legacy(s, "memoQ");
                var tFiles = FilesUnder(tLegacy);
                var mFiles = FilesUnder(mLegacy);
                var memoQ = Path.Combine(s.Root, "prompt_library_memoQ");

                var results = Program.RunChildren(4,
                    i => $"--resolve-library \"{s.Shared}\" {(i % 2 == 0 ? "Trados" : "memoQ")} \"{s.GoFile}\"", s.GoFile);

                Assert.Equal(Join(tFiles), Join(FilesUnder(s.Trados)), "Trados complete [" + Join(results) + "]");
                Assert.Equal(Join(mFiles), Join(FilesUnder(memoQ)), "memoQ complete");
                // A file one process is still copying cannot be deleted by the other
                // at that instant (Windows refuses); it stays, with the marker, for
                // the next start. One more start of each clears both old folders.
                ProductPromptLibrary.Forget();
                ProductPromptLibrary.Resolve(s.Shared, "Trados");
                ProductPromptLibrary.Resolve(s.Shared, "memoQ");
                ProductPromptLibrary.Forget();
                Assert.True(!Directory.Exists(tLegacy) && !Directory.Exists(mLegacy), "both old folders gone after the next start [" + Join(results) + "]");
                Assert.Equal(Join(tFiles), Join(FilesUnder(s.Trados)), "and still complete");
                foreach (var f in tFiles.Where(f => f.EndsWith(".md")))
                    Assert.Equal(new FileInfo(Path.Combine(s.Shared, f)).Exists ? new FileInfo(Path.Combine(s.Shared, f)).Length : new FileInfo(Path.Combine(s.Trados, f)).Length,
                                 new FileInfo(Path.Combine(s.Trados, f)).Length, "whole: " + f);
                Assert.True(results.All(r => r.StartsWith("used prompt_library_")), "every process ends on a new folder [" + Join(results) + "]");
            }
        }

        /// <summary>Once done, a prompt the user deletes from their own library stays deleted.</summary>
        public static void Resolve_AfterTheMarker_DoesNotCopyAgain()
        {
            using (var s = new Scene())
            {
                Library(s);
                ProductPromptLibrary.Forget();
                Assert.Equal(s.Trados, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "the product folder is used");
                File.Delete(Path.Combine(s.Trados, @"Translate\Client A.md"));

                ProductPromptLibrary.Forget();   // a new session
                ProductPromptLibrary.Resolve(s.Shared, "Trados");
                Assert.True(!File.Exists(Path.Combine(s.Trados, @"Translate\Client A.md")), "the deleted prompt does not come back");
                ProductPromptLibrary.Forget();
            }
        }

        /// <summary>When the product's folder cannot be made (here a file is in
        /// the way), the shared folder is used, so the user still has their prompts.</summary>
        public static void Resolve_ProductFolderCannotBeMade_UsesTheSharedFolder()
        {
            using (var s = new Scene())
            {
                Library(s);
                File.WriteAllText(s.Trados, "a file, not a folder");
                ProductPromptLibrary.Forget();
                Assert.Equal(s.Shared, ProductPromptLibrary.Resolve(s.Shared, "Trados"), "falls back to the shared folder");
                Assert.True(ProductPromptLibrary.LastResult.Failed.Count > 0, "and says why");
                ProductPromptLibrary.Forget();
            }
        }

        public static void Copy_NoSharedFolderYet_JustMakesTheProductFolder()
        {
            using (var s = new Scene())
            {
                Directory.Delete(s.Shared);
                var r = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");
                Assert.True(Directory.Exists(s.Trados) && r.MarkerWritten, "a new user gets an empty folder, done");
            }
        }

        public static void SetProduct_RejectsAnUnknownName()
        {
            bool threw = false;
            try { SupervertalerPaths.SetProduct("Wordfast"); }
            catch (ArgumentException) { threw = true; }
            Assert.True(threw, "only the known product folders");
            Assert.True(SupervertalerPaths.Product == null, "and nothing was set");
        }

        /// <summary>Through SupervertalerPaths: with a product the library is its
        /// folder; without one, the shared folder's scan skips the product folders,
        /// so a product that has not moved yet does not list Trados's copies.</summary>
        public static void Paths_ProductFolderWhenSet_AndTheSharedScanSkipsProductFolders()
        {
            using (var s = new Scene())
            {
                Library(s);
                SupervertalerPaths.Set(s.Root);
                try
                {
                    // Never go near the real data folder: stop if the root is not ours.
                    Assert.Equal(s.Shared, SupervertalerPaths.SharedPromptLibraryDir, "the data root is the test folder");

                    SupervertalerPaths.SetProduct("trados");
                    Assert.Equal("Trados", SupervertalerPaths.Product, "the folder name's own spelling");
                    Assert.Equal(s.Trados, SupervertalerPaths.PromptLibraryDir, "Trados reads its own folder");
                    var tradosNames = new PromptLibrary().GetAllPrompts().Select(p => p.RelativePath).ToList();
                    Assert.True(tradosNames.All(r => !r.StartsWith("Trados", StringComparison.OrdinalIgnoreCase)),
                                "paths are relative to the product folder: " + Join(tradosNames));
                    Assert.True(tradosNames.Contains(@"Translate\Client A.md"), "and the same as before: " + Join(tradosNames));

                    SupervertalerPaths.ClearProduct();
                    SupervertalerPaths.Set(s.Root);
                    Assert.Equal(s.Shared, SupervertalerPaths.PromptLibraryDir, "without a product, the shared folder");
                    var sharedNames = new PromptLibrary().GetAllPrompts().Select(p => p.RelativePath).ToList();
                    Assert.True(sharedNames.All(r => !r.StartsWith("Trados", StringComparison.OrdinalIgnoreCase)),
                                "the shared scan skips the Trados folder: " + Join(sharedNames));
                    Assert.True(sharedNames.Contains(@"Translate\Client C [memoQ].md"), "and still lists the rest");
                }
                finally
                {
                    SupervertalerPaths.ClearProduct();
                    SupervertalerPaths.Reset();
                }
            }
        }

        /// <summary>Scale: 3,000 prompts, three folders deep, copied once.</summary>
        public static void Copy_ThreeThousandPrompts_InSeconds()
        {
            using (var s = new Scene())
            {
                for (int i = 0; i < 3000; i++)
                    s.Write($@"Translate\Client {i % 30}\Year {i % 4}\Prompt {i}.md", i % 3 == 0 ? "memoq" : null, new string('y', 3000));
                var clock = Stopwatch.StartNew();
                var r = ProductPromptLibrary.CopyFromShared(s.Shared, s.Trados, "Trados");
                clock.Stop();
                Console.WriteLine($"      3,000 prompts: {r} in {clock.Elapsed.TotalSeconds:F1} s");
                Assert.Equal(2000, r.Copied, "copied");
                Assert.True(clock.Elapsed.TotalSeconds < 60, "well under a minute on a local disk");
            }
        }
    }
}
