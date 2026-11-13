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

        // ---- a sentence that differs only in its numbers (13 Nov 2026) -------------------
        // Studio scores it 100%. Sent as "100% match" with the stored texts, the
        // model was told the source was the same and shown another claim number.

        private static BatchSegmentInput Numbers(int number, string segment, string memory, string translation,
            int percent, FuzzyNumberDifference numbers) => new BatchSegmentInput
        {
            Number = number, SourceText = segment, FuzzySourceText = memory, FuzzyTargetText = translation,
            FuzzyMatchPercent = percent, FuzzyNumbers = numbers
        };

        public static void MemoryBlock_DifferentNumbers_IsNamedAndShowsBothTexts()
        {
            var block = TranslationPrompt.BuildMemoryBlock(new List<BatchSegmentInput>
            {
                // Even if a caller left the memory's 100 on it, no percentage is printed.
                Numbers(4, "Werkwijze volgens conclusie 7.", "Werkwijze volgens conclusie 3.",
                    "The method according to claim 3.", 100, FuzzyNumberDifference.Stored),
            }).Replace("\r", "");

            Assert.True(block.Contains(
                "Segment 4 – same sentence, different numbers\n" +
                "  this segment:          Werkwijze volgens conclusie 7.\n" +
                "  source in memory:      Werkwijze volgens conclusie 3.\n" +
                "  translation in memory: The method according to claim 3.\n\n"), "named, both texts:\n" + block);
            Assert.True(!block.Contains("% match"), "no percentage for it:\n" + block);
            Assert.True(block.Contains("A match marked \"same sentence, different numbers\" differs from the " +
                "segment only in its numbers. The translation in memory still has the memory's numbers: use the " +
                "segment's own numbers, each in its own place. Do NOT skip"), "the instruction:\n" + block);
            Assert.True(!block.Contains("note:"), "no note when nothing was replaced");
            Assert.True(!block.Contains("already replaced"), "no word about replacing when nothing was");
        }

        public static void MemoryBlock_NumbersAlreadyReplaced_SaysSo()
        {
            var block = TranslationPrompt.BuildMemoryBlock(new List<BatchSegmentInput>
            {
                Numbers(2, "Werkwijze volgens conclusie 7.", "Werkwijze volgens conclusie 3.",
                    "The method according to claim 7.", 99, FuzzyNumberDifference.Replaced),
            }).Replace("\r", "");

            Assert.True(block.Contains(
                "Segment 2 – same sentence, different numbers\n" +
                "  this segment:          Werkwijze volgens conclusie 7.\n" +
                "  source in memory:      Werkwijze volgens conclusie 3.\n" +
                "  translation in memory: The method according to claim 7.\n" +
                "  note:                  the numbers in this translation were already replaced with this segment's\n\n"),
                "the note:\n" + block);
            Assert.True(block.Contains("still has the memory's numbers, unless a note under it says they were " +
                "already replaced: use the segment's own numbers"), "the instruction names the note:\n" + block);
            Assert.True(!block.Contains("99%"), "no percentage for it");
        }

        public static void MemoryBlock_DifferentNumbers_BesideOrdinaryMatches()
        {
            var block = TranslationPrompt.BuildMemoryBlock(new List<BatchSegmentInput>
            {
                Numbers(1, "12 minuten bij 4 bar.", "4 minuten bij 12 bar.", "4 minutes at 12 bar.", 99,
                    FuzzyNumberDifference.Stored),
                Numbers(2, "De klep is dicht.", "De klep is dicht.", "The valve is shut.", 100,
                    FuzzyNumberDifference.None),
                Numbers(3, "De klep is gesloten.", "De klep is dicht.", "The valve is shut.", 85,
                    FuzzyNumberDifference.None),
            }).Replace("\r", "");

            Assert.True(block.Contains("Segment 1 – same sentence, different numbers\n" +
                "  this segment:          12 minuten bij 4 bar.\n"), "swapped numbers named:\n" + block);
            Assert.True(block.Contains("Segment 2 – 100% match\n  source in memory:      De klep is dicht.\n"),
                "an identical sentence stays 100%, its own text not repeated:\n" + block);
            Assert.True(block.Contains("Segment 3 – 85% match\n  this segment:          De klep is gesloten.\n"),
                "an ordinary fuzzy is unchanged:\n" + block);
        }

        public static void MemoryBlock_NumbersFlagWithoutTheMemorysSource_IsIgnored()
        {
            // The label promises a difference the model can see. Without the
            // memory's source there is none to see, so the flag does nothing.
            var block = TranslationPrompt.BuildMemoryBlock(new List<BatchSegmentInput>
            {
                new BatchSegmentInput { Number = 1, SourceText = "Stap 7", FuzzyTargetText = "Step 7",
                    FuzzyMatchPercent = 100, FuzzyNumbers = FuzzyNumberDifference.Stored },
            }).Replace("\r", "");
            Assert.True(block.Contains("Segment 1 – 100% match\n  translation in memory: Step 7\n"), block);
            Assert.True(!block.Contains("different numbers"), "no label, no instruction:\n" + block);
        }

        private static void Same(string a, string b, string why) =>
            Assert.Equal(TmSourceDifference.Same, TmSourceCompare.Compare(a, b), why);
        private static void NumbersOnly(string a, string b, string why) =>
            Assert.Equal(TmSourceDifference.NumbersOnly, TmSourceCompare.Compare(a, b), why);
        private static void Other(string a, string b, string why) =>
            Assert.Equal(TmSourceDifference.Other, TmSourceCompare.Compare(a, b), why);

        public static void SourceCompare_IdenticalText_IsTheSame()
        {
            Same("Werkwijze volgens conclusie 7, waarbij de sensor een temperatuursensor is.",
                 "Werkwijze volgens conclusie 7, waarbij de sensor een temperatuursensor is.", "identical, with a number");
            Same("De klep is dicht.", "De klep is dicht.", "identical, no numbers");
            Same("De  klep is dicht. ", "De klep is dicht.", "white space does not count");
            Same("", null, "nothing against nothing");
        }

        public static void SourceCompare_OnlyNumbersDiffer()
        {
            NumbersOnly("Werkwijze volgens conclusie 7, waarbij de sensor een temperatuursensor is.",
                        "Werkwijze volgens conclusie 3, waarbij de sensor een temperatuursensor is.", "one changed number");
            NumbersOnly("Gedurende 4 minuten bij 12 bar.", "Gedurende 12 minuten bij 4 bar.", "numbers that changed places");
            NumbersOnly("Van 5 tot 5 en nog eens 5.", "Van 8 tot 8 en nog eens 8.", "a number occurring three times");
            NumbersOnly("Stap 5 volgt op stap 5.", "Stap 5 volgt op stap 6.", "one of two equal numbers changed");
            NumbersOnly("Een dikte van 1,5 mm.", "Een dikte van 2,75 mm.", "decimals");
            NumbersOnly("Zie figuur 12.", "Zie figuur 3.", "another number of digits");
        }

        public static void SourceCompare_AnythingElse_IsOther()
        {
            Other("De klep is gesloten.", "De klep is dicht.", "a word differs, no numbers");
            Other("Werkwijze volgens conclusie 7.", "Inrichting volgens conclusie 3.", "a word and a number differ");
            Other("Werkwijze volgens conclusie 3 of 4.", "Werkwijze volgens conclusie 3.", "a number added");
            Other("Een dikte van 15 mm.", "Een dikte van 1,5 mm.", "a decimal comma is not a digit");
            Other("de klep is dicht.", "De klep is dicht.", "capitals count");
            Other("De klep is dicht.", "", "nothing in the memory");
            Other("Bout M8.", "Bout M.", "a number against none");
        }

        // The comparison runs once per distinct sentence of a batch. Twenty thousand
        // long sentences must cost nothing a translator would notice.
        public static void SourceCompare_TwentyThousandLongSentences_TakeWellUnderThreeSeconds()
        {
            var a = string.Join(" ", System.Linq.Enumerable.Repeat("waarbij de sensor 7 een temperatuursensor is,", 12));
            var b = a.Replace("7", "31");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20000; i++)
                if (TmSourceCompare.Compare(a, b) != TmSourceDifference.NumbersOnly) throw new System.Exception("wrong answer");
            clock.Stop();
            Assert.True(clock.ElapsedMilliseconds < 3000, "20,000 comparisons of " + a.Length
                + "-character texts took " + clock.ElapsedMilliseconds + " ms");
            System.Console.WriteLine("    (20,000 comparisons of " + a.Length + "-character texts: "
                + clock.ElapsedMilliseconds + " ms)");
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
