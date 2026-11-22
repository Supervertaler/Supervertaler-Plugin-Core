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
        /// <summary>How many numbers of the stored translation were replaced by the document's (<see cref="VaultNumbers"/>).</summary>
        public int NumbersReplaced;
        /// <summary>The tokens of those numbers, in the document and in <see cref="Source"/>; null when none were replaced.</summary>
        public (HashSet<int> doc, HashSet<int> source)? ReplacedTokens;
        public List<VaultToken> DocumentTokens, SourceTokens;
        /// <summary>Concordance only: the query's words in the searched side's text (its text pieces run together, tags
        /// left out), for the CAT tool to highlight. Offset and Length are characters in that text.</summary>
        public List<VaultToken> Hits;
    }

    /// <summary>
    /// A SuperVault lookup: candidates from the index, scored with <see cref="VaultDiff"/>, tags placed with
    /// <see cref="VaultTagMap"/>, duplicates (the same target twice) removed. CAT-tool neutral.
    ///
    /// Cost per lookup, at any index size: one exact query, one query on the 8 rarest words (200 candidates, of which
    /// those whose length can reach the minimum are read), an upper-bound estimate of the score for each
    /// (<see cref="VaultDiff.Estimate"/>), and the full alignment for the best <see cref="Aligned"/> at most, best
    /// estimate first, stopping as soon as none left can reach the rows found.
    /// </summary>
    public static class VaultLookup
    {
        const int Aligned = 40;

        /// <param name="extra">Units just confirmed and not yet in the index, as exact matches. They are the newest of
        /// their group: when one is there, the index's other translations of the same source are hidden.</param>
        /// <param name="targetCulture">The project's target language with its variant ("en-GB"): that variant wins first.</param>
        public static List<VaultMatch> Search(VaultIndex index, IList<VaultPiece> document, string sourceLang, string targetLang, int minScore, int maxResults,
                                              IEnumerable<VaultCandidate> extra = null, string targetCulture = null)
        {
            var matches = new List<VaultMatch>();
            var extras = extra?.ToList() ?? new List<VaultCandidate>();
            if ((index == null && extras.Count == 0) || document == null || maxResults <= 0) return matches;
            var docPlain = PlainOf(document);
            if (docPlain.Length == 0) return matches;

            var docTokens = VaultDiff.Tokenize(document);
            var rules = VaultLanguage.Rules(sourceLang);   // stems and stopwords: this lookup's own

            // Cheap first pass: an upper bound of each candidate's score, on the tokens it will be scored on (its tags
            // placed first), so a candidate that could reach the minimum is never dropped unaligned. Exact matches always
            // go through.
            var ranked = new List<(VaultMatch m, int quick)>();
            var found = index == null ? new List<VaultCandidate>() : index.Candidates(docPlain, sourceLang, targetLang, maxResults * 2, minScore, targetCulture, new VaultLengthBound(docTokens));
            var newest = extras.LastOrDefault(c => c.Exact);   // the last one handed over: a correction comes after what it corrects
            // ... unless the index already has a newer translation (this one was confirmed earlier, elsewhere, and arrived late).
            var indexed = found.FirstOrDefault(c => c.Exact);
            if (newest != null && indexed != null && newest.Changed != null && indexed.Date > VaultFields.ToSeconds(newest.Changed.Value))
            {
                var keepIndexed = PlainOf(VaultMarkup.Parse(indexed.TargetRaw));
                extras.RemoveAll(c => c.Exact && PlainOf(VaultMarkup.Parse(c.TargetRaw)) != keepIndexed);
                newest = null;
            }
            if (newest != null)
            {
                var keep = PlainOf(VaultMarkup.Parse(newest.TargetRaw));
                int hidden = found.Where(c => c.Exact).Select(c => PlainOf(VaultMarkup.Parse(c.TargetRaw))).Where(t => t != keep).Distinct().Count();
                found.RemoveAll(c => c.Exact && PlainOf(VaultMarkup.Parse(c.TargetRaw)) != keep);
                extras.RemoveAll(c => c.Exact && c != newest && PlainOf(VaultMarkup.Parse(c.TargetRaw)) != keep);
                foreach (var c in found.Concat(extras)) if (c.Exact) c.Older = Math.Max(c.Older, hidden);
            }
            var candidates = found.Concat(extras);
            foreach (var c in candidates)
            {
                var src = VaultMarkup.Parse(c.SourceRaw);
                var trg = VaultMarkup.Parse(c.TargetRaw);
                var srcPlain = PlainOf(src);
                if (c.Exact && srcPlain != docPlain) c.Exact = false;   // a hash collision, however unlikely
                var map = VaultTagMap.Build(document, src, trg);
                var m = new VaultMatch { Candidate = c, Tags = map.Outcome, TagReason = map.Reason, DocumentTokens = docTokens };
                Place(document, src, map.Source, out m.Source, out m.SourceDocTag);
                Place(document, trg, map.Target, out m.Target, out m.TargetDocTag);
                m.SourceTokens = VaultDiff.Tokenize(m.Source);
                int quick = VaultDiff.Estimate(docTokens, m.SourceTokens, c.Exact);
                if (quick < minScore) continue;   // an upper bound: this one can't reach the minimum
                ranked.Add((m, quick));
            }

            // Aligned best estimate first. The estimate is an upper bound, so once the rows found so far (one per
            // translation) beat the next candidate's estimate, no candidate left can change the result: stop there.
            // Usually far fewer than Aligned are aligned.
            var order = ranked.OrderByDescending(x => x.quick).Take(Aligned + maxResults).ToList();
            var best = new Dictionary<string, int>(StringComparer.Ordinal);   // translation -> its best score so far
            foreach (var r in order)
            {
                if (best.Count >= maxResults && best.Values.OrderByDescending(v => v).ElementAt(maxResults - 1) > r.quick) break;
                var m = r.m;
                // A different number put right in the proposal, where that can't go wrong (VaultNumbers).
                var plan = m.Candidate.Exact ? null : VaultNumbers.Make(document, m.Source, m.Target);
                if (plan != null)
                {
                    m.Target = VaultNumbers.Apply(plan, m.Target);
                    m.NumbersReplaced = plan.Swaps.Count;
                }
                if (plan != null) m.ReplacedTokens = VaultNumbers.Tokens(plan, docTokens, m.SourceTokens);
                m.Diff = VaultDiff.Compare(docTokens, m.SourceTokens, m.Candidate.Exact, rules, m.ReplacedTokens);
                m.Score = m.Diff.Score;
                if (m.Score < minScore) continue;
                matches.Add(m);
                var key = PlainOf(m.Target);
                if (!best.TryGetValue(key, out int had) || had < m.Score) best[key] = m.Score;
            }

            // The same translation twice is noise: keep its best version (higher score, then tags placed).
            var shown = matches
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Tags == VaultTagOutcome.Unmapped ? 1 : 0)
                .GroupBy(m => PlainOf(m.Target), StringComparer.Ordinal)
                .Select(g => g.First())
                .Take(maxResults)
                .ToList();
            // The differences as they are drawn: the display alignment, for these rows only (VaultDisplay). The score
            // stays the Studio-like one computed above.
            foreach (var m in shown)
            {
                if (m.Diff?.Edits == null) continue;
                var edits = VaultDisplay.Edits(docTokens, m.SourceTokens);
                // A replaced number is marked as such in the drawn differences too (not drawn as a difference).
                if (edits != null && m.ReplacedTokens != null)
                    for (int i = 0; i < edits.Count; i++)
                        if (edits[i].Op == VaultEditOp.Change && m.ReplacedTokens.Value.doc.Contains(edits[i].A) && m.ReplacedTokens.Value.source.Contains(edits[i].B))
                            edits[i] = new VaultEdit { Op = VaultEditOp.Change, A = edits[i].A, B = edits[i].B, Cost = 0, Replaced = true };
                m.Diff.Edits = edits;
            }
            return shown;
        }

        /// <summary>
        /// Concordance search: units whose <paramref name="searchLang"/> side contains the query's words. Score = the
        /// share of the query's words the unit contains. Results are oriented searchLang → otherLang, as plain text.
        /// </summary>
        public static List<VaultMatch> Concordance(VaultIndex index, string query, string searchLang, string otherLang, int minScore, int maxResults, string otherCulture = null)
        {
            var matches = new List<VaultMatch>();
            var plain = VaultMarkup.Normalize(query);
            if (index == null || plain.Length == 0 || maxResults <= 0) return matches;
            var words = new HashSet<string>(VaultDiff.Words(plain));
            if (words.Count == 0) return matches;
            foreach (var c in index.Concordance(plain, searchLang, otherLang, maxResults, otherCulture))
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

        /// <summary>
        /// Another translation of a match's stored source (<see cref="VaultLibrary.Translations"/>) made ready to insert
        /// like the match: its tags placed on the document's, a different number put right where the match had one.
        /// Its source text is the match's, so the score and the differences are the match's own: no new alignment, so a
        /// page of 50 costs only their tag maps.
        /// </summary>
        public static VaultMatch Offer(IList<VaultPiece> document, VaultMatch match, VaultCandidate other)
        {
            if (document == null || match == null || other == null) return null;
            var src = VaultMarkup.Parse(other.SourceRaw);
            var trg = VaultMarkup.Parse(other.TargetRaw);
            var map = VaultTagMap.Build(document, src, trg);
            var m = new VaultMatch
            {
                Candidate = other, Score = match.Score, Diff = match.Diff, Tags = map.Outcome, TagReason = map.Reason,
                DocumentTokens = match.DocumentTokens,
            };
            Place(document, src, map.Source, out m.Source, out m.SourceDocTag);
            Place(document, trg, map.Target, out m.Target, out m.TargetDocTag);
            m.SourceTokens = VaultDiff.Tokenize(m.Source);
            var plan = match.Candidate != null && match.Candidate.Exact ? null : VaultNumbers.Make(document, m.Source, m.Target);
            if (plan != null)
            {
                m.Target = VaultNumbers.Apply(plan, m.Target);
                m.NumbersReplaced = plan.Swaps.Count;
            }
            return m;
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
    }
}
