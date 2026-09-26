using System.Collections.Generic;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The termbase section can travel in the system prompt (a batch run: one
    /// list for every batch) or in the user prompt (one segment: that segment's
    /// terms, which change with every segment and would stop the system prompt
    /// from ever being cached). Either way the model must see the same words.
    /// </summary>
    [Tests]
    internal static class TermbaseBlockTests
    {
        private static List<TermEntry> Terms() => new List<TermEntry>
        {
            new TermEntry { SourceTerm = "klep", TargetTerm = "valve", Domain = "Mechanics", Notes = "not 'flap'" },
            new TermEntry { SourceTerm = "schuif", TargetTerm = "slider", Forbidden = true },
            new TermEntry { SourceTerm = "Acme", TargetTerm = "Acme", IsNonTranslatable = true },
            new TermEntry { SourceTerm = "printplaat", TargetTerm = "printed circuit board",
                            SourceAbbreviation = "PCB|pcb", TargetAbbreviation = "PCB" },
            new TermEntry { SourceTerm = "leeg", TargetTerm = "" },
        };

        public static void TheBlock_IsWordForWordWhatTheSystemPromptCarries()
        {
            var block = TranslationPrompt.BuildTermbaseBlock(Terms(), includeTermMetadata: true);
            var system = TranslationPrompt.BuildSystemPrompt("Dutch", "English", null, Terms(), null,
                new List<string> { "De klep is dicht." }, 500, includeTermMetadata: true);

            Assert.True(block.StartsWith("# TERMBASE"), "starts with its heading");
            Assert.True(system.Contains("\r\n\r\n" + block + "\r\n\r\n\r\n# DOCUMENT CONTENT"), "system prompt holds the block verbatim, where it always sat");
            Assert.True(block.Contains("- klep → valve\r\n  Domain: Mechanics\r\n  Notes: not 'flap'"), "metadata");
            Assert.True(block.Contains("- schuif → ⚠️ DO NOT USE: slider"), "forbidden");
            Assert.True(block.Contains("- Acme → Acme (do not translate)"), "non-translatable");
            Assert.True(block.Contains("- PCB, pcb → PCB (abbreviation of: printplaat)"), "abbreviation");
            Assert.True(!block.Contains("leeg"), "a term with no target is left out");
        }

        public static void WithoutMetadata_TheBlockHasNone()
        {
            var block = TranslationPrompt.BuildTermbaseBlock(Terms(), includeTermMetadata: false);
            Assert.True(!block.Contains("Domain:"), "no metadata");
        }

        public static void NoUsableTerms_NoBlock()
        {
            Assert.Equal(null, TranslationPrompt.BuildTermbaseBlock(null), "null");
            Assert.Equal(null, TranslationPrompt.BuildTermbaseBlock(new List<TermEntry>()), "empty");
            Assert.Equal(null, TranslationPrompt.BuildTermbaseBlock(new List<TermEntry> { new TermEntry { SourceTerm = "x" } }), "no target");
        }
    }
}
