using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The finder for restated passages and its report (docs/design/restated-passages.md):
    /// a description sentence that repeats a claim inside a longer sentence, and
    /// came back from Batch Translate worded differently 300 segments later. The
    /// document is invented (Acme / PROJ-001); the real job was measured apart
    /// and only its counts are recorded.
    /// </summary>
    [Tests]
    internal static class RestatedPassagesTests
    {
        // ---- the invented document ---------------------------------------------

        private const string Claim =
            "Inrichting volgens conclusie 1, waarbij de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld.";

        private const string Description =
            "In een voorkeursuitvoering van de inrichting van Acme is voorzien dat de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld, zodat de klep vrij blijft.";

        /// <summary>The description with one word of the passage changed: lengteas -> dwarsas.</summary>
        private const string DescriptionOtherAxis =
            "In een voorkeursuitvoering van de inrichting van Acme is voorzien dat de klemarm in een richting loodrecht op de dwarsas van het huis beweegbaar is opgesteld, zodat de klep vrij blijft.";

        private const string ClaimEn =
            "Device according to claim 1, wherein the clamping arm is arranged to be movable in a direction perpendicular to the longitudinal axis of the housing.";

        private const string DescriptionEn =
            "In a preferred embodiment of the Acme device, provision is made that the clamping arm is arranged to be movable in a direction perpendicular to the longitudinal axis of the housing, so that the valve remains free.";

        /// <summary>The same, with "perpendicular direction to" for "direction perpendicular to".</summary>
        private const string DescriptionEnReworded =
            "In a preferred embodiment of the Acme device, provision is made that the clamping arm is arranged to be movable in a perpendicular direction to the longitudinal axis of the housing, so that the valve remains free.";

        private const string DescriptionEnOtherAxis =
            "In a preferred embodiment of the Acme device, provision is made that the clamping arm is arranged to be movable in a direction perpendicular to the transverse axis of the housing, so that the valve remains free.";

        private static readonly string[] Filler =
        {
            "De uitvinding heeft betrekking op een inrichting voor het vastzetten van een klep.",
            "Figuur 1 toont een aanzicht van de inrichting.",
            "Het huis is vervaardigd uit staal.",
        };

        /// <summary>Filler, the claim, filler, and the given sentence: the claim is segment 3, the other segment 6.</summary>
        private static List<string> Document(string other)
        {
            var doc = new List<string>();
            doc.AddRange(Filler);
            doc.Add(Claim);
            doc.Add("De klep wordt daarna gesloten.");
            doc.Add("Dit is in figuur 2 weergegeven.");
            doc.Add(other);
            return doc;
        }

        private static RestatedTarget Target(int i, string text) =>
            new RestatedTarget { Label = "segment " + (i + 1), Text = text };

        /// <summary>Targets for <see cref="Document"/>: the filler translated, the claim and the other as given.</summary>
        private static List<RestatedTarget> Targets(string claim, string other)
        {
            var texts = new[]
            {
                "The invention relates to a device for securing a valve.",
                "Figure 1 shows a view of the device.",
                "The housing is made of steel.",
                claim,
                "The valve is then closed.",
                "This is shown in Figure 2.",
                other,
            };
            return texts.Select((t, i) => Target(i, t)).ToList();
        }

        private static string[] Lines(string summary) =>
            summary == null ? new string[0] : summary.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

        // ---- the finder --------------------------------------------------------

        public static void AClaimRestatedInsideALongerSentence_IsFound()
        {
            var found = RestatedPassages.Find(Document(Description));

            Assert.Equal(1, found.Pairs.Count, "one pair in the document");
            var pair = found.Pairs[0];
            Assert.Equal(3, pair.A, "the claim");
            Assert.Equal(6, pair.B, "the description sentence");
            Assert.Equal(1, pair.Runs.Count, "one unbroken run");
            Assert.Equal(15, pair.Runs[0].Length, "de klemarm ... is opgesteld: 15 words");
            Assert.Equal(5, pair.Runs[0].StartA, "where it sits in the claim: after 'Inrichting volgens conclusie 1 waarbij'");
            Assert.Equal(11, pair.Runs[0].StartB, "where it sits in the description");
            Assert.Equal(0, pair.Breaks, "no difference inside it");
            Assert.True(!pair.NearTwin, "a clause inside a longer sentence is not a near-twin");
            Assert.Equal("de klemarm in een richting loodrecht op de …", pair.Passage, "the passage as the log quotes it");
            Assert.Equal(15, pair.PassageWords, "and its length");
        }

        public static void OneWordChanged_IsStillFound_ThroughAShorterRun()
        {
            var found = RestatedPassages.Find(Document(DescriptionOtherAxis));

            Assert.Equal(1, found.Pairs.Count, "still a pair");
            var pair = found.Pairs[0];
            Assert.Equal(2, pair.Runs.Count, "the passage in two runs, either side of the changed word");
            Assert.Equal(8, pair.Runs[0].Length, "de klemarm in een richting loodrecht op de: 8 words, just enough");
            Assert.Equal(6, pair.Runs[1].Length, "van het huis beweegbaar is opgesteld");
            Assert.Equal(1, pair.Passages, "one passage, not two");
            Assert.Equal(1, pair.InteriorBreaks, "broken once, inside");
        }

        public static void ARunOneWordShortOfTheMinimum_IsNotAPair()
        {
            // Seven words in common: under MinRunWords, however alike the sentences look.
            var found = RestatedPassages.Find(new[]
            {
                "De klemarm is op het huis gemonteerd met een bout.",
                "Hierbij geldt dat de klemarm is op het huis gemonteerd door middel van lijm.",
            });
            Assert.Equal(0, found.Pairs.Count, "seven shared words is not a restated passage");
        }

        public static void ABoilerplatePhraseIn200Segments_PairsNothing()
        {
            // Every dependent claim opens the same way. Without the cap that is
            // 200 x 199 / 2 = 19,900 pairs, each a line in the log.
            var doc = new List<string>();
            for (int i = 0; i < 200; i++)
                doc.Add("Inrichting volgens een der voorgaande conclusies, met het kenmerk, dat onderdeel "
                    + Word(i) + " van " + Word(i + 500) + " is.");

            var clock = Stopwatch.StartNew();
            var found = RestatedPassages.Find(doc);
            clock.Stop();

            Assert.Equal(0, found.Pairs.Count, "boilerplate alone pairs nothing");
            Assert.True(!found.Limited, "and the pair limit was never reached");
            Assert.True(clock.ElapsedMilliseconds < 2000, "no blow-up: " + clock.ElapsedMilliseconds + " ms");
        }

        public static void TwoSegmentsBehindTheSameBoilerplate_StillPairOnTheirOwnWords()
        {
            var doc = new List<string>();
            for (int i = 0; i < 200; i++)
                doc.Add("Inrichting volgens een der voorgaande conclusies, met het kenmerk, dat onderdeel "
                    + Word(i) + " van " + Word(i + 500) + " is.");
            const string passage = " waarbij de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld.";
            doc[20] += passage;
            doc[170] += passage;

            var found = RestatedPassages.Find(doc);

            Assert.Equal(1, found.Pairs.Count, "the two that share a passage of their own");
            Assert.Equal(20, found.Pairs[0].A, "the first");
            Assert.Equal(170, found.Pairs[0].B, "the second");
        }

        public static void TagsInsideThePassage_AndCase_DecideNothing()
        {
            // The claim with "klemarm" in bold, a tag in the middle of a word, a
            // standalone tag between two words, and the passage in capitals.
            var tagged = RestatedPassages.PlainText(new[]
            {
                RepetitionToken.Text("Inrichting volgens conclusie 1, waarbij DE "),
                RepetitionToken.Open("b"),
                RepetitionToken.Text("KLEMARM"),
                RepetitionToken.Close("b"),
                RepetitionToken.Text(" in een rich"),
                RepetitionToken.Standalone("ph"),
                RepetitionToken.Text("ting loodrecht op de"),
                RepetitionToken.Standalone("ph"),
                RepetitionToken.Text(" lengteas van het huis beweegbaar is opgesteld."),
            });

            var doc = Document(Description);
            doc[3] = tagged;
            var found = RestatedPassages.Find(doc);

            Assert.Equal(1, found.Pairs.Count, "the tagged claim still pairs with the description");
            Assert.Equal(15, found.Pairs[0].Runs[0].Length, "over the same 15 words");
            Assert.Equal("", RestatedPassages.PlainText(null), "no tokens, no text");
        }

        public static void NoSegments_OneSegment_AndEmptyOnes_FindNothing()
        {
            Assert.Equal(0, RestatedPassages.Find(null).Pairs.Count, "null");
            Assert.Equal(0, RestatedPassages.Find(new string[0]).Pairs.Count, "no segments");
            Assert.Equal(0, RestatedPassages.Find(new[] { Claim }).Pairs.Count, "one segment");
            Assert.Equal(0, RestatedPassages.Find(new[] { null, "", "   ", null }).Pairs.Count, "nothing in any of them");
            Assert.Equal(1, RestatedPassages.Find(new[] { null, Claim, "", Description }).Pairs.Count,
                "empty segments between two that pair do no harm");
        }

        public static void APassageRepeatedInsideOneSegment_IsNotAPairWithItself()
        {
            var found = RestatedPassages.Find(new[] { Claim + " " + Claim, "Het huis is vervaardigd uit staal." });
            Assert.Equal(0, found.Pairs.Count, "a pair is two segments");
        }

        public static void ASegmentOverTheWordLimit_IsLeftOut_AndCounted()
        {
            var huge = new StringBuilder(Claim);
            for (int i = 0; i < RestatedPassages.MaxWords; i++) huge.Append(' ').Append(Word(i));

            var found = RestatedPassages.Find(new[] { huge.ToString(), Description, Claim });

            Assert.Equal(1, found.TooLong, "one segment too long to compare");
            Assert.Equal(1, found.Pairs.Count, "the other two still pair");
            Assert.Equal(1, found.Pairs[0].A, "the description");
            Assert.Equal(2, found.Pairs[0].B, "the claim");
        }

        public static void BoilerplateAndOneWordInCommon_IsNotAPair()
        {
            // Twenty dependent claims open with the same formula; two of them
            // happen to go on with the same word. Eleven words in a row, one of
            // them their own.
            var doc = new List<string>();
            for (int i = 0; i < 20; i++)
                doc.Add("Inrichting volgens een der voorgaande conclusies, met het kenmerk, dat "
                    + (i == 3 || i == 12 ? "klemarm" : Word(i)) + " " + Word(i + 100) + " " + Word(i + 200) + " is.");

            Assert.Equal(0, RestatedPassages.Find(doc).Pairs.Count, "the formula and one word are not a restated passage");
        }

        public static void MoreCandidatePairsThanTheLimit_StopsThere_AndSaysSo()
        {
            // Ten passages, each in two segments: ten pairs, limit five.
            var doc = new List<string>();
            for (int p = 0; p < 10; p++)
            {
                var passage = string.Join(" ", Enumerable.Range(p * 20, 12).Select(Word));
                doc.Add("Eerst " + passage + ".");
                doc.Add("Daarna geldt " + passage + " ook.");
            }

            var limited = RestatedPassages.Find(doc, 5);
            Assert.True(limited.Limited, "says it stopped");
            Assert.Equal(5, limited.Pairs.Count, "at the limit");

            var all = RestatedPassages.Find(doc, 10);
            Assert.True(!all.Limited, "exactly at the limit is not over it");
            Assert.Equal(10, all.Pairs.Count, "all ten");
        }

        public static void TenThousandSegments_AreSearchedInSeconds()
        {
            // 10,000 segments of 30 words from a vocabulary of 2,000, with 300
            // passages planted twice each and a boilerplate opening on every
            // tenth segment. The second document is the worst real shape: a
            // text followed by a copy of itself, so every segment is a twin.
            var random = new Random(20261114);
            var doc = new List<string>(10000);
            for (int i = 0; i < 10000; i++)
            {
                var text = new StringBuilder();
                if (i % 10 == 0) text.Append("Inrichting volgens een der voorgaande conclusies met het kenmerk dat ");
                for (int w = 0; w < 30; w++) text.Append(Word(random.Next(2000))).Append(' ');
                doc.Add(text.ToString());
            }
            for (int p = 0; p < 300; p++)
            {
                var passage = " " + string.Join(" ", Enumerable.Range(0, 14).Select(_ => Word(2000 + random.Next(2000))));
                doc[p * 3 + 1] += passage;
                doc[9999 - p * 3] += passage + " en verder niets";
            }

            var clock = Stopwatch.StartNew();
            var found = RestatedPassages.Find(doc);
            clock.Stop();
            Assert.Equal(300, found.Pairs.Count, "every planted passage, and nothing else");
            Assert.True(clock.ElapsedMilliseconds < 5000, "10,000 segments: " + clock.ElapsedMilliseconds + " ms (limit 5,000)");

            var doubled = new List<string>(doc.Take(5000));
            doubled.AddRange(doc.Take(5000));
            var targets = doubled.Select((s, i) => Target(i, s)).ToList();
            clock.Restart();
            var report = RestatedPassages.Report(doubled, targets);
            clock.Stop();
            Assert.True(report.Compared >= 5000, "every segment compared with its copy (" + report.Compared + " pairs)");
            Assert.Equal(0, report.Different.Count, "and none differs from its copy");
            Assert.True(clock.ElapsedMilliseconds < 10000, "a document and its copy, found and compared: "
                + clock.ElapsedMilliseconds + " ms (limit 10,000)");
        }

        // ---- the report ----------------------------------------------------------

        public static void SameTranslations_GiveNoLine()
        {
            var report = RestatedPassages.Report(Document(Description), Targets(ClaimEn, DescriptionEn));

            Assert.Equal(1, report.Compared, "the pair was compared");
            Assert.Equal(0, report.Different.Count, "and translated the same way");
            Assert.True(report.Summary() == null, "so there is nothing to say");
        }

        public static void DifferentTranslations_GiveOneLine()
        {
            var report = RestatedPassages.Report(Document(Description), Targets(ClaimEn, DescriptionEnReworded));

            Assert.Equal(1, report.Different.Count, "one passage translated differently");
            var lines = Lines(report.Summary());
            Assert.Equal(2, lines.Length, "the count and the case");
            Assert.Equal("1 translated differently (listed).", lines[0], "the count");
            Assert.Equal("Translated differently: segment 4 and segment 7 – source “de klemarm in een richting loodrecht op de …” (15 words the same)",
                lines[1], "the case, by Studio's numbers, with the source wording to search for");
        }

        public static void ADifferenceNearTheEndOfThePassage_IsReportedToo()
        {
            // "the housing" / "the casing", with three shared words after it. The
            // sources share those too ("bij normaal gebruik").
            var sources = Document(Description.Replace("is opgesteld, zodat", "is opgesteld bij normaal gebruik, zodat"));
            sources[3] = Claim.Replace("is opgesteld.", "is opgesteld bij normaal gebruik.");
            var claim = ClaimEn.Replace("of the housing.", "of the housing during normal use.");
            var description = DescriptionEn.Replace("of the housing, so", "of the casing during normal use, so");

            var report = RestatedPassages.Report(sources, Targets(claim, description));
            Assert.Equal(1, report.Different.Count, "a different word with shared wording after it is a difference");
        }

        public static void ADifferenceTheSourcesHaveToo_IsNotReported()
        {
            // The description says "dwarsas" where the claim says "lengteas", and the
            // translations differ in exactly that word.
            var report = RestatedPassages.Report(Document(DescriptionOtherAxis), Targets(ClaimEn, DescriptionEnOtherAxis));

            Assert.Equal(1, report.Compared, "compared");
            Assert.Equal(0, report.Different.Count, "the sources differ there, so the translations may");
            Assert.True(report.Summary() == null, "nothing to say");
        }

        public static void TwoChangedWordsWithAnArticleBetweenThemInTheTranslation_AreOnePlace()
        {
            // "zonder lamp" / "met schuif" is one place in the sources. English puts
            // "the" between the two changed words, which must not make it two: a pair
            // translated exactly alike was reported as different (15 Nov 2026).
            var sources = new List<string>
            {
                "Het gewicht van de houder zonder lamp bedraagt maximaal 120 gram en de breedte van de houder is kleiner dan de lengte van de stang.",
                "Het gewicht van de houder met schuif bedraagt maximaal 125 gram en de breedte van de houder is kleiner dan de lengte van de stang.",
            };
            const string first = "The weight of the holder without the lamp is at most 120 grams and the width of the holder is smaller than the length of the rod.";
            const string second = "The weight of the holder with the slide is at most 125 grams and the width of the holder is smaller than the length of the rod.";

            var report = RestatedPassages.Report(sources, new List<RestatedTarget> { Target(0, first), Target(1, second) });
            Assert.Equal(1, report.Compared, "compared");
            Assert.Equal(0, report.Different.Count, "the translations differ only where the sources do");

            // A difference of the translator's own, further on, is still reported.
            var reworded = second.Replace("is smaller than", "is less than");
            report = RestatedPassages.Report(sources, new List<RestatedTarget> { Target(0, first), Target(1, reworded) });
            Assert.Equal(1, report.Different.Count, "more changed words than the sources have is a difference");
        }

        public static void ADifferenceTheSourcesHaveAtTheEdge_DoesNotExcuseOneInTheMiddle()
        {
            // The sources differ in one word (lengteas / dwarsas, inside); the
            // translations differ there and are reworded a second time.
            var twice = DescriptionEnOtherAxis.Replace("is arranged to be movable in a direction", "is movably arranged in a direction");
            var report = RestatedPassages.Report(Document(DescriptionOtherAxis), Targets(ClaimEn, twice));
            Assert.Equal(1, report.Different.Count, "two differences where the sources have one");
        }

        public static void APassageTranslatedQuiteDifferently_IsReported()
        {
            var other = "In a preferred embodiment of the Acme device, the gripper can travel at right angles to the long axis of the casing, so that the valve remains free.";
            var report = RestatedPassages.Report(Document(Description), Targets(ClaimEn, other));
            Assert.Equal(1, report.Different.Count, "hardly a word in common");
        }

        public static void IdenticalSources_MustHaveIdenticalWords()
        {
            var sources = new[] { Claim, "Het huis is vervaardigd uit staal.", Claim };
            var same = new List<RestatedTarget> { Target(0, ClaimEn), Target(1, "The housing is made of steel."), Target(2, ClaimEn.ToUpperInvariant()) };
            Assert.Equal(0, RestatedPassages.Report(sources, same).Different.Count, "capitals are not a difference");

            // The last word differs: no shared wording follows it, so only the exact rule sees it.
            var differs = new List<RestatedTarget> { Target(0, ClaimEn), Target(1, "The housing is made of steel."), Target(2, ClaimEn.Replace("housing.", "casing.")) };
            Assert.Equal(1, RestatedPassages.Report(sources, differs).Different.Count, "one word differs in a repeated segment");
        }

        public static void ATwinWithANoteOnOnlyOne_GivesNoLine()
        {
            // The same sentence but for a reference numeral; the AI flagged the
            // source in the first and said nothing about the second. That is not
            // reported: the note line was built and dropped (15 Nov 2026).
            var sources = new[]
            {
                "De borgpen 12 wordt in de uitsparing 14 van de flens 16 geplaatst en daarna vastgezet met de moer.",
                "Het huis is vervaardigd uit staal.",
                "De borgpen 22 wordt in de uitsparing 14 van de flens 16 geplaatst en daarna vastgezet met de moer.",
            };
            var targets = new List<RestatedTarget>
            {
                Target(0, "The locking pin 12 is placed in the recess 14 of the flange 16 and then secured with the nut. [[TC: the figures show no flange 16]]"),
                Target(1, "The housing is made of steel."),
                Target(2, "The locking pin 22 is placed in the recess 14 of the flange 16 and then secured with the nut."),
            };

            var report = RestatedPassages.Report(sources, targets);

            Assert.Equal(1, report.Compared, "the twins are compared");
            Assert.Equal(0, report.Different.Count, "the translations differ only where the sources do (de borgpen 12 / 22); the note is not compared");
            Assert.True(report.Summary() == null, "a note on only one of the two is nothing to say");
        }

        public static void ANoteOnAClauseInALongerSentence_IsNotPartOfTheTranslationCompared()
        {
            var targets = Targets(ClaimEn + " [[TC: 'huis' may be a typo]]", DescriptionEn);
            var report = RestatedPassages.Report(Document(Description), targets);
            Assert.Equal(1, report.Compared, "compared");
            Assert.Equal(0, report.Different.Count, "the note is not part of the translation compared");
            Assert.True(report.Summary() == null, "nothing to say");
        }

        public static void AnEmptyOrFailedTarget_IsSkipped_NeverADifference()
        {
            foreach (var missing in new[] { null, "", "   " })
            {
                var report = RestatedPassages.Report(Document(Description), Targets(ClaimEn, missing));
                Assert.Equal(0, report.Different.Count, "no translation is not a different translation");
                Assert.Equal(0, report.Compared, "not compared");
                Assert.Equal(1, report.Skipped, "skipped, and counted as such");
                Assert.True(report.Summary() == null, "nothing to say");
            }

            var nulls = Targets(ClaimEn, DescriptionEn);
            nulls[6] = null;
            Assert.Equal(1, RestatedPassages.Report(Document(Description), nulls).Skipped, "a segment with no target entry at all");
        }

        public static void SourcesAndTargetsOfDifferentLengths_AreRefused()
        {
            bool threw = false;
            try { RestatedPassages.Report(Document(Description), new List<RestatedTarget> { Target(0, "x") }); }
            catch (ArgumentException) { threw = true; }
            Assert.True(threw, "seven sources and one target is a caller's mistake, not a report");

            Assert.True(RestatedPassages.Report(null, null).Summary() == null, "nothing given, nothing to say");
        }

        public static void ALongList_IsCutOff_AndSaysHowManyMore()
        {
            // 25 passages, each in two segments and each translated two ways.
            var sources = new List<string>();
            var targets = new List<RestatedTarget>();
            for (int p = 0; p < 25; p++)
            {
                var passage = string.Join(" ", Enumerable.Range(p * 20, 12).Select(Word));
                sources.Add("Eerst " + passage + ".");
                sources.Add("Daarna geldt " + passage + " ook.");
                var english = Enumerable.Range(p * 20, 12).Select(i => "t" + Word(i)).ToList();
                targets.Add(Target(p * 2, "First " + string.Join(" ", english) + "."));
                english.Reverse(4, 4);
                targets.Add(Target(p * 2 + 1, "Then " + string.Join(" ", english) + " too."));
            }

            var report = RestatedPassages.Report(sources, targets);
            Assert.Equal(25, report.Different.Count, "all 25 found");

            var lines = Lines(report.Summary());
            Assert.Equal(1 + RestatedPassages.MaxListed + 1, lines.Length, "the count, 20 cases, and one line for the rest");
            Assert.Equal("25 translated differently (listed).", lines[0], "the count is the whole count");
            Assert.Equal("… and 5 more translated differently, not listed.", lines[lines.Length - 1], "the rest");
        }

        /// <summary>An invented word for a number: distinct numbers give distinct words.</summary>
        private static string Word(int n)
        {
            const string consonants = "bdfghklmnprstvz", vowels = "aeiou";
            var word = new StringBuilder();
            int x = n;
            do
            {
                word.Append(consonants[x % consonants.Length]);
                x /= consonants.Length;
                word.Append(vowels[x % vowels.Length]);
                x /= vowels.Length;
            } while (x > 0);
            return word.Append('x').ToString();
        }
    }
}
