using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// The decided form of <see cref="GlossDetector"/>'s candidates: for each
    /// bracketed English gloss found in the Dutch source, whether translating the
    /// head term makes the bracket say the same thing twice (issue #113).
    ///
    /// <para><b>Why a register and not an instruction.</b> A standing rule - "if the
    /// bracket duplicates your translation, drop it" - asks the model to notice,
    /// translate, compare and decide on every segment, competing with several
    /// hundred other instructions. It will mostly work and will occasionally
    /// over-apply, deleting a gloss that was not a duplicate. That failure is silent
    /// and ships to the client. Deciding once, up front, turns a judgement into a
    /// lookup.</para>
    ///
    /// <para><b>Keyed by the bracket, not by segment number.</b> The issue proposed
    /// segment numbers. They are not stable: a batch numbers the segments it was
    /// given, so the same paragraph is number 174 in a run over the whole document
    /// and number 12 in a run over the empty segments only. A register keyed to
    /// positions would quietly point at the wrong sentences for every scope but
    /// one. The bracket text is the same in both.</para>
    ///
    /// <para><b>Defaults hard to KEEP.</b> Uncertain detection, a failed model call,
    /// anything other than an exact match, or the same bracket decided both ways in
    /// one document - all come out KEEP. The costs are asymmetric: over-keeping
    /// leaves an ugly sentence that review catches, over-dropping silently deletes
    /// the applicant's text and may never be noticed.</para>
    /// </summary>
    public static class GlossRegister
    {
        public class Row
        {
            /// <summary>The bracket exactly as it appears in the source, brackets included.</summary>
            public string Parenthetical;
            /// <summary>The words it glosses, as the model read them.</summary>
            public string HeadTerm;
            /// <summary>True when translating the head makes the bracket redundant.</summary>
            public bool Drop;
            /// <summary>How many times this bracket occurs in the document.</summary>
            public int Occurrences;
        }

        /// <summary>
        /// The question put to the model, one candidate at a time. Deliberately a
        /// single well-posed question with a one-word answer: it is asked in
        /// isolation precisely so it is not competing with the rest of a prompt.
        /// </summary>
        public static string BuildQuestion(GlossDetector.Candidate candidate)
        {
            var sb = new StringBuilder();
            sb.AppendLine("A Dutch technical sentence contains a bracketed phrase that may be an English");
            sb.AppendLine("gloss of the words immediately before it.");
            sb.AppendLine();
            sb.AppendLine("SENTENCE:");
            sb.AppendLine(candidate.SegmentText);
            sb.AppendLine();
            sb.AppendLine("BRACKETED PHRASE: " + candidate.Parenthetical);
            sb.AppendLine();
            sb.AppendLine("Translate into English the Dutch term immediately before that bracket - the term");
            sb.AppendLine("the bracket would be glossing. Compare your translation with the bracketed text.");
            sb.AppendLine();
            sb.AppendLine("Answer with ONE line, in this exact form:");
            sb.AppendLine("DROP | <the head term> | <your English translation of it>");
            sb.AppendLine("KEEP | <the head term> | <your English translation of it>");
            sb.AppendLine();
            sb.AppendLine("Answer DROP only when your translation and the bracketed text are the SAME");
            sb.AppendLine("English wording, so that keeping the bracket would say the same thing twice.");
            sb.AppendLine("Answer KEEP in every other case, including when the bracket carries anything your");
            sb.AppendLine("translation does not - a narrower or wider term, an extra or missing word, an");
            sb.AppendLine("abbreviation, or a bracket wedged inside a compound. If you are unsure, answer KEEP.");
            sb.AppendLine();
            sb.Append("Output the single line and nothing else.");
            return sb.ToString();
        }

        /// <summary>
        /// Reads one answer. Anything unparseable is KEEP - see the class remarks.
        /// </summary>
        public static Row ParseAnswer(GlossDetector.Candidate candidate, string answer)
        {
            var row = new Row
            {
                Parenthetical = candidate.Parenthetical,
                HeadTerm = null,
                Drop = false,
                Occurrences = 1
            };
            if (string.IsNullOrWhiteSpace(answer)) return row;

            var line = answer.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .FirstOrDefault(l => l.TrimStart().StartsWith("DROP", StringComparison.OrdinalIgnoreCase)
                                               || l.TrimStart().StartsWith("KEEP", StringComparison.OrdinalIgnoreCase));
            if (line == null) return row;

            var parts = line.Split('|').Select(p => p.Trim()).ToList();
            row.Drop = parts[0].StartsWith("DROP", StringComparison.OrdinalIgnoreCase);
            if (parts.Count > 1 && parts[1].Length > 0) row.HeadTerm = parts[1];
            return row;
        }

        /// <summary>
        /// Folds per-occurrence answers into one row per bracket. The same bracket
        /// decided both ways in one document becomes KEEP: a register cannot say
        /// "sometimes drop this" without reintroducing the judgement it exists to
        /// remove, and KEEP is the safe half.
        /// </summary>
        public static List<Row> Consolidate(IEnumerable<Row> rows)
        {
            return (rows ?? Enumerable.Empty<Row>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Parenthetical))
                .GroupBy(r => r.Parenthetical, StringComparer.Ordinal)
                .Select(g => new Row
                {
                    Parenthetical = g.Key,
                    HeadTerm = g.Select(r => r.HeadTerm).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)),
                    Drop = g.All(r => r.Drop),
                    Occurrences = g.Count()
                })
                .OrderByDescending(r => r.Drop)
                .ThenBy(r => r.Parenthetical, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The register as it goes into the meta-prompt, for the generated prompt to
        /// carry verbatim. Returns null when there is nothing to say, so the caller
        /// emits no heading rather than an empty one.
        /// </summary>
        public static string Render(List<Row> rows)
        {
            if (rows == null || rows.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("Dutch technical writers gloss a Dutch term with its English equivalent in");
            sb.AppendLine("brackets. That does real work in the Dutch - it tells a Dutch reader what the");
            sb.AppendLine("thing is called in English. Translate the head term and some of these glosses");
            sb.AppendLine("collapse onto it, so the English would say the same thing twice.");
            sb.AppendLine();
            sb.AppendLine("Each bracket below has already been checked, once, against a translation of the");
            sb.AppendLine("term in front of it. The verdict is settled - reproduce this table in the prompt");
            sb.AppendLine("you write, verbatim and complete, and instruct the translating model to follow it");
            sb.AppendLine("as a lookup rather than re-deciding per segment:");
            sb.AppendLine();

            foreach (var r in rows)
            {
                var head = string.IsNullOrWhiteSpace(r.HeadTerm) ? "" : "  [after: " + r.HeadTerm + "]";
                var times = r.Occurrences > 1 ? "  (×" + r.Occurrences + ")" : "";
                sb.AppendLine((r.Drop ? "DROP the bracket:  " : "KEEP verbatim:     ")
                              + r.Parenthetical + head + times);
            }

            sb.AppendLine();
            sb.AppendLine("DROP means: omit that bracket and its contents from the English, because the");
            sb.AppendLine("translation of the term in front of it already says exactly that. Nothing else is");
            sb.AppendLine("omitted. KEEP means: translate the sentence with the bracket intact, however");
            sb.AppendLine("clumsy the result reads - the bracket carries something the head term does not.");
            sb.AppendLine();
            sb.AppendLine("A bracket that is not listed here is not covered by this rule and is KEPT. Do not");
            sb.AppendLine("generalise from the DROP rows to brackets that resemble them: deleting content");
            sb.Append("from a technical description is only safe where it has been shown to be empty.");
            return sb.ToString();
        }
    }
}
