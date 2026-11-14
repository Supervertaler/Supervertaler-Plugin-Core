using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Step 2 of cross-batch consistency: a segment that repeats a passage is
    /// sent with the earlier segment and its translation, so the AI keeps the
    /// wording (docs/design/in-run-references.md). Which segments those are, and
    /// the block of the user prompt that carries them. All text invented.
    /// </summary>
    [Tests]
    internal static class EarlierTranslationsTests
    {
        // ---- three kinds of document ---------------------------------------------------

        private static readonly string[] Contract =
        {
            "This Agreement is made between Acme Ltd and the Supplier.",
            "\"Confidential Information\" means any information disclosed by one party to the other party in connection with this Agreement, whether in writing or orally.",
            "The Supplier shall deliver the Goods within thirty days.",
            "The Receiving Party shall not use any information disclosed by one party to the other party in connection with this Agreement for any other purpose.",
        };

        private static readonly string[] Manual =
        {
            "Chapter 2: Cleaning",
            "Disconnect the appliance from the mains supply before removing the cover.",
            "Chapter 7: Replacing the filter",
            "Warning: disconnect the appliance from the mains supply before removing the cover or the filter.",
        };

        private static readonly string[] PatentStyle =
        {
            "Inrichting volgens conclusie 1, waarbij de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld.",
            "Het huis is vervaardigd uit staal.",
            "In een voorkeursuitvoering van de inrichting van Acme is voorzien dat de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld, zodat de klep vrij blijft.",
        };

        private static EarlierTranslations For(IList<string> sources) =>
            new EarlierTranslations(sources, RestatedPassages.Find(sources));

        /// <summary>Translations for the segments given, none for the rest.</summary>
        private static Func<int, string> Known(params object[] indexThenText)
        {
            var map = new Dictionary<int, string>();
            for (int i = 0; i < indexThenText.Length; i += 2) map[(int)indexThenText[i]] = (string)indexThenText[i + 1];
            return i => map.TryGetValue(i, out var t) ? t : null;
        }

        public static void Contract_TheLaterClause_IsShownTheDefinitionAndItsTranslation()
        {
            const string german = "„Vertrauliche Informationen“ sind alle Informationen, die eine Partei der anderen Partei im Zusammenhang mit diesem Vertrag offenlegt.";
            var found = For(Contract).For(3, Known(1, german));

            Assert.Equal(1, found.Count, "one earlier translation for the later clause");
            Assert.Equal(Contract[1], found[0].Source, "the definition");
            Assert.Equal(german, found[0].Target, "and its translation");
            Assert.Equal(15, found[0].PassageWords, "over the 15 words they share");
            Assert.Equal(0, For(Contract).For(2, Known(1, german)).Count, "a clause that repeats nothing gets none");
        }

        public static void Manual_TheLaterWarning_IsShownTheEarlierOne()
        {
            var found = For(Manual).For(3, Known(1, "Débranchez l'appareil du secteur avant de retirer le capot."));
            Assert.Equal(1, found.Count, "the warning of chapter 2 for chapter 7");
            Assert.Equal(Manual[1], found[0].Source, "its source");
        }

        public static void PatentStyle_ASentenceThatRepeatsAnother_IsShownIt_InEitherDirection()
        {
            var known = For(PatentStyle);
            Assert.Equal(1, known.For(2, Known(0, "Device according to claim 1, wherein the clamping arm is movable.")).Count,
                "the later sentence is shown the earlier one");
            Assert.Equal(1, known.For(0, Known(2, "In a preferred embodiment the clamping arm is movable.")).Count,
                "and the earlier one the later, when that is the one already translated (part of a document translated first)");
        }

        // ---- what counts as known --------------------------------------------------------

        public static void ASegmentWithNoTranslationYet_IsNotShown()
        {
            var known = For(Contract);
            Assert.Equal(0, known.For(3, Known()).Count, "nothing translated yet");
            Assert.Equal(0, known.For(3, Known(1, "")).Count, "an empty translation: failed, or declined by the CAT tool");
            Assert.Equal(0, known.For(3, Known(1, "   ")).Count, "a blank one");
            Assert.Equal(0, known.For(3, null).Count, "nobody to ask");
            Assert.Equal(0, known.For(99, Known(1, "x")).Count, "a segment the document does not have");
            Assert.Equal(0, new EarlierTranslations(Contract, null).For(3, Known(1, "x")).Count, "no pairs at all");
            Assert.Equal(0, new EarlierTranslations(null, null).For(0, Known(0, "x")).Count, "no document at all");
        }

        public static void AtMostTwoPerSegment_TheOneThatSharesMostFirst_AndNoTranslationTwice()
        {
            // The same passage in four segments; the last also shares more with the third.
            const string passage = "the pump must be primed with clean water before it is started for the first time";
            var sources = new[]
            {
                "Note that " + passage + ".",
                "Remember: " + passage + " today.",
                "After every repair of the seal by Acme staff, " + passage + " again.",
                "After every repair of the seal by Acme staff, " + passage + " and checked.",
            };
            var known = For(sources);

            var all = known.For(3, Known(0, "Eins.", 1, "Zwei.", 2, "Drei."));
            Assert.Equal(EarlierTranslations.MaxPerSegment, all.Count, "two, though three are known");
            Assert.Equal("Drei.", all[0].Target, "the one that shares most comes first");

            var same = known.For(3, Known(0, "Gleich.", 1, "Gleich.", 2, "Gleich."));
            Assert.Equal(1, same.Count, "three segments with one translation are one thing to show");
        }

        public static void AnEarlierSegmentOverTheTokenLimit_IsSkipped_NotCutShort()
        {
            // The definition made longer than 400 tokens (words, punctuation and
            // spaces): too long to show, in source or in translation.
            var longer = new StringBuilder(Contract[1]);
            for (int i = 0; i < 210; i++) longer.Append(" word").Append(i);
            var sources = new[] { longer.ToString(), Contract[3], Contract[1] };
            var known = For(sources);

            var found = known.For(1, Known(0, "Die lange Definition.", 2, "Die kurze Definition."));
            Assert.Equal(1, found.Count, "only the one that fits");
            Assert.Equal("Die kurze Definition.", found[0].Target, "the short definition");
            Assert.Equal(Contract[1], found[0].Source, "whole, not a part of the long one");

            var longTarget = string.Join(" ", Enumerable.Range(0, 210).Select(i => "Wort" + i));
            Assert.Equal(0, known.For(1, Known(2, longTarget)).Count, "a translation over the limit is not shown either");
        }

        public static void ATranslationInALanguageWrittenWithoutSpaces_IsNotShown()
        {
            Assert.Equal(0, For(Contract).For(3, Known(1, "“保密信息”是指一方就本协议向另一方披露的任何信息。")).Count,
                "Chinese: not supported yet, so no reference either");
        }

        // ---- the block in the user prompt -------------------------------------------------

        private static List<BatchSegmentInput> TwoSegments() => new List<BatchSegmentInput>
        {
            new BatchSegmentInput { Number = 1, SourceText = "The Supplier shall deliver the Goods within thirty days." },
            new BatchSegmentInput { Number = 2, SourceText = Contract[3] },
        };

        public static void WithNoReference_TheUserPromptIsByteForByteWhatItWas()
        {
            // What Batch Translate sent before this block existed, written out.
            var expected = string.Join("\r\n", new[]
            {
                "**SEGMENTS TO TRANSLATE (2 segments):**",
                "",
                "⚠️ CRITICAL INSTRUCTIONS:",
                "1. You must provide EXACTLY one translation per segment.",
                "2. You MUST translate ALL 2 segments.",
                "3. Format: Each translation MUST start with its segment number, a period, then a space, then the translation.",
                "4. If the source segment contains line breaks, preserve them in your translation. The number label (e.g., '1.') appears only ONCE at the start.",
                "5. NO explanations, NO commentary, ONLY the numbered translations.",
                "",
                "1. The Supplier shall deliver the Goods within thirty days.",
                "2. " + Contract[3],
                "",
                "**YOUR TRANSLATIONS (numbered list):**",
                "Begin your translations now:",
            });

            Assert.Equal(expected, TranslationPrompt.BuildBatchUserPrompt(TwoSegments()), "no references: as before");

            var empty = TwoSegments();
            empty[1].References = new List<EarlierTranslation>();
            Assert.Equal(expected, TranslationPrompt.BuildBatchUserPrompt(empty), "an empty list is no reference");

            var blank = TwoSegments();
            blank[1].References = new List<EarlierTranslation> { null, new EarlierTranslation { Source = "x", Target = "  " } };
            Assert.Equal(expected, TranslationPrompt.BuildBatchUserPrompt(blank), "nor is one without a translation");

            Assert.Equal("", TranslationPrompt.BuildReferenceBlock(null), "no segments, no block");
        }

        public static void WithAReference_TheBlockStandsBeforeTheSegments_AndSaysWhatToDo()
        {
            var segments = TwoSegments();
            segments[1].References = new List<EarlierTranslation>
            {
                new EarlierTranslation
                {
                    Source = Contract[1],
                    Target = "„Vertrauliche Informationen“ sind alle Informationen, die eine Partei offenlegt.",
                    Passage = "any information disclosed by one party to the …",
                    PassageWords = 15,
                }
            };

            var prompt = TranslationPrompt.BuildBatchUserPrompt(segments);
            int block = prompt.IndexOf("**EARLIER TRANSLATIONS FROM THIS DOCUMENT", StringComparison.Ordinal);
            int list = prompt.IndexOf("**SEGMENTS TO TRANSLATE", StringComparison.Ordinal);
            Assert.True(block == 0 && list > block, "the block comes first, the segments after it");

            Assert.True(prompt.Contains("Segment 2 – repeats “any information disclosed by one party to the …” (15 words)\r\n"
                + "  earlier source:      " + Contract[1] + "\r\n"
                + "  earlier translation: „Vertrauliche Informationen“ sind alle Informationen, die eine Partei offenlegt.\r\n"),
                "the entry: which segment, what it repeats, the earlier source and its translation");
            Assert.True(prompt.Contains("unless that wording is wrong for this segment"), "keep the wording unless it is wrong here");
            Assert.True(prompt.Contains("have not been reviewed"), "and says the earlier translations are unreviewed");
            Assert.True(prompt.Contains("must not be returned"), "and that the earlier segments are not to be translated");
            Assert.True(!prompt.Contains("[[TC:"), "no word about notes when there is none");
            Assert.True(prompt.EndsWith("Begin your translations now:"), "the rest of the prompt is as it was");
        }

        public static void ANoteOnTheEarlierTranslation_TravelsWithIt()
        {
            var segments = TwoSegments();
            segments[1].References = new List<EarlierTranslation>
            {
                new EarlierTranslation { Source = Contract[1], Target = "Alle Informationen. [[TC: 'orally' has no counterpart in clause 4]]" }
            };

            var block = TranslationPrompt.BuildReferenceBlock(segments);
            Assert.True(block.Contains("  earlier translation: Alle Informationen. [[TC: 'orally' has no counterpart in clause 4]]"),
                "the note is shown with the translation it is on");
            Assert.True(block.Contains("If your segment has the same problem, flag it in the same way; otherwise leave the note out."),
                "and the block says what a note is and what to do with it");
        }

        public static void TheBlockHasAFixedMaximumSize()
        {
            // Fifteen segments with two earlier translations each: thirty offered, twenty shown.
            var segments = new List<BatchSegmentInput>();
            for (int n = 1; n <= 15; n++)
                segments.Add(new BatchSegmentInput
                {
                    Number = n,
                    SourceText = "Source " + n,
                    References = new List<EarlierTranslation>
                    {
                        new EarlierTranslation { Source = "A" + n, Target = "First " + n },
                        new EarlierTranslation { Source = "B" + n, Target = "Second " + n },
                    }
                });

            var block = TranslationPrompt.BuildReferenceBlock(segments);
            int shown = block.Split(new[] { "  earlier translation: " }, StringSplitOptions.None).Length - 1;
            Assert.Equal(EarlierTranslations.MaxPerRequest, shown, "no more than the maximum in one request");
            Assert.True(block.Contains("Second 10") && !block.Contains("First 11"), "the first twenty, in the order of the segments");
        }

        public static void ALineBreakInAnEarlierText_IsASpace()
        {
            var segments = TwoSegments();
            segments[1].References = new List<EarlierTranslation>
            {
                new EarlierTranslation { Source = "Line one\r\nline two", Target = "Zeile eins\nZeile zwei" }
            };
            var block = TranslationPrompt.BuildReferenceBlock(segments);
            Assert.True(block.Contains("  earlier source:      Line one line two\r\n  earlier translation: Zeile eins Zeile zwei\r\n"),
                "each earlier text on one line");
        }

        // ---- AutoPrompt ----------------------------------------------------------------------

        public static void AutoPrompt_NoLongerTellsTheAiItSeesNothingOfEarlierRequests()
        {
            var meta = PromptGenerator.BuildMetaPrompt(new PromptGenerationContext
            {
                SourceLang = "Dutch", TargetLang = "English", DetectedDomain = "general", SegmentCount = 40,
                SourceSegments = new List<string> { "Een.", "Twee." },
            });
            Assert.True(!meta.Contains("There is no memory between requests"), "the sentence the new block would contradict is gone");
            Assert.True(meta.Contains("earlier translations from this document of a"), "and what a request can show is named");
            Assert.True(meta.Contains("never from an assumption about what an earlier batch chose"), "guessing at an earlier batch is still ruled out");
        }

        // ---- scale -----------------------------------------------------------------------------

        public static void TenThousandSegments_AreIndexedOnce_AndLookedUpInNoTime()
        {
            // 10,000 segments, 1,000 passages each in two of them. The lookup now
            // runs for every segment of every request.
            var random = new Random(20261114);
            var doc = new List<string>(10000);
            for (int i = 0; i < 10000; i++)
                doc.Add(string.Join(" ", Enumerable.Range(0, 30).Select(_ => "w" + random.Next(2000))));
            for (int p = 0; p < 1000; p++)
            {
                var passage = " " + string.Join(" ", Enumerable.Range(0, 14).Select(k => "p" + p + "x" + k));
                doc[p * 3] += passage;
                doc[9999 - p * 3] += passage + " en verder";
            }
            var targets = doc.Select(s => s.ToUpperInvariant()).ToList();

            var clock = Stopwatch.StartNew();
            var known = new EarlierTranslations(doc, RestatedPassages.Find(doc));
            long built = clock.ElapsedMilliseconds;

            clock.Restart();
            int shown = 0;
            for (int s = 0; s < doc.Count; s++)
                if (known.For(s, other => other < s ? targets[other] : null).Count > 0) shown++;
            long looked = clock.ElapsedMilliseconds;

            Assert.Equal(1000, shown, "each later segment of a pair is shown the earlier one; no earlier one is shown a later");
            Assert.True(built < 5000, "found and indexed once: " + built + " ms (limit 5,000)");
            Assert.True(looked < 2000, "10,000 lookups: " + looked + " ms (limit 2,000)");
        }
    }
}
