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
