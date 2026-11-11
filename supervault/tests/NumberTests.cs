using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// Replacing a different number in EverTM's proposal (Michael's decision 2, 11 Nov 2026): only when it can't go
    /// wrong. Every case that must NOT replace is here as well as the ones that must.
    /// </summary>
    static class NumberTests
    {
        static List<VaultPiece> P(string s) => new List<VaultPiece> { new VaultPiece { Text = s } };

        /// <summary>The proposal after replacing, or null when nothing may be replaced.</summary>
        static string Swap(string doc, string source, string target)
        {
            var plan = VaultNumbers.Make(P(doc), P(source), P(target));
            return plan == null ? null : string.Concat(VaultNumbers.Apply(plan, P(target)).Select(p => p.Text));
        }

        public static void ADifferentNumberIsReplaced()
        {
            A.Eq("De werkwijze van conclusie 9.", Swap("The method of claim 9.", "The method of claim 8.", "De werkwijze van conclusie 8."), "claim number");
            A.Eq("de prefetcher (118) wordt", Swap("the prefetcher (118) is", "the prefetcher (116) is", "de prefetcher (116) wordt"), "a reference numeral");
            A.Eq("van 6 tot 12 mm", Swap("from 6 to 12 mm", "from 5 to 10 mm", "van 5 tot 10 mm"), "both ends of a range");
            A.Eq("6-12 mm", Swap("6-12 mm", "5-10 mm", "5-10 mm"), "a range written with a hyphen");
            A.Eq("conclusies 8 of 10", Swap("claims 8 or 10", "claims 8 or 9", "conclusies 8 of 9"), "one of two numbers, the other unchanged");
            A.Eq("4.25 mm dik", Swap("4.25 mm thick", "3.5 mm thick", "3.5 mm dik"), "a decimal written the same way in source and translation");
            A.Eq("2,000 eenheden", Swap("2,000 units", "1,000 units", "1,000 eenheden"), "thousands written the same way");
        }

        public static void NothingIsReplacedWhenItCouldGoWrong()
        {
            // Reordered: the document's number is one of the stored source's own.
            A.True(Swap("claims 9 and 8", "claims 8 and 9", "conclusies 8 en 9") == null, "numbers moved around");
            // A number that occurs twice: which one in the translation?
            A.True(Swap("from 5 to 5 mm", "from 4 to 4 mm", "van 4 tot 4 mm") == null, "the changed number occurs twice");
            A.True(Swap("between 3 and 7, then 7", "between 3 and 6, then 6", "tussen 3 en 6, dan 6") == null, "the changed number occurs twice, with another");
            // Dates: day and month the same number; and a translation that writes the date the other way round.
            A.True(Swap("on 12/11/2026", "on 11/11/2026", "op 11-11-2026") == null, "a date whose day and month are the same");
            A.True(Swap("on 13/12/2026", "on 11/10/2026", "op 10-11-2026") == null, "a date the translation writes in another order");
            A.Eq("op 13-12-2026", Swap("on 13/12/2026", "on 11/10/2026", "op 11-10-2026"), "a date written in the same order: each part replaced");
            // Decimals: comma against point.
            A.True(Swap("3,5 mm", "3.5 mm", "3.5 mm") == null, "the document writes a decimal comma, the stored source a point");
            A.True(Swap("4.25 mm", "3.5 mm", "3,5 mm") == null, "the translation writes the number differently from the stored source");
            A.True(Swap("999 units", "1,000 units", "1,000 eenheden") == null, "written with different separators");
            // Counts.
            A.True(Swap("claim 9 or 10", "claim 8", "conclusie 8") == null, "the document has more numbers");
            A.True(Swap("claim 9", "claim 8", "conclusie 8 (2)") == null, "the translation has a number the stored source hasn't");
            A.True(Swap("claim 9", "claim 8", "conclusie acht") == null, "the translation writes the number in words");
            // Not numbers: glued to letters.
            A.True(Swap("an M10 bolt", "an M8 bolt", "een M8-bout") == null, "M8 is a word, not a number");
            A.True(Swap("the 3rd step", "the 2nd step", "de 2e stap") == null, "2nd is a word");
            // Units: the number is the same, only the unit differs.
            A.True(Swap("5 cm", "5 mm", "5 mm") == null, "nothing to replace: the unit is a word");
            // Nothing differs.
            A.True(Swap("claim 8", "claim 8", "conclusie 8") == null, "the same numbers");
        }

        public static void AReplacedNumberCostsNothingAndIsMarked()
        {
            var doc = P("The method of claim 9, wherein the valve closes."); var src = P("The method of claim 8, wherein the valve closes.");
            var trg = P("De werkwijze van conclusie 8, waarbij de klep sluit.");
            var d = VaultDiff.Tokenize(doc); var s = VaultDiff.Tokenize(src);
            var rules = VaultLanguage.Rules("en");
            var plan = VaultNumbers.Make(doc, src, trg);
            A.True(plan != null, "a plan");
            var replaced = VaultDiff.Compare(d, s, false, rules, VaultNumbers.Tokens(plan, d, s));
            var charged = VaultDiff.Compare(d, s, false, rules);
            A.Eq(99, replaced.Score, "only the number differs, and it was replaced: 99 (100 is for identical text only)");
            A.True(charged.Score < replaced.Score, $"not replaced, it counts: {charged.Score}");
            A.True(replaced.Edits.Any(e => e.Op == VaultEditOp.Change && e.Replaced && e.Cost == 0), "the change is marked as replaced, at no cost");
        }

        public static void TheEstimateStaysAboveAReplacedNumbersScore()
        {
            foreach (var (doc, src, trg) in new[]
            {
                ("The method of claim 9, wherein the valve closes.", "The method of claim 8, wherein the valve closes.", "De werkwijze van conclusie 8, waarbij de klep sluit."),
                ("from 6 to 12 mm in 4.25 s", "from 5 to 10 mm in 3.5 s", "van 5 tot 10 mm in 3.5 s"),
            })
            {
                var d = VaultDiff.Tokenize(P(doc)); var s = VaultDiff.Tokenize(P(src));
                var plan = VaultNumbers.Make(P(doc), P(src), P(trg));
                int score = VaultDiff.Compare(d, s, false, VaultLanguage.Rules("en"), VaultNumbers.Tokens(plan, d, s)).Score;
                A.True(VaultDiff.Estimate(d, s, false) >= score, $"estimate {VaultDiff.Estimate(d, s, false)} against score {score}");
            }
        }

        public static void TagsAroundANumberAreKept()
        {
            var target = new List<VaultPiece>
            {
                new VaultPiece { Text = "conclusie " }, new VaultPiece { Kind = VaultTagKind.Open, Name = "b", Key = "1", Raw = "<b>" },
                new VaultPiece { Text = "8" }, new VaultPiece { Kind = VaultTagKind.Close, Name = "b", Key = "1", Raw = "</b>" },
            };
            var plan = VaultNumbers.Make(P("claim 9"), P("claim 8"), target);
            var result = VaultNumbers.Apply(plan, target);
            A.Eq("conclusie |<b>|9|</b>", string.Join("|", result.Select(p => p.IsTag ? p.Raw : p.Text)), "pieces");
        }
    }
}
