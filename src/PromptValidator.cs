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
            /// <summary>The number of leading '#', 0 when the line is not a Markdown heading.</summary>
            public int Level;
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
            @"^[ \t]*(?<level>#{0,6})[ \t]*\*{0,2}(?<n>\d{1,2})\.[ \t]*\*{0,2}(?<title>[^\r\n]*)$",
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
            return Validate(prompt, null);
        }

        /// <summary>
        /// <paramref name="requestedSections"/> is the section list the meta-prompt
        /// asked for (PromptGenerator.SectionsFor). Pass it wherever it is known:
        /// without it, a cut inside the FINAL section's body passes every other
        /// check, because OUTPUT FORMAT is present, the last heading has a body and
        /// no table is ragged. With it, the sections that never arrived are named.
        /// </summary>
        public static Result Validate(string prompt, IList<string> requestedSections)
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
            CheckRequestedSectionsArrived(prompt, requestedSections, result);

            return result;
        }

        /// <summary>
        /// Every section the model was asked for should be somewhere in the
        /// finished prompt.
        ///
        /// Only a MISSING TAIL counts. A truncated response loses a run of sections
        /// off the end; a model that renames or merges one in the middle has not
        /// lost anything, and refusing that would cost a regeneration for a
        /// paraphrase. So sections missing from the middle are ignored and a
        /// missing suffix is a failure.
        /// </summary>
        private static void CheckRequestedSectionsArrived(
            string prompt, IList<string> requestedSections, Result result)
        {
            if (requestedSections == null || requestedSections.Count == 0) return;

            var haystack = prompt.ToUpperInvariant();
            var arrived = new bool[requestedSections.Count];
            for (var i = 0; i < requestedSections.Count; i++)
            {
                var headline = Headline(requestedSections[i]);
                arrived[i] = headline.Length > 0
                             && haystack.IndexOf(headline, StringComparison.Ordinal) >= 0;
            }

            // Walk back from the end over sections that never arrived.
            var lastArrived = requestedSections.Count - 1;
            while (lastArrived >= 0 && !arrived[lastArrived]) lastArrived--;

            var missingTail = requestedSections.Count - 1 - lastArrived;
            if (missingTail == 0) return;

            var names = new List<string>();
            for (var i = lastArrived + 1; i < requestedSections.Count; i++)
                names.Add("\"" + Shorten(Headline(requestedSections[i])) + "\"");

            result.Failures.Add(
                "The last " + (missingTail == 1 ? "section" : missingTail + " sections") +
                " the model was asked for never arrived: " + string.Join(", ", names) +
                ". Everything before " + (missingTail == 1 ? "it" : "them") +
                " is present, which is what a response cut off part-way through looks like.");
        }

        /// <summary>
        /// The name part of a requested section spec: "TERMINOLOGY CONSISTENCY
        /// HIERARCHY - (1) Previous correct translations..." is asked for as a
        /// heading plus a description of what belongs under it, and only the
        /// heading is expected verbatim in the output.
        /// </summary>
        private static string Headline(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return "";
            var s = spec.Trim();
            var cut = s.Length;
            foreach (var sep in new[] { " \u2013 ", " - ", " (" })
            {
                var at = s.IndexOf(sep, StringComparison.Ordinal);
                if (at > 0 && at < cut) cut = at;
            }
            return s.Substring(0, cut).Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Section headers, in document order.
        ///
        /// Every numbered line is a candidate, and a numbered list inside a
        /// section is made of them too. Taken in document order, a list that runs
        /// past the section numbers replaces the real headings after it: a
        /// complete prompt whose PREVIOUS CORRECT TRANSLATIONS section listed 31
        /// numbered pairs was refused as "stopping at section 31" with no OUTPUT
        /// FORMAT section (reported 5 Oct 2026). So candidates are tried in order
        /// of how surely they are headings, and the first kind that yields a run
        /// from 1 is used:
        ///   1. Markdown headings at the level of the first "1." heading, the
        ///      "## 1. ROLE" form the generator asks for;
        ///   2. lines whose name is in capitals, as every section name the
        ///      generator asks for is ("**12. PREVIOUS CORRECT TRANSLATIONS**");
        ///   3. any numbered line.
        /// </summary>
        private static List<Section> FindSections(string[] lines)
        {
            var found = new List<Section>();
            for (var i = 0; i < lines.Length; i++)
            {
                var m = SectionHeader.Match(lines[i]);
                if (!m.Success) continue;

                // "**12. NAME**" leaves its closing asterisks on the title.
                var title = m.Groups["title"].Value.Trim().TrimEnd('*').Trim();
                if (title.Length == 0) continue;

                found.Add(new Section
                {
                    Number = int.Parse(m.Groups["n"].Value),
                    Title = title,
                    LineIndex = i,
                    Level = m.Groups["level"].Value.Length
                });
            }

            var firstHeadings = found.Where(s => s.Number == 1 && s.Level > 0).ToList();
            if (firstHeadings.Count > 0)
            {
                var level = firstHeadings.Min(s => s.Level);
                var headings = AscendingRun(found.Where(s => s.Level == level));
                if (headings.Count > 0) return headings;
            }

            var capitals = AscendingRun(found.Where(s => IsInCapitals(s.Title)));
            if (capitals.Count > 0) return capitals;

            var any = AscendingRun(found);
            return any.Count > 0 ? any : found;
        }

        /// <summary>1, 2, 3 ... taken in document order; anything out of step is skipped.</summary>
        private static List<Section> AscendingRun(IEnumerable<Section> candidates)
        {
            var run = new List<Section>();
            foreach (var s in candidates)
            {
                if (s.Number == run.Count + 1) run.Add(s);
            }
            return run;
        }

        /// <summary>
        /// "TRANSLATION MANDATE (NON-NEGOTIABLE) - faithful ..." is in capitals: the
        /// name, before any description, has letters and none of them lower case.
        /// "Source: Afdeling / Augustus 2026" is not.
        /// </summary>
        private static bool IsInCapitals(string title)
        {
            var name = title.Replace("*", "").Replace("`", "").Trim();
            foreach (var sep in new[] { " – ", " - ", " (", ":" })
            {
                var at = name.IndexOf(sep, StringComparison.Ordinal);
                if (at > 0) name = name.Substring(0, at);
            }
            return name.Count(char.IsLetter) >= 2 && !name.Any(char.IsLower);
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
