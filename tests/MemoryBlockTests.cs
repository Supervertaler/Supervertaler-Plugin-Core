using System.Collections.Generic;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The translation-memory block of the batch prompt. Clipboard Mode lays its
    /// segments out differently from the API batch, but has to show the AI the
    /// same matches under the same instruction, so it takes this block from the
    /// same function. The expected text is pinned here in full: a change to it is
    /// a change to what every translation request says about the TM.
    /// </summary>
    [Tests]
    internal static class MemoryBlockTests
    {
        // Trados's shapes: a searched fuzzy with its source, and an exact match
        // read off the segment, which has a percentage but no source.
        private static List<BatchSegmentInput> Fixture() => new List<BatchSegmentInput>
        {
            new BatchSegmentInput
            {
                Number = 1, SourceText = "Wireless keyboard battery",
                FuzzySourceText = "Wireless keyboard", FuzzyTargetText = "Teclado sem fios",
                FuzzyMatchPercent = 70
            },
            new BatchSegmentInput { Number = 2, SourceText = "Done" },
            new BatchSegmentInput
            {
                Number = 3, SourceText = "Close",
                FuzzyTargetText = "Fechar", FuzzyMatchPercent = 100
            },
        };

        private const string ExpectedBlock =
            "**TRANSLATION MEMORY MATCHES \u2013 REFERENCE ONLY**\n" +
            "\n" +
            "These are earlier translations of similar sentences, found in the project's translation " +
            "memory. A memory can hold translations made for other documents, products or clients, and " +
            "they can be wrong, even at 100%: a 100% match means the source sentence is the same, not " +
            "that the translation is right. Check each one against the segment's source and the " +
            "document before using any of it. Reuse its wording and terminology only where it is " +
            "correct for this segment in this document; the termbase and the document take precedence. " +
            "Where a match does not fit, or you are in doubt, translate from the source. Below 100% the " +
            "segment's own text is shown above the memory's: compare them word by word, because a small " +
            "difference can change the meaning. Do NOT skip these segments \u2013 return a translation " +
            "for every segment in the list below, these included.\n" +
            "\n" +
            "Segment 1 \u2013 70% match\n" +
            "  this segment:          Wireless keyboard battery\n" +
            "  source in memory:      Wireless keyboard\n" +
            "  translation in memory: Teclado sem fios\n" +
            "\n" +
            "Segment 3 \u2013 100% match\n" +
            "  translation in memory: Fechar\n" +
            "\n";

        public static void BatchPrompt_OpensWithTheMemoryBlock()
        {
            var prompt = TranslationPrompt.BuildBatchUserPrompt(Fixture()).Replace("\r", "");
            Assert.True(prompt.StartsWith(ExpectedBlock + "**SEGMENTS TO TRANSLATE (3 segments):**"),
                "batch prompt opens with the pinned block:\n" + prompt);
        }

        public static void MemoryBlock_IsTheBlockTheBatchPromptSends()
        {
            var block = TranslationPrompt.BuildMemoryBlock(Fixture()).Replace("\r", "");
            Assert.Equal(ExpectedBlock, block, "memory block");
        }

        public static void MemoryBlock_NeverCallsAMatchApprovedOrSaysToFollowIt()
        {
            var block = TranslationPrompt.BuildMemoryBlock(Fixture()).ToLowerInvariant();
            Assert.True(!block.Contains("approved"), "no match is presented as approved");
            Assert.True(!block.Contains("follow them"), "no instruction to follow the matches");
            Assert.True(!block.Contains("as it stands"), "no instruction to reuse a 100% match unchecked");
        }

        // memoQ's shape: source and target, no percentage. Closeness is unknown,
        // so the segment's own text is shown for the comparison, and nothing is
        // said about percentages that cannot arise.
        public static void MemoryBlock_WithoutPercentages_ShowsTheSegmentAndClaimsNoScore()
        {
            var memoq = new List<BatchSegmentInput>
            {
                new BatchSegmentInput
                {
                    Number = 4, SourceText = "De klep is gesloten.",
                    FuzzySourceText = "De klep is dicht.", FuzzyTargetText = "The valve is shut."
                },
            };
            var block = TranslationPrompt.BuildMemoryBlock(memoq).Replace("\r", "");

            Assert.True(block.Contains("they can be wrong. Check each one"), "no 100% sentence:\n" + block);
            Assert.True(!block.Contains("100%"), "no percentage claimed");
            Assert.True(block.Contains(" The segment's own text is shown above the memory's"), "comparison sentence");
            Assert.True(block.Contains("Segment 4\n" +
                "  this segment:          De klep is gesloten.\n" +
                "  source in memory:      De klep is dicht.\n" +
                "  translation in memory: The valve is shut.\n"), "segment shown above the memory's:\n" + block);
        }

        public static void MemoryBlock_IsEmptyWithoutMatches()
        {
            var none = new List<BatchSegmentInput>
            {
                new BatchSegmentInput { Number = 1, SourceText = "Done" },
                // A source with no target is not a match.
                new BatchSegmentInput { Number = 2, SourceText = "Open", FuzzySourceText = "Open", FuzzyMatchPercent = 80 },
            };
            Assert.Equal("", TranslationPrompt.BuildMemoryBlock(none), "no matches, no block");
            Assert.Equal("", TranslationPrompt.BuildMemoryBlock(new List<BatchSegmentInput>()), "no segments");
            Assert.True(TranslationPrompt.BuildBatchUserPrompt(none).StartsWith("**SEGMENTS TO TRANSLATE"),
                "batch prompt without matches starts with the segment list");
        }

        public static void OutputContract_FlagsOnlyCorrectionsToExactMatches()
        {
            var t = OutputContract.Text.Replace("\r", "");
            Assert.True(t.Contains("an error you corrected in a 100% translation memory match"),
                "a corrected exact match is worth a note");
            Assert.True(!t.Contains("departure from a translation memory match"),
                "departing from a fuzzy is routine, not a note");
        }
    }
}
