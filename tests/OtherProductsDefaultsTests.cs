using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Built-in prompts tagged for one product in another product's own library
    /// (found 23 Nov 2026: memoQ 0.1.32 wrote ten Trados-only QuickLauncher
    /// prompts into prompt_library_memoQ, where memoQ shows them greyed out).
    /// A product's library gets only its own and the untagged built-ins; copies
    /// already written are removed only while still exactly as shipped; the
    /// shared folder, for a product that has not moved, is unchanged.
    /// Every test runs in its own temporary data folder and checks that first.
    /// </summary>
    [Tests]
    internal static class OtherProductsDefaultsTests
    {
        private sealed class Scene : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "sv-otherdefaults-" + Guid.NewGuid().ToString("N"));
            public string Shared => Path.Combine(Root, "prompt_library");
            public string MemoQ => Path.Combine(Root, "prompt_library_memoQ");
            public string Trados => Path.Combine(Root, "prompt_library_Trados");

            public Scene(string product)
            {
                Directory.CreateDirectory(Shared);
                SupervertalerPaths.Set(Root);
                // Never go near the real data folder: stop if the root is not ours.
                Assert.Equal(Shared, SupervertalerPaths.SharedPromptLibraryDir, "the data root is the test folder");
                if (product == null) SupervertalerPaths.ClearProduct();
                else SupervertalerPaths.SetProduct(product);
            }

            public void Dispose()
            {
                SupervertalerPaths.ClearProduct();
                SupervertalerPaths.Reset();
                try
                {
                    foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Root, true);
                }
                catch { }
            }
        }

        private const string Define = @"QuickLauncher\Default\Define [Trados].md";
        private const string Brief = @"QuickLauncher\Default\Generate project brief [Trados].md";
        private const string ExplainGeneral = @"QuickLauncher\Default\Explain\Explain selection (in general) [Trados].md";
        private const string Filename = @"QuickLauncher\Default\Files\Show current filename [Trados].md";
        private const string DefaultTranslation = @"Translate\Default\Default Translation Prompt.md";

        /// <summary>Library-relative paths of every prompt tagged for Trados under <paramref name="dir"/>.</summary>
        private static string[] TaggedTrados(string dir) => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories)
                .Where(f => ProductPromptLibrary.ReadApp(f) == "trados")
                .Select(f => f.Substring(dir.Length + 1)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : new string[0];

        /// <summary>The bug itself: before the fix memoQ's library got the ten Trados-only built-ins.</summary>
        public static void MemoQ_EnsureDefaults_WritesNoTradosOnlyBuiltIns()
        {
            using (var s = new Scene("memoQ"))
            {
                Assert.Equal(s.MemoQ, SupervertalerPaths.PromptLibraryDir, "memoQ's own library");
                new PromptLibrary().EnsureDefaultPrompts();

                Assert.Equal("", string.Join(", ", TaggedTrados(s.MemoQ)), "no Trados-only built-in in memoQ's library");
                Assert.True(File.Exists(Path.Combine(s.MemoQ, DefaultTranslation)), "the untagged built-ins are still written");
                Assert.True(File.Exists(Path.Combine(s.MemoQ, @"QuickLauncher\Default\Text operations\Strip U+2028.md")),
                            "untagged QuickLauncher built-ins too");
            }
        }

        public static void Trados_EnsureDefaults_StillWritesItsOwnBuiltIns()
        {
            using (var s = new Scene("Trados"))
            {
                new PromptLibrary().EnsureDefaultPrompts();
                var tagged = TaggedTrados(s.Trados);
                Assert.Equal(10, tagged.Length, "all ten Trados built-ins: " + string.Join(", ", tagged));
                Assert.True(tagged.Contains(Define, StringComparer.OrdinalIgnoreCase), "Define among them");
                Assert.True(File.Exists(Path.Combine(s.Trados, DefaultTranslation)), "and the untagged ones");
            }
        }

        /// <summary>A host that has not moved (older Workbench-style, no product set)
        /// still gets every built-in in the shared folder, and nothing is removed there.</summary>
        public static void SharedFolder_NoProduct_Unchanged()
        {
            using (var s = new Scene(null))
            {
                Assert.Equal(s.Shared, SupervertalerPaths.PromptLibraryDir, "the shared folder");
                var lib = new PromptLibrary();
                var removed = lib.EnsureDefaultPrompts();
                Assert.Equal(10, TaggedTrados(s.Shared).Length, "the shared folder still gets the Trados built-ins");
                Assert.Equal(0, removed.Count, "nothing removed");
                Assert.Equal(0, lib.EnsureDefaultPrompts().Count, "nor on a second start");
                Assert.Equal(10, TaggedTrados(s.Shared).Length, "still all there");
                Assert.True(!Directory.Exists(s.MemoQ) && !Directory.Exists(s.Trados), "no product folder made");
            }
        }

        /// <summary>
        /// The cleanup of what 0.1.32 wrote. Built-ins as shipped go, including
        /// one with the keys products add themselves (read_only, QuickLauncher
        /// flags), a byte-order mark and LF endings, as on Michael's PC. A copy
        /// whose text was edited, one no longer flagged default and one retagged
        /// for memoQ all stay.
        /// </summary>
        public static void Cleanup_RemovesOnlyUntouchedCopies()
        {
            using (var s = new Scene(null))
            {
                // What memoQ 0.1.32 did: every built-in written into its folder.
                new PromptLibrary().EnsureDefaultPrompts();
                CopyTree(s.Shared, s.MemoQ);
                File.WriteAllText(Path.Combine(s.MemoQ, ProductPromptLibrary.MarkerFileName), "done");

                // As on Michael's PC: extra keys, app last, BOM, LF.
                var definePath = Path.Combine(s.MemoQ, Define);
                File.WriteAllText(definePath,
                    "---\ntype: prompt\nname: \"Define\"\ndescription: \"Defines the selected term and provides usage examples\"\n" +
                    "category: \"QuickLauncher/Default\"\ndefault: true\nread_only: true\nquicklauncher: true\nquicklauncher_grid: true\n" +
                    "app: \"trados\"\n---\n\nDefine \"{{SELECTION}}\" and give practical examples showing how it's used.",
                    new UTF8Encoding(true));
                File.SetAttributes(definePath, FileAttributes.ReadOnly);

                var edited = Path.Combine(s.MemoQ, Brief);
                File.AppendAllText(edited, "\nAlso list the client's name.");
                var notDefault = Path.Combine(s.MemoQ, ExplainGeneral);
                File.WriteAllText(notDefault, File.ReadAllText(notDefault).Replace("default: true", "default: false"));
                var retagged = Path.Combine(s.MemoQ, Filename);
                File.WriteAllText(retagged, File.ReadAllText(retagged).Replace("app: \"trados\"", "app: \"memoq\""));

                SupervertalerPaths.SetProduct("memoQ");
                Assert.Equal(s.MemoQ, SupervertalerPaths.PromptLibraryDir, "memoQ's own library");
                var lib = new PromptLibrary();
                var removed = lib.EnsureDefaultPrompts();

                Assert.Equal(7, removed.Count, "the seven untouched copies: " + string.Join(", ", removed));
                Assert.True(removed.Contains(Define, StringComparer.OrdinalIgnoreCase), "Define with the extra keys, read-only, BOM and LF");
                Assert.True(!File.Exists(definePath), "and it is gone");
                Assert.True(File.Exists(edited) && File.ReadAllText(edited).Contains("client's name"), "the edited copy stays as it was");
                Assert.True(File.Exists(notDefault), "a copy no longer flagged default stays");
                Assert.True(File.Exists(retagged), "a copy retagged for memoQ stays");
                Assert.Equal(2, TaggedTrados(s.MemoQ).Length, "only the edited and the un-flagged Trados copies are left");
                Assert.True(File.Exists(Path.Combine(s.MemoQ, DefaultTranslation)), "memoQ's own built-ins are untouched");
                Assert.Equal(0, lib.EnsureDefaultPrompts().Count, "the next start finds nothing more to do");

                // The shared folder still has all of them.
                Assert.Equal(10, TaggedTrados(s.Shared).Length, "the shared folder is not touched");
            }
        }

        /// <summary>A copy that is open elsewhere cannot be deleted: no exception,
        /// the rest still removed, and it goes on the next start.</summary>
        public static void Cleanup_LockedFile_LeftThenRemovedNextStart()
        {
            using (var s = new Scene(null))
            {
                new PromptLibrary().EnsureDefaultPrompts();
                CopyTree(s.Shared, s.MemoQ);
                File.WriteAllText(Path.Combine(s.MemoQ, ProductPromptLibrary.MarkerFileName), "done");
                SupervertalerPaths.SetProduct("memoQ");

                var definePath = Path.Combine(s.MemoQ, Define);
                var lib = new PromptLibrary();
                using (new FileStream(definePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var removed = lib.EnsureDefaultPrompts();
                    Assert.Equal(9, removed.Count, "the other nine go: " + string.Join(", ", removed));
                    Assert.True(File.Exists(definePath), "the locked one stays");
                }
                var next = lib.EnsureDefaultPrompts();
                Assert.Equal(1, next.Count, "next start removes it");
                Assert.Equal(0, TaggedTrados(s.MemoQ).Length, "none left");
            }
        }

        /// <summary>An untagged built-in copied in from the shared folder (Workbench's
        /// "Define.md") is memoQ's own prompt: it must not be tagged Trados, by
        /// EnsureDefaultPrompts or by the editor's SyncDefaultPromptApps.</summary>
        public static void MemoQ_UntaggedDefineIsNotRetaggedTrados()
        {
            using (var s = new Scene("memoQ"))
            {
                var untagged = Path.Combine(s.MemoQ, @"QuickLauncher\Default\Define.md");
                Directory.CreateDirectory(Path.GetDirectoryName(untagged));
                File.WriteAllText(untagged, "---\ntype: prompt\ndefault: true\n---\n\nDefine \"{{SELECTION}}\".\n");
                File.WriteAllText(Path.Combine(s.MemoQ, ProductPromptLibrary.MarkerFileName), "done");

                var lib = new PromptLibrary();
                lib.SyncDefaultPromptApps();
                lib.EnsureDefaultPrompts();

                Assert.True(File.Exists(untagged), "Define.md keeps its name");
                Assert.Equal("", ProductPromptLibrary.ReadApp(untagged), "and gets no Trados tag");
                Assert.Equal(0, TaggedTrados(s.MemoQ).Length, "no Trados copy appears beside it");
            }
        }

        /// <summary>"Restore default prompts" in a product's library restores only its own.</summary>
        public static void MemoQ_RestoreDefaults_WritesNoTradosOnlyBuiltIns()
        {
            using (var s = new Scene("memoQ"))
            {
                new PromptLibrary().RestoreDefaultPrompts();
                Assert.Equal(0, TaggedTrados(s.MemoQ).Length, "none restored");
                Assert.True(File.Exists(Path.Combine(s.MemoQ, DefaultTranslation)), "memoQ's own are");
            }
        }

        private static void CopyTree(string from, string to)
        {
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(to, f.Substring(from.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(f, dest, true);
            }
        }
    }
}
