using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// EverTM's fuzzy score, scored the way Studio does (docs/design/evertm-fuzzy-score.md, Michael's answer 1 of 11 Nov
    /// 2026), and the two upper bounds that decide which candidates are aligned at all.
    /// </summary>
    static class ScoreTests
    {
        static List<VaultToken> Tok(string s) => VaultDiff.Tokenize(new[] { new VaultPiece { Text = s } });
        static VaultDiffResult En(string doc, string mem) => VaultDiff.Compare(Tok(doc), Tok(mem), false, VaultLanguage.Rules("en"));

        // Michael's two claim pairs (11 Nov; the text is fine to use, no client or project names). Studio: 81 and 87.
        const string ClaimDoc1 = "in response to the amount of memory bandwidth being consumed exceeding a second higher threshold, disabling, by the one or more processors, the hardware prefetcher (116) after a preconfigured time passes, and causing, by the one or more processors, the hardware prefetcher (116) to remain disabled until the memory bandwidth being consumed falls below the first lower threshold and stays below the first lower threshold for the preconfigured time;";
        const string ClaimMem1 = "in response to the amount of memory bandwidth being consumed exceeding the second higher threshold, disable the hardware prefetcher (116) after a preconfigured time passes, wherein the hardware prefetcher (116) is configured to remain disabled until the memory bandwidth being consumed falls below the first lower threshold and stays below the first lower threshold for the preconfigured time;";
        const string ClaimDoc2 = "The method of claim 8 or 9, further comprising determining, by the one or more processors, the first lower threshold and the second higher threshold based on ablation studies performed on a plurality of server machines, wherein the ablation studies use a first group of server machines running with a plurality of modulating hardware prefetchers and a second group of server machines running with a plurality of hardware prefetchers (116) that are always active.";
        const string ClaimMem2 = "determine the first lower threshold and the second higher threshold based on ablation studies performed on a plurality of server machines, wherein the ablation studies use a first group of server machines running with a plurality of modulating hardware prefetchers and a second group of server machines running with a plurality of hardware prefetchers (116) that are always active.";

        public static void MichaelsClaimPairsScoreAsInStudio()
        {
            A.Eq(81, En(ClaimDoc1, ClaimMem1).Score, "pair 1 (EverTM gave 72)");
            A.Eq(87, En(ClaimDoc2, ClaimMem2).Score, "pair 2 (EverTM gave 76)");
        }

        /// <summary>
        /// Michael's choice (11 Nov): score like Studio, but draw the differences the clearer way (VaultDisplay). The
        /// display alignment pairs "wherein" with "processors" and strikes "is configured", where the score's alignment
        /// pairs words across the rephrased stretch as Studio does; the percentage stays the score's.
        /// </summary>
        public static void TheDisplayAlignmentIsTheClearerOneAndLeavesTheScoreAlone()
        {
            var d = Tok(ClaimDoc1); var m = Tok(ClaimMem1);
            var shown = VaultDisplay.Edits(d, m);
            string Changes(IEnumerable<VaultEdit> e) => string.Join(" ", e.Where(x => x.Op == VaultEditOp.Change).Select(x => m[x.B].Text + ">" + d[x.A].Text));
            A.Eq("the>a disable>disabling wherein>processors", Changes(shown), "the changes drawn");
            A.Eq("is configured", string.Join(" ", shown.Where(x => x.Op == VaultEditOp.Insert && m[x.B].Kind == VaultTokenKind.Word).Select(x => m[x.B].Text)), "the stored words struck");
            A.Eq(81, En(ClaimDoc1, ClaimMem1).Score, "the score");
            A.True(VaultDisplay.Edits(Tok(string.Join(" ", Enumerable.Range(0, 300).Select(i => "w" + i))), m) == null, "nothing drawn for a segment over 400 tokens");
        }

        public static void ErrorsCountAgainstBothSegments()
        {
            // One word in ten changed: 1.5 against 20 words and 18 spaces (21.8) is 6 points off.
            A.Eq(94, VaultDiff.Compare(Tok("one two three four five six seven eight nine ten"), Tok("one two three four five six seven eight nine eleven"), false).Score, "one word in ten");
        }

        static double ChargeOf(VaultDiffResult r, VaultEditOp op) => r.Edits.Where(e => e.Op == op).Sum(e => e.Cost);

        public static void NearlyTheSameWordIsASmallChange()
        {
            A.Eq(0.2, ChargeOf(En("Then disabling the pump stops the flow.", "Then disable the pump stops the flow."), VaultEditOp.Change), "the same stem");
            A.Eq(0.2, ChargeOf(En("The pump stops the flow quickly.", "the pump stops the flow quickly."), VaultEditOp.Change), "a capital letter");
            A.Eq(0.2, ChargeOf(En("The pump stops the flow at the café.", "The pump stops the flow at the cafe."), VaultEditOp.Change), "an accent");
            A.Eq(0.7, ChargeOf(En("The pump stops the flow of colour.", "The pump stops the flow of color."), VaultEditOp.Change), "a word that looks alike");
            A.Eq(1.5, ChargeOf(En("The pump stops the flow of water.", "The pump stops the flow of oil."), VaultEditOp.Change), "another word");
            A.Eq(0.1, ChargeOf(En("The pump stops; the flow ends.", "The pump stops, the flow ends."), VaultEditOp.Change), "another punctuation mark");
        }

        public static void MissingWordsChargeByLength()
        {
            var r = En("The pump stops a flow of water now.", "The pump stops flow of water now.");
            A.Eq(0.7 + 0.1, r.Edits.Where(e => e.Op == VaultEditOp.Delete).Sum(e => e.Cost), "\"a\" (a word of 1-2 letters) and its space");
            r = En("The pump stops the flow of water now.", "The pump stops the flow of clean water now.");
            A.Eq(1.5 + 0.1, r.Edits.Where(e => e.Op == VaultEditOp.Insert).Sum(e => e.Cost), "\"clean\" and its space");
        }

        public static void AMovedWordIsOneMove()
        {
            var r = En("Slowly the pump stops the flow of water.", "The pump stops the flow of water slowly.");
            A.True(r.Edits.Any(e => e.Op == VaultEditOp.Move), "\"slowly\" moved: " + string.Join(" ", r.Edits.Where(e => e.Op != VaultEditOp.Identity).Select(e => e.Op + "" + e.A + ">" + e.B)));
            A.Eq(r.Distance, r.Edits.Sum(e => e.Cost), "the distance is the sum of the charges");
        }

        public static void AWordIsNeverChangedIntoPunctuation()
        {
            foreach (var (doc, mem) in new[] { (ClaimDoc1, ClaimMem1), (ClaimDoc2, ClaimMem2), ("further comprising determining, by the processors, the first threshold", "determine the first threshold") })
            {
                var d = Tok(doc); var m = Tok(mem);
                var r = VaultDiff.Compare(d, m, false, VaultLanguage.Rules("en"));
                var mixed = r.Edits.Where(e => e.Op == VaultEditOp.Change && (d[e.A].Kind == VaultTokenKind.Word) != (m[e.B].Kind == VaultTokenKind.Word)).ToList();
                A.Eq(0, mixed.Count, "word/punctuation changes in \"" + mem.Substring(0, 20) + "...\"");
            }
            var x = Tok("further comprising determining, by the processors, the first threshold"); var y = Tok("determine the first threshold");
            var ch = VaultDiff.Compare(x, y, false, VaultLanguage.Rules("en")).Edits.Where(e => e.Op == VaultEditOp.Change).Select(e => y[e.B].Text + ">" + x[e.A].Text);
            A.Eq("determine>determining", string.Join(" ", ch), "changes");
        }

        public static void StopwordsOnlyEverLowerAScore()
        {
            // Mostly stopwords in common: the shared words say little, so a few points come off.
            const string doc = "the pump of the valve in the tank", mem = "the hose of the motor in the shed";   // shared: only stopwords
            var withRules = En(doc, mem).Score;
            var without = VaultDiff.Compare(Tok(doc), Tok(mem), false, new VaultWordRules { Stem = VaultLanguage.Rules("en").Stem }).Score;
            A.True(withRules < without, $"stopwords in common take points off: {withRules} against {without}");
        }

        public static void NoRulesForAnUnknownLanguage()
        {
            A.True(VaultLanguage.Rules("xx") == null && VaultLanguage.Rules("") == null && VaultLanguage.Rules(null) == null, "no rules");
            A.True(VaultLanguage.Rules("nl-BE") != null && VaultLanguage.Rules("pt-BR") != null && VaultLanguage.Rules("nb-NO") != null, "variants fold into the base language");
            A.Eq("fiets", VaultLanguage.Rules("nl").Stem("fietsen"), "Dutch stem");
            A.True(VaultLanguage.Rules("de").IsStopword("und") && !VaultLanguage.Rules("de").IsStopword("pumpe"), "German stopwords");
            // Without rules nothing is stemmed: "disabling"/"disable" is a full change, which only ever lowers the score.
            int none = VaultDiff.Compare(Tok(ClaimDoc1), Tok(ClaimMem1), false).Score;
            A.True(none <= 81, $"without rules: {none}");
        }

        public static void RulesCanBeMadeOnManyThreadsAtOnce()
        {
            var errors = 0;
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                try
                {
                    var r = VaultLanguage.Rules(i % 2 == 0 ? "en" : "nl");
                    for (int k = 0; k < 200; k++)
                        if (r.Stem(i % 2 == 0 ? "disabling" : "fietsen") != (i % 2 == 0 ? "disabl" : "fiets") || !r.IsStopword(i % 2 == 0 ? "the" : "de"))
                            System.Threading.Interlocked.Increment(ref errors);
                }
                catch { System.Threading.Interlocked.Increment(ref errors); }
            });
            A.Eq(0, errors, "wrong stems or stopwords");
        }

        public static void SoftHyphensDontSplitWords()
        {
            var t = Tok("elec­tric pump​ station");
            A.Eq("electric|pump|station", string.Join("|", t.Where(x => x.Kind == VaultTokenKind.Word).Select(x => x.Text)), "words");
            A.Eq(VaultDiff.Words(VaultMarkup.Normalize("elec­tric pump​ station")).Count, t.Count(x => x.Kind == VaultTokenKind.Word), "the same count as the index's");
        }

        public static void LongSegmentsScoreOnWhatTheyShare()
        {
            var a = string.Join(" ", Enumerable.Range(0, 300).Select(i => "w" + i));
            var b = string.Join(" ", Enumerable.Range(0, 300).Select(i => i == 7 ? "x" : "w" + i));
            var r = VaultDiff.Compare(Tok(a), Tok(b), false);
            A.True(r.Edits == null, "too long to align");
            A.Eq(99, r.Score, "299 of 300 words in common");
            var c = string.Join(" ", Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? "x" + i : "w" + i));
            int half = VaultDiff.Compare(Tok(a), Tok(c), false).Score;
            A.True(half > 0 && half < 70, $"half the words different: {half}");
        }

        // ---- the upper bounds ----

        static readonly string[] Vocabulary = ("the a pump valve stops starts flow of water oil and or by one more processors " +
            "disable disabling disabled threshold second first higher lower time preconfigured 116 8 9 2024 3.5 , ; ( ) . : " +
            "colour color Pump THE café cafe is configured to remain until").Split(' ');

        static string Sentence(Random rnd, int words)
        {
            var parts = new List<string>();
            for (int i = 0; i < words; i++) parts.Add(Vocabulary[rnd.Next(Vocabulary.Length)]);
            return string.Join(rnd.Next(10) == 0 ? "  " : " ", parts);
        }

        static string Mutate(Random rnd, string s)
        {
            var w = s.Split(' ').ToList();
            int edits = rnd.Next(0, 1 + w.Count / 2);
            for (int e = 0; e < edits && w.Count > 1; e++)
            {
                int i = rnd.Next(w.Count);
                switch (rnd.Next(5))
                {
                    case 0: w.RemoveAt(i); break;
                    case 1: w.Insert(i, Vocabulary[rnd.Next(Vocabulary.Length)]); break;
                    case 2: w[i] = Vocabulary[rnd.Next(Vocabulary.Length)]; break;
                    case 3: var x = w[i]; w.RemoveAt(i); w.Insert(rnd.Next(w.Count + 1), x); break;   // a move
                    default: w[i] = w[i].ToUpperInvariant(); break;
                }
            }
            return string.Join(" ", w);
        }

        /// <summary>A document as pieces, with a bold tag pair around one word or inside it (both happen).</summary>
        static List<VaultPiece> WithTags(Random rnd, string s)
        {
            if (rnd.Next(3) != 0 || s.Length < 4) return new List<VaultPiece> { new VaultPiece { Text = s } };
            int at = rnd.Next(1, s.Length - 2), end = Math.Min(s.Length - 1, at + rnd.Next(1, 6));
            return new List<VaultPiece>
            {
                new VaultPiece { Text = s.Substring(0, at) }, new VaultPiece { Kind = VaultTagKind.Open, Name = "b", Key = "1", Raw = "<b>" },
                new VaultPiece { Text = s.Substring(at, end - at) }, new VaultPiece { Kind = VaultTagKind.Close, Name = "b", Key = "1", Raw = "</b>" },
                new VaultPiece { Text = s.Substring(end) },
            };
        }

        /// <summary>
        /// The estimates must never be below the real score, or a match that reaches the minimum could be dropped before
        /// it is aligned. 20,000 random pairs (words dropped, added, changed, moved, capitalised; tags inside words; long
        /// ones too), and Michael's claim pairs.
        /// </summary>
        public static void EstimatesAreNeverBelowTheScore()
        {
            var rnd = new Random(20261111);
            var rules = VaultLanguage.Rules("en");
            int checkedPairs = 0, worstGap = int.MaxValue;
            string firstFail = null;
            var pairs = new List<(List<VaultPiece> doc, List<VaultPiece> mem)>
            {
                (new List<VaultPiece> { new VaultPiece { Text = ClaimDoc1 } }, new List<VaultPiece> { new VaultPiece { Text = ClaimMem1 } }),
                (new List<VaultPiece> { new VaultPiece { Text = ClaimDoc2 } }, new List<VaultPiece> { new VaultPiece { Text = ClaimMem2 } }),
            };
            for (int i = 0; i < 20000; i++)
            {
                var s = Sentence(rnd, i % 50 == 0 ? rnd.Next(150, 260) : rnd.Next(1, 40));
                pairs.Add((WithTags(rnd, s), WithTags(rnd, Mutate(rnd, s))));
            }
            foreach (var (doc, mem) in pairs)
            {
                var d = VaultDiff.Tokenize(doc); var m = VaultDiff.Tokenize(mem);
                bool identical = VaultLookup.PlainOf(doc) == VaultLookup.PlainOf(mem);
                int score = VaultDiff.Compare(d, m, identical, rules).Score;
                int estimate = VaultDiff.Estimate(d, m, identical);
                int byLength = identical ? 100 : new VaultLengthBound(d).MaxScore(VaultDiff.Words(VaultLookup.PlainOf(mem)).Count);
                checkedPairs++;
                worstGap = Math.Min(worstGap, Math.Min(estimate, byLength) - score);
                if ((estimate < score || byLength < score) && firstFail == null)
                    firstFail = $"score {score}, estimate {estimate}, by length {byLength}: \"{VaultLookup.PlainOf(doc)}\" / \"{VaultLookup.PlainOf(mem)}\"";
            }
            A.True(firstFail == null, "an estimate below the score: " + firstFail);
            Console.WriteLine($"      ({checkedPairs:N0} pairs; the estimates are at least the score, closest {worstGap} points above)");
        }

        public static void TheLengthBoundLetsMoreThroughThanBefore()
        {
            // The old rule (min/max words + 10) dropped a 10-word unit against a 20-word segment at a 70% minimum; now
            // that can score up to 79, so it is read.
            var bound = new VaultLengthBound(Tok("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty"));
            A.True(bound.MaxScore(10) >= 70, "a 10-word unit can reach 70: " + bound.MaxScore(10));
            A.True(bound.MaxScore(2) < 70, "a 2-word unit can't: " + bound.MaxScore(2));
        }

        public static void ScoringStaysFast()
        {
            var rules = VaultLanguage.Rules("en");
            var d = Tok(ClaimDoc1); var m = Tok(ClaimMem1);
            VaultDiff.Compare(d, m, false, rules);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) VaultDiff.Compare(d, m, false, rules);
            double ms = sw.Elapsed.TotalMilliseconds / 200;
            var big = Tok(string.Join(" ", Enumerable.Range(0, 199).Select(i => "word" + (i % 50))) + ".");
            var big2 = Tok(string.Join(" ", Enumerable.Range(0, 199).Select(i => "word" + ((i * 7) % 50))) + ".");
            sw.Restart();
            for (int i = 0; i < 20; i++) VaultDiff.Compare(big, big2, false, rules);
            double msBig = sw.Elapsed.TotalMilliseconds / 20;
            // The display alignment, run for each row handed to Studio only.
            VaultDisplay.Edits(d, m);
            sw.Restart();
            for (int i = 0; i < 200; i++) VaultDisplay.Edits(d, m);
            double msShow = sw.Elapsed.TotalMilliseconds / 200;
            sw.Restart();
            for (int i = 0; i < 20; i++) VaultDisplay.Edits(big, big2);
            double msShowBig = sw.Elapsed.TotalMilliseconds / 20;
            Console.WriteLine($"      (score: a claim pair {ms:F2} ms, two 400-token segments {msBig:F1} ms; display: {msShow:F2} ms and {msShowBig:F1} ms a row)");
            // Limits that catch a slip of an order of magnitude, also in an unoptimised 32-bit Debug build (where a
            // 400-token row takes about 60 ms; a 64-bit Release build about 10).
            A.True(ms < 15 && msBig < 200 && msShow < 15 && msShowBig < 200, $"{ms:F2} ms, {msBig:F1} ms, {msShow:F2} ms, {msShowBig:F1} ms");
        }
    }
}
