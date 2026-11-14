using System;
using System.Collections.Generic;
using System.Text;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Core
{
    /// <summary>
    /// An earlier segment of the same document that the one being translated
    /// repeats, and the translation it has - shown to the AI so it keeps the
    /// wording (<see cref="TranslationPrompt.BuildReferenceBlock"/>).
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

        /// <summary>
        /// The other segment is the same sentence but for a few words or numbers
        /// (<see cref="RestatedPair.NearTwin"/>): what a translation memory calls
        /// a high fuzzy match. Shown whatever its length, with
        /// <see cref="Differences"/>.
        /// </summary>
        public bool Twin;

        /// <summary>
        /// For a twin: where the two sources differ, each as "yours X / earlier
        /// Y", at most <see cref="EarlierTranslations.MaxDifferencesListed"/>.
        /// The numbers are the point: a model that copies a twin's translation
        /// copies its numbers with it.
        /// </summary>
        public List<string> Differences;

        /// <summary>How many places differ in all, when more than are listed.</summary>
        public int DifferenceCount;
    }

    /// <summary>
    /// The run's short-term memory: which segments of a document can be shown to
    /// the AI as earlier translations of what a segment repeats. Step 2 of the
    /// cross-batch consistency design (docs/design/in-run-references.md). Built
    /// once per run from the pairs <see cref="RestatedPassages.Find"/> returns,
    /// so "repeats" means here exactly what it means in the end-of-run report.
    ///
    /// <para>Two kinds, as in a translation memory. A <b>twin</b> is the same
    /// sentence but for a few words or numbers - a high fuzzy match - and is
    /// shown whole whatever its length, with the differences named. A
    /// <b>passage</b> is a run of words inside two otherwise different
    /// sentences - a fragment match - and is shown only when the earlier segment
    /// is short enough to be read as an example.</para>
    ///
    /// <para>The document is the memory. This holds no translation: the caller
    /// says, each time it asks, which translation a segment has now - one that
    /// was already in the document and the run will not overwrite, or one the
    /// run wrote and the CAT tool accepted. Nothing is kept between runs.</para>
    ///
    /// <para>Scale: tested at 10,000 segments, designed for 10,000. Building is
    /// one pass over the pairs; a lookup reads one segment's few partners.
    /// Whether two segments are twins was decided by the finder, from the runs
    /// they share; nothing here compares a segment with every other. Naming a
    /// twin's differences aligns two segments word by word (words × words, at
    /// most 1,000 × 1,000, the finder's own limit), for a twin that is shown
    /// and no other.</para>
    /// </summary>
    public sealed class EarlierTranslations
    {
        /// <summary>The most earlier translations shown for one segment.</summary>
        public const int MaxPerSegment = 2;

        /// <summary>The most shown in one request, over all its segments.</summary>
        public const int MaxPerRequest = 20;

        /// <summary>
        /// The most characters of earlier sources and translations in one
        /// request (about 6,000 tokens). One that does not fit is left out whole;
        /// a long twin and its translation are about 3,000 characters, so eight
        /// of them fit.
        /// </summary>
        public const int MaxRequestCharacters = 24000;

        /// <summary>
        /// A passage - not a twin - from an earlier segment whose source or
        /// translation is longer than this in tokens (<see cref="VaultDiff.Tokenize"/>:
        /// words, punctuation and spaces) is not shown: a few shared words inside
        /// 400 words of something else is no example. Never cut short.
        /// </summary>
        public const int MaxTokens = VaultDiff.MaxAlignTokens;

        /// <summary>The most differences named for a twin; the rest are counted.</summary>
        public const int MaxDifferencesListed = 12;

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

            // A twin first, then most shared words; of equals, the one that comes
            // first in the document. Decided once, so a segment's references do
            // not depend on the order requests happen to come back in.
            foreach (var list in _partners.Values)
                list.Sort((x, y) => x.NearTwin != y.NearTwin ? (x.NearTwin ? -1 : 1)
                    : x.SharedWords != y.SharedWords ? y.SharedWords.CompareTo(x.SharedWords)
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
        /// it repeats has a translation yet. Each other segment appears once,
        /// as a twin if it is one.
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

                // A passage inside a very long segment is no example. A twin is
                // the whole sentence, and is shown at any length.
                if (!pair.NearTwin && (TooLong(source) || TooLong(target))) continue;

                // A language written without spaces: the finder cannot say whether
                // such a translation agrees with another, so none is offered either.
                if (RestatedPassages.WrittenWithoutSpaces(target)) continue;

                // The same translation twice teaches nothing the first did not.
                bool seen = false;
                foreach (var r in result)
                    if (string.Equals(r.Target, target, StringComparison.Ordinal)) { seen = true; break; }
                if (seen) continue;

                var earlier = new EarlierTranslation
                {
                    Source = source.Trim(),
                    Target = target.Trim(),
                    Passage = pair.Passage,
                    PassageWords = pair.PassageWords,
                    Twin = pair.NearTwin
                };
                if (pair.NearTwin)
                {
                    var own = segment >= 0 && segment < _sources.Count ? _sources[segment] : "";
                    earlier.Differences = RestatedPassages.WordDifferences(own, source, MaxDifferencesListed, out earlier.DifferenceCount);
                }

                result.Add(earlier);
                if (result.Count == MaxPerSegment) break;
            }

            return result;
        }

        private static bool TooLong(string text) =>
            VaultDiff.Tokenize(new[] { new VaultPiece { Text = text } }).Count > MaxTokens;
    }
}
