using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Memory bank files for one target language (style.nl-BE.md), sent only
    /// with work into that language, and the "when sources disagree" order the
    /// SUPERMEMORY block states. See BankLanguageFiles for the rules.
    /// </summary>
    [Tests]
    internal static class BankLanguageFileTests
    {
        public static void FileNames_AreLanguageFilesOnlyWhenTheyEndInATwoLetterTag()
        {
            Assert.Equal("nl-BE", BankLanguageFiles.TagOf("style.nl-BE.md"), "variant");
            Assert.Equal("nl-BE", BankLanguageFiles.TagOf("style.nl_be.md"), "underscore and any case");
            Assert.Equal("nl", BankLanguageFiles.TagOf("style.nl.md"), "language only");
            Assert.Equal("sr-Latn-RS", BankLanguageFiles.TagOf("style.sr-latn-rs.md"), "script and region");
            Assert.Equal("es-419", BankLanguageFiles.TagOf("style.es-419.md"), "numeric region");
            Assert.Equal("nl", BankLanguageFiles.TagOf("house.rules.nl.md"), "dots in the name");
            Assert.True(BankLanguageFiles.TagOf("figures.md") == null, "a general file");
            Assert.True(BankLanguageFiles.TagOf("welocalize-style-guide.md") == null, "hyphens are not a tag");
            Assert.True(BankLanguageFiles.TagOf("notes.may.md") == null, "three letters are words, not tags (may = Malay in ISO 639-2)");
            Assert.True(BankLanguageFiles.TagOf("notes.in.md") == null, "a withdrawn code (in, now id) is not a tag");
            Assert.True(BankLanguageFiles.TagOf("notes.xx.md") == null, "an unknown code is not a tag");
            Assert.True(BankLanguageFiles.TagOf("report.v2.md") == null, "a version is not a tag");
            Assert.True(BankLanguageFiles.TagOf("nl-BE.md") == null, "a tag needs a name in front of it");
            Assert.True(BankLanguageFiles.TagOf(null) == null && BankLanguageFiles.TagOf("") == null, "nothing");
        }

        public static void TargetLanguage_IsReadHoweverTheProductSpellsIt()
        {
            Assert.Equal("nl-BE", BankLanguageFiles.ResolveTarget("nl-BE"), "code");
            Assert.Equal("nl-BE", BankLanguageFiles.ResolveTarget("nl_be"), "underscore, lower case");
            Assert.Equal("nl-BE", BankLanguageFiles.ResolveTarget("dut-BE"), "memoQ's three-letter code");
            Assert.Equal("en-GB", BankLanguageFiles.ResolveTarget("eng-GB"), "memoQ's English");
            Assert.Equal("nl-BE", BankLanguageFiles.ResolveTarget("Dutch (Belgium)"), "Trados Studio's display name");
            Assert.Equal("en-GB", BankLanguageFiles.ResolveTarget("English (United Kingdom)"), "another display name");
            Assert.Equal("nl", BankLanguageFiles.ResolveTarget("Dutch"), "a bare language name");
            Assert.Equal("nl", BankLanguageFiles.ResolveTarget("nl"), "a bare code");
            Assert.True(BankLanguageFiles.ResolveTarget("Klingon (Qo'noS)") == null, "unknown");
            Assert.True(BankLanguageFiles.ResolveTarget("") == null && BankLanguageFiles.ResolveTarget(null) == null, "none");
        }

        public static void Classify_LanguageGoesWithEveryVariant_VariantOnlyWithItself()
        {
            Assert.Equal(BankLanguageFiles.Fit.Variant, BankLanguageFiles.Classify("style.nl-BE.md", "Dutch (Belgium)"), "nl-BE file, nl-BE job");
            Assert.Equal(BankLanguageFiles.Fit.Language, BankLanguageFiles.Classify("style.nl.md", "Dutch (Belgium)"), "nl file, nl-BE job");
            Assert.Equal(BankLanguageFiles.Fit.Language, BankLanguageFiles.Classify("style.nl.md", "dut-NL"), "nl file, nl-NL job");
            Assert.Equal(BankLanguageFiles.Fit.Other, BankLanguageFiles.Classify("style.nl-BE.md", "dut-NL"), "nl-BE file, nl-NL job");
            Assert.Equal(BankLanguageFiles.Fit.Other, BankLanguageFiles.Classify("style.nl-BE.md", "Dutch"), "variant file, job with no variant");
            Assert.Equal(BankLanguageFiles.Fit.Other, BankLanguageFiles.Classify("style.fr-FR.md", "nl-BE"), "another language");
            Assert.Equal(BankLanguageFiles.Fit.TargetUnknown, BankLanguageFiles.Classify("style.fr-FR.md", null), "no target");
            Assert.Equal(BankLanguageFiles.Fit.TargetUnknown, BankLanguageFiles.Classify("style.fr-FR.md", "Klingon"), "unrecognised target");
            Assert.Equal(BankLanguageFiles.Fit.General, BankLanguageFiles.Classify("figures.md", "nl-BE"), "general file");
        }

        public static void LoadContext_SendsOnlyTheTargetsFiles_InOrder()
        {
            using (var b = new Banks())
            {
                b.Client("brief.md", "# Acme\r\n\r\nFormal register.");
                b.Client("figures.md", "Figures stay in English.");
                b.Client("style.nl.md", "Dutch: no capital after a colon.");
                b.Client("style.nl-BE.md", "Belgian Dutch: inclusive language as agreed.");
                b.Client("style.fr-FR.md", "French: espace insécable avant les deux-points.");
                b.Client("style.en-GB.md", "British spelling.");
                b.Shared("method.md", "Verify every number.");
                b.Shared("house.nl-BE.md", "House nl-BE list punctuation.");
                b.Shared("house.fr-FR.md", "House fr-FR list punctuation.");

                var be = b.Load("Dutch (Belgium)");
                Assert.Equal("figures.md|style.nl.md|style.nl-BE.md", string.Join("|", be.ExtraPaths),
                    "general first, then the language, then the variant");
                Assert.Equal("_shared/method.md|_shared/house.nl-BE.md", string.Join("|", be.SharedExtraPaths), "the shared bank the same way");
                Assert.Equal("style.en-GB.md|style.fr-FR.md|_shared/house.fr-FR.md", string.Join("|", be.LanguageSkippedPaths),
                    "other languages are named as not sent");
                Assert.Equal("nl-BE", be.TargetLanguage, "the target as understood");
                var block = MemoryBankReader.FormatForPrompt(be);
                Assert.True(!block.Contains("espace insécable") && !block.Contains("British spelling"), "no other language's rules in the prompt");
                Assert.True(block.Contains("inclusive language") && block.Contains("no capital after a colon"), "both Dutch files in the prompt");
                Assert.True(block.Contains("a file for a language variant (style.nl-BE.md) wins"), "and which of the two wins");

                var nl = b.Load("dut-NL");
                Assert.Equal("figures.md|style.nl.md", string.Join("|", nl.ExtraPaths), "nl-NL: the language file, not the Belgian one");

                var fr = b.Load("fr-FR");
                Assert.Equal("figures.md|style.fr-FR.md", string.Join("|", fr.ExtraPaths), "fr-FR: French only");

                var de = b.Load("de-DE");
                Assert.Equal("figures.md", string.Join("|", de.ExtraPaths), "a language the bank has no file for: general files only");
                Assert.True(!MemoryBankReader.FormatForPrompt(de).Contains("language variant"), "and no word about language files");

                var none = b.Load(null);
                Assert.Equal("figures.md|style.en-GB.md|style.fr-FR.md|style.nl-BE.md|style.nl.md", string.Join("|", none.ExtraPaths),
                    "no target known: every language file is sent, nothing is hidden on a guess");
                Assert.True(none.LanguageSkippedPaths.Count == 0 && none.TargetLanguage == null, "nothing skipped");
            }
        }

        public static void ExistingBank_WithNoLanguageFiles_IsUnchanged()
        {
            using (var b = new Banks())
            {
                b.Client("brief.md", "# Acme\r\n\r\nFormal register.");
                b.Client("terminology.md", "| nl | en |\r\n|---|---|\r\n| hellingmeter | inclinometer |");
                b.Client("method.md", "Check numbers.");
                b.Client("figures.md", "Figures stay in English.");
                b.Shared("style.md", "British spelling.");

                var a = MemoryBankReader.FormatForPrompt(b.Load("nl-BE"));
                Assert.Equal(a, MemoryBankReader.FormatForPrompt(b.Load(null)), "the same block with no target");
                Assert.Equal(a, MemoryBankReader.FormatForPrompt(b.Load("fr-FR")), "and with any other target");
                Assert.Equal("figures.md|method.md", string.Join("|", b.Load("fr-FR").ExtraPaths), "files in name order, as before");
                Assert.True(!a.Contains("language variant"), "no word about language files");
            }
        }

        public static void TwoFilesForOneLanguage_BothGo_ByName()
        {
            using (var b = new Banks())
            {
                b.Client("brief.md", "# Acme");
                b.Client("terms.nl-BE.md", "Terms for Belgium.");
                b.Client("style.nl-BE.md", "Style for Belgium.");
                b.Client("style.nl_BE.md", "Same tag, other spelling.");
                var ctx = b.Load("nl-BE");
                Assert.Equal("style.nl-BE.md|style.nl_BE.md|terms.nl-BE.md", string.Join("|", ctx.ExtraPaths), "all three, by name");
            }
        }

        public static void UnreadableFiles_NeverStopTheBank()
        {
            using (var b = new Banks())
            {
                b.Client("brief.md", "# Acme");
                b.Client("style.fr-FR.md", "French.");
                b.Client("style.nl-BE.md", "Belgian.");
                b.Client("figures.md", "Figures.");
                using (new FileStream(b.ClientPath("style.fr-FR.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (new FileStream(b.ClientPath("style.nl-BE.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var ctx = b.Load("nl-BE");
                    Assert.True(ctx != null, "loads");
                    Assert.Equal("figures.md", string.Join("|", ctx.ExtraPaths), "a locked file for the target is left out, the rest goes");
                    Assert.Equal("style.fr-FR.md", string.Join("|", ctx.LanguageSkippedPaths), "a locked file for another language is skipped by name, never read");
                }
            }
        }

        public static void Precedence_ClientThenTermbaseThenHouseThenTm()
        {
            var all = MemoryBankReader.PrecedenceLayers(true, true);
            Assert.Equal(4, all.Count, "four layers");
            Assert.True(all[0].Contains("client section") && all[1].Contains("# TERMBASE")
                        && all[2].Contains("house defaults") && all[3].Contains("Translation memory"),
                        "client > termbase > house defaults > TM");
            var sharedOnly = MemoryBankReader.PrecedenceLayers(false, true);
            Assert.True(sharedOnly.Count == 3 && sharedOnly[0].Contains("# TERMBASE"), "no client: the termbase leads");
            var clientOnly = MemoryBankReader.PrecedenceLayers(true, false);
            Assert.True(clientOnly.Count == 3 && clientOnly[2].Contains("Translation memory"), "no house defaults: three layers");

            using (var b = new Banks())
            {
                b.Client("brief.md", "# Acme");
                b.Shared("style.md", "British spelling.");
                var block = MemoryBankReader.FormatForPrompt(b.Load("nl-BE"));
                var i1 = block.IndexOf("1. The client section", StringComparison.Ordinal);
                var i2 = block.IndexOf("2. The # TERMBASE", StringComparison.Ordinal);
                var i3 = block.IndexOf("3. The house defaults", StringComparison.Ordinal);
                var i4 = block.IndexOf("4. Translation memory", StringComparison.Ordinal);
                Assert.True(i1 > 0 && i1 < i2 && i2 < i3 && i3 < i4, "the block states the order, numbered");
                Assert.True(block.IndexOf("## When sources disagree", StringComparison.Ordinal) < block.IndexOf("# House defaults", StringComparison.Ordinal),
                    "before the material it ranks");
            }
        }

        public static void Scale_ThreeHundredFiles_TwentyLanguages()
        {
            using (var b = new Banks())
            {
                var langs = new[] { "nl-BE", "nl-NL", "fr-FR", "fr-BE", "en-GB", "en-US", "de-DE", "de-CH", "es-ES", "es-419",
                                    "it-IT", "pt-PT", "pt-BR", "pl-PL", "sv-SE", "da-DK", "fi-FI", "cs-CZ", "ja-JP", "zh-CN" };
                b.Client("brief.md", "# Acme");
                for (int i = 0; i < 100; i++) b.Client("general" + i.ToString("000") + ".md", "General note " + i + ".");
                for (int i = 0; i < 200; i++) b.Client("note" + i.ToString("000") + "." + langs[i % 20] + ".md", "Note " + i + ".");
                var cold = Stopwatch.StartNew();
                b.Load("Dutch (Belgium)");          // first use builds the language-name table once
                cold.Stop();
                var sw = Stopwatch.StartNew();
                var ctx = b.Load("Dutch (Belgium)");
                sw.Stop();
                Assert.Equal(110, ctx.ExtraPaths.Count, "100 general + 10 for nl-BE");
                Assert.Equal(190, ctx.LanguageSkippedPaths.Count, "190 for other languages, not read");
                Assert.True(sw.ElapsedMilliseconds < 500, "300 files load in " + sw.ElapsedMilliseconds + " ms");
                Console.WriteLine("      (300 files, 20 languages: first load " + cold.ElapsedMilliseconds + " ms, then " + sw.ElapsedMilliseconds + " ms)");
            }
        }

        /// <summary>A client bank and the _shared bank beside it, in a temp folder.</summary>
        private sealed class Banks : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "sv-banklang-" + Guid.NewGuid().ToString("N"));
            private string ClientDir => Path.Combine(_root, "acme");
            private string SharedDir => Path.Combine(_root, MemoryBankReader.SharedBankName);

            public Banks() { Directory.CreateDirectory(ClientDir); }

            public string ClientPath(string name) => Path.Combine(ClientDir, name);
            public void Client(string name, string text) => File.WriteAllText(ClientPath(name), text);
            public void Shared(string name, string text)
            {
                Directory.CreateDirectory(SharedDir);
                File.WriteAllText(Path.Combine(SharedDir, name), text);
            }

            public KbContext Load(string target) =>
                new MemoryBankReader(ClientDir).LoadContext("Project", null, "en", target, tokenBudget: 0, forTranslation: true);

            public void Dispose()
            {
                try { Directory.Delete(_root, true); } catch { }
            }
        }
    }
}
