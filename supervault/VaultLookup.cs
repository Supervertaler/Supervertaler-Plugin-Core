using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>A SuperVault match for one document segment, ready for a CAT tool to turn into its own result type.</summary>
    public sealed class VaultMatch
    {
        public VaultCandidate Candidate;
        public int Score;
        /// <summary>The stored source, as pieces. Tags are the document's own where they could be mapped; dropped otherwise.</summary>
        public List<VaultPiece> Source;
        /// <summary>The stored target, likewise.</summary>
        public List<VaultPiece> Target;
        /// <summary>For each tag piece in <see cref="Source"/>/<see cref="Target"/>: the index of the document piece it is. -1 for text.</summary>
        public List<int> SourceDocTag, TargetDocTag;
        public VaultTagOutcome Tags;
        public string TagReason;
        public VaultDiffResult Diff;          // document tokens against Source tokens
        public List<VaultToken> DocumentTokens, SourceTokens;
        /// <summary>Concordance only: the query's words in the searched side's text (its text pieces run together, tags
        /// left out), for the CAT tool to highlight. Offset and Length are characters in that text.</summary>
        public List<VaultToken> Hits;
    }

    /// <summary>
    /// A SuperVault lookup: candidates from the index, scored with <see cref="VaultDiff"/>, tags placed with
    /// <see cref="VaultTagMap"/>, duplicates (the same target twice) removed. CAT-tool neutral.
    ///
    /// Cost per lookup, at any index size: one exact query, one query on the 8 rarest words (200 candidates), a cheap
    /// word-overlap score for all 200, and the full alignment for the best <see cref="Aligned"/> only.
    /// </summary>
    public static class VaultLookup
    {
        const int Aligned = 40;

        public static List<VaultMatch> Search(VaultIndex index, IList<VaultPiece> document, string sourceLang, string targetLang, int minScore, int maxResults,
                                              IEnumerable<VaultCandidate> extra = null)
        {
            var matches = new List<VaultMatch>();
            var extras = extra?.ToList() ?? new List<VaultCandidate>();
            if ((index == null && extras.Count == 0) || document == null || maxResults <= 0) return matches;
            var docPlain = PlainOf(document);
            if (docPlain.Length == 0) return matches;

            var docTokens = VaultDiff.Tokenize(document);
            var docContent = ContentCounts(docTokens, out int docContentCount);

            // Cheap first pass: word overlap with the document. Exact matches always go through.
            var ranked = new List<(VaultCandidate c, List<VaultPiece> src, List<VaultPiece> trg, string srcPlain, int quick)>();
            var candidates = index == null ? (IEnumerable<VaultCandidate>)extras : index.Candidates(docPlain, sourceLang, targetLang, maxResults * 2, minScore).Concat(extras);
            foreach (var c in candidates)
            {
                var src = VaultMarkup.Parse(c.SourceRaw);
                var trg = VaultMarkup.Parse(c.TargetRaw);
                var srcPlain = PlainOf(src);
                if (c.Exact && srcPlain != docPlain) c.Exact = false;   // a hash collision, however unlikely
                int quick = c.Exact ? 100 : Overlap(docContent, docContentCount, VaultDiff.Tokenize(src));
                if (quick + 10 < minScore) continue;   // overlap is an optimistic estimate; 10 points of slack
                ranked.Add((c, src, trg, srcPlain, quick));
            }

            foreach (var r in ranked.OrderByDescending(x => x.quick).Take(Aligned + maxResults))
            {
                var map = VaultTagMap.Build(document, r.src, r.trg);
                var m = new VaultMatch { Candidate = r.c, Tags = map.Outcome, TagReason = map.Reason, DocumentTokens = docTokens };
                Place(document, r.src, map.Source, out m.Source, out m.SourceDocTag);
                Place(document, r.trg, map.Target, out m.Target, out m.TargetDocTag);
                m.SourceTokens = VaultDiff.Tokenize(m.Source);
                m.Diff = VaultDiff.Compare(docTokens, m.SourceTokens, r.c.Exact);
                m.Score = m.Diff.Score;
                if (m.Score >= minScore) matches.Add(m);
            }

            // The same translation twice is noise: keep its best version (higher score, then tags placed).
            return matches
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Tags == VaultTagOutcome.Unmapped ? 1 : 0)
                .GroupBy(m => PlainOf(m.Target), StringComparer.Ordinal)
                .Select(g => g.First())
                .Take(maxResults)
                .ToList();
        }

        /// <summary>
        /// Concordance search: units whose <paramref name="searchLang"/> side contains the query's words. Score = the
        /// share of the query's words the unit contains. Results are oriented searchLang → otherLang, as plain text.
        /// </summary>
        public static List<VaultMatch> Concordance(VaultIndex index, string query, string searchLang, string otherLang, int minScore, int maxResults)
        {
            var matches = new List<VaultMatch>();
            var plain = VaultMarkup.Normalize(query);
            if (index == null || plain.Length == 0 || maxResults <= 0) return matches;
            var words = new HashSet<string>(VaultDiff.Words(plain));
            if (words.Count == 0) return matches;
            foreach (var c in index.Concordance(plain, searchLang, otherLang, maxResults))
            {
                var src = Strip(VaultMarkup.Parse(c.SourceRaw));
                var unitWords = new HashSet<string>(VaultDiff.Words(PlainOf(src)));
                int score = (int)Math.Round(100.0 * words.Count(unitWords.Contains) / words.Count);
                if (score < minScore) continue;
                var trg = Strip(VaultMarkup.Parse(c.TargetRaw));
                var text = string.Concat(src.Where(p => !p.IsTag).Select(p => p.Text));
                matches.Add(new VaultMatch
                {
                    Candidate = c, Score = score, Source = src, Target = trg, Tags = VaultTagOutcome.NoTags,
                    Hits = VaultDiff.Tokenize(new[] { new VaultPiece { Text = text } })
                                    .Where(t => t.Kind == VaultTokenKind.Word && words.Contains(t.Text.ToLowerInvariant())).ToList(),
                    SourceDocTag = src.Select(_ => -1).ToList(), TargetDocTag = trg.Select(_ => -1).ToList(),
                });
            }
            return matches.OrderByDescending(m => m.Score).Take(maxResults).ToList();
        }

        /// <summary>Plain, normalised text of a list of pieces.</summary>
        public static string PlainOf(IList<VaultPiece> pieces)
        {
            var sb = new StringBuilder();
            foreach (var p in pieces) if (!p.IsTag) sb.Append(p.Text);
            return VaultMarkup.Normalize(sb.ToString());
        }

        /// <summary>
        /// The pieces as they will be offered: a mapped tag takes the document tag's key (so the diff sees it as the same
        /// tag), an unmapped tag is dropped. Adjacent text runs left behind by a dropped tag are joined.
        /// </summary>
        static void Place(IList<VaultPiece> document, List<VaultPiece> pieces, int[] map, out List<VaultPiece> placed, out List<int> docTag)
        {
            placed = new List<VaultPiece>(pieces.Count);
            docTag = new List<int>(pieces.Count);
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (!p.IsTag)
                {
                    if (placed.Count > 0 && !placed[placed.Count - 1].IsTag)
                        placed[placed.Count - 1] = new VaultPiece { Text = placed[placed.Count - 1].Text + p.Text };
                    else { placed.Add(p); docTag.Add(-1); }
                    continue;
                }
                if (map[i] < 0) continue;
                var d = document[map[i]];
                placed.Add(new VaultPiece { Kind = d.Kind, Name = d.Name, Key = d.Key, Raw = p.Raw });
                docTag.Add(map[i]);
            }
        }

        static List<VaultPiece> Strip(List<VaultPiece> pieces)
        {
            var text = new StringBuilder();
            foreach (var p in pieces) if (!p.IsTag) text.Append(p.Text);
            return new List<VaultPiece> { new VaultPiece { Text = text.ToString() } };
        }

        static Dictionary<string, int> ContentCounts(List<VaultToken> tokens, out int count)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            count = 0;
            foreach (var t in tokens)
            {
                if (!t.IsContent) continue;
                count++;
                d.TryGetValue(t.Text, out int c);
                d[t.Text] = c + 1;
            }
            return d;
        }

        /// <summary>Word-overlap estimate (Dice), an upper-ish bound on the aligned score, used to pick what to align.</summary>
        static int Overlap(Dictionary<string, int> doc, int docCount, List<VaultToken> other)
        {
            var left = new Dictionary<string, int>(doc, StringComparer.Ordinal);
            int common = 0, count = 0;
            foreach (var t in other)
            {
                if (!t.IsContent) continue;
                count++;
                if (left.TryGetValue(t.Text, out int c) && c > 0) { common++; left[t.Text] = c - 1; }
            }
            if (docCount + count == 0) return 0;
            return (int)Math.Floor(100.0 * 2 * common / (docCount + count));
        }
    }
}
