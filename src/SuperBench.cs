using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// SuperBench (Trados #107): the same text translated by several models under
    /// identical settings, then judged blind by a top model. This is the shared
    /// half - the run record, the judge's prompts, the report - with no CAT-tool
    /// types in it; each plugin supplies the segments, runs its own batch pipeline
    /// per contender, and shows the result.
    ///
    /// Blind judging: contenders are labelled A, B, C in a shuffled order and the
    /// judge never sees a provider or model name, so no brand colours the verdict.
    /// The legend is added to the report afterwards.
    /// </summary>
    public static class SuperBench
    {
        public sealed class Contender
        {
            /// <summary>The blind label the judge sees: A, B, C...</summary>
            public string Label;
            public string Provider;
            public string Model;
            public string DisplayModel;
            /// <summary>One entry per source segment, index-aligned; null where the model gave nothing.</summary>
            public List<string> Translations = new List<string>();
            public decimal Cost;
            public bool CostKnown = true;
            public int InputTokens;
            public int OutputTokens;
            /// <summary>A machine-translation engine's: the characters it charged, in place of tokens.</summary>
            public int Characters;
            public TimeSpan Elapsed;
            public string Error;

            /// <summary>DeepL, Google Translate or Microsoft Translator rather than an AI model.</summary>
            public bool IsMachineTranslation => MachineTranslation.IsEngine(Provider);
        }

        public sealed class Run
        {
            public DateTime Timestamp = DateTime.Now;
            public string DocumentName;
            public string SourceLang;
            public string TargetLang;
            public List<string> Sources = new List<string>();
            public List<Contender> Contenders = new List<Contender>();
            public string JudgeProvider;
            public string JudgeModel;
            public string JudgeDisplayModel;
            public string JudgeReport;
            public decimal JudgeCost;
            public string JudgeError;
            /// <summary>Which prompt and how many terms travelled with every contender - stated in the report.</summary>
            public string SettingsSummary;
        }

        /// <summary>Assigns blind labels in a shuffled order. Call once, before judging.</summary>
        public static void AssignBlindLabels(Run run, Random rng = null)
        {
            rng = rng ?? new Random();
            var order = run.Contenders.OrderBy(_ => rng.Next()).ToList();
            for (int i = 0; i < order.Count; i++)
                order[i].Label = ((char)('A' + i)).ToString();
        }

        /// <summary>The contenders in label order (A, B, C), for anything shown to the judge or the reader.</summary>
        public static List<Contender> InLabelOrder(Run run) =>
            run.Contenders.OrderBy(c => c.Label, StringComparer.Ordinal).ToList();

        // ─── The judge ────────────────────────────────────────────────────────────────

        public static string BuildJudgeSystemPrompt(Run run)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a senior reviser of " + (run.SourceLang ?? "source") + " to " + (run.TargetLang ?? "target") +
                          " translations of patent, legal and technical texts, judging machine translations for a professional translator.");
            sb.AppendLine();
            sb.AppendLine("You will receive the source segments and " + run.Contenders.Count + " candidate translations of each, labelled " +
                          string.Join(", ", InLabelOrder(run).Select(c => c.Label)) + ". You do not know which system produced which; judge the text alone.");
            sb.AppendLine();
            sb.AppendLine("Judge each candidate on, in this order of weight:");
            sb.AppendLine("1. ACCURACY - meaning preserved, nothing added, nothing dropped, numbers and references intact.");
            sb.AppendLine("2. TERMINOLOGY - the approved terms used where given; consistent renderings across segments.");
            sb.AppendLine("3. TAGS AND FORMATTING - every inline tag reproduced verbatim, same count, order and nesting; no tag invented or lost.");
            sb.AppendLine("4. REGISTER AND FLUENCY - reads as a native professional translation of this text type.");
            sb.AppendLine("5. INSTRUCTIONS - follows the translation instructions given below where they apply.");
            sb.AppendLine();
            sb.AppendLine("Write a report in Markdown, SHORT and concrete, for a translator in a hurry:");
            sb.AppendLine("- '## Verdict': one sentence naming the best candidate for THIS text and why, then a ranked list (best first) with one line each.");
            sb.AppendLine("- '## Errors': the significant errors you found, grouped by candidate, each as 'segment N: what is wrong' - quote the words, at most eight per candidate. Say 'none found' when that is true.");
            sb.AppendLine("- '## Terminology': how each candidate handled the approved terms, in two or three lines.");
            sb.AppendLine("- '## Tags': any candidate that lost, added or moved a tag, with the segment numbers; otherwise 'all candidates kept the tags'.");
            sb.AppendLine("- '## Recommendation': which candidate to use for this project, and whether the difference is worth paying for.");
            sb.AppendLine();
            sb.AppendLine("Refer to candidates only by their labels. Do not guess or mention which system made them. Do not rewrite the translations yourself. Do not praise; describe.");
            return sb.ToString();
        }

        public static string BuildJudgeUserPrompt(Run run, string translationInstructions, string terminologyBlock)
        {
            // "The project's", not "the candidates were given": a machine-translation
            // contender is given neither, and saying which candidate was not would
            // unblind the judge. Every candidate is judged against what the job needs
            // (docs/design/mt-engines.md, decision 3); the report, not the judge,
            // says who saw what.
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(translationInstructions))
            {
                sb.AppendLine("# THE PROJECT'S TRANSLATION INSTRUCTIONS");
                sb.AppendLine(translationInstructions.Trim());
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(terminologyBlock))
            {
                sb.AppendLine("# THE PROJECT'S APPROVED TERMS");
                sb.AppendLine(terminologyBlock.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("# SEGMENTS AND CANDIDATES");
            sb.AppendLine("Inline tags appear as <tN>...</tN> or <tN/> placeholders and must be reproduced exactly.");
            sb.AppendLine();
            var ordered = InLabelOrder(run);
            for (int i = 0; i < run.Sources.Count; i++)
            {
                sb.AppendLine("## Segment " + (i + 1));
                sb.AppendLine("SOURCE: " + (run.Sources[i] ?? ""));
                foreach (var c in ordered)
                {
                    var t = i < c.Translations.Count ? c.Translations[i] : null;
                    sb.AppendLine(c.Label + ": " + (string.IsNullOrEmpty(t) ? "(no translation returned)" : t));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ─── The report ───────────────────────────────────────────────────────────────

        public static string RenderMarkdown(Run run)
        {
            var sb = new StringBuilder();
            var ordered = InLabelOrder(run);
            sb.AppendLine("# SuperBench – " + (run.DocumentName ?? "(document)"));
            sb.AppendLine();
            sb.AppendLine("- Run: " + run.Timestamp.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("- Languages: " + run.SourceLang + " → " + run.TargetLang);
            sb.AppendLine("- Segments: " + run.Sources.Count);
            if (!string.IsNullOrWhiteSpace(run.SettingsSummary)) sb.AppendLine("- Settings: " + run.SettingsSummary);
            var engines = ordered.Where(c => c.IsMachineTranslation).Select(c => MachineTranslation.DisplayName(c.Provider)).ToList();
            if (engines.Count > 0)
                sb.AppendLine("- Machine translation: " + JoinNames(engines) + (engines.Count == 1 ? " was" : " were")
                              + " given no prompt and no termbase. The judge marked " + (engines.Count == 1 ? "it" : "them")
                              + " against both, like every candidate.");
            sb.AppendLine("- Judge: " + (run.JudgeDisplayModel ?? run.JudgeModel) + (run.JudgeCost > 0 ? " (" + Money(run.JudgeCost) + ")" : ""));
            sb.AppendLine();

            sb.AppendLine("## Contenders");
            sb.AppendLine();
            sb.AppendLine("| Label | Model | Cost | Tokens in / out · characters | Time | Segments returned |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var c in ordered)
            {
                var returned = c.Translations.Count(t => !string.IsNullOrEmpty(t));
                var model = c.IsMachineTranslation
                    ? MachineTranslation.DisplayName(c.Provider) + " (machine translation)"
                    : (c.DisplayModel ?? c.Model) + " (" + c.Provider + ")";
                var usage = c.IsMachineTranslation
                    ? c.Characters.ToString("N0") + " characters" + (c.Provider == MachineTranslation.Google ? " (computed)" : "")
                    : c.InputTokens.ToString("N0") + " / " + c.OutputTokens.ToString("N0");
                sb.AppendLine("| **" + c.Label + "** | " + model + " | " +
                              (c.CostKnown ? Money(c.Cost) : "unknown") + " | " + usage +
                              " | " + c.Elapsed.TotalSeconds.ToString("F0") + " s | " + returned + " / " + run.Sources.Count +
                              (string.IsNullOrEmpty(c.Error) ? "" : " – " + c.Error) + " |");
            }
            if (engines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Machine-translation costs are at list price, in US dollars, for characters beyond what a plan includes. " +
                              "Within a free allowance, or the characters a plan includes, they cost nothing extra.");
            }
            sb.AppendLine();

            sb.AppendLine("## Judge's report");
            sb.AppendLine();
            sb.AppendLine("Legend: " + string.Join(", ", ordered.Select(c => c.Label + " = " + (c.DisplayModel ?? c.Model))));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(run.JudgeReport)) sb.AppendLine(run.JudgeReport.Trim());
            else sb.AppendLine("(no report" + (string.IsNullOrEmpty(run.JudgeError) ? "" : ": " + run.JudgeError) + ")");
            sb.AppendLine();

            sb.AppendLine("## Translations");
            sb.AppendLine();
            sb.AppendLine("| # | Source | " + string.Join(" | ", ordered.Select(c => c.Label + " – " + (c.DisplayModel ?? c.Model))) + " |");
            sb.AppendLine("|---|---|" + string.Concat(ordered.Select(_ => "---|")));
            for (int i = 0; i < run.Sources.Count; i++)
            {
                sb.Append("| " + (i + 1) + " | " + Cell(run.Sources[i]));
                foreach (var c in ordered)
                    sb.Append(" | " + Cell(i < c.Translations.Count ? c.Translations[i] : null));
                sb.AppendLine(" |");
            }
            return sb.ToString();
        }

        public static string DefaultFileName(Run run)
        {
            var doc = run.DocumentName ?? "";
            var dot = doc.LastIndexOf('.');
            if (dot > 0) doc = doc.Substring(0, dot);
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) doc = doc.Replace(c, '_');
            return run.Timestamp.ToString("yyyy-MM-dd HHmmss") + " " + (doc.Length > 0 ? doc + " " : "") + "SuperBench.md";
        }

        private static string Cell(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");

        private static string Money(decimal usd) => "$" + usd.ToString("0.00##");

        /// <summary>"DeepL", "DeepL and Google Translate", "DeepL, Google Translate and Microsoft Translator".</summary>
        private static string JoinNames(IList<string> names) =>
            names.Count <= 1 ? string.Concat(names)
            : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
    }
}
