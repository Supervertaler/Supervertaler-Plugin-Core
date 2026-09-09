using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Checks an AutoPrompt-generated prompt for the damage a truncated model
    /// response leaves behind, BEFORE it is written to the prompt library.
    ///
    /// Why this exists: on 2026-09-09 a generated prompt was cut off mid-file -
    /// the last line was half a glossary row - and the missing third of the
    /// glossary, along with the output-format and previous-translations
    /// sections, was never noticed. It was used to translate an entire
    /// document. The prompt's own text cross-referred to a section number the
    /// file did not contain: machine-checkable, and sitting there for the whole
    /// job.
    ///
    /// The prompt is written by the MODEL, not composed by us, so these checks
    /// read the finished text. They are deliberately conservative: a false
    /// refusal costs one regeneration, a false pass costs a document.
    /// </summary>
    public static class PromptValidator
    {
        /// <summary>A numbered section header found in the prompt body.</summary>
        private sealed class Section
        {
            public int Number;
            public string Title;
            public int LineIndex;
        }

        public sealed class Result
        {
            /// <summary>Every check that failed, in the order they are run. Empty means the prompt is safe to write.</summary>
            public List<string> Failures = new List<string>();

            public bool Ok { get { return Failures.Count == 0; } }

            /// <summary>One block of text naming what failed, for a dialog.</summary>
            public string Describe()
            {
                return string.Join(Environment.NewLine + Environment.NewLine, Failures);
            }
        }

        // "16. PROJECT-SPECIFIC GLOSSARY", "## 16. GLOSSARY", "**16.** ..." - the
        // model is asked for a numbered list of sections and decorates it variously.
        private static readonly Regex SectionHeader = new Regex(
            @"^[ \t]*#{0,6}[ \t]*\*{0,2}(?<n>\d{1,2})\.[ \t]*\*{0,2}(?<title>[^\r\n]*)$",
            RegexOptions.Compiled);

        // "section 16", "Section 16", "sections 15 and 16" (first number only -
        // enough to catch the failure this exists for).
        private static readonly Regex CrossReference = new Regex(
            @"\bsections?\s+(?<n>\d{1,2})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Runs every check. The prompt is the body only - no frontmatter, no
        /// ===PROMPT_START=== delimiters.
        /// </summary>
        public static Result Validate(string prompt)
        {
            var result = new Result();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                result.Failures.Add("The prompt is empty.");
                return result;
            }

            var lines = prompt.Replace("\r\n", "\n").Split('\n');
            var sections = FindSections(lines);

            CheckHasOutputFormat(lines, sections, result);
            CheckNoPartialTableRow(lines, result);
            CheckContiguousNumbering(sections, result);
            CheckCrossReferencesResolve(prompt, sections, result);

            return result;
        }

        /// <summary>
        /// Section headers, in document order. Only lines whose number continues
        /// or starts a plausible run are taken, so a glossary row that happens to
        /// begin "12. " does not register as a section.
        /// </summary>
        private static List<Section> FindSections(string[] lines)
        {
            var found = new List<Section>();
            for (var i = 0; i < lines.Length; i++)
            {
                var m = SectionHeader.Match(lines[i]);
                if (!m.Success) continue;

                var title = m.Groups["title"].Value.Trim();
                // A section header names something; a numbered list item inside a
                // section usually runs on into prose. Require a title that is not
                // empty and does not read as a sentence continuing below.
                if (title.Length == 0) continue;

                found.Add(new Section
                {
                    Number = int.Parse(m.Groups["n"].Value),
                    Title = title,
                    LineIndex = i
                });
            }

            // Keep the longest ascending run starting at 1: the numbered sections.
            // Anything else numbered in the body (enumerated rules, examples) is noise.
            var run = new List<Section>();
            var expected = 1;
            foreach (var s in found)
            {
                if (s.Number == expected)
                {
                    run.Add(s);
                    expected++;
                }
            }
            return run.Count > 0 ? run : found;
        }

        private static void CheckHasOutputFormat(string[] lines, List<Section> sections, Result result)
        {
            if (sections.Count == 0)
            {
                result.Failures.Add(
                    "No numbered sections were found. A generated prompt is a numbered list of " +
                    "sections; a prompt without them is not one.");
                return;
            }

            // NOT "the last section is OUTPUT FORMAT": complete prompts in the
            // library legitimately place TRANSLATOR COMMENT FORMAT after it. What a
            // truncated prompt lacks is the section altogether.
            var hasOutputFormat = sections.Any(
                x => x.Title.IndexOf("OUTPUT FORMAT", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasOutputFormat)
            {
                var last0 = sections[sections.Count - 1];
                result.Failures.Add(
                    "The prompt has no OUTPUT FORMAT section. It stops at section " +
                    last0.Number + ", \"" + Shorten(last0.Title) + "\" - every generated prompt " +
                    "is asked for one, so the file is very likely truncated.");
            }

            // A heading with nothing under it is the other shape of the same failure.
            var last = sections[sections.Count - 1];
            var body = string.Join(" ", lines.Skip(last.LineIndex + 1)).Trim();
            if (body.Length < 40)
            {
                result.Failures.Add(
                    "Section " + last.Number + ", \"" + Shorten(last.Title) + "\", has no body - " +
                    "the prompt stops at its heading.");
            }
        }

        /// <summary>
        /// A truncated table row is what the 2026-09-09 failure ended on: a
        /// glossary row that stopped mid-cell and then nothing. Within one run of
        /// table lines, a row that never closes its last cell while its
        /// neighbours do is damage rather than style.
        /// </summary>
        private static void CheckNoPartialTableRow(string[] lines, Result result)
        {
            var blockStart = -1;
            for (var i = 0; i <= lines.Length; i++)
            {
                var isRow = i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal);
                if (isRow)
                {
                    if (blockStart < 0) blockStart = i;
                    continue;
                }

                if (blockStart >= 0)
                {
                    ReportRaggedRow(lines, blockStart, i - 1, result);
                    blockStart = -1;
                }
            }
        }

        private static void ReportRaggedRow(string[] lines, int from, int to, Result result)
        {
            if (to - from < 1) return;   // a single line is not a table

            // Comparing field counts across the block is too eager: two different
            // tables can sit adjacent with no blank line between them, and the
            // shape change is not damage. A row that stops without its closing pipe
            // while its neighbours have one is damage - that is exactly how the
            // 2026-09-09 prompt ended.
            var closed = 0;
            var open = -1;
            for (var i = from; i <= to; i++)
            {
                if (lines[i].TrimEnd().EndsWith("|", StringComparison.Ordinal)) closed++;
                else if (open < 0) open = i;
            }

            if (open < 0 || closed == 0) return;   // all closed, or a style with no trailing pipes

            result.Failures.Add(
                "A table row is unterminated: line " + (open + 1) + " stops without closing its " +
                "last cell, while other rows in the same table close theirs.\r\n" +
                "    " + Shorten(lines[open].Trim()) + "\r\n" +
                "This is what a response cut off mid-generation looks like.");
        }

        private static void CheckContiguousNumbering(List<Section> sections, Result result)
        {
            if (sections.Count == 0) return;   // already reported

            for (var i = 0; i < sections.Count; i++)
            {
                if (sections[i].Number == i + 1) continue;

                result.Failures.Add(
                    "The section numbering is not contiguous: section " + (i + 1) +
                    " is missing (the next one found is " + sections[i].Number + ", \"" +
                    Shorten(sections[i].Title) + "\").");
                return;
            }
        }

        /// <summary>
        /// The check that would have caught the real failure most directly: the
        /// body refers to "section 16" for previous correct translations, and the
        /// written file stopped at section 15.
        /// </summary>
        private static void CheckCrossReferencesResolve(string prompt, List<Section> sections, Result result)
        {
            if (sections.Count == 0) return;

            var present = new HashSet<int>(sections.Select(s => s.Number));
            var dangling = new List<int>();

            foreach (Match m in CrossReference.Matches(prompt))
            {
                var n = int.Parse(m.Groups["n"].Value);
                if (!present.Contains(n) && !dangling.Contains(n))
                    dangling.Add(n);
            }

            if (dangling.Count == 0) return;

            dangling.Sort();
            result.Failures.Add(
                "The prompt refers to " + (dangling.Count == 1 ? "a section it does not contain" : "sections it does not contain") +
                ": " + string.Join(", ", dangling.Select(n => "section " + n)) +
                ". The prompt has " + sections.Count + " section(s). A reference to a section that " +
                "was never written is the clearest sign the file is incomplete.");
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s.Length <= 80 ? s : s.Substring(0, 77) + "...";
        }
    }
}
