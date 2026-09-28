using System;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The output contract and the reply check that backs it. Written after a
    /// model's note to the translator - English, in markdown, after a Dutch
    /// translation - reached a client job's target text. Client wording is
    /// replaced throughout.
    /// </summary>
    [Tests]
    internal static class OutputContractTests
    {
        private const string Source =
            "Installation requirements for anchor bolts (cabinet, console, chiller) (earthquake prone areas)";
        private const string Dutch =
            "Installatievereisten voor ankerbouten (kast, console, koeler) (aardbevingsgevoelige gebieden)";
        private const string Note =
            " Note: I followed the approved match but changed \"oppervlakken\" to \"gebieden\", since areas means regions here.";

        private static void Refused(string source, string reply, string what) =>
            Assert.True(ReplyCheck.Problem(source, reply) != null, what + " should be refused");

        private static void Passes(string source, string reply, string what) =>
            Assert.True(ReplyCheck.Problem(source, reply) == null,
                what + " should pass, but: " + ReplyCheck.Problem(source, reply));

        public static void TheLiveCase_IsRefused()
        {
            Refused(Source, Dutch + "**" + Note.Trim() + "**", "translation + **Note: ...**");
            Refused(Source, Dutch + Note, "the same note without markdown");
        }

        public static void Commentary_IsRefused()
        {
            Refused(Source, Dutch + " I kept the approved wording.", "first person");
            Refused(Source, Dutch + " (the fuzzy match said otherwise)", "a remark about the match");
            Refused("Press Start.", "Druk op **Start**.", "markdown the source lacks");
            Refused("One line only.", "Een regel.\nEn uitleg.", "a line break the source lacks");
            Refused(Source, Dutch + " " + string.Concat(System.Linq.Enumerable.Repeat("en verder uitgelegd ", 12)),
                "a reply far longer than the source");
        }

        public static void CleanTranslations_Pass()
        {
            Passes(Source, Dutch, "a clean translation");
            Passes("Specification of anchor bolts", "Specificatie van ankerbouten", "a clean short segment");
            Passes("OK", "In orde", "a short source, which length does not judge");
        }

        public static void OneTrailingMarker_IsTheOnlyPlaceForAComment()
        {
            Passes(Source, Dutch + " [[TC: \"areas\" read as regions, not surfaces. Please check.]]", "one trailing marker");
            Passes(Source, Dutch + " ⟦TC: older bracket form⟧", "the older bracket form");
            Refused(Source, Dutch + " [[TC: one]] [[TC: two]]", "two markers");
            Refused(Source, "[[TC: first]] " + Dutch, "a marker that is not at the end");
        }

        public static void SignalsInTheSource_AreNotCommentary()
        {
            Passes("Opmerking: niet afdekken.", "Note: do not cover.", "a label at the start");
            Passes("Note: I changed the filter.", "Note: I changed the filter.", "first person in the source");
            Passes("Press **Start**.", "Druk op **Start**.", "markdown in the source");
        }

        public static void Tags_AreReportedNeverRefused()
        {
            const string tagged = "<inline_tag id=\"0\"/>Specification of anchor bolts";
            Passes(tagged, "Specificatie van ankerbouten", "a dropped tag");
            Assert.True(ReplyCheck.TagDifference(tagged, "Specificatie van ankerbouten") != null, "the dropped tag is reported");
            Assert.True(ReplyCheck.TagDifference(tagged, "<inline_tag id=\"0\"/>Specificatie van ankerbouten") == null,
                "matching tags report nothing");
            Assert.True(ReplyCheck.TagDifference(tagged, "<inline_tag id=\"0\"/>Specificatie [[TC: <b>check</b>]]") == null,
                "tags inside a marker are not counted");
        }

        public static void Contract_NamesTheMarker_AndNothingAGridFontCannotShow()
        {
            Assert.True(OutputContract.Text.StartsWith(OutputContract.Heading, StringComparison.Ordinal), "starts with its heading");
            Assert.True(OutputContract.Text.Contains("[[TC: "), "names the [[TC: ...]] marker");
            Assert.True(OutputContract.Text.IndexOf('⟦') < 0, "not the older bracket form");
            Assert.True(OutputContract.Text.IndexOf('—') < 0, "no em dash");
        }

        private const string Declaration = "Ik verklaar dat ik dit document naar waarheid heb vertaald.";
        private const string DeclarationEn = "I declare that I have translated this document truthfully.";

        public static void FirstPersonSource_TranslatedIntoEnglish_Passes()
        {
            Passes(Declaration, DeclarationEn, "a declaration in the first person");
            Passes("J'ai traduit ce document.", "I translated this document.", "French j'");
            Passes("Ik verklaar dat ik dit document naar waarheid heb vertaald",
                DeclarationEn, "a full stop the source does not have is not a sentence");
            Passes("Hierbij verklaar ik: ik heb dit document vertaald. Datum: 1 mei.",
                "I hereby declare: I have translated this document. Date: 1 May.", "two sentences into two");
        }

        public static void FirstPersonSource_DoesNotExcuseAnAddedRemark()
        {
            // Condition (b): a sentence or a bracket the source does not have.
            Refused(Declaration, DeclarationEn + " I kept the approved wording.", "an appended sentence");
            Refused(Declaration, "I declare that I have translated this document truthfully (I kept the wording).",
                "an appended bracket");
            // Condition (a): no word for "I" in the source, however well the sentences line up.
            Refused("De klep is gesloten.", "The valve is closed, I kept the wording.", "a pronoun-free source");
            Refused("De EU-verordening is gewijzigd.", "The EU regulation has been amended, I changed the wording.",
                "\"EU\" is not the Portuguese \"eu\"");
        }

        public static void WithoutComment_TakesOffTheClosingMarker()
        {
            var reply = Dutch + " [[TC: \"areas\" read as regions, not surfaces. Please check.]]";
            Assert.Equal(Dutch, ReplyCheck.WithoutComment(reply, out var comment), "the translation");
            Assert.Equal("\"areas\" read as regions, not surfaces. Please check.", comment, "the comment");

            Assert.Equal(Dutch, ReplyCheck.WithoutComment(Dutch + " ⟦TC: older form⟧\r\n", out comment), "older form, trailing newline");
            Assert.Equal("older form", comment, "older form's comment");

            const string tagged = "<t1>Opslaan</t1> nu";
            Assert.Equal(tagged, ReplyCheck.WithoutComment(tagged + " [[TC: check the button name]]", out comment), "tags kept");
            Assert.Equal("check the button name", comment, "comment after tags");

            // Taken whole: the comment's own brackets and tags are its text.
            Assert.Equal(Dutch, ReplyCheck.WithoutComment(Dutch + " [[TC: see [3] and <b>x</b>]]", out comment), "brackets inside");
            Assert.Equal("see [3] and <b>x</b>", comment, "brackets inside the comment");
        }

        public static void WithoutComment_LeavesEverythingElseAlone()
        {
            Assert.Equal(Dutch, ReplyCheck.WithoutComment(Dutch, out var comment), "no marker");
            Assert.True(comment == null, "no marker, no comment");

            // Replies the check refuses are never half taken apart.
            var twice = Dutch + " [[TC: one]] [[TC: two]]";
            Assert.Equal(twice, ReplyCheck.WithoutComment(twice, out comment), "two markers");
            Assert.True(comment == null, "two markers, no comment");
            var first = "[[TC: first]] " + Dutch;
            Assert.Equal(first, ReplyCheck.WithoutComment(first, out comment), "a marker that is not at the end");
            Assert.True(comment == null, "not at the end, no comment");

            Assert.Equal(Dutch, ReplyCheck.WithoutComment(Dutch + " [[TC: ]]", out comment), "an empty marker is removed");
            Assert.True(comment == null, "an empty marker gives no comment");

            Assert.True(ReplyCheck.WithoutComment(null, out comment) == null && comment == null, "null");
            Assert.Equal("", ReplyCheck.WithoutComment("", out comment), "empty");
        }

        public static void ProofreadContract_FixesTheFormatTheParserReads()
        {
            var t = OutputContract.ProofreadText;
            Assert.True(t.StartsWith(OutputContract.Heading, StringComparison.Ordinal), "starts with its heading");
            foreach (var part in new[] { "[SEGMENT 0001] OK", "[SEGMENT 0001] ISSUE", "Issue:", "Evidence:", "Suggestion:", "Source query:" })
                Assert.True(t.Contains(part), "names " + part);
            Assert.True(t.IndexOf('—') < 0, "no em dash");
            Assert.True(t.IndexOf('⟦') < 0, "not the older bracket form");
        }

        public static void DefaultTranslationPrompt_OldCopyIsRefreshed_EditedCopyIsNot()
        {
            const string oldLine = "- When a term has no established equivalent, keep the source term and add a brief explanation in parentheses if needed";

            Assert.True(PromptLibrary.IsOutdatedDefaultTranslationPrompt("---\ndefault: true\n---\n" + oldLine),
                "the shipped old default is refreshed");
            Assert.True(!PromptLibrary.IsOutdatedDefaultTranslationPrompt("---\ndefault: false\n---\n" + oldLine),
                "a user's copy of it is never touched");
            Assert.True(!PromptLibrary.IsOutdatedDefaultTranslationPrompt(
                    "---\ndefault: true\n---\n- keep the source term; if that needs flagging, use a [[TC: ...]] marker"),
                "the new default is left alone, so the refresh runs once");
            Assert.True(!PromptLibrary.IsOutdatedDefaultTranslationPrompt(null), "no file, no refresh");
        }
    }
}
