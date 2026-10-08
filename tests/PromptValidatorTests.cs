using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The checks that stop a truncated AutoPrompt prompt reaching the library.
    /// Written after a complete prompt was refused (reported 5 Oct 2026): its
    /// PREVIOUS CORRECT TRANSLATIONS section listed 31 numbered pairs, and the
    /// checker took the list's items for section headings, so it reported the
    /// prompt as stopping at "section 31" with no OUTPUT FORMAT section. The
    /// wording here is invented.
    /// </summary>
    [Tests]
    internal static class PromptValidatorTests
    {
        private static readonly IList<string> Requested = PromptGenerator.SectionsFor("general");

        private const string Body =
            "Follow this rule for every segment in the batch, without exception or abbreviation.";

        /// <summary>
        /// A prompt in the shape AutoPrompt asks for: one heading per requested
        /// section, each with a body, and the previous translations as a numbered
        /// list. <paramref name="heading"/> turns a number and a name into a
        /// heading line. Stops after <paramref name="stopAfterPair"/> pairs when
        /// given, the way a cut-off response does.
        /// </summary>
        private static string Prompt(Func<int, string, string> heading, int pairs = 31,
            int? stopAfterPair = null, bool outputFormatBody = true)
        {
            var sb = new StringBuilder();
            sb.Append("# Translation prompt\n\n## For Acme, PROJ-001\n\n");
            for (var i = 0; i < Requested.Count; i++)
            {
                var name = Requested[i].Split(new[] { " – " }, StringSplitOptions.None)[0];
                sb.Append(heading(i + 1, name)).Append("\n\n");

                if (name == "PREVIOUS CORRECT TRANSLATIONS")
                {
                    for (var p = 1; p <= pairs; p++)
                    {
                        sb.Append(p).Append(". Source: Afdeling ").Append(p).Append(" / Augustus 2026\n")
                          .Append("   Target: Department ").Append(p).Append(" / August 2026\n\n");
                        if (p == stopAfterPair) return sb.ToString();
                    }
                }
                else if (name != "OUTPUT FORMAT" || outputFormatBody)
                {
                    sb.Append(Body).Append("\n\n");
                }
            }
            return sb.ToString();
        }

        private static string H2(int n, string name) => "## " + n + ". " + name;
        private static string Bold(int n, string name) => "**" + n + ". " + name + "**";

        private static PromptValidator.Result Check(string prompt) => PromptValidator.Validate(prompt, Requested);

        private static void Passes(string prompt, string what)
        {
            var r = Check(prompt);
            Assert.True(r.Ok, what + " should pass, but: " + r.Describe());
        }

        public static void TheLiveCase_ACompletePromptWithALongNumberedList_Passes()
        {
            Passes(Prompt(H2), "## headings with a 31-item list of previous translations");
            Passes(Prompt(Bold), "**N. NAME** headings with the same list");
        }

        public static void CutInsideTheList_IsRefused_AtTheRealSection()
        {
            foreach (var style in new Func<int, string, string>[] { H2, Bold })
            {
                var r = Check(Prompt(style, stopAfterPair: 20));
                var what = r.Describe();
                Assert.True(!r.Ok, "a prompt cut off inside its list should be refused");
                Assert.True(what.Contains("no OUTPUT FORMAT section"), "it should say OUTPUT FORMAT is missing: " + what);
                Assert.True(what.Contains("section 12, \"PREVIOUS CORRECT TRANSLATIONS"),
                    "it should name the last real section, not a list item: " + what);
            }
        }

        public static void CutAtTheLastHeading_IsRefused()
        {
            var r = Check(Prompt(H2, outputFormatBody: false));
            Assert.True(!r.Ok && r.Describe().Contains("has no body"),
                "a prompt that stops at its OUTPUT FORMAT heading should be refused: " + r.Describe());
        }

        public static void NumberedSubheadings_AreNotSections()
        {
            var prompt = Prompt(H2).Replace(
                "## 5. CORE EXECUTION PRINCIPLES\n\n",
                "## 5. CORE EXECUTION PRINCIPLES\n\n### 1. Absolute requirements\n\n" + Body +
                "\n\n### 2. Absolute prohibitions\n\n" + Body + "\n\n");
            Assert.True(prompt.Contains("### 2. Absolute prohibitions"), "the fixture should contain the subheadings");
            Passes(prompt, "numbered ### subheadings inside a section");
        }

        public static void PlainNumberedSections_AreStillChecked()
        {
            // No Markdown headings and no capitals: the oldest shape, where every
            // numbered line is a candidate. A short list cannot outrun the sections.
            Func<int, string, string> plain = (n, name) =>
                n + ". " + name.Substring(0, 1) + name.Substring(1).ToLowerInvariant();
            Passes(Prompt(plain, pairs: 3), "plain numbered sections");

            var cut = Prompt(plain, pairs: 3);
            cut = cut.Substring(0, cut.IndexOf("6. Translation style", StringComparison.Ordinal));
            Assert.True(!Check(cut).Ok, "plain numbered sections cut after section 5 should be refused");
        }

        public static void DanglingCrossReference_IsStillRefused()
        {
            var prompt = Prompt(H2).Replace(Body + "\n\n## 2.", "See section 14 for the glossary.\n\n## 2.");
            var r = Check(prompt);
            Assert.True(!r.Ok && r.Describe().Contains("section 14"),
                "a reference to a section the prompt does not contain should be refused: " + r.Describe());
        }
    }
}
