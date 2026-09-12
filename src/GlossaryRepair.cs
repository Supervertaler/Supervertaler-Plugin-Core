using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// Forces the glossary in a generated prompt back to the termbase rows that were
    /// supplied to generate it (issue #124).
    ///
    /// <para><b>Why this is needed.</b> AutoPrompt hands the generating model the
    /// project's termbase and asks it to reproduce every row in a section marked
    /// MANDATORY and LOCKED. It mostly does. Measured on one real 279-term run: one
    /// row dropped, one invented, and two targets rewritten into variants -
    /// <c>vermalen: to grind</c> became <c>grinding / ground</c>, and
    /// <c>bovenzijde: upper side</c> became <c>upper side / top side</c>. The same
    /// generated prompt, three paragraphs above that table, contained the rule
    /// "never introduce alternatives ... such formulations silently reopen the very
    /// choice the lock was meant to close". The model wrote the rule and then broke
    /// it. About 1% of rows, silently, in the one section whose entire value is
    /// being exact.</para>
    ///
    /// <para><b>Why repair rather than a sterner instruction.</b> The instruction is
    /// already there, in capitals, twice. What is missing is not emphasis but a
    /// check: we supplied the rows, so we can simply put them back.</para>
    ///
    /// <para><b>What it does NOT do.</b> Rows the model added are left alone. When a
    /// project has no AI-enabled termbase, deriving a glossary from the source is the
    /// point of AutoPrompt, and even with a termbase an extra row is a judgement
    /// about the document rather than a contradiction of the termbase. Only rows
    /// whose source term was supplied are forced, and only their target cell.</para>
    /// </summary>
    public static class GlossaryRepair
    {
        public class Result
        {
            /// <summary>The prompt after repair - unchanged when nothing needed it.</summary>
            public string Prompt;
            /// <summary>"term: supplied 'x' -> generated 'y'", one per forced target.</summary>
            public List<string> Corrected = new List<string>();
            /// <summary>Supplied terms that were missing and have been appended.</summary>
            public List<string> Restored = new List<string>();
            /// <summary>Set when the glossary table could not be located at all.</summary>
            public bool TableNotFound;
            public bool Changed { get { return Corrected.Count > 0 || Restored.Count > 0; } }

            public string Summary()
            {
                if (TableNotFound)
                    return "The generated prompt has no glossary table matching the supplied terms - "
                         + "nothing was checked. Look at the prompt by hand.";
                if (!Changed) return "Glossary checked against the termbase: every supplied term is present and exact.";

                var sb = new StringBuilder();
                sb.AppendLine("The generated glossary did not match the termbase it was built from. Repaired:");
                sb.AppendLine();
                if (Corrected.Count > 0)
                {
                    sb.AppendLine(Corrected.Count + " target(s) put back to the termbase wording:");
                    foreach (var c in Corrected) sb.AppendLine("  " + c);
                    sb.AppendLine();
                }
                if (Restored.Count > 0)
                {
                    sb.AppendLine(Restored.Count + " term(s) the prompt had dropped, restored:");
                    foreach (var r in Restored) sb.AppendLine("  " + r);
                }
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Repairs the glossary and returns the corrected prompt. Never throws: a
        /// prompt that cannot be parsed comes back exactly as it went in, because a
        /// prompt with a drifted glossary is still far better than no prompt.
        /// </summary>
        public static Result Apply(string generatedPrompt, IList<TermEntry> suppliedTerms)
        {
            var result = new Result { Prompt = generatedPrompt };
            if (string.IsNullOrWhiteSpace(generatedPrompt)) return result;
            if (suppliedTerms == null || suppliedTerms.Count == 0) return result;

            try { return Repair(generatedPrompt, suppliedTerms, result); }
            catch { return new Result { Prompt = generatedPrompt }; }
        }

        private static Result Repair(string prompt, IList<TermEntry> terms, Result result)
        {
            // One target per source term. A termbase with two rows for one source is
            // an ambiguity we cannot resolve from here, so leave those alone entirely
            // rather than picking one and locking the wrong half.
            var wanted = terms
                .Where(t => t != null
                         && !string.IsNullOrWhiteSpace(t.SourceTerm)
                         && !string.IsNullOrWhiteSpace(t.TargetTerm))
                .GroupBy(t => Key(t.SourceTerm), StringComparer.Ordinal)
                .Where(g => g.Select(t => t.TargetTerm.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().TargetTerm.Trim(),
                              StringComparer.Ordinal);
            if (wanted.Count == 0) return result;

            // Source terms are matched on a normalised key, not literally. The
            // generating model formats the cell: it italicised a species name and
            // added a full stop, turning "Miscanthus sp" into "*Miscanthus* sp." -
            // which a literal comparison reads as a different term, so the repair
            // "restored" a row that was already present, as a near-duplicate.
            var original = terms
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.SourceTerm))
                .GroupBy(t => Key(t.SourceTerm), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().SourceTerm.Trim(), StringComparer.Ordinal);

            var lines = prompt.Replace("\r\n", "\n").Split('\n').ToList();

            // Find the glossary by CONTENT, not by heading. Headings vary between
            // generations - "PROJECT-SPECIFIC GLOSSARY (MANDATORY, LOCKED)", "Locked
            // glossary", numbered differently each time - but the glossary is always
            // the table whose first column is full of the terms we supplied.
            var best = FindGlossaryTable(lines, wanted);
            if (best == null) { result.TableNotFound = true; return result; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int columns = 0;

            for (int i = best.Item1; i <= best.Item2; i++)
            {
                var cells = SplitRow(lines[i]);
                if (cells == null || cells.Count < 2) continue;
                if (columns == 0) columns = cells.Count;
                if (IsSeparatorRow(cells)) continue;

                var source = Key(Unescape(cells[0]));
                string target;
                if (!wanted.TryGetValue(source, out target)) continue;

                seen.Add(source);
                // Compared on the normalised key, so FORMATTING is not drift. The
                // model italicised a species name and added a full stop -
                // "*Miscanthus* sp." for "Miscanthus sp" - which is correct
                // scientific convention and an improvement on the termbase. Forcing
                // that back would also leave the row's own note ("italicise the
                // genus name only") contradicting the target beside it. A genuine
                // variant differs in WORDS - "grinding / ground" for "to grind" -
                // and is still corrected.
                var have = Unescape(cells[1]).Trim();
                if (string.Equals(Key(have), Key(target), StringComparison.Ordinal)) continue;

                // Report the termbase's own spelling, not the normalised key -
                // "miscanthus sp" is an internal detail and reads like a second bug.
                string label;
                if (!original.TryGetValue(source, out label)) label = source;
                result.Corrected.Add(label + ": termbase '" + target + "', prompt had '" + have + "'");
                cells[1] = Escape(target);
                lines[i] = JoinRow(cells);
            }

            // Anything supplied and never seen was dropped. Put it back at the end of
            // the table, where it is in the locked list rather than absent from it.
            var missing = wanted.Where(kv => !seen.Contains(kv.Key)).ToList();
            if (missing.Count > 0)
            {
                if (columns < 2) columns = 2;
                var insertAt = best.Item2 + 1;
                foreach (var kv in missing)
                {
                    string shown;
                    if (!original.TryGetValue(kv.Key, out shown)) shown = kv.Key;
                    var cells = new List<string> { Escape(shown), Escape(kv.Value) };
                    while (cells.Count < columns) cells.Add("");
                    lines.Insert(insertAt++, JoinRow(cells));
                    result.Restored.Add(shown + " → " + kv.Value);
                }
            }

            if (result.Changed) result.Prompt = string.Join(Environment.NewLine, lines);
            return result;
        }

        /// <summary>
        /// The contiguous run of table lines whose first column matches the most
        /// supplied terms. Returns (firstLine, lastLine), or null when no table
        /// matches anything - which means the prompt's glossary is not where or what
        /// we think, and touching it would be guesswork.
        /// </summary>
        private static Tuple<int, int> FindGlossaryTable(List<string> lines, Dictionary<string, string> wanted)
        {
            Tuple<int, int> best = null;
            int bestHits = 0;

            int i = 0;
            while (i < lines.Count)
            {
                if (!IsTableLine(lines[i])) { i++; continue; }

                int start = i;
                while (i < lines.Count && IsTableLine(lines[i])) i++;
                int end = i - 1;

                int hits = 0;
                for (int k = start; k <= end; k++)
                {
                    var cells = SplitRow(lines[k]);
                    if (cells == null || cells.Count < 2 || IsSeparatorRow(cells)) continue;
                    if (wanted.ContainsKey(Key(Unescape(cells[0])))) hits++;
                }

                if (hits > bestHits) { bestHits = hits; best = Tuple.Create(start, end); }
            }

            return bestHits > 0 ? best : null;
        }

        private static bool IsTableLine(string line)
        {
            var t = (line ?? "").Trim();
            return t.StartsWith("|") && t.Length > 1;
        }

        /// <summary>Cells of a markdown row, or null when it is not one.</summary>
        private static List<string> SplitRow(string line)
        {
            var t = (line ?? "").Trim();
            if (!t.StartsWith("|")) return null;
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            t = t.Substring(1);
            // Split on unescaped pipes only - a term may legitimately contain "\|".
            var cells = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append("\\|"); i++; continue; }
                if (t[i] == '|') { cells.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(t[i]);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static string JoinRow(List<string> cells)
        {
            return "| " + string.Join(" | ", cells.Select(c => c.Trim())) + " |";
        }

        /// <summary>The |---|---| rule under a header row.</summary>
        private static bool IsSeparatorRow(List<string> cells)
        {
            return cells.All(c =>
            {
                var t = (c ?? "").Trim();
                return t.Length > 0 && t.All(ch => ch == '-' || ch == ':' || ch == ' ');
            });
        }

        /// <summary>
        /// The form two source terms are compared in: lower case, no markdown
        /// emphasis, no surrounding punctuation, single spaces. Everything a model
        /// does to a term while formatting a table cell, and nothing that changes
        /// which term it is.
        /// </summary>
        private static string Key(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool pendingSpace = false;
            foreach (var ch in s)
            {
                if (ch == '*' || ch == '_' || ch == '`') continue;
                if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString().Trim('.', ',', ';', ':', '"', '\'', ' ');
        }

        private static string Escape(string s) { return (s ?? "").Replace("|", "\\|"); }
        private static string Unescape(string s) { return (s ?? "").Replace("\\|", "|"); }
    }
}
