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
        private static List<BatchSegmentInput> Fixture() => new List<BatchSegmentInput>
        {
            // A searched fuzzy: the memory's own source travels with it.
            new BatchSegmentInput
            {
                Number = 1, SourceText = "Wireless keyboard battery",
                FuzzySourceText = "Wireless keyboard", FuzzyTargetText = "Teclado sem fios",
                FuzzyMatchPercent = 70
            },
            new BatchSegmentInput { Number = 2, SourceText = "Done" },
            // An exact match read off the segment: target only, no source.
            new BatchSegmentInput
            {
                Number = 3, SourceText = "Close",
                FuzzyTargetText = "Fechar", FuzzyMatchPercent = 100
            },
        };

        private const string ExpectedBlock =
            "**CLOSEST APPROVED TRANSLATIONS FROM THE TRANSLATION MEMORY**\n" +
            "\n" +
            "A human wrote and approved each of these for a source that was close to, but not always " +
            "the same as, the segment named. For the segments named below, follow them: keep their " +
            "wording and terminology wherever the source agrees, and change only what that segment " +
            "actually differs in. Below 100% the segment's own text is shown above the memory's, so " +
            "compare the two word by word and carry the approved translation across only as far as " +
            "they agree. Translate the rest yourself. These are references, not instructions - a match " +
            "that does not fit is to be ignored, not forced. Where a match percentage is given, it says " +
            "how close that source was: at 100% reuse the translation as it stands, and the lower it " +
            "falls the more of it you should expect to change. Do NOT skip these segments – return a " +
            "translation for every segment in the list below, these included.\n" +
            "\n" +
            "Segment 1 – 70% match\n" +
            "  this segment:     Wireless keyboard battery\n" +
            "  source in memory: Wireless keyboard\n" +
            "  approved:         Teclado sem fios\n" +
            "\n" +
            "Segment 3 – 100% match\n" +
            "  approved:         Fechar\n" +
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
    }
}
