using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Core
{
    /// <summary>
    /// One run of words two segments share: words <see cref="StartA"/> onwards of
    /// the first equal words <see cref="StartB"/> onwards of the second, for
    /// <see cref="Length"/> words. Positions count words only, from 0.
    /// </summary>
    public struct SharedRun
    {
        public int StartA, StartB, Length;
    }

    /// <summary>
    /// Two segments whose sources share at least one long run of identical words
    /// (<see cref="RestatedPassages.Find"/>).
    /// </summary>
    public sealed class RestatedPair
    {
        /// <summary>The two segments, by their position in the list given; A comes first.</summary>
        public int A, B;

        /// <summary>How many words each source has.</summary>
        public int WordsA, WordsB;

        /// <summary>
        /// The shared runs, in the order they stand in A. At least one is
        /// <see cref="RestatedPassages.MinRunWords"/> long; the others are shorter
        /// runs a few words away from it, the same passage carrying on after a
        /// word or two that differ.
        /// </summary>
        public List<SharedRun> Runs = new List<SharedRun>();

        /// <summary>How many separate passages the runs make up; usually 1.</summary>
        public int Passages;

        /// <summary>The words in all the runs together.</summary>
        public int SharedWords;

        /// <summary>The first words of the longest run as A has them, for a person to search for.</summary>
        public string Passage;

        /// <summary>How many words the longest run has.</summary>
        public int PassageWords;

        /// <summary>The two sources are the same sentence but for a few words.</summary>
        public bool NearTwin;

        /// <summary>
        /// For near-twins: in how many places the two sources differ, counted over
        /// the whole of both (<see cref="RestatedPassages.Differences"/>).
        /// </summary>
        public int TwinDifferences;

        /// <summary>
        /// For near-twins: how many words of the longer source are not in the
        /// other. With <see cref="TwinDifferences"/> it says how much the two
        /// translations may differ: see <see cref="RestatedPassages.Compare"/>.
        /// </summary>
        public int TwinChangedWords;

        /// <summary>
        /// Places where the two sources differ inside a passage, with more than
        /// <see cref="RestatedPassages.EdgeWords"/> shared words on both sides.
        /// </summary>
        public int InteriorBreaks;

        /// <summary>Places where they differ within <see cref="RestatedPassages.EdgeWords"/> shared words of a passage's start or end.</summary>
        public int EdgeBreaks;

        /// <summary>
        /// Passage ends where the words just outside it are mostly the same words
        /// in both sources, in another order (<see cref="RestatedPassages.EdgeWindow"/>).
        /// </summary>
        public int ReorderedEdges;

        /// <summary>Places inside the passages where the two sources differ.</summary>
        public int Breaks => InteriorBreaks + EdgeBreaks;
    }

    /// <summary>What <see cref="RestatedPassages.Find"/> found.</summary>
    public sealed class RestatedPairs
    {
        public List<RestatedPair> Pairs = new List<RestatedPair>();

        /// <summary>The document had more candidate pairs than <see cref="RestatedPassages.MaxPairs"/>; the rest were not looked at.</summary>
        public bool Limited;

        /// <summary>Segments left out because they are longer than <see cref="RestatedPassages.MaxWords"/> words.</summary>
        public int TooLong;
    }

    /// <summary>A segment's translation, for <see cref="RestatedPassages.Report"/>.</summary>
    public sealed class RestatedTarget
    {
        /// <summary>The segment as a log line names it: "segment 12".</summary>
        public string Label;

        /// <summary>The translation as plain text, the host's own tag notation taken out; null or blank when there is none.</summary>
        public string Text;
    }

    public enum RestatedVerdict
    {
        /// <summary>Not compared: a translation is missing, too long, or in a language written without spaces.</summary>
        Skipped,
        Same,
        Different,
    }

    /// <summary>One line of the report: a pair, and how its segments are named.</summary>
    public sealed class RestatedFinding
    {
        public RestatedPair Pair;
        public string LabelA, LabelB;
    }

    /// <summary>
    /// Where a run's translations of restated passages differ
    /// (<see cref="RestatedPassages.Report"/>).
    /// </summary>
    public sealed class RestatedReport
    {
        /// <summary>The same source wording, translated differently.</summary>
        public List<RestatedFinding> Different = new List<RestatedFinding>();

        /// <summary>Pairs whose two translations were compared, and pairs left alone because one was missing.</summary>
        public int Compared, Skipped;

        /// <summary>As <see cref="RestatedPairs.Limited"/>.</summary>
        public bool Limited;

        /// <summary>
        /// The lines for the end of a run, or null when there is nothing to say:
        /// one line with the count, then one line per case, at most
        /// <see cref="RestatedPassages.MaxListed"/>.
        /// </summary>
        public string Summary()
        {
            if (Different.Count == 0) return null;

            var text = new StringBuilder();
            text.Append(Number(Different.Count)).Append(" translated differently (listed).");
            if (Limited)
                text.Append(" Only the first ").Append(Number(RestatedPassages.MaxPairs)).Append(" candidate pairs were checked.");

            for (int i = 0; i < Different.Count && i < RestatedPassages.MaxListed; i++)
            {
                var f = Different[i];
                text.AppendLine();
                text.Append("Translated differently: ").Append(f.LabelA).Append(" and ").Append(f.LabelB)
                    .Append(" – source “").Append(f.Pair.Passage).Append("” (")
                    .Append(Number(f.Pair.PassageWords)).Append(" words the same)");
            }
            if (Different.Count > RestatedPassages.MaxListed)
            {
                text.AppendLine();
                text.Append("… and ").Append(Number(Different.Count - RestatedPassages.MaxListed))
                    .Append(" more translated differently, not listed.");
            }

            return text.ToString();
        }

        private static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Finds restated passages - segments whose sources share a long run of
    /// identical words - and says where their translations differ. No AI: a pure
    /// function of the text, so it costs nothing, changes nothing that is sent and
    /// keeps no state (docs/design/restated-passages.md).
    ///
    /// <para>It knows nothing about what kind of document it reads: any two
    /// segments that share a run of words are a pair. A contract restating a
    /// definition in a later clause, a manual repeating a warning in every
    /// chapter, an error message that differs from another in one word, a
    /// patent's description repeating a claim inside a longer sentence.</para>
    ///
    /// <para>Why: Batch Translate sends a document a few segments at a time and
    /// each request starts from scratch, so a passage restated 300 segments later
    /// can come back worded differently. Consistent repetitions
    /// (<see cref="RepetitionRun"/>) covers whole segments that are identical;
    /// this covers a passage inside a segment.</para>
    ///
    /// <para>What counts as the same text: words only, compared without case.
    /// Tags, punctuation and spacing decide nothing (a tag never splits a word:
    /// the text on either side of it is joined, as <see cref="RepetitionKey"/>
    /// joins it). Words are <see cref="VaultDiff.Tokenize"/>'s.</para>
    ///
    /// <para>Languages: any language written with spaces between words, in any
    /// script. A language written without them (Chinese, Japanese, Thai, Lao,
    /// Khmer, Burmese) is not supported yet: there a whole clause is one "word",
    /// so such a source pairs nothing, and a pair with such a translation is
    /// skipped rather than judged (<see cref="WrittenWithoutSpaces"/>). Either
    /// way no lines are written.</para>
    ///
    /// <para>Scale: tested at 10,000 segments, designed for 10,000. The index is
    /// one entry per run of <see cref="MinRunWords"/> words (about 300,000 at
    /// 10,000 segments of 30 words); only pairs that share such a run are
    /// compared word by word, and a phrase found in more than
    /// <see cref="MaxSegmentsPerPhrase"/> segments pairs nothing, so a formula
    /// that opens every clause cannot pair each with every other. Comparing one pair is
    /// O(words × words), so segments over <see cref="MaxWords"/> words are left
    /// out, and no more than <see cref="MaxPairs"/> pairs are compared.</para>
    /// </summary>
    public static class RestatedPassages
    {
        /// <summary>The fewest identical words in a row that make two segments a pair.</summary>
        public const int MinRunWords = 8;

        /// <summary>A phrase in more segments than this is boilerplate and pairs nothing.</summary>
        public const int MaxSegmentsPerPhrase = 6;

        /// <summary>
        /// Of a pair's long run, the fewest words that must lie outside
        /// boilerplate. Without it, two segments that open with the same formula
        /// and happen to go on with the same word are a pair.
        /// </summary>
        public const int MinOwnWords = 4;

        /// <summary>
        /// Of a pair's long run, the fewest different words, numbers not counted.
        /// A list of values ("more preferably at least 5.1 mm, more preferably at
        /// least 5.2 mm, ...") is many words and few different ones, and two such
        /// lists share long runs by the accident of their numbers. On the first
        /// real document measured, half the lines reported came from such lists.
        /// </summary>
        public const int MinDifferentWords = 6;

        /// <summary>
        /// A segment in which fewer than this share of the three-word sequences
        /// are different from one another (every number counting as the same
        /// word) mostly repeats itself: a list of values, a table row. Such a
        /// segment is compared with its near-twins only. Against any other
        /// segment the shared run is an accident of the list's formula, and the
        /// two translations cannot be lined up: on the first real document
        /// measured, 14 of 33 lines came from pairs of that kind and none of the
        /// 14 was worth reading.
        /// </summary>
        public const double MinVariety = 0.7;

        /// <summary>
        /// The fewest words in a row that count as a translated passage carrying
        /// on after a difference. Two words ("of the") coincide too often.
        /// </summary>
        public const int MinPieceWords = 3;

        /// <summary>
        /// The same for the sources, where it is one word fewer on purpose: a
        /// source's two shared words ("de borgpen") are often three in the
        /// translation ("the locking pin"), and a difference the sources have must
        /// not be reported as the translation's.
        /// </summary>
        public const int MinSourcePieceWords = 2;

        /// <summary>
        /// A difference with no more than this many shared words between it and
        /// the passage's start or end is at the edge; otherwise it is inside.
        /// </summary>
        public const int EdgeWords = 5;

        /// <summary>
        /// How many words just outside a passage's start and end are looked at
        /// for the same words in another order: "on the upper left [side of the
        /// base plate ...]" against "at the left upper [side of the base plate
        /// ...]". Three of the four must be the same words. Without this a
        /// difference in a passage's first or last words went unseen: on the
        /// first real document measured, a known case was missed that way.
        /// </summary>
        public const int EdgeWindow = 4;

        /// <summary>The most shared runs of one pair that are grouped into passages; the longest are kept.</summary>
        private const int MaxRuns = 2000;

        /// <summary>How many differing words a passage may step over, in either segment, and still be one passage.</summary>
        public const int MaxGapWords = 6;

        /// <summary>The share of the longer source the shared words must make up for two segments to be near-twins.</summary>
        public const double TwinShare = 0.8;

        /// <summary>
        /// Two translations sharing less than this share of what the passage's
        /// length leads one to expect were translated differently, however few
        /// breaks there are.
        /// </summary>
        public const double MinTargetShare = 0.5;

        /// <summary>Segments longer than this are left out: comparing a pair is words × words.</summary>
        public const int MaxWords = 1000;

        /// <summary>The most candidate pairs compared in one document.</summary>
        public const int MaxPairs = 50000;

        /// <summary>The most cases the summary lists.</summary>
        public const int MaxListed = 20;

        /// <summary>How many words of a passage the summary quotes.</summary>
        private const int QuotedWords = 8;

        // ---- text ------------------------------------------------------------

        /// <summary>
        /// A segment given as <see cref="RepetitionKey"/>'s tokens, as the plain
        /// text the finder reads: the text runs joined, the tags left out.
        /// </summary>
        public static string PlainText(IEnumerable<RepetitionToken> tokens)
        {
            if (tokens == null) return "";
            var text = new StringBuilder();
            foreach (var token in tokens)
                if (token != null && token.Kind == RepetitionTokenKind.Text) text.Append(token.Value);
            return text.ToString();
        }

        /// <summary>The words of a text as written, in order.</summary>
        private static List<string> WordsOf(string text)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(text)) return words;
            foreach (var token in VaultDiff.Tokenize(new[] { new VaultPiece { Text = text } }))
                if (token.Kind == VaultTokenKind.Word) words.Add(token.Text);
            return words;
        }

        /// <summary>The words of a text as numbers: the same number for the same word, whatever its case.</summary>
        private static int[] Ids(string text, Dictionary<string, int> ids)
        {
            var words = WordsOf(text);
            var result = new int[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i].ToLowerInvariant();
                if (!ids.TryGetValue(word, out var id))
                {
                    id = ids.Count;
                    ids.Add(word, id);
                }
                result[i] = id;
            }
            return result;
        }

        /// <summary>
        /// Whether a text is in a script written without spaces between words:
        /// more than a tenth of its letters are Chinese characters, Japanese kana,
        /// Thai, Lao, Khmer or Burmese. (Korean is written with spaces.) A name
        /// quoted in such a script inside a sentence in another does not count.
        /// </summary>
        internal static bool WrittenWithoutSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int letters = 0, unspaced = 0;
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if ((c >= '\u0E00' && c <= '\u0EFF')        // Thai, Lao
                    || (c >= '\u1000' && c <= '\u109F')     // Burmese
                    || (c >= '\u1780' && c <= '\u17FF')     // Khmer
                    || (c >= '\u3040' && c <= '\u30FF')     // Japanese kana
                    || (c >= '\u3400' && c <= '\u4DBF')     // Chinese characters
                    || (c >= '\u4E00' && c <= '\u9FFF')
                    || (c >= '\uF900' && c <= '\uFAFF'))
                    unspaced++;
            }
            return unspaced * 10 > letters;
        }

        // ---- the finder ------------------------------------------------------

        /// <summary>
        /// The pairs of segments whose sources share a run of at least
        /// <see cref="MinRunWords"/> identical words, in document order. Each
        /// source is plain text (<see cref="PlainText"/> for tokens); a null or
        /// empty one is a segment with nothing in it.
        /// </summary>
        public static RestatedPairs Find(IList<string> sources) => Find(sources, MaxPairs);

        /// <summary>A smaller limit on pairs, for tests.</summary>
        internal static RestatedPairs Find(IList<string> sources, int maxPairs)
        {
            var result = new RestatedPairs();
            if (sources == null || sources.Count < 2) return result;

            int count = sources.Count;
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var words = new int[count][];
            for (int s = 0; s < count; s++)
            {
                words[s] = Ids(sources[s], ids);
                if (words[s].Length > MaxWords)
                {
                    words[s] = new int[0];
                    result.TooLong++;
                }
            }

            // The words that are numbers: they do not count as different words.
            var numbers = new HashSet<int>();
            foreach (var entry in ids)
            {
                bool digits = entry.Key.Length > 0;
                foreach (char c in entry.Key)
                    if (!char.IsDigit(c)) { digits = false; break; }
                if (digits) numbers.Add(entry.Value);
            }

            var repeatsItself = new bool[count];
            for (int s = 0; s < count; s++) repeatsItself[s] = RepeatsItself(words[s], numbers);

            // First pass: in how many segments each phrase occurs.
            var seen = new Dictionary<ulong, PhraseCount>();
            for (int s = 0; s < count; s++)
            {
                var w = words[s];
                for (int i = 0; i + MinRunWords <= w.Length; i++)
                {
                    var hash = Hash(w, i);
                    seen.TryGetValue(hash, out var c);
                    if (c.Segments > 0 && c.LastSegment == s) continue;   // twice in one segment counts once
                    c.Segments++;
                    c.LastSegment = s;
                    seen[hash] = c;
                }
            }

            // Second pass: the segments of every phrase that two to
            // MaxSegmentsPerPhrase segments have. Everything else is unique, or
            // boilerplate.
            var shared = new Dictionary<ulong, List<int>>();
            for (int s = 0; s < count; s++)
            {
                var w = words[s];
                for (int i = 0; i + MinRunWords <= w.Length; i++)
                {
                    var hash = Hash(w, i);
                    int n = seen[hash].Segments;
                    if (n < 2 || n > MaxSegmentsPerPhrase) continue;
                    if (!shared.TryGetValue(hash, out var list)) shared.Add(hash, list = new List<int>(n));
                    if (list.Count == 0 || list[list.Count - 1] != s) list.Add(s);
                }
            }

            var candidates = new HashSet<long>();
            foreach (var list in shared.Values)
            {
                for (int x = 0; x < list.Count && !result.Limited; x++)
                    for (int y = x + 1; y < list.Count; y++)
                    {
                        var key = ((long)list[x] << 32) | (uint)list[y];
                        if (candidates.Contains(key)) continue;
                        if (candidates.Count >= maxPairs) { result.Limited = true; break; }
                        candidates.Add(key);
                    }
                if (result.Limited) break;
            }
            shared = null;

            var ordered = new List<long>(candidates);
            ordered.Sort();

            foreach (var key in ordered)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                var pair = PairOf(a, b, words[a], words[b]);
                if (pair == null) continue;   // two different phrases with one hash: nothing shared after all
                if (!pair.NearTwin && (repeatsItself[a] || repeatsItself[b])) continue;

                // The run the pair is known by: the longest with enough words of
                // its own. A pair with none shares boilerplate and little else.
                var longest = default(SharedRun);
                foreach (var run in pair.Runs)
                    if (run.Length >= MinRunWords && run.Length > longest.Length
                        && OwnWords(words[a], run.StartA, run.Length, seen) >= MinOwnWords
                        && DifferentWords(words[a], run.StartA, run.Length, numbers) >= MinDifferentWords) longest = run;
                if (longest.Length == 0) continue;
                pair.PassageWords = longest.Length;
                pair.Passage = Quote(sources[a], longest.StartA, longest.Length);
                result.Pairs.Add(pair);
            }

            return result;
        }

        private struct PhraseCount
        {
            public int Segments, LastSegment;
        }

        /// <summary>A number for the <see cref="MinRunWords"/> words from <paramref name="start"/> (FNV-1a over the word numbers).</summary>
        private static ulong Hash(int[] words, int start)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = start; i < start + MinRunWords; i++)
            {
                unchecked
                {
                    uint id = (uint)words[i];
                    for (int shift = 0; shift < 32; shift += 8)
                    {
                        hash ^= (id >> shift) & 0xFF;
                        hash *= 1099511628211UL;
                    }
                }
            }
            return hash;
        }

        /// <summary>Whether a segment mostly repeats itself (<see cref="MinVariety"/>).</summary>
        private static bool RepeatsItself(int[] words, HashSet<int> numbers)
        {
            int total = words.Length - 2;
            if (total < MinRunWords) return false;

            var different = new HashSet<long>();
            for (int i = 0; i < total; i++)
            {
                long key = 0;
                for (int k = 0; k < 3; k++)
                    key = key * 2097152 + (numbers.Contains(words[i + k]) ? 0 : words[i + k] + 1);
                different.Add(key);
            }
            return different.Count < MinVariety * total;
        }

        /// <summary>How many different words a run has, numbers not counted.</summary>
        private static int DifferentWords(int[] words, int start, int length, HashSet<int> numbers)
        {
            var different = new HashSet<int>();
            for (int i = start; i < start + length; i++)
                if (!numbers.Contains(words[i])) different.Add(words[i]);
            return different.Count;
        }

        /// <summary>
        /// How many words of a run lie outside boilerplate: in no phrase that
        /// more than <see cref="MaxSegmentsPerPhrase"/> segments have.
        /// </summary>
        private static int OwnWords(int[] words, int start, int length, Dictionary<ulong, PhraseCount> seen)
        {
            var boilerplate = new bool[length];
            for (int i = start; i + MinRunWords <= start + length; i++)
            {
                if (!seen.TryGetValue(Hash(words, i), out var c) || c.Segments <= MaxSegmentsPerPhrase) continue;
                for (int k = 0; k < MinRunWords; k++) boilerplate[i - start + k] = true;
            }

            int own = 0;
            foreach (var b in boilerplate)
                if (!b) own++;
            return own;
        }

        private static RestatedPair PairOf(int a, int b, int[] wordsA, int[] wordsB)
        {
            var runs = CommonRuns(wordsA, wordsB, MinSourcePieceWords);
            var chains = Chains(runs, MinRunWords, int.MaxValue);
            if (chains.Count == 0) return null;

            var pair = new RestatedPair { A = a, B = b, WordsA = wordsA.Length, WordsB = wordsB.Length, Passages = chains.Count };
            foreach (var chain in chains) pair.Runs.AddRange(chain);
            pair.Runs.Sort((x, y) => x.StartA != y.StartA ? x.StartA.CompareTo(y.StartA) : x.StartB.CompareTo(y.StartB));
            foreach (var run in pair.Runs) pair.SharedWords += run.Length;
            pair.NearTwin = pair.SharedWords >= TwinShare * Math.Max(pair.WordsA, pair.WordsB);
            if (pair.NearTwin)
            {
                pair.TwinDifferences = Differences(wordsA, wordsB, out var common);
                pair.TwinChangedWords = Math.Max(wordsA.Length, wordsB.Length) - common;
            }
            CountBreaks(chains, out pair.InteriorBreaks, out pair.EdgeBreaks);
            pair.ReorderedEdges = ReorderedEdges(chains, wordsA, wordsB);
            return pair;
        }

        private static string Quote(string source, int start, int length)
        {
            var words = WordsOf(source);
            var text = new StringBuilder();
            for (int i = start; i < start + length && i < words.Count && i < start + QuotedWords; i++)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(words[i]);
            }
            if (length > QuotedWords) text.Append(" …");
            return text.ToString();
        }

        // ---- shared runs, and the passages they make up -----------------------

        /// <summary>
        /// Every run of at least <paramref name="minLength"/> words that both
        /// have and that cannot be made longer at either end. O(a × b).
        /// </summary>
        private static List<SharedRun> CommonRuns(int[] a, int[] b, int minLength)
        {
            var runs = new List<SharedRun>();
            if (a.Length == 0 || b.Length == 0) return runs;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    if (a[i - 1] != b[j - 1]) { current[j] = 0; continue; }

                    int length = previous[j - 1] + 1;
                    current[j] = length;
                    bool ends = i == a.Length || j == b.Length || a[i] != b[j];
                    if (ends && length >= minLength)
                        runs.Add(new SharedRun { StartA = i - length, StartB = j - length, Length = length });
                }
                var swap = previous; previous = current; current = swap;
            }
            return runs;
        }

        /// <summary>
        /// Groups shared runs into passages. A passage starts from a run of at
        /// least <paramref name="seedLength"/> words, longest first, and takes in
        /// the runs that follow or precede it within <see cref="MaxGapWords"/>
        /// words in both segments: the same passage, carrying on after a few
        /// words that differ. At most <paramref name="maxChains"/> passages.
        /// </summary>
        private static List<List<SharedRun>> Chains(List<SharedRun> runs, int seedLength, int maxChains)
        {
            var chains = new List<List<SharedRun>>();
            if (runs.Count == 0) return chains;

            var order = new List<SharedRun>(runs);
            order.Sort((x, y) =>
                x.Length != y.Length ? y.Length.CompareTo(x.Length)
                : x.StartA != y.StartA ? x.StartA.CompareTo(y.StartA)
                : x.StartB.CompareTo(y.StartB));
            if (order.Count > MaxRuns) order.RemoveRange(MaxRuns, order.Count - MaxRuns);
            var used = new bool[order.Count];
            var taken = new List<SharedRun>();

            for (int s = 0; s < order.Count && chains.Count < maxChains; s++)
            {
                var seed = order[s];
                if (seed.Length < seedLength) break;
                if (used[s] || Overlaps(seed, taken)) continue;

                used[s] = true;
                var chain = new List<SharedRun> { seed };
                taken.Add(seed);

                // Onwards from the end, then back from the start.
                int endA = seed.StartA + seed.Length, endB = seed.StartB + seed.Length;
                while (true)
                {
                    int best = -1, bestGap = int.MaxValue;
                    for (int r = 0; r < order.Count; r++)
                    {
                        if (used[r]) continue;
                        int gapA = order[r].StartA - endA, gapB = order[r].StartB - endB;
                        if (gapA < 0 || gapB < 0 || gapA > MaxGapWords || gapB > MaxGapWords) continue;
                        if (gapA + gapB < bestGap && !Overlaps(order[r], taken)) { best = r; bestGap = gapA + gapB; }
                    }
                    if (best < 0) break;
                    used[best] = true;
                    chain.Add(order[best]);
                    taken.Add(order[best]);
                    endA = order[best].StartA + order[best].Length;
                    endB = order[best].StartB + order[best].Length;
                }

                int startA = seed.StartA, startB = seed.StartB;
                while (true)
                {
                    int best = -1, bestGap = int.MaxValue;
                    for (int r = 0; r < order.Count; r++)
                    {
                        if (used[r]) continue;
                        int gapA = startA - (order[r].StartA + order[r].Length), gapB = startB - (order[r].StartB + order[r].Length);
                        if (gapA < 0 || gapB < 0 || gapA > MaxGapWords || gapB > MaxGapWords) continue;
                        if (gapA + gapB < bestGap && !Overlaps(order[r], taken)) { best = r; bestGap = gapA + gapB; }
                    }
                    if (best < 0) break;
                    used[best] = true;
                    chain.Add(order[best]);
                    taken.Add(order[best]);
                    startA = order[best].StartA;
                    startB = order[best].StartB;
                }

                chains.Add(chain);
            }

            return chains;
        }

        /// <summary>
        /// The places where passages are broken - shared words, a few that differ,
        /// shared words again - counted as inside a passage or at its edge
        /// (<see cref="EdgeWords"/>).
        /// </summary>
        private static void CountBreaks(List<List<SharedRun>> chains, out int interior, out int edge)
        {
            interior = 0;
            edge = 0;
            foreach (var chain in chains)
            {
                if (chain.Count < 2) continue;
                chain.Sort((x, y) => x.StartA.CompareTo(y.StartA));
                int total = 0;
                foreach (var run in chain) total += run.Length;

                int before = 0;
                for (int i = 0; i + 1 < chain.Count; i++)
                {
                    before += chain[i].Length;
                    if (Math.Min(before, total - before) <= EdgeWords) edge++;
                    else interior++;
                }
            }
        }

        /// <summary>
        /// At how many passage ends the <see cref="EdgeWindow"/> words just
        /// outside the passage are, in both segments, mostly the same words (all
        /// but one) - which, since the passage stopped there, means in another
        /// order or with one changed.
        /// </summary>
        private static int ReorderedEdges(List<List<SharedRun>> chains, int[] a, int[] b)
        {
            int edges = 0;
            foreach (var chain in chains)
            {
                if (chain.Count == 0) continue;
                chain.Sort((x, y) => x.StartA.CompareTo(y.StartA));
                var first = chain[0];
                var last = chain[chain.Count - 1];
                if (SameWordsNearby(a, first.StartA - EdgeWindow, b, first.StartB - EdgeWindow)) edges++;
                if (SameWordsNearby(a, last.StartA + last.Length, b, last.StartB + last.Length)) edges++;
            }
            return edges;
        }

        /// <summary>Whether the <see cref="EdgeWindow"/> words from each start have all but one word in common.</summary>
        private static bool SameWordsNearby(int[] a, int startA, int[] b, int startB)
        {
            if (startA < 0 || startB < 0 || startA + EdgeWindow > a.Length || startB + EdgeWindow > b.Length) return false;

            int common = 0;
            var used = new bool[EdgeWindow];
            for (int i = 0; i < EdgeWindow; i++)
                for (int j = 0; j < EdgeWindow; j++)
                    if (!used[j] && a[startA + i] == b[startB + j]) { used[j] = true; common++; break; }
            return common >= EdgeWindow - 1;
        }

        /// <summary>Whether a run uses words, in either segment, that a run already taken uses.</summary>
        private static bool Overlaps(SharedRun run, List<SharedRun> taken)
        {
            foreach (var t in taken)
            {
                bool inA = run.StartA < t.StartA + t.Length && t.StartA < run.StartA + run.Length;
                bool inB = run.StartB < t.StartB + t.Length && t.StartB < run.StartB + run.Length;
                if (inA || inB) return true;
            }
            return false;
        }

        // ---- the translations --------------------------------------------------

        /// <summary>
        /// Whether a pair's shared passage was translated the same way in both
        /// segments. There is no word alignment, so this is a judgement from the
        /// two translations alone, made the way the sources were paired:
        ///
        /// <para>1. Two sources that are word for word the same must have
        /// translations that are word for word the same.</para>
        ///
        /// <para>2. Near-twins - the same sentence but for a few words - are
        /// compared whole: the translations may differ in as many places as the
        /// sources do, wherever those places are. One source word changed is one
        /// place, even where the other language puts it somewhere else in the
        /// sentence ("The file could not be saved" / "No se pudo guardar el
        /// archivo"). More places than the sources have are still no difference
        /// when no more words changed than in the sources: "zonder lamp" / "met
        /// schuif" is one place, and its English, "without the lamp" / "with the
        /// slide", is two only because "the" stands between the changed words
        /// (seen 15 Nov 2026: a pair translated exactly alike was reported).</para>
        ///
        /// <para>3. Otherwise the translations' own shared runs are grouped into
        /// passages, as many as the sources have. If those share less than
        /// <see cref="MinTargetShare"/> of the words the source passage's length
        /// leads one to expect (its words × the shorter segment's
        /// translation-to-source length), it was translated differently.</para>
        ///
        /// <para>4. Otherwise, if the translations' passages are broken in more
        /// places than the sources' are - shared wording, a few different words,
        /// shared wording again - it was translated differently: a break the
        /// source does not have is the translator's, or the AI's. Breaks inside a
        /// passage and breaks at its edge are counted apart, so a difference the
        /// sources have at the edge cannot excuse one the translations have in
        /// the middle.</para>
        ///
        /// <para>Words only, without case: a difference in punctuation or capitals
        /// is never reported. Where it can be wrong is set out in
        /// docs/design/restated-passages.md.</para>
        /// </summary>
        public static RestatedVerdict Compare(RestatedPair pair, string targetA, string targetB)
        {
            if (pair == null) return RestatedVerdict.Skipped;

            var textA = WithoutNote(targetA);
            var textB = WithoutNote(targetB);
            if (WrittenWithoutSpaces(textA) || WrittenWithoutSpaces(textB)) return RestatedVerdict.Skipped;

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var a = Ids(textA, ids);
            var b = Ids(textB, ids);
            if (a.Length == 0 || b.Length == 0) return RestatedVerdict.Skipped;
            if (a.Length > MaxWords || b.Length > MaxWords) return RestatedVerdict.Skipped;
            if (pair.WordsA == 0 || pair.WordsB == 0) return RestatedVerdict.Skipped;

            if (pair.SharedWords == pair.WordsA && pair.SharedWords == pair.WordsB && pair.Runs.Count == 1)
                return SameWords(a, b) ? RestatedVerdict.Same : RestatedVerdict.Different;

            if (pair.NearTwin)
            {
                int places = Differences(a, b, out var common);
                if (common < MinTargetShare * Math.Min(a.Length, b.Length)) return RestatedVerdict.Different;
                int changed = Math.Max(a.Length, b.Length) - common;
                return places > pair.TwinDifferences && changed > pair.TwinChangedWords
                    ? RestatedVerdict.Different
                    : RestatedVerdict.Same;
            }

            var chains = Chains(CommonRuns(a, b, MinPieceWords), MinPieceWords, pair.Passages);
            int shared = 0;
            foreach (var chain in chains)
                foreach (var run in chain) shared += run.Length;

            double ratio = Math.Min((double)a.Length / pair.WordsA, (double)b.Length / pair.WordsB);
            if (shared < MinTargetShare * pair.SharedWords * ratio) return RestatedVerdict.Different;

            CountBreaks(chains, out var interior, out var edge);
            if (interior > pair.InteriorBreaks || interior + edge > pair.Breaks) return RestatedVerdict.Different;

            // The passage's first and last words: the same words in another order
            // just outside the shared wording, where the sources have no such thing.
            return ReorderedEdges(chains, a, b) > pair.ReorderedEdges
                ? RestatedVerdict.Different
                : RestatedVerdict.Same;
        }

        /// <summary>
        /// In how many places two word sequences differ: the stretches, in either,
        /// that are left over when the longest sequence of words they have in
        /// common, in order, is taken out. <paramref name="common"/> is how many
        /// words that is. O(a × b) in time and memory: for near-twins only, which
        /// are under <see cref="MaxWords"/> words.
        /// </summary>
        internal static int Differences(int[] a, int[] b, out int common)
        {
            int n = a.Length, m = b.Length;
            var length = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    length[i, j] = a[i] == b[j]
                        ? length[i + 1, j + 1] + 1
                        : Math.Max(length[i + 1, j], length[i, j + 1]);
            common = length[0, 0];

            int places = 0, x = 0, y = 0;
            bool inGap = false;
            while (x < n && y < m)
            {
                if (a[x] == b[y]) { x++; y++; inGap = false; continue; }
                if (!inGap) { places++; inGap = true; }
                if (length[x + 1, y] >= length[x, y + 1]) x++; else y++;
            }
            if ((x < n || y < m) && !inGap) places++;
            return places;
        }

        /// <summary>
        /// The places where two texts differ, each as "yours X / earlier Y" with
        /// the words as written, in order; at most <paramref name="max"/>, with
        /// <paramref name="total"/> saying how many there are in all. For showing
        /// a near-twin to the AI (<see cref="EarlierTranslations"/>): the words
        /// and numbers it must take from its own segment. O(words x words) in
        /// time and memory, so for a pair already known to be twins and no other.
        /// </summary>
        internal static List<string> WordDifferences(string own, string earlier, int max, out int total)
        {
            var result = new List<string>();
            total = 0;
            var a = WordsOf(own);
            var b = WordsOf(earlier);
            if (a.Count == 0 || b.Count == 0 || a.Count > MaxWords || b.Count > MaxWords) return result;

            int n = a.Count, m = b.Count;
            var length = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    length[i, j] = string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase)
                        ? length[i + 1, j + 1] + 1
                        : Math.Max(length[i + 1, j], length[i, j + 1]);

            var mine = new List<string>();
            var theirs = new List<string>();
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && string.Equals(a[x], b[y], StringComparison.OrdinalIgnoreCase))
                {
                    Close(result, mine, theirs, max, ref total);
                    x++; y++;
                }
                else if (y >= m || (x < n && length[x + 1, y] >= length[x, y + 1])) mine.Add(a[x++]);
                else theirs.Add(b[y++]);
            }
            Close(result, mine, theirs, max, ref total);
            return result;
        }

        private static void Close(List<string> result, List<string> mine, List<string> theirs, int max, ref int total)
        {
            if (mine.Count == 0 && theirs.Count == 0) return;
            total++;
            if (result.Count < max)
                result.Add("yours \u201C" + string.Join(" ", mine) + "\u201D / earlier \u201C" + string.Join(" ", theirs) + "\u201D");
            mine.Clear();
            theirs.Clear();
        }

        private static bool SameWords(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>A translation without the <c>[[TC: ...]]</c> note at its end.</summary>
        private static string WithoutNote(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return "";
            return ReplyCheck.WithoutComment(target, out _) ?? "";
        }

        /// <summary>
        /// Where the translations of restated passages differ.
        /// <paramref name="targets"/> has one entry per source, in the same
        /// order. A pair with a translation missing is skipped, never reported
        /// as a difference.
        /// </summary>
        public static RestatedReport Report(IList<string> sources, IList<RestatedTarget> targets)
        {
            var report = new RestatedReport();
            if (sources == null || targets == null) return report;
            if (sources.Count != targets.Count)
                throw new ArgumentException("One target is needed for each source: "
                    + sources.Count + " sources, " + targets.Count + " targets.");

            return ReportOn(Find(sources), targets);
        }

        /// <summary>
        /// The same for pairs already found (<see cref="Find"/>), so a run that
        /// needed them earlier does not find them twice.
        /// </summary>
        public static RestatedReport ReportOn(RestatedPairs found, IList<RestatedTarget> targets)
        {
            var report = new RestatedReport();
            if (found == null || targets == null) return report;
            report.Limited = found.Limited;

            foreach (var pair in found.Pairs)
            {
                var a = targets[pair.A];
                var b = targets[pair.B];
                var verdict = Compare(pair, a?.Text, b?.Text);
                if (verdict == RestatedVerdict.Skipped)
                {
                    report.Skipped++;
                    continue;
                }

                report.Compared++;
                if (verdict == RestatedVerdict.Different)
                    report.Different.Add(new RestatedFinding { Pair = pair, LabelA = a.Label, LabelB = b.Label });
            }

            return report;
        }
    }
}
