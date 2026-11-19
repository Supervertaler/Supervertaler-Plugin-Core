using System;
using System.Collections.Generic;
using System.Linq;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// AutoPrompt must rank terminology the way the plugin's own TM block does:
    /// the termbase and the document first, translation memory after them, as a
    /// wording to check rather than approved work. Until 209 the generator put
    /// "previous correct translations from TM (highest priority)" above the
    /// termbase, so every prompt it wrote contradicted the block sent with each
    /// translation request. These fail if that wording comes back.
    /// </summary>
    [Tests]
    internal static class AutoPromptTermOrderTests
    {
        private static readonly string[] Domains =
            { "patent", "legal", "medical", "technical", "financial", "marketing", "general" };

        // Wording that ranks or describes the memory as authority. Case-insensitive.
        private static readonly string[] Barred =
        {
            "highest priority",
            "previous correct translations",
            "outrank",
            "human-validated",
            "validated translation",
            "style anchor",
        };

        private static PromptGenerationContext Ctx(string domain, bool withData) => new PromptGenerationContext
        {
            SourceLang = "Dutch",
            TargetLang = "English",
            DetectedDomain = domain,
            SegmentCount = 3,
            SourceSegments = new List<string> { "De klep sluit.", "De pomp draait.", "Einde." },
            TermbaseTerms = withData
                ? new List<TermEntry> { new TermEntry { SourceTerm = "klep", TargetTerm = "valve", TermbaseName = "Acme" } }
                : new List<TermEntry>(),
            TmPairs = withData
                ? new List<TmMatch> { new TmMatch { SourceText = "De klep opent.", TargetText = "The valve opens.", MatchPercentage = 86 } }
                : new List<TmMatch>(),
        };

        private static IEnumerable<(string what, string meta)> MetaPrompts()
        {
            foreach (var d in Domains)
                foreach (var withData in new[] { true, false })
                    yield return (d + (withData ? " with TM and terms" : " with neither"),
                        PromptGenerator.BuildMetaPrompt(Ctx(d, withData)));
        }

        public static void Priority_PutsTermbaseAndDocumentBeforeTm()
        {
            var p = PromptGenerator.TerminologyPriority.ToLowerInvariant();
            var termbase = p.IndexOf("termbase", StringComparison.Ordinal);
            var document = p.IndexOf("document", StringComparison.Ordinal);
            var tm = p.IndexOf("translation memory", StringComparison.Ordinal);
            Assert.True(termbase >= 0 && document >= 0 && tm >= 0, "the priority names all three: " + p);
            Assert.True(termbase < tm, "termbase before translation memory: " + p);
            Assert.True(document < tm, "the document before translation memory: " + p);
            Assert.True(p.StartsWith("(1) project-specific glossary (termbase)", StringComparison.Ordinal),
                "the termbase is first: " + p);
        }

        public static void MetaPrompt_StatesTheOnePriority_InEveryDomain()
        {
            foreach (var (what, meta) in MetaPrompts())
            {
                Assert.True(meta.Contains(PromptGenerator.TerminologyPriority), what + ": states TerminologyPriority");
                Assert.True(meta.Contains(PromptGenerator.TmMatchStance), what + ": states TmMatchStance");
                Assert.True(meta.Contains(PromptGenerator.ReferenceSectionName), what + ": asks for the reference section by its name");
                // The reference pairs are this document's confirmed segments, and
                // are said to be, wherever there are any.
                Assert.True(meta.Contains(PromptGenerator.ReferenceStance) == what.Contains("with TM"),
                    what + ": states ReferenceStance exactly when there are pairs");
                Assert.True(meta.IndexOf("from the project's Translation", StringComparison.OrdinalIgnoreCase) < 0,
                    what + ": the confirmed pairs are not called translation memory");
            }
        }

        public static void MetaPrompt_NeverCallsTmAuthority()
        {
            foreach (var (what, meta) in MetaPrompts())
            {
                var lower = meta.ToLowerInvariant();
                foreach (var b in Barred)
                    Assert.True(!lower.Contains(b), what + ": the meta-prompt says \"" + b + "\"");
            }
        }

        public static void SectionsAndTemplates_UseTheTmSectionName()
        {
            foreach (var d in Domains)
            {
                var sections = PromptGenerator.SectionsFor(d);
                Assert.True(sections.Contains(PromptGenerator.ReferenceSectionName), d + ": has the reference section");
                foreach (var s in sections)
                    foreach (var b in Barred)
                        Assert.True(s.IndexOf(b, StringComparison.OrdinalIgnoreCase) < 0,
                            d + ": section \"" + s + "\" says \"" + b + "\"");
            }
        }

        public static void AutoPromptAndTmBlock_ShareThePrecedenceWording()
        {
            var block = TranslationPrompt.BuildMemoryBlock(new List<BatchSegmentInput>
            {
                new BatchSegmentInput { Number = 1, SourceText = "Close", FuzzyTargetText = "Fechar", FuzzyMatchPercent = 100 },
            });
            var meta = PromptGenerator.BuildMetaPrompt(Ctx("general", true));
            Assert.True(block.Contains(TranslationPrompt.TermbaseAndDocumentTakePrecedence), "the TM block says it");
            Assert.True(meta.Contains(TranslationPrompt.TermbaseAndDocumentTakePrecedence), "AutoPrompt says it");
        }
    }
}
