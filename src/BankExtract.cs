using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// The part of a memory bank worth sending for one job, rather than the whole
    /// bank.
    ///
    /// <para>Every request used to carry the whole bank up to a token budget, and
    /// when a bank outgrew the budget, what survived was decided by a fixed order
    /// (<see cref="KbContext.TrimToTokenBudget"/>), not by what the job needed. A
    /// large <c>_shared</c> terminology table is mostly noise for any one
    /// document - house defaults for patents reaching a training deck - and it is
    /// also where banks grow fastest.</para>
    ///
    /// <para>So, once per job:</para>
    /// <list type="number">
    /// <item><b>Terminology, with no AI.</b> Only table rows whose term occurs in
    /// the document are kept - in EITHER term column, because a bank's tables
    /// run in whichever direction the translator wrote them, and on a job in the
    /// other direction every relevant term sits in the target column.</item>
    /// <item><b>Prose articles, whenever selection runs.</b> The host is asked
    /// which of the articles matter, through
    /// <c>selectArticles</c> - an AI call in the products, a plain function in the
    /// tests. A failure there means today's behaviour: every article kept, the
    /// usual trimming applied.</item>
    /// <item><b>Never selected away:</b> the client's brief, its domain article and
    /// its style guide. Only the budget can drop those, as before.</item>
    /// </list>
    ///
    /// <para>Below <see cref="Threshold"/> nothing is selected: a small bank is
    /// sent whole, exactly as before, and costs no AI call. Nothing here reads a
    /// file, calls a model or keeps state - the host decides when to build an
    /// extract, caches it for the job so the prompt stays byte-identical (and
    /// cached by the provider), and writes <see cref="FormatFile"/> where the
    /// translator can read it.</para>
    /// </summary>
    public static class BankExtract
    {
        /// <summary>
        /// Banks at or below this many tokens are sent whole. Selection is for
        /// banks large enough that sending all of them costs relevance; a small
        /// one is cheaper to send than to reason about.
        /// </summary>
        public const int Threshold = 8000;

        /// <summary>How much of the document the article choice is shown.</summary>
        public const int SampleChars = 12000;

        /// <summary>How much of each article the article choice is shown.</summary>
        public const int OpeningChars = 400;

        /// <summary>
        /// Selects the part of <paramref name="full"/> relevant to
        /// <paramref name="documentText"/>, then applies the budget as always.
        ///
        /// <para><paramref name="full"/> must be UNTRIMMED - load it with a token
        /// budget of 0 - and is modified in place. <paramref name="earlierChoice"/>
        /// is the article choice made earlier in the same job: a job's topic does
        /// not change as more of its document is seen, so the choice is made once
        /// and reused, and the host pays for one call per job, not one per
        /// rebuild.</para>
        /// </summary>
        public static BankExtractResult Build(
            KbContext full,
            string documentText,
            string sourceLang,
            string targetLang,
            int tokenBudget,
            Func<ArticleSelectionRequest, IList<string>> selectArticles,
            IList<string> earlierChoice = null,
            string chooserName = null)
        {
            if (full == null) return null;

            var result = new BankExtractResult { Context = full, TokensBefore = full.EstimatedTokens };

            if (full.AssistantOnlyPaths.Count > 0)
                result.Report.Add("Left out as notes for the assistants only (audience: assistant): "
                    + string.Join(", ", full.AssistantOnlyPaths) + ".");

            if (result.TokensBefore <= Threshold)
            {
                result.Report.Add("Sent whole: the bank is " + Tokens(result.TokensBefore)
                    + ", at or under the " + Tokens(Threshold) + " threshold for selecting.");
                return Finish(result, tokenBudget, null);
            }

            if (string.IsNullOrWhiteSpace(documentText))
            {
                result.Report.Add("Sent whole within the budget: no document text is available yet to select against.");
                return Finish(result, tokenBudget, null);
            }

            result.Selected = true;

            // ---- 1. terminology, deterministic ----------------------------------
            var termTexts = new List<string>(full.TerminologyArticles);
            if (!string.IsNullOrWhiteSpace(full.SharedTerminologyText)) termTexts.Add(full.SharedTerminologyText);

            var doc = new DocumentTerms(documentText, TerminologyFilter.LongestTerm(termTexts));

            for (var i = 0; i < full.TerminologyArticles.Count; i++)
            {
                var filtered = TerminologyFilter.Filter(full.TerminologyArticles[i], doc, out var kept, out var total);
                if (total == 0) continue;

                full.TerminologyArticles[i] = filtered;
                var path = i < full.TerminologyPaths.Count ? full.TerminologyPaths[i] : MemoryBankReader.TerminologyFile;
                result.Report.Add("Terminology (" + (path ?? "client") + "): " + kept.ToString("N0", CultureInfo.InvariantCulture)
                    + " of " + total.ToString("N0", CultureInfo.InvariantCulture) + " rows occur in this document.");
            }

            if (!string.IsNullOrWhiteSpace(full.SharedTerminologyText))
            {
                var filtered = TerminologyFilter.Filter(full.SharedTerminologyText, doc, out var kept, out var total);
                if (total > 0)
                {
                    full.SharedTerminologyText = string.IsNullOrWhiteSpace(filtered) ? null : filtered;
                    result.Report.Add("Terminology (" + MemoryBankReader.SharedBankName + "/" + MemoryBankReader.TerminologyFile + "): "
                        + kept.ToString("N0", CultureInfo.InvariantCulture) + " of "
                        + total.ToString("N0", CultureInfo.InvariantCulture) + " rows occur in this document.");
                }
            }

            // ---- 2. prose articles, whenever selection runs ---------------------
            // Not only when the bank is still over the budget. The first live run
            // had a bank that fitted after terminology filtering, so the question
            // was never asked - and a file of assistant workflow notes, a third of
            // everything sent, went with every row of a job it had nothing to do
            // with. Fitting the budget is not the same as being relevant. One
            // request per job, reused; a failure still keeps every article.
            var candidates = Candidates(full);
            if (candidates.Count > 0)
            {
                IList<string> chosen = earlierChoice;
                string failure = null;

                if (chosen == null && selectArticles != null)
                {
                    try
                    {
                        chosen = selectArticles(new ArticleSelectionRequest
                        {
                            DocumentSample = documentText.Length <= SampleChars ? documentText : documentText.Substring(0, SampleChars),
                            SourceLang = sourceLang,
                            TargetLang = targetLang,
                            Candidates = candidates,
                            TokenBudget = tokenBudget
                        });
                        result.AskedForChoice = true;
                        if (chosen == null) failure = "the article choice gave no usable answer";
                    }
                    catch (Exception ex)
                    {
                        failure = "the article choice failed (" + ex.Message + ")";
                        chosen = null;
                    }
                }

                if (chosen != null)
                {
                    var keep = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);
                    var left = RemoveArticles(full, keep);
                    result.ArticleChoice = chosen.ToList();
                    result.Report.Add("Articles: " + (candidates.Count - left.Count) + " of " + candidates.Count
                        + " chosen as relevant to this document"
                        + (string.IsNullOrWhiteSpace(chooserName) || earlierChoice != null ? "" : " by " + chooserName)
                        + (left.Count > 0 ? "; left out: " + string.Join(", ", left) : "") + ".");
                }
                else
                {
                    result.Report.Add("Articles: all " + candidates.Count + " kept - "
                        + (failure ?? "no way to choose was available")
                        + ", so the usual trimming order applies.");
                }
            }

            return Finish(result, tokenBudget, doc);
        }

        /// <summary>
        /// The budget, applied in the one order <see cref="KbContext.TrimToTokenBudget"/>
        /// keeps. When the document is known, a terminology file that order would
        /// drop whole is first cut by rows instead - the rows this document uses
        /// least - because every row left by now was kept for being relevant.
        /// </summary>
        private static BankExtractResult Finish(BankExtractResult result, int tokenBudget, DocumentTerms doc)
        {
            var cutRows = 0;
            Func<string, int, string> shrink = null;
            if (doc != null)
            {
                shrink = (text, maxChars) =>
                {
                    var shrunk = TerminologyFilter.Shrink(text, doc, maxChars, out var cut);
                    cutRows += cut;
                    return shrunk;
                };
            }

            result.Context.TrimToTokenBudget(tokenBudget, shrink);

            if (cutRows > 0)
                result.Report.Add("Over the " + Tokens(tokenBudget) + " budget: " + cutRows.ToString("N0", CultureInfo.InvariantCulture)
                    + " terminology rows cut, those this document uses least first, in the usual order.");
            if (result.Context.TrimmedPaths.Count > 0)
                result.Report.Add("Over the " + Tokens(tokenBudget) + " budget even so; left out by the usual order: "
                    + string.Join(", ", result.Context.TrimmedPaths) + ".");
            result.TokensAfter = result.Context.EstimatedTokens;
            return result;
        }

        /// <summary>
        /// The articles the choice may remove: the extras of both layers. The
        /// brief, the domain article, the style guides and terminology are not
        /// candidates - they are never selected away.
        /// </summary>
        private static List<ArticleCandidate> Candidates(KbContext ctx)
        {
            var list = new List<ArticleCandidate>();
            for (var i = 0; i < ctx.ExtraArticles.Count; i++)
                list.Add(Candidate(i < ctx.ExtraPaths.Count ? ctx.ExtraPaths[i] : "article-" + i, ctx.ExtraArticles[i]));
            for (var i = 0; i < ctx.SharedExtraArticles.Count; i++)
                list.Add(Candidate(i < ctx.SharedExtraPaths.Count ? ctx.SharedExtraPaths[i] : "shared-article-" + i, ctx.SharedExtraArticles[i]));
            return list;
        }

        private static ArticleCandidate Candidate(string path, string text)
        {
            text = text ?? "";
            var title = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("#", StringComparison.Ordinal));
            return new ArticleCandidate
            {
                Path = path,
                Title = title == null ? null : title.TrimStart('#', ' '),
                Opening = text.Length <= OpeningChars ? text.Trim() : text.Substring(0, OpeningChars).Trim() + " …",
                EstimatedTokens = text.Length / 4
            };
        }

        /// <summary>Removes every extra article whose path is not kept; returns the removed paths.</summary>
        private static List<string> RemoveArticles(KbContext ctx, HashSet<string> keep)
        {
            var removed = new List<string>();
            RemoveFrom(ctx.ExtraArticles, ctx.ExtraPaths, keep, removed);
            RemoveFrom(ctx.SharedExtraArticles, ctx.SharedExtraPaths, keep, removed);
            return removed;
        }

        private static void RemoveFrom(List<string> articles, List<string> paths, HashSet<string> keep, List<string> removed)
        {
            for (var i = articles.Count - 1; i >= 0; i--)
            {
                var path = i < paths.Count ? paths[i] : null;
                if (path != null && keep.Contains(path)) continue;

                articles.RemoveAt(i);
                if (i < paths.Count) paths.RemoveAt(i);
                removed.Insert(0, path ?? "(unnamed)");
            }
        }

        // ---- the article choice, as a prompt and a parser ----------------------

        /// <summary>
        /// The instructions for the article choice. The host sends these with
        /// <see cref="SelectionUserPrompt"/> to a model and hands the reply to
        /// <see cref="ParseSelection"/>.
        /// </summary>
        public const string SelectionSystemPrompt =
            "You choose which reference articles from a translator's notes are relevant to one document. "
            + "You are shown the start of the document and a list of articles, each with its path and opening. "
            + "Keep an article if it could affect how this document is translated: its subject, its client, its "
            + "document type, or rules that apply to all work. Leave out articles about unrelated subjects or "
            + "clients. When unsure, keep it. Reply with a JSON array of the paths to keep and nothing else, "
            + "for example [\"method.md\", \"_shared/patents.md\"]. An empty array means none is relevant.";

        public static string SelectionUserPrompt(ArticleSelectionRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Language pair: " + (request.SourceLang ?? "?") + " to " + (request.TargetLang ?? "?"));
            sb.AppendLine();
            sb.AppendLine("## The start of the document");
            sb.AppendLine();
            sb.AppendLine(request.DocumentSample ?? "");
            sb.AppendLine();
            sb.AppendLine("## The articles");
            foreach (var c in request.Candidates ?? new List<ArticleCandidate>())
            {
                sb.AppendLine();
                sb.AppendLine("### " + c.Path + (string.IsNullOrWhiteSpace(c.Title) ? "" : " - " + c.Title)
                    + " (about " + c.EstimatedTokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens)");
                sb.AppendLine(c.Opening ?? "");
            }
            sb.AppendLine();
            sb.Append("Reply with the JSON array of paths to keep.");
            return sb.ToString();
        }

        /// <summary>
        /// The paths a reply chose, restricted to the candidates offered, or null
        /// when the reply holds no JSON array of strings - which the caller treats
        /// as "no choice made", never as "keep nothing". An empty array is a real
        /// answer and comes back as an empty list.
        /// </summary>
        public static IList<string> ParseSelection(string reply, IList<ArticleCandidate> candidates)
        {
            if (string.IsNullOrWhiteSpace(reply)) return null;

            var start = reply.IndexOf('[');
            var end = reply.LastIndexOf(']');
            if (start < 0 || end <= start) return null;

            var body = reply.Substring(start + 1, end - start - 1);
            if (body.Trim().Length == 0) return new List<string>();

            var items = Regex.Matches(body, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<Match>()
                .Select(m => m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/"))
                .ToList();
            if (items.Count == 0) return null;

            var known = new HashSet<string>((candidates ?? new List<ArticleCandidate>()).Select(c => c.Path),
                StringComparer.OrdinalIgnoreCase);
            return items.Where(known.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// A readable file for the translator: what this job sends from their
        /// banks, what it leaves out and why, then the text itself. Something
        /// left out that should not have been must be findable, not invisible.
        /// </summary>
        public static string FormatFile(BankExtractResult result, string label, DateTime madeLocal)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Memory bank extract");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(label)) sb.AppendLine("For: " + label + "  ");
            sb.AppendLine("Made: " + madeLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  ");
            sb.AppendLine("Size: " + Tokens(result?.TokensAfter ?? 0) + " sent, of " + Tokens(result?.TokensBefore ?? 0) + " in the banks");
            sb.AppendLine();
            sb.AppendLine("This file is written for you to read; editing it changes nothing. It is made again");
            sb.AppendLine("when the bank or the document changes.");
            sb.AppendLine();
            sb.AppendLine("## What was selected");
            sb.AppendLine();
            foreach (var line in result?.Report ?? new List<string>()) sb.AppendLine("- " + line);
            sb.AppendLine();
            sb.AppendLine("## The text sent to the model");
            sb.AppendLine();
            sb.AppendLine(MemoryBankReader.FormatForPrompt(result?.Context) ?? "(nothing)");
            return sb.ToString();
        }

        private static string Tokens(int n) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " tokens";
    }

    public sealed class BankExtractResult
    {
        /// <summary>The selected, budget-trimmed context: what to send.</summary>
        public KbContext Context;

        /// <summary>True when selection ran; false when the bank was sent whole.</summary>
        public bool Selected;

        /// <summary>True when the article choice was asked for in this build.</summary>
        public bool AskedForChoice;

        /// <summary>The paths the article choice kept, to reuse for the rest of the job; null when none was made.</summary>
        public List<string> ArticleChoice;

        public int TokensBefore;
        public int TokensAfter;

        /// <summary>One line per decision, for the extract file and the log.</summary>
        public List<string> Report = new List<string>();
    }

    public sealed class ArticleSelectionRequest
    {
        public string DocumentSample;
        public string SourceLang;
        public string TargetLang;
        public List<ArticleCandidate> Candidates;
        public int TokenBudget;
    }

    public sealed class ArticleCandidate
    {
        public string Path;
        public string Title;
        public string Opening;
        public int EstimatedTokens;
    }

    /// <summary>
    /// Which terms occur in a document, answered in constant time per term.
    ///
    /// <para>The document is read ONCE: into its words and every run of up to
    /// <c>longestTerm</c> consecutive words, lower-cased, in a hash set. A term is
    /// then a lookup, not a search. Checking each row of a 12,000-row table by
    /// searching a 5,000-segment document is seconds; this is milliseconds.</para>
    ///
    /// <para>Compounds - Dutch and German write "inlegschoeisel" where the term is
    /// "schoeisel" - are found through a second set, built only if needed, of the
    /// PREFIXES and SUFFIXES of at least <see cref="MinCompoundLength"/> letters of
    /// every distinct document word: the parts of a compound sit at its ends
    /// ("inlegSCHOEISEL", "SCHOEISELmaat"). Every fragment of a word would find a
    /// middle part too ("inlegSCHOEISELmaat"), but costs (L-4)(L-3)/2 entries per
    /// word against 2(L-4) - measured at 76 MB against 17 MB on a Dutch-like
    /// 5,000-segment document (12,000 distinct words, median length 11) - and a term found only in the middle of a compound
    /// almost always also occurs on its own somewhere in the same document. Only
    /// single-word terms that missed the word set are looked up there, so a short
    /// word does not match inside everything.</para>
    /// </summary>
    public class DocumentTerms
    {
        /// <summary>Shortest term matched inside a longer word.</summary>
        public const int MinCompoundLength = 5;

        /// <summary>Longest run of words indexed; longer terms fall back to a search.</summary>
        public const int MaxWords = 8;

        private const int MaxWordLengthForFragments = 40;

        private readonly Dictionary<string, int> _runs = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _words = new HashSet<string>(StringComparer.Ordinal);
        private readonly int _maxWords;
        private readonly string _joined;
        private HashSet<string> _fragments;

        public DocumentTerms(string text, int longestTerm)
        {
            _maxWords = Math.Max(1, Math.Min(MaxWords, longestTerm));
            var words = Words(text);
            foreach (var w in words) _words.Add(w);

            var sb = new StringBuilder();
            for (var i = 0; i < words.Count; i++)
            {
                sb.Clear();
                for (var n = 0; n < _maxWords && i + n < words.Count; n++)
                {
                    if (n > 0) sb.Append(' ');
                    sb.Append(words[i + n]);
                    var run = sb.ToString();
                    _runs.TryGetValue(run, out var seen);
                    _runs[run] = seen + 1;
                }
            }

            _joined = " " + string.Join(" ", words) + " ";
        }

        /// <summary>True when <paramref name="term"/> occurs in the document.</summary>
        public bool Contains(string term) => Count(term) > 0;

        /// <summary>
        /// How often <paramref name="term"/> occurs as words in the document; 1 for
        /// a term found only inside a compound, or only by searching because it is
        /// longer than the index; 0 when absent. Decides which rows are cut first
        /// when even the relevant terminology is too big.
        /// </summary>
        public virtual int Count(string term)
        {
            var words = Words(term);
            if (words.Count == 0) return 0;

            var key = string.Join(" ", words);
            if (words.Count <= _maxWords)
            {
                if (_runs.TryGetValue(key, out var n)) return n;
            }
            else if (_joined.IndexOf(" " + key + " ", StringComparison.Ordinal) >= 0)
            {
                return 1;
            }

            return words.Count == 1 && key.Length >= MinCompoundLength && Fragments().Contains(key) ? 1 : 0;
        }

        private HashSet<string> Fragments()
        {
            if (_fragments != null) return _fragments;

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in _words)
            {
                if (w.Length <= MinCompoundLength || w.Length > MaxWordLengthForFragments) continue;
                for (var len = MinCompoundLength; len < w.Length; len++)
                {
                    set.Add(w.Substring(0, len));              // prefix: "schoeisel" in "schoeiselmaat"
                    set.Add(w.Substring(w.Length - len, len)); // suffix: "schoeisel" in "inlegschoeisel"
                }
            }
            _fragments = set;
            return set;
        }

        /// <summary>Lower-cased runs of letters and digits; everything else separates words.</summary>
        internal static List<string> Words(string text)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(text)) return words;

            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) words.Add(sb.ToString());
            return words;
        }
    }

    /// <summary>
    /// Filters a bank's terminology Markdown to the table rows that occur in a
    /// document. Everything that is not a table row - headings, the explanation
    /// under them - is kept as written; a table none of whose rows occur is
    /// dropped whole, header and all.
    /// </summary>
    public static class TerminologyFilter
    {
        private static readonly Regex Separator = new Regex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex Parenthetical = new Regex(@"\([^)]*\)", RegexOptions.Compiled);

        /// <summary>
        /// <paramref name="markdown"/> with only the rows whose source or target
        /// term occurs in <paramref name="doc"/>. <paramref name="total"/> is 0 when
        /// the text holds no table rows at all, in which case it is returned as is.
        /// </summary>
        public static string Filter(string markdown, DocumentTerms doc, out int kept, out int total)
        {
            kept = 0;
            total = 0;
            if (string.IsNullOrEmpty(markdown) || doc == null) return markdown;

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var output = new List<string>(lines.Length);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsTableLine(lines[i]))
                {
                    output.Add(lines[i]);
                    continue;
                }

                // A table: header, separator, rows.
                var block = new List<string>();
                while (i < lines.Length && IsTableLine(lines[i])) block.Add(lines[i++]);
                i--;

                var hasHeader = block.Count >= 2 && Separator.IsMatch(block[1].Trim());
                var firstRow = hasHeader ? 2 : 0;
                var keptRows = new List<string>();

                for (var r = firstRow; r < block.Count; r++)
                {
                    var cells = Cells(block[r]);
                    if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;

                    total++;
                    if (Occurs(cells, doc))
                    {
                        kept++;
                        keptRows.Add(block[r]);
                    }
                }

                if (keptRows.Count == 0) continue;
                if (hasHeader) { output.Add(block[0]); output.Add(block[1]); }
                output.AddRange(keptRows);
            }

            return total == 0 ? markdown : string.Join("\r\n", output);
        }

        /// <summary>
        /// Cuts table rows from <paramref name="markdown"/> until it is at most
        /// <paramref name="maxChars"/> long, the rows whose terms occur least often
        /// in the document going first. Headings and prose are never cut; a table
        /// left with no rows loses its header too.
        /// </summary>
        public static string Shrink(string markdown, DocumentTerms doc, int maxChars, out int cut)
        {
            cut = 0;
            if (string.IsNullOrEmpty(markdown) || doc == null || markdown.Length <= maxChars) return markdown;

            var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
            var rows = new List<KeyValuePair<int, int>>();   // line index, how often its term occurs
            for (var i = 0; i < lines.Count; i++)
            {
                if (!IsTableLine(lines[i]) || Separator.IsMatch(lines[i].Trim())) continue;
                var isHeader = i + 1 < lines.Count && IsTableLine(lines[i + 1]) && Separator.IsMatch(lines[i + 1].Trim());
                if (isHeader) continue;
                rows.Add(new KeyValuePair<int, int>(i,
                    Cells(lines[i]).Take(2).SelectMany(Variants).Select(doc.Count).DefaultIfEmpty(0).Max()));
            }

            var length = markdown.Length;
            var drop = new HashSet<int>();
            // Least frequent first; among equals the later row first, so the order
            // the translator wrote them in decides what stays.
            foreach (var row in rows.OrderBy(r => r.Value).ThenByDescending(r => r.Key))
            {
                if (length <= maxChars) break;
                drop.Add(row.Key);
                length -= lines[row.Key].Length + 2;
                cut++;
            }

            var kept = lines.Where((l, i) => !drop.Contains(i));
            return Filter(string.Join("\n", kept), new AlwaysDocument(), out _, out _);
        }

        /// <summary>
        /// Keeps every row: passing the shrunk text back through
        /// <see cref="Filter"/> with this removes the header of any table left
        /// empty, and nothing else.
        /// </summary>
        private sealed class AlwaysDocument : DocumentTerms
        {
            public AlwaysDocument() : base("", 1) { }
            public override int Count(string term) => 1;
        }

        /// <summary>The most words in any term cell, so the document is indexed deep enough and no deeper.</summary>
        public static int LongestTerm(IEnumerable<string> markdownTexts)
        {
            var longest = 1;
            foreach (var text in markdownTexts ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    if (!IsTableLine(line) || Separator.IsMatch(line.Trim())) continue;
                    foreach (var variant in Cells(line).Take(2).SelectMany(Variants))
                        longest = Math.Max(longest, DocumentTerms.Words(variant).Count);
                }
            }
            return Math.Min(longest, DocumentTerms.MaxWords);
        }

        /// <summary>Either of the first two cells - source or target, whichever way the table runs.</summary>
        private static bool Occurs(List<string> cells, DocumentTerms doc)
        {
            return cells.Take(2).SelectMany(Variants).Any(doc.Contains);
        }

        /// <summary>
        /// The terms a cell names: markup removed, a parenthetical note dropped,
        /// alternatives written "a / b" or "a; b" taken one by one.
        /// </summary>
        private static IEnumerable<string> Variants(string cell)
        {
            if (string.IsNullOrWhiteSpace(cell)) yield break;
            var clean = Parenthetical.Replace(cell.Replace("*", "").Replace("`", "").Replace("_", " "), " ");
            foreach (var part in clean.Split('/', ';'))
            {
                // Under three letters is not a term: "and/or" would otherwise keep a
                // row through "or", which occurs in almost any English text.
                var t = part.Trim();
                if (t.Length >= 3) yield return t;
            }
        }

        private static bool IsTableLine(string line)
        {
            return line != null && line.TrimStart().StartsWith("|", StringComparison.Ordinal);
        }

        private static List<string> Cells(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(c => c.Trim()).ToList();
        }
    }
}
