using System;
using System.Collections.Generic;
using System.Text;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Core
{
    /// <summary>
    /// An earlier segment of the same document that shares a passage with the one
    /// being translated, and the translation it has - shown to the AI so it keeps
    /// the wording (<see cref="TranslationPrompt.BuildReferenceBlock"/>).
    /// </summary>
    public sealed class EarlierTranslation
    {
        /// <summary>The other segment's source, as plain text.</summary>
        public string Source;

        /// <summary>
        /// Its translation, as plain text, with its <c>[[TC: ...]]</c> note if it
        /// has one: the note travels with the pair, so a near-identical segment
        /// later in the run is shown the problem the AI found in the first.
        /// </summary>
        public string Target;

        /// <summary>The first words of the passage the two share, and how many words it has.</summary>
        public string Passage;
        public int PassageWords;
    }

    /// <summary>
    /// Which segments of a document can be shown to the AI as earlier
    /// translations of a passage a segment repeats: step 2 of the cross-batch
    /// consistency design (docs/design/in-run-references.md). Built once per run
    /// from the pairs <see cref="RestatedPassages.Find"/> returns, so "repeats a
    /// passage" means here exactly what it means in the end-of-run report.
    ///
    /// <para>The document is the memory. This holds no translation: the caller
    /// says, each time it asks, which translation a segment has now - one that
    /// was already in the document and the run will not overwrite, or one the
    /// run wrote and the CAT tool accepted. Nothing is kept between runs.</para>
    ///
    /// <para>Scale: tested at 10,000 segments, designed for 10,000. Building is
    /// one pass over the pairs; a lookup reads one segment's few partners.</para>
    /// </summary>
    public sealed class EarlierTranslations
    {
        /// <summary>The most earlier translations shown for one segment.</summary>
        public const int MaxPerSegment = 2;

        /// <summary>The most shown in one request, over all its segments: the block has a fixed maximum size.</summary>
        public const int MaxPerRequest = 20;

        /// <summary>
        /// An earlier segment whose source or translation is longer than this in
        /// tokens (<see cref="VaultDiff.Tokenize"/>: words, punctuation and
        /// spaces) is not shown at all. Never cut short: half a sentence as a
        /// model is worse than none.
        /// </summary>
        public const int MaxTokens = VaultDiff.MaxAlignTokens;

        private readonly IList<string> _sources;
        private readonly Dictionary<int, List<RestatedPair>> _partners = new Dictionary<int, List<RestatedPair>>();

        /// <param name="sources">Every segment's source as plain text, in document order.</param>
        /// <param name="pairs">What <see cref="RestatedPassages.Find"/> returned for them.</param>
        public EarlierTranslations(IList<string> sources, RestatedPairs pairs)
        {
            _sources = sources ?? new string[0];
            if (pairs == null) return;

            foreach (var pair in pairs.Pairs)
            {
                Add(pair.A, pair);
                Add(pair.B, pair);
            }

            // Most shared words first; of equals, the one that comes first in the
            // document. Decided once, so a segment's references do not depend on
            // the order requests happen to come back in.
            foreach (var list in _partners.Values)
                list.Sort((x, y) => x.SharedWords != y.SharedWords
                    ? y.SharedWords.CompareTo(x.SharedWords)
                    : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        }

        private void Add(int segment, RestatedPair pair)
        {
            if (!_partners.TryGetValue(segment, out var list)) _partners.Add(segment, list = new List<RestatedPair>());
            list.Add(pair);
        }

        /// <summary>
        /// The earlier translations to show for a segment, best first, at most
        /// <see cref="MaxPerSegment"/>; empty when it repeats nothing, or nothing
        /// it repeats has a translation yet.
        /// </summary>
        /// <param name="segment">The segment's position in the sources given.</param>
        /// <param name="targetOf">
        /// The translation a segment has now, as plain text, or null or blank when
        /// it has none - not yet translated, failed, declined by the CAT tool, or
        /// about to be overwritten by this run.
        /// </param>
        public List<EarlierTranslation> For(int segment, Func<int, string> targetOf)
        {
            var result = new List<EarlierTranslation>();
            if (targetOf == null || !_partners.TryGetValue(segment, out var pairs)) return result;

            foreach (var pair in pairs)
            {
                int other = pair.A == segment ? pair.B : pair.A;
                var target = targetOf(other);
                if (string.IsNullOrWhiteSpace(target)) continue;

                var source = other >= 0 && other < _sources.Count ? _sources[other] : null;
                if (string.IsNullOrWhiteSpace(source)) continue;
                if (TooLong(source) || TooLong(target)) continue;

                // A language written without spaces: the finder cannot say whether
                // such a translation agrees with another, so none is offered either.
                if (RestatedPassages.WrittenWithoutSpaces(target)) continue;

                // The same translation twice teaches nothing the first did not.
                bool seen = false;
                foreach (var r in result)
                    if (string.Equals(r.Target, target, StringComparison.Ordinal)) { seen = true; break; }
                if (seen) continue;

                result.Add(new EarlierTranslation
                {
                    Source = source.Trim(),
                    Target = target.Trim(),
                    Passage = pair.Passage,
                    PassageWords = pair.PassageWords
                });
                if (result.Count == MaxPerSegment) break;
            }

            return result;
        }

        private static bool TooLong(string text) =>
            VaultDiff.Tokenize(new[] { new VaultPiece { Text = text } }).Count > MaxTokens;
    }
}
