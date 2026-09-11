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
            /// <summary>The model's English rendering of the head term.</summary>
            public string Translation;
            /// <summary>
            /// True when <see cref="Translation"/> and the bracket are the same
            /// wording. Decided in code by string comparison, never by the model -
            /// see the class remarks.
            /// </summary>
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
            sb.AppendLine("Translate into English the Dutch term immediately before that bracket - the");
            sb.AppendLine("term the bracket would be glossing. Translate the term ONLY: not the sentence,");
            sb.AppendLine("not the bracket, and not a longer phrase than the term itself.");
            sb.AppendLine();
            sb.AppendLine("Answer with ONE line, in this exact form, and nothing else:");
            sb.AppendLine("<the Dutch term> | <your English translation of it>");
            sb.AppendLine();
            sb.Append("Do not comment, do not explain, and do not say whether the bracket should be kept.");
            return sb.ToString();
        }

        /// <summary>
        /// Reads one answer and decides the row. The model supplies a translation of
        /// the head term; whether that translation and the bracket are the same
        /// wording is settled HERE, by string comparison.
        ///
        /// <para>It used to be settled by the model, which was asked to translate and
        /// to judge identity in one answer. Measured on a real document, the judging
        /// half failed badly: of five DROP verdicts only one was defensible, and two
        /// were on the very fixtures chosen because the gloss is nearly-but-not-quite
        /// the head term - "(expandable graphite)" where the head is expandable
        /// graphite FLAKES, and "(volatile organic compounds)" where the head is the
        /// abbreviation VOC. The model read "close enough" as identical, confidently,
        /// which no KEEP-on-failure default can catch. Translating is a thing a model
        /// is good at; deciding whether two strings are equal is a thing code is good
        /// at.</para>
        ///
        /// Anything unparseable is KEEP - see the class remarks.
        /// </summary>
        public static Row ParseAnswer(GlossDetector.Candidate candidate, string answer)
        {
            var row = new Row
            {
                Parenthetical = candidate.Parenthetical,
                Drop = false,
                Occurrences = 1
            };
            if (string.IsNullOrWhiteSpace(answer)) return row;

            var line = answer.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Trim())
                             .FirstOrDefault(l => l.IndexOf('|') > 0);
            if (line == null) return row;

            var parts = line.Split('|').Select(p => p.Trim()).ToList();
            if (parts.Count < 2) return row;

            row.HeadTerm = parts[0].Length > 0 ? parts[0] : null;
            row.Translation = parts[1].Length > 0 ? parts[1] : null;
            if (row.Translation == null) return row;

            row.Drop = SameWording(row.Translation, candidate.GlossText);
            return row;
        }

        /// <summary>
        /// Whether two English strings are the same wording. Deliberately strict:
        /// case and surrounding punctuation are noise, everything else is a
        /// difference. "Expandable graphite flakes" is NOT "expandable graphite" -
        /// that extra word is the whole reason the bracket has to stay.
        /// </summary>
        private static bool SameWording(string a, string b)
        {
            var x = Normalise(a);
            var y = Normalise(b);
            return x.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalise(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var trimmed = s.Trim().Trim(
                '"', '“', '”', '‘', '’', '\'',
                '.', ',', ';', ':', '(', ')');
            var sb = new StringBuilder(trimmed.Length);
            bool lastWasSpace = false;
            foreach (var ch in trimmed)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                }
                else { sb.Append(ch); lastWasSpace = false; }
            }
            return sb.ToString().TrimEnd();
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
                    Translation = g.Select(r => r.Translation).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
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
                // The rendering the verdict rests on. A DROP is only valid while the
                // head term is translated this way: render it differently and the
                // bracket is no longer a duplicate, so the reader of this table has
                // to be told which wording it was decided against.
                var rendered = r.Drop && !string.IsNullOrWhiteSpace(r.Translation)
                    ? "  [only when rendered: " + r.Translation + "]" : "";
                var times = r.Occurrences > 1 ? "  (×" + r.Occurrences + ")" : "";
                sb.AppendLine((r.Drop ? "DROP the bracket:  " : "KEEP verbatim:     ")
                              + r.Parenthetical + head + rendered + times);
            }

            sb.AppendLine();
            sb.AppendLine("DROP means: omit that bracket and its contents from the English, because the");
            sb.AppendLine("translation of the term in front of it already says exactly that. Nothing else is");
            sb.AppendLine("omitted. KEEP means: translate the sentence with the bracket intact, however");
            sb.AppendLine("clumsy the result reads - the bracket carries something the head term does not.");
            sb.AppendLine();
            sb.AppendLine("A bracket that is not listed here is not covered by this rule and is KEPT. Do not");
            sb.AppendLine("generalise from the DROP rows to brackets that resemble them: deleting content");
            sb.AppendLine("from a technical description is only safe where it has been shown to be empty.");
            sb.AppendLine();
            sb.AppendLine("These verdicts are SETTLED. They were decided by comparing the bracket against a");
            sb.AppendLine("translation of the term in front of it, character by character, outside the");
            sb.AppendLine("translation task. Carry them into the prompt as they stand - do not re-argue a");
            sb.AppendLine("row, do not add rows of your own, and do not replace the table with a rule and");
            sb.AppendLine("some examples. A rule invites the translating model to decide per segment, which");
            sb.AppendLine("is exactly what this table exists to stop.");
            sb.AppendLine();
            sb.AppendLine("DROPPING A BRACKET IS A SILENT CORRECTION, so the prompt you write MUST require");
            sb.AppendLine("the translator-comment marker for it, exactly as it does for every other silent");
            sb.AppendLine("correction:");
            sb.AppendLine();
            sb.AppendLine("    [[TC: duplicate English gloss omitted]]");
            sb.AppendLine();
            sb.Append("Never instruct the translating model to drop a bracket without that marker. "
                    + "Nothing may leave the source silently, however certainly redundant - the "
                    + "translator decides in the grid whether the deletion stands, and cannot decide "
                    + "about a deletion nobody mentioned.");
            return sb.ToString();
        }
    }
}
