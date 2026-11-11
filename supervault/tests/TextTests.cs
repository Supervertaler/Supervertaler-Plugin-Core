using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Core.SuperVault.Tests
{
    static class MarkupTests
    {
        static string Shape(List<VaultPiece> p) => string.Join(" ", p.Select(x => x.IsTag ? x.Kind.ToString()[0] + "(" + x.Key + ")" : "'" + x.Text + "'"));

        public static void BoldPairIsOpenTextClose()
        {
            var p = VaultMarkup.Parse("Click <b>Save</b> now.");
            A.Eq("'Click ' O(b@1) 'Save' C(b@1/close) ' now.'", Shape(p), "shape");
        }

        public static void IdsPairByAttribute()
        {
            var p = VaultMarkup.Parse("<ph x=\"1\" /><pt x=\"2\">CODE</pt>");
            A.Eq("S(ph#1) O(pt#2) 'CODE' C(pt#2/close)", Shape(p), "shape");
        }

        public static void MemoQCodes()
        {
            var p = VaultMarkup.Parse("[1}De heer{1] en {2} en {}");
            A.Eq("O(mq#1) 'De heer' C(mq#1/close) ' en ' S(mq#2) ' en ' S(mq/@1)", Shape(p), "shape");
        }

        public static void LiteralAnglesStayText()
        {
            A.Eq("'a < b and x<5'", Shape(VaultMarkup.Parse("a < b and x<5")), "comparison");
            A.Eq("'Press <Enter> to continue.'", Shape(VaultMarkup.Parse("Press <Enter> to continue.")), "key name without a closing tag");
        }

        public static void PlainStripsTagsAndNormalises()
        {
            A.Eq("Click Save now.", VaultMarkup.Plain("Click <b>Save</b>  now.­"), "plain");
            A.Eq("multiomicagegevens", VaultMarkup.Plain("multiomica<sdt id=\"0\"></sdt>gegevens"), "joined runs");
            A.Eq("", VaultMarkup.Plain("<ph x=\"1\"/> {}"), "only codes");
        }

        public static void BaseLanguageFoldsVariantsButNotScripts()
        {
            A.Eq("nl", VaultMarkup.BaseLanguage("nl-BE"), "nl-BE");
            A.Eq("en", VaultMarkup.BaseLanguage("EN_us"), "EN_us");
            A.Eq("sr-latn", VaultMarkup.BaseLanguage("sr-Latn-RS"), "sr-Latn-RS");
            A.Eq("zh-hant", VaultMarkup.BaseLanguage("zh-TW"), "zh-TW");
            A.Eq("zh-hans", VaultMarkup.BaseLanguage("zh-CN"), "zh-CN");
        }

        public static void JunkFilterCatchesTheGluedMenuArtefactOnly()
        {
            A.True(VaultMarkup.IsJunk("Other", "AndereSearch in:\" \"All Categories"), "glued");
            A.True(VaultMarkup.IsJunk("", "x"), "empty source");
            A.True(!VaultMarkup.IsJunk("Search in:", "Zoeken in:"), "a real label");
            A.True(!VaultMarkup.IsJunk("He said \"in: out\".", "Hij zei \"in: uit\"."), "quotes after in:");
            A.True(VaultMarkup.IsJunk("the Filename", "BestandsnaamSearch\" \"In:\" \"the Tag"), "glued, other form");
            A.True(!VaultMarkup.IsJunk("kdialog --inputbox \"Enter your name:\" \"YourName\"", "kdialog --inputbox \"Voer uw naam in:\" \"Uw naam\""), "a real command line");
        }
    }

    static class TagMapTests
    {
        static List<VaultPiece> Doc(params object[] parts) => parts.Select(p => p is string s ? new VaultPiece { Text = s } : (VaultPiece)p).ToList();
        static VaultPiece T(VaultTagKind k, int i) => new VaultPiece { Kind = k, Name = "doc", Key = "doc#" + i, Raw = "<" + i + ">" };

        static readonly List<VaultPiece> BoldDoc = Doc("Click ", T(VaultTagKind.Open, 1), "Save", T(VaultTagKind.Close, 3), " now.");

        public static void SameShapeMaps()
        {
            var m = VaultTagMap.Build(BoldDoc, VaultMarkup.Parse("Click <b>Save</b> now."), VaultMarkup.Parse("Klik nu op <b>Opslaan</b>."));
            A.Eq(VaultTagOutcome.Mapped, m.Outcome, "outcome");
            A.Eq("-1,1,-1,3,-1", string.Join(",", m.Target), "target tags onto document tags 1 and 3");
        }

        public static void TargetMayReorderAndDropTags()
        {
            var doc = Doc(T(VaultTagKind.Standalone, 0), " A ", T(VaultTagKind.Standalone, 2), " B");
            var m = VaultTagMap.Build(doc, VaultMarkup.Parse("<ph x=\"1\"/> A <ph x=\"2\"/> B"), VaultMarkup.Parse("B <ph x=\"2\"/> A"));
            A.Eq(VaultTagOutcome.Mapped, m.Outcome, "outcome");
            A.Eq("-1,2,-1", string.Join(",", m.Target), "x=2 is document tag 2");
        }

        public static void PlainUnitForFormattedDocumentIsUnmapped()
        {
            var m = VaultTagMap.Build(BoldDoc, VaultMarkup.Parse("Click Save now."), VaultMarkup.Parse("Klik nu op Opslaan."));
            A.Eq(VaultTagOutcome.Unmapped, m.Outcome, "outcome");
        }

        public static void FormattedUnitForPlainDocumentIsUnmapped()
        {
            var m = VaultTagMap.Build(Doc("Click Save now."), VaultMarkup.Parse("Click <b>Save</b> now."), VaultMarkup.Parse("Klik op <b>Opslaan</b>."));
            A.Eq(VaultTagOutcome.Unmapped, m.Outcome, "outcome");
        }

        public static void TargetTagWithoutSourceCounterpartIsUnmapped()
        {
            var m = VaultTagMap.Build(BoldDoc, VaultMarkup.Parse("Click <b>Save</b> now."), VaultMarkup.Parse("<i>Klik</i> op <b>Opslaan</b>."));
            A.Eq(VaultTagOutcome.Unmapped, m.Outcome, "outcome");
        }

        public static void DifferentKindsAreUnmapped()
        {
            var doc = Doc("Click ", T(VaultTagKind.Standalone, 1), "Save", T(VaultTagKind.Standalone, 3));
            var m = VaultTagMap.Build(doc, VaultMarkup.Parse("Click <b>Save</b>"), VaultMarkup.Parse("Klik <b>Opslaan</b>"));
            A.Eq(VaultTagOutcome.Unmapped, m.Outcome, "outcome");
        }

        public static void NoTagsAnywhere()
        {
            A.Eq(VaultTagOutcome.NoTags, VaultTagMap.Build(Doc("Hello."), VaultMarkup.Parse("Hello."), VaultMarkup.Parse("Hallo.")).Outcome, "outcome");
        }
    }

    static class DiffTests
    {
        static List<VaultToken> Tok(string s) => VaultDiff.Tokenize(new[] { new VaultPiece { Text = s } });

        public static void IdenticalIs100()
        {
            var r = VaultDiff.Compare(Tok("The pump is started."), Tok("The pump is started."), true);
            A.Eq(100, r.Score, "score");
            A.True(r.Edits.All(e => e.Op == VaultEditOp.Identity), "all identity");
        }

        public static void OneWordInTenIs90()
        {
            var r = VaultDiff.Compare(Tok("one two three four five six seven eight nine ten"), Tok("one two three four five six seven eight nine eleven"), false);
            A.Eq(90, r.Score, "score");
            A.Eq(1, r.Edits.Count(e => e.Op == VaultEditOp.Change), "one change");
        }

        public static void NeverReaches100UnlessIdentical()
        {
            A.Eq(99, VaultDiff.Compare(Tok("Save"), Tok("Save"), false).Score, "equal tokens but not declared identical");
        }

        public static void MissingWordIsDeleteAndExtraIsInsert()
        {
            var r = VaultDiff.Compare(Tok("Click Save right now."), Tok("Click Save now."), false);
            A.True(r.Edits.Any(e => e.Op == VaultEditOp.Delete), "document word the memory lacks");
            r = VaultDiff.Compare(Tok("Click Save now."), Tok("Click Save right now."), false);
            A.True(r.Edits.Any(e => e.Op == VaultEditOp.Insert), "memory word the document lacks");
        }

        // Studio's convention (11 Nov 2026): a document word the memory lacks (Delete) carries the memory position it
        // goes BEFORE, and a memory word the document lacks (Insert) the document position it goes before. One less
        // made Studio draw every inserted word a token too early.
        public static void ExtraOrMissingWordCarriesThePositionItGoesBefore()
        {
            // "the pump starts quickly now" against "the pump starts now": the space (5) and "quickly" (6) go before the stored
            // space (5), as Studio's own TM gives them (D5>5 D6>5; the old positions were D5>4 D6>4).
            var r = VaultDiff.Compare(Tok("the pump starts quickly now"), Tok("the pump starts now"), false);
            A.Eq("D5>5 D6>5", string.Join(" ", r.Edits.Where(e => e.Op == VaultEditOp.Delete).Select(e => "D" + e.A + ">" + e.B)), "deletes");
            r = VaultDiff.Compare(Tok("the pump starts now"), Tok("the pump starts quickly now"), false);
            A.Eq("I5>5 I5>6", string.Join(" ", r.Edits.Where(e => e.Op == VaultEditOp.Insert).Select(e => "I" + e.A + ">" + e.B)), "inserts");
            // At the very start and the very end.
            r = VaultDiff.Compare(Tok("Moreover the pump starts"), Tok("the pump starts"), false);
            A.True(r.Edits.Where(e => e.Op == VaultEditOp.Delete).All(e => e.B == 0), "extra words at the start go before memory token 0");
            r = VaultDiff.Compare(Tok("the pump starts fully"), Tok("the pump starts"), false);
            A.True(r.Edits.Where(e => e.Op == VaultEditOp.Delete).All(e => e.B == 5), "extra words at the end go after the last memory token");
        }

        public static void WordsPairWithWordsThatLookAlikeWithoutChangingTheScore()
        {
            // Two alignments cost the same here; the one drawn pairs "determine" with "determining", not with a comma.
            var doc = Tok("further comprising determining, by the processors, the first threshold");
            var mem = Tok("determine the first threshold");
            var r = VaultDiff.Compare(doc, mem, false);
            var ch = r.Edits.Where(e => e.Op == VaultEditOp.Change).Select(e => mem[e.B].Text + ">" + doc[e.A].Text).ToList();
            A.Eq("determine>determining", string.Join(" ", ch), "changes");
            A.Eq(8.0, r.Distance, "distance: 7 extra words and commas, 1 changed word");
        }

        public static void WordsKeepApostrophesAndHyphens()
        {
            A.Eq("don't|e-mail|it|s", string.Join("|", VaultDiff.Words("Don't e-mail it's".Replace("it's", "it' s"))), "words");
        }

        public static void LongSegmentsScoreWithoutAlignment()
        {
            var a = string.Join(" ", Enumerable.Range(0, 300).Select(i => "w" + i));
            var b = string.Join(" ", Enumerable.Range(0, 300).Select(i => i == 7 ? "x" : "w" + i));
            var r = VaultDiff.Compare(Tok(a), Tok(b), false);
            A.True(r.Edits == null, "too long to align");
            A.Eq(99, r.Score, "299 of 300 words in common");
        }
    }
}
