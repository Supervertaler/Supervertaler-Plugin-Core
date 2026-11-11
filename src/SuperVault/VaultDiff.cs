using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.SuperVault
{
    public enum VaultTokenKind { Word, Punctuation, Space, Tag }

    /// <summary>A token of a segment: where it is (piece index, character offset within the piece) and what it is.</summary>
    public sealed class VaultToken
    {
        public VaultTokenKind Kind;
        public string Text;      // the word/punctuation/space; for a tag, its pairing key
        public int Piece;        // index of the piece (text run or tag) it lies in
        public int Offset;       // first character within that piece
        public int Length;
        public bool IsContent => Kind == VaultTokenKind.Word || Kind == VaultTokenKind.Punctuation;
        public override string ToString() => Kind + ":" + Text;
    }

    public enum VaultEditOp { Identity, Change, Insert, Delete, Move }

    /// <summary>
    /// One step of the alignment: document token <see cref="A"/> against memory token <see cref="B"/>. For a Delete (a
    /// document token the memory lacks), B is the memory position it goes before; for an Insert (a memory token the
    /// document lacks), A is the document position it goes before: Studio's convention for its edit lists. A Move is a
    /// token both have in different places: A where the document has it, B where the memory has it. Cost is the charge
    /// toward the score.
    /// </summary>
    public struct VaultEdit
    {
        public VaultEditOp Op;
        public int A, B;
        public double Cost;
        /// <summary>A number replaced in the proposal by the document's (<see cref="VaultNumbers"/>): no charge, and not
        /// drawn as a difference, as Studio does with the numbers it replaces itself.</summary>
        public bool Replaced;
    }

    /// <summary>How a document segment compares with a stored one: the match % and the token alignment behind it.</summary>
    public sealed class VaultDiffResult
    {
        public int Score;                       // 0..100; 100 only for identical normalised text
        public List<VaultEdit> Edits;           // null when the segments were too long to align (scored on what they share)
        public double Distance;                 // total charge of the differences
    }

    /// <summary>
    /// Word-level comparison of a document segment with a stored unit: the match % SuperVault reports, and the
    /// alignment that lets a CAT tool show the differences. Our own, because the CAT tools' scorers are not public; it
    /// scores the way Studio does (docs/design/evertm-fuzzy-score.md), so EverTM's matches sit in the same bands as a
    /// Studio TM's.
    ///
    /// Scale: tokenising is linear; the alignment is O(n·m) in tokens, so it is only run on the few best candidates of
    /// a lookup (see the callers; the others get <see cref="Estimate"/>, an upper bound), and segments over
    /// <see cref="MaxAlignTokens"/> tokens are scored on what they share instead, without differences shown. A 400×400
    /// alignment is 160,000 cells, about a millisecond.
    /// </summary>
    public static class VaultDiff
    {
        public const int MaxAlignTokens = 400;

        /// <summary>
        /// Tokens of a segment given as pieces. Words are runs of letters, digits and combining marks (an apostrophe or
        /// hyphen between two letters stays inside the word: "don't", "e-mail"); every other visible character is
        /// punctuation on its own; whitespace runs are one token; each tag is one token.
        /// </summary>
        public static List<VaultToken> Tokenize(IList<VaultPiece> pieces)
        {
            var tokens = new List<VaultToken>();
            for (int p = 0; p < pieces.Count; p++)
            {
                var piece = pieces[p];
                if (piece.IsTag) { tokens.Add(new VaultToken { Kind = VaultTokenKind.Tag, Text = piece.Key ?? piece.Raw, Piece = p, Offset = 0, Length = 0 }); continue; }
                var s = piece.Text ?? "";
                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    int start = i;
                    VaultTokenKind kind;
                    if (IsSpace(c)) { while (i < s.Length && IsSpace(s[i])) i++; kind = VaultTokenKind.Space; }
                    else if (IsWordChar(s, i))
                    {
                        while (i < s.Length && (IsWordChar(s, i) || ((IsJoiner(s[i]) || IsInvisible(s[i])) && i + 1 < s.Length && IsWordChar(s, i + 1) && i > start))) i++;
                        kind = VaultTokenKind.Word;
                    }
                    else if (IsInvisible(c)) { i++; continue; }   // a soft hyphen or zero-width character on its own: no token
                    else { i += char.IsSurrogatePair(s, i) ? 2 : 1; kind = VaultTokenKind.Punctuation; }
                    var text = s.Substring(start, i - start);
                    // Inside a word, soft hyphens and zero-width characters are left out of its text, as the plain text
                    // the index counts words in leaves them out (VaultMarkup.Normalize): the word counts must agree.
                    if (kind == VaultTokenKind.Word && text.IndexOfAny(Invisible) >= 0) text = string.Concat(text.Where(ch => !IsInvisible(ch)));
                    tokens.Add(new VaultToken { Kind = kind, Text = text, Piece = p, Offset = start, Length = i - start });
                }
            }
            return tokens;
        }

        static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == ' ' || c == ' ' || c == ' ';
        static readonly char[] Invisible = { '\u00AD', '\u200B', '\u200C', '\u200D', '\uFEFF' };
        static bool IsInvisible(char c) => Array.IndexOf(Invisible, c) >= 0;
        static bool IsJoiner(char c) => c == '\'' || c == '’' || c == '-';
        static bool IsWordChar(string s, int i)
        {
            if (char.IsLetterOrDigit(s, i)) return true;
            var cat = CharUnicodeInfo.GetUnicodeCategory(s, i);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.ConnectorPunctuation && s[i] == '_';
        }

        /// <summary>Lower-cased words of a text, for the index (one term per word, punctuation dropped).</summary>
        public static List<string> Words(string text)
        {
            var words = new List<string>();
            foreach (var t in Tokenize(new[] { new VaultPiece { Text = text ?? "" } }))
                if (t.Kind == VaultTokenKind.Word) words.Add(t.Text.ToLowerInvariant());
            return words;
        }

        // ---- Scoring: Studio's way of scoring a fuzzy match, as described in docs/design/evertm-fuzzy-score.md (1a),
        // in our own code (11 Nov 2026; Michael's answer 1). Not exactly Studio's: its stopword lists, stemmers and
        // tokenizer (URLs, measurements as one token) are its own, so about one pair in eight scores a few points higher.

        // Charges, against the weight of both segments together.
        const double GapWord = 1.5, GapShortWord = 0.7, GapMinor = 0.1;              // an extra or missing token
        const double ChangeNear = 0.2, ChangeAlike = 0.7, ChangeWord = 1.5, ChangeMinor = 0.1, MoveCharge = 0.8;
        /// <summary>A different number. Charged as a changed word: EverTM doesn't replace numbers in its proposal.</summary>
        internal const double NumberChange = 1.5;
        const double WeightWord = 1.0, WeightMinor = 0.1;                            // a word or number; punctuation or space; a tag weighs 0

        enum K { Word, Number, Punct, Space, Tag }

        /// <summary>A token as the scorer sees it.</summary>
        sealed class T
        {
            public K Kind; public string Text, Lower, Folded, Stem;
            // The same strings as numbers, for the alignment's inner loop: equal numbers, equal strings (one numbering
            // per comparison). Stem -1: none. First: the lower-cased first character.
            public int TextId, FoldId, StemId; public char First;
            public bool IsWordLike => Kind == K.Word || Kind == K.Number;
            public bool IsMinor => Kind == K.Punct || Kind == K.Space;
        }

        static K KindOf(VaultToken t)
        {
            switch (t.Kind)
            {
                case VaultTokenKind.Space: return K.Space;
                case VaultTokenKind.Punctuation: return K.Punct;
                case VaultTokenKind.Tag: return K.Tag;
                default:
                    foreach (var c in t.Text) if (!char.IsDigit(c)) return K.Word;
                    return K.Number;
            }
        }

        static T[] Prepare(List<VaultToken> tokens, VaultWordRules rules, Dictionary<string, int> ids = null)
        {
            if (ids == null) ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int Id(string v) { if (!ids.TryGetValue(v, out int n)) ids[v] = n = ids.Count; return n; }
            var list = new T[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
            {
                var k = KindOf(tokens[i]);
                var text = tokens[i].Text ?? "";
                var lower = k == K.Tag ? text : text.ToLowerInvariant();
                string stem = "";
                if (k == K.Word && rules?.Stem != null) { try { stem = rules.Stem(lower) ?? ""; } catch { stem = ""; } }
                var folded = k == K.Word ? Fold(lower) : lower;
                list[i] = new T
                {
                    Kind = k, Text = text, Lower = lower, Folded = folded, Stem = stem,
                    // Tags are numbered by key apart from text, so a tag never equals a word of the same spelling.
                    TextId = Id((k == K.Tag ? "\u0001" : "") + text), FoldId = Id((k == K.Tag ? "\u0001" : "") + folded), StemId = stem.Length > 0 ? Id(stem) : -1,
                    First = text.Length > 0 ? char.ToLowerInvariant(text[0]) : '\0',
                };
            }
            return list;
        }

        static string Fold(string s)
        {
            bool plain = true;
            foreach (var c in s) if (c > 0x7f) { plain = false; break; }
            if (plain) return s;
            var d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>How alike two tokens are, for the alignment: 1 the same, 0.95 nearly (case, accents, stem; two tags),
        /// 0.94 two different punctuation marks or spaces, 0.85 two different numbers, 0 two different words; -1 when one
        /// can never be changed into the other (a word, a punctuation mark, a space and a tag).</summary>
        static double Sim(T a, T b)
        {
            if (a.Kind == K.Space || b.Kind == K.Space) return a.Kind == b.Kind ? (a.TextId == b.TextId ? 1 : 0.94) : -1;
            if (a.Kind == K.Punct || b.Kind == K.Punct) return a.Kind == b.Kind ? (a.TextId == b.TextId ? 1 : 0.94) : -1;
            if (a.Kind == K.Tag || b.Kind == K.Tag) return a.Kind == b.Kind ? (a.TextId == b.TextId ? 1 : 0.95) : -1;
            if (a.TextId == b.TextId) return 1;
            if (a.Kind == K.Number && b.Kind == K.Number) return 0.85;
            if (a.Kind != b.Kind) return 0;   // a word against a number
            if (a.FoldId == b.FoldId) return 0.95;
            if (a.StemId >= 0 && a.StemId == b.StemId) return 0.95;
            return 0;
        }

        // Where two alignments cost the same, prefer the one Studio would draw: a word paired with a word that starts
        // the same way ("disable" changed into "disabling"), as in the display fix of 11 Nov 2026. Only a tie-break:
        // alignment costs are multiples of 0.01, and this times the longest alignment (2 x MaxAlignTokens) stays far
        // below that, so the cheapest alignment, and with it the score, is unchanged.
        const double KindTieBreak = 1e-6;
        static double Pairing(T x, T y)
        {
            if (!x.IsWordLike || !y.IsWordLike || x.TextId == y.TextId) return 0;
            if (x.First != y.First) return KindTieBreak / 2;   // nothing in common at the start (most pairs: no string work)
            int len = Math.Max(x.Text.Length, y.Text.Length), common = 0;
            while (common < x.Text.Length && common < y.Text.Length && char.ToLowerInvariant(x.Text[common]) == char.ToLowerInvariant(y.Text[common])) common++;
            return len == 0 || common == len ? 0 : KindTieBreak / 2 * (1 - (double)common / len);
        }

        static double Gap(T t) => t.IsMinor ? GapMinor : t.Kind == K.Tag ? 0 : t.Text.Length > 2 ? GapWord : GapShortWord;

        static double ChangeCharge(T p, T q)
        {
            if (p.IsMinor && q.IsMinor) return ChangeMinor;
            if (p.Kind == K.Tag) return 0;
            if (p.Kind == K.Number && q.Kind == K.Number) return NumberChange;
            if (p.Folded == q.Folded || (p.Stem.Length > 0 && p.Stem == q.Stem)) return ChangeNear;
            if (p.Text.Length + q.Text.Length >= 100) return ChangeWord;
            return CharSimilarity(p.Text, q.Text) >= 0.75 ? ChangeAlike : ChangeWord;
        }

        /// <summary>How alike two words look, 0..1: their character edit distance against their total length. A
        /// different case costs 0.05, and one small change in a short word counts 0.3.</summary>
        static double CharSimilarity(string a, string b)
        {
            int n = a.Length, m = b.Length;
            var d = new double[n + 1, m + 1];
            for (int i = 1; i <= n; i++) d[i, 0] = i;
            for (int j = 1; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                {
                    double s = a[i - 1] == b[j - 1] ? 1 : char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0.95 : 0;
                    d[i, j] = Math.Min(d[i - 1, j - 1] + (1 - s), Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
                }
            double dist = d[n, m];
            if (n + m <= 6 && Math.Abs(n - m) <= 1 && dist == 1.0) dist = 0.3;
            double total = n + m;
            return total <= 0 ? 0 : Math.Max(0, (total - 2 * dist) / total);
        }

        static double Weight(T t) => t.IsWordLike ? WeightWord : t.IsMinor ? WeightMinor : 0;

        /// <summary>
        /// Compares document tokens <paramref name="a"/> with memory tokens <paramref name="b"/>. <paramref name="identical"/>
        /// says whether their normalised plain texts are equal: only then is the score 100. <paramref name="rules"/> gives
        /// the source language's stems and stopwords (null: none, which only ever lowers a score).
        /// </summary>
        public static VaultDiffResult Compare(List<VaultToken> a, List<VaultToken> b, bool identical, VaultWordRules rules = null,
                                             (HashSet<int> doc, HashSet<int> source)? replaced = null)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var x = Prepare(a, rules, ids);
            var y = Prepare(b, rules, ids);
            if (identical) return new VaultDiffResult { Score = 100, Edits = Align(x, y), Distance = 0 };
            if (!x.Any(t => t.IsWordLike || t.IsMinor) || !y.Any(t => t.IsWordLike || t.IsMinor)) return new VaultDiffResult { Score = 0 };

            if (a.Count > MaxAlignTokens || b.Count > MaxAlignTokens)
                return new VaultDiffResult { Score = LongScore(x, y) };

            var edits = Align(x, y);
            FindMoves(edits, x, y);
            double charge = 0;
            int sameStop = 0, sameOther = 0;
            for (int i = 0; i < edits.Count; i++)
            {
                var e = edits[i];
                switch (e.Op)
                {
                    case VaultEditOp.Identity:
                        if (x[e.A].Kind == K.Word) { if (rules?.IsStopword != null && rules.IsStopword(x[e.A].Lower)) sameStop++; else sameOther++; }
                        e.Cost = 0; break;
                    case VaultEditOp.Change:
                        if (replaced != null && replaced.Value.doc.Contains(e.A) && replaced.Value.source.Contains(e.B)) { e.Cost = 0; e.Replaced = true; }
                        else e.Cost = ChangeCharge(x[e.A], y[e.B]);
                        break;
                    case VaultEditOp.Move: e.Cost = MoveCharge; break;
                    case VaultEditOp.Delete: e.Cost = Gap(x[e.A]); break;
                    default: e.Cost = Gap(y[e.B]); break;
                }
                edits[i] = e;
                charge += e.Cost;
            }
            double weight = x.Sum(Weight) + y.Sum(Weight);
            int off = OffFor(charge, weight);
            if (rules?.IsStopword != null) off += StopwordMalus(x, y, rules, sameStop, sameOther, off, weight);
            return new VaultDiffResult { Score = ScoreFrom(off, charge), Edits = edits, Distance = charge };
        }

        // The points off for a charge: floor(100 x charge / weight). The tolerance keeps rounding noise from ever putting
        // the real score above the estimates (Estimate, VaultLengthBound), which round the other way.
        static int OffFor(double charge, double weight) => weight <= 0 ? (charge > 0 ? 100 : 0) : (int)Math.Floor(charge * 100 / weight + 1e-9);

        static int ScoreFrom(int off, double charge)
        {
            if (off > 100) return 0;
            if (off == 0 && charge > 0) off = 1;
            return Math.Max(0, Math.Min(99, 100 - off));
        }

        /// <summary>When the words the two share are mostly stopwords ("the", "of"), up to 7 points more off; for short
        /// segments, a smaller amount. Never negative.</summary>
        static int StopwordMalus(T[] x, T[] y, VaultWordRules rules, int sameStop, int sameOther, int off, double weight)
        {
            if (sameStop + sameOther == 0) return 0;
            int wa = 0, wb = 0, sa = 0, sb = 0;
            foreach (var t in x) if (t.Kind == K.Word) { wa++; if (rules.IsStopword(t.Lower)) sa++; }
            foreach (var t in y) if (t.Kind == K.Word) { wb++; if (rules.IsStopword(t.Lower)) sb++; }
            if (wa > 5 && wb > 5)
            {
                float ratio = ((float)sa / wa + (float)sb / wb) / 2f, same = (float)sameStop / (sameStop + sameOther);
                if (ratio < same) return ratio <= same * 0.65f ? 7 : ratio <= same * 0.8f ? 3 : 0;
                return 0;
            }
            if (off > 0 && sameStop > 0 && weight > 0)
                return Math.Max(0, (int)((float)sameStop / (sameStop + sameOther) * (int)(150f / weight)));
            return 0;
        }

        /// <summary>
        /// Minimum-cost alignment by token similarity (insert or delete 1, change 1 - similarity). An inserted or missing
        /// token carries the position it goes before, Studio's convention (<see cref="VaultEdit"/>). Costs are set later.
        /// </summary>
        static List<VaultEdit> Align(T[] a, T[] b)
        {
            int n = a.Length, m = b.Length, w = m + 1;
            // One flat array, row by row (a two-dimensional array is markedly slower in 32-bit .NET Framework, which
            // Studio 2024 runs in); D(i, j) is d[i * w + j].
            var d = new double[(n + 1) * w];
            for (int i = 1; i <= n; i++) d[i * w] = i;
            for (int j = 1; j <= m; j++) d[j] = j;
            for (int i = 1; i <= n; i++)
            {
                var ai = a[i - 1];
                int row = i * w, up = row - w;
                for (int j = 1; j <= m; j++)
                {
                    var bj = b[j - 1];
                    // The same text: nothing to compute (most cells of a good match).
                    double best = d[up + j - 1] + (ai.TextId == bj.TextId ? 0 : SubCost(ai, bj));
                    double del = d[up + j] + 1, ins = d[row + j - 1] + 1;
                    if (del < best) best = del;
                    if (ins < best) best = ins;
                    d[row + j] = best;
                }
            }

            var edits = new List<VaultEdit>(n + m);
            int x = n, y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0)
                {
                    double sub = a[x - 1].TextId == b[y - 1].TextId ? 0 : SubCost(a[x - 1], b[y - 1]);
                    if (!double.IsInfinity(sub) && Same(d[x * w + y], d[(x - 1) * w + y - 1] + sub))
                    {
                        edits.Add(new VaultEdit { Op = Sim(a[x - 1], b[y - 1]) == 1 ? VaultEditOp.Identity : VaultEditOp.Change, A = x - 1, B = y - 1 });
                        x--; y--; continue;
                    }
                }
                if (x > 0 && Same(d[x * w + y], d[(x - 1) * w + y] + 1))
                {
                    // A document token the memory lacks. Studio calls this Delete; B is the position in the memory it
                    // goes BEFORE (the memory token that follows it; the memory's length at the end), as in Studio's own
                    // edit lists. Studio draws the inserted word there: one less put it a token too early (11 Nov 2026).
                    edits.Add(new VaultEdit { Op = VaultEditOp.Delete, A = x - 1, B = y });
                    x--; continue;
                }
                // A memory token the document lacks (Insert): A is the document position it goes before, likewise.
                edits.Add(new VaultEdit { Op = VaultEditOp.Insert, A = x, B = y - 1 });
                y--;
            }
            edits.Reverse();
            return edits;
        }

        static double SubCost(T a, T b)
        {
            double s = Sim(a, b);
            return s < 0 ? double.PositiveInfinity : 1 - s + Pairing(a, b);
        }

        /// <summary>A missing and an extra token that are (nearly) the same become one move: A where the document has it,
        /// B where the memory has it. Spaces and tags don't move.</summary>
        static void FindMoves(List<VaultEdit> edits, T[] a, T[] b)
        {
            for (int k = 0; k < edits.Count; k++)
            {
                var ek = edits[k];
                if (ek.Op != VaultEditOp.Delete && ek.Op != VaultEditOp.Insert) continue;
                var tok = ek.Op == VaultEditOp.Delete ? a[ek.A] : b[ek.B];
                if (tok.Kind == K.Space || tok.Kind == K.Tag) continue;
                for (int l = k + 1; l < edits.Count; l++)
                {
                    var el = edits[l];
                    int docPos, memPos;
                    if (ek.Op == VaultEditOp.Delete && el.Op == VaultEditOp.Insert) { docPos = ek.A; memPos = el.B; }
                    else if (ek.Op == VaultEditOp.Insert && el.Op == VaultEditOp.Delete) { docPos = el.A; memPos = ek.B; }
                    else continue;
                    if (Sim(a[docPos], b[memPos]) < 0.95) continue;
                    edits[k] = new VaultEdit { Op = VaultEditOp.Move, A = docPos, B = memPos };
                    edits.RemoveAt(l);
                    break;
                }
            }
        }

        // ---- Segments too long to align, and the estimates that decide what gets aligned ----

        /// <summary>Exact-text counts of the tokens, by kind: what the estimates and the long-segment score work from.</summary>
        sealed class Profile
        {
            public readonly Dictionary<string, int> Words = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Punct = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Spaces = new Dictionary<string, int>(StringComparer.Ordinal);
            public int WordCount, NumberCount, PunctCount, SpaceCount;
            public double Weight;

            public Profile(T[] tokens)
            {
                foreach (var t in tokens)
                {
                    Weight += VaultDiff.Weight(t);
                    switch (t.Kind)
                    {
                        case K.Word: case K.Number: WordCount++; if (t.Kind == K.Number) NumberCount++; Add(Words, t.Text); break;
                        case K.Punct: PunctCount++; Add(Punct, t.Text); break;
                        case K.Space: SpaceCount++; Add(Spaces, t.Text); break;
                    }
                }
            }

            static void Add(Dictionary<string, int> d, string s) { d.TryGetValue(s, out int c); d[s] = c + 1; }
        }

        static int Common(Dictionary<string, int> p, Dictionary<string, int> q)
        {
            int c = 0;
            foreach (var kv in p) if (q.TryGetValue(kv.Key, out int n)) c += Math.Min(kv.Value, n);
            return c;
        }

        static int CommonNumbers(T[] x, T[] y)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in x) if (t.Kind == K.Number) { d.TryGetValue(t.Text, out int c); d[t.Text] = c + 1; }
            int common = 0;
            foreach (var t in y) if (t.Kind == K.Number && d.TryGetValue(t.Text, out int c) && c > 0) { common++; d[t.Text] = c - 1; }
            return common;
        }

        /// <summary>
        /// The least the differences between the two can be charged, from what they share (exact words, punctuation
        /// marks and spaces): every word one has and the other lacks is at best a near-identical change (0.2; two
        /// numbers nothing, as one may be replaced by the other), or, where one side has more, a missing short word
        /// (0.7); every unshared punctuation mark or space at least 0.1.
        /// </summary>
        static double LeastCharge(Profile p, Profile q, int unsharedNumbersP, int unsharedNumbersQ)
        {
            int common = Common(p.Words, q.Words);
            int up = p.WordCount - common, uq = q.WordCount - common;
            int pairs = Math.Min(up, uq), numberPairs = Math.Min(pairs, Math.Min(unsharedNumbersP, unsharedNumbersQ));
            double words = ChangeNear * (pairs - numberPairs) + 0 * numberPairs + GapShortWord * Math.Abs(up - uq);   // a number may be replaced: free
            int pp = p.PunctCount - Common(p.Punct, q.Punct), pq = q.PunctCount - Common(p.Punct, q.Punct);
            int sp = p.SpaceCount - Common(p.Spaces, q.Spaces), sq = q.SpaceCount - Common(p.Spaces, q.Spaces);
            return words + GapMinor * Math.Max(pp, pq) + GapMinor * Math.Max(sp, sq);
        }

        /// <summary>
        /// The score of segments too long to align: from what they share, without moves or near-identical words. Each
        /// unshared word is charged as a changed word or, where one side has more, as a missing one; punctuation and
        /// spaces 0.1. At least the charge <see cref="LeastCharge"/> counts, so <see cref="Estimate"/> stays above it.
        /// </summary>
        static int LongScore(T[] x, T[] y)
        {
            var p = new Profile(x); var q = new Profile(y);
            int common = Common(p.Words, q.Words);
            int up = p.WordCount - common, uq = q.WordCount - common;
            int pairs = Math.Min(up, uq);
            int commonNumbers = CommonNumbers(x, y);
            int numberPairs = Math.Min(pairs, Math.Min(p.NumberCount - commonNumbers, q.NumberCount - commonNumbers));
            double charge = ChangeWord * (pairs - numberPairs) + NumberChange * numberPairs + GapWord * Math.Abs(up - uq);
            int pp = p.PunctCount - Common(p.Punct, q.Punct), pq = q.PunctCount - Common(p.Punct, q.Punct);
            int sp = p.SpaceCount - Common(p.Spaces, q.Spaces), sq = q.SpaceCount - Common(p.Spaces, q.Spaces);
            charge += GapMinor * Math.Max(pp, pq) + GapMinor * Math.Max(sp, sq);
            return ScoreFrom(OffFor(charge, p.Weight + q.Weight), charge);
        }

        /// <summary>
        /// An upper bound of <see cref="Compare"/>'s score, cheap enough for every candidate: used to pick which ones to
        /// align and to skip those that can't reach the minimum. Never below the real score, so no match that would
        /// reach the minimum is dropped before it is aligned (checked against every pair in the score measurements).
        /// </summary>
        public static int Estimate(List<VaultToken> a, List<VaultToken> b, bool identical)
        {
            if (identical) return 100;
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var x = Prepare(a, null, ids); var y = Prepare(b, null, ids);
            if (a.Count > MaxAlignTokens || b.Count > MaxAlignTokens) return LongScore(x, y);
            var p = new Profile(x); var q = new Profile(y);
            if (p.WordCount + p.PunctCount + p.SpaceCount == 0 || q.WordCount + q.PunctCount + q.SpaceCount == 0) return 0;
            int commonNumbers = CommonNumbers(x, y);
            double least = LeastCharge(p, q, p.NumberCount - commonNumbers, q.NumberCount - commonNumbers);
            double weight = p.Weight + q.Weight;
            int off = weight <= 0 ? 0 : Math.Max(0, (int)Math.Floor(least * 100 / weight - 1e-9));
            return Math.Max(0, Math.Min(99, 100 - off));
        }

        static bool Same(double p, double q) => Math.Abs(p - q) < 1e-9;
    }

    /// <summary>The source language's word rules for scoring: stems and stopwords. Made per lookup, not shared between
    /// threads (a stemmer keeps state). Plugin Core's index part makes them (VaultLanguage).</summary>
    public sealed class VaultWordRules
    {
        /// <summary>Lower-case word to its stem; null: no stem matching.</summary>
        public Func<string, string> Stem;
        /// <summary>Whether a lower-case word is a stopword; null: no stopword correction.</summary>
        public Func<string, bool> IsStopword;
    }

    /// <summary>
    /// The best score a stored unit of a given word count can reach against a document, from the word count alone (the
    /// index keeps it per unit, so nothing has to be read): every word one side has more than the other is missing at
    /// least as a short word (0.7), against the weight of both. An upper bound of <see cref="VaultDiff.Compare"/>:
    ///  - the unit's punctuation and spaces are unknown; the bound takes the count that gives the highest score (as
    ///    many as the document has: fewer or more both cost more than they weigh);
    ///  - the index counts words in the unit's plain text; a tag the match keeps can split a word in two, so the unit
    ///    may have up to as many more words as the document has tags.
    /// </summary>
    public sealed class VaultLengthBound
    {
        readonly int _words, _tags;
        readonly double _weight, _minor;
        readonly bool _long;

        public VaultLengthBound(List<VaultToken> document)
        {
            foreach (var t in document)
            {
                if (t.Kind == VaultTokenKind.Word) { _words++; _weight += 1; }
                else if (t.Kind == VaultTokenKind.Tag) _tags++;
                else { _minor += 1; _weight += 0.1; }
            }
            _long = document.Count > VaultDiff.MaxAlignTokens;
        }

        /// <summary>The highest score a unit of <paramref name="words"/> words (plain text) can reach; 99 at most.</summary>
        public int MaxScore(long words)
        {
            if (_long) return 99;   // scored without alignment; no length bound is claimed for those
            long k = Math.Max(0, Math.Max(words - _words, _words - (words + _tags)));
            if (k == 0) return 99;
            double weight = _weight + words + _tags + 0.1 * (_minor + _tags);
            int off = Math.Max(0, (int)Math.Floor(0.7 * k * 100 / weight - 1e-9));
            return Math.Max(0, Math.Min(99, 100 - off));
        }
    }

    /// <summary>
    /// How the differences are drawn: an alignment of its own, apart from the score's (Michael's choice, 11 Nov 2026:
    /// "clearer highlighting"). The score aligns as Studio does, by word similarity with moves, and the percentage is
    /// always that one; but Studio's way of pairing words across a rephrased stretch reads badly ("whereinby the hardware
    /// prefetcherone or..."). This one, from the display fix of 11 Nov, counts every word and punctuation mark the same,
    /// so a rephrased stretch shows as words inserted and struck, in order. Run only for the rows handed to the CAT tool
    /// (VaultLookup), never for every candidate: O(n·m) in tokens, at most 400 × 400.
    /// </summary>
    public static class VaultDisplay
    {
        const double WordCost = 1.0, MinorCost = 0.05;

        /// <summary>The steps to draw, in Studio's convention (<see cref="VaultEdit"/>); null when either side is over
        /// <see cref="VaultDiff.MaxAlignTokens"/> (nothing is drawn then).</summary>
        public static List<VaultEdit> Edits(List<VaultToken> a, List<VaultToken> b)
        {
            if (a == null || b == null || a.Count > VaultDiff.MaxAlignTokens || b.Count > VaultDiff.MaxAlignTokens) return null;
            int n = a.Count, m = b.Count;
            // Each token's text as a number (equal numbers, equal texts) and its lower-cased first character: the inner
            // loop then compares numbers, and only words starting alike are compared as strings.
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] ia = Ids(a, ids), ib = Ids(b, ids);
            char[] fa = Firsts(a), fb = Firsts(b);
            double Step(int i, int j) => SubCost(a[i], b[j], ia[i] == ib[j]) + Pairing(a[i], b[j], ia[i] == ib[j], fa[i] == fb[j]);
            int w = m + 1;
            var d = new double[(n + 1) * w];   // flat, as in the scorer's alignment: D(i, j) is d[i * w + j]
            for (int i = 1; i <= n; i++) d[i * w] = d[(i - 1) * w] + GapCost(a[i - 1]);
            for (int j = 1; j <= m; j++) d[j] = d[j - 1] + GapCost(b[j - 1]);
            for (int i = 1; i <= n; i++)
            {
                int row = i * w, up = row - w;
                double gapA = GapCost(a[i - 1]);
                for (int j = 1; j <= m; j++)
                {
                    double best = d[up + j - 1] + (ia[i - 1] == ib[j - 1] && a[i - 1].Kind == b[j - 1].Kind ? 0 : Step(i - 1, j - 1));
                    double del = d[up + j] + gapA;
                    double ins = d[row + j - 1] + GapCost(b[j - 1]);
                    if (del < best) best = del;
                    if (ins < best) best = ins;
                    d[row + j] = best;
                }
            }

            var edits = new List<VaultEdit>(n + m);
            int x = n, y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0)
                {
                    double sub = SubCost(a[x - 1], b[y - 1], ia[x - 1] == ib[y - 1]);
                    if (Same(d[x * w + y], d[(x - 1) * w + y - 1] + Step(x - 1, y - 1)))
                    {
                        edits.Add(new VaultEdit { Op = sub == 0 ? VaultEditOp.Identity : VaultEditOp.Change, A = x - 1, B = y - 1, Cost = sub });
                        x--; y--; continue;
                    }
                }
                if (x > 0 && Same(d[x * w + y], d[(x - 1) * w + y] + GapCost(a[x - 1])))
                {
                    // A document token the memory lacks (Delete): B is the memory position it goes before.
                    edits.Add(new VaultEdit { Op = VaultEditOp.Delete, A = x - 1, B = y, Cost = GapCost(a[x - 1]) });
                    x--; continue;
                }
                // A memory token the document lacks (Insert): A is the document position it goes before.
                edits.Add(new VaultEdit { Op = VaultEditOp.Insert, A = x, B = y - 1, Cost = GapCost(b[y - 1]) });
                y--;
            }
            edits.Reverse();
            return edits;
        }

        static int[] Ids(List<VaultToken> tokens, Dictionary<string, int> ids)
        {
            var r = new int[tokens.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var key = (tokens[i].Kind == VaultTokenKind.Tag ? "\u0001" : "") + (tokens[i].Text ?? "");
                if (!ids.TryGetValue(key, out r[i])) ids[key] = r[i] = ids.Count;
            }
            return r;
        }

        static char[] Firsts(List<VaultToken> tokens)
        {
            var r = new char[tokens.Count];
            for (int i = 0; i < r.Length; i++) r[i] = string.IsNullOrEmpty(tokens[i].Text) ? '\0' : char.ToLowerInvariant(tokens[i].Text[0]);
            return r;
        }

        static double SubCost(VaultToken x, VaultToken y, bool same)
        {
            if (x.Kind == VaultTokenKind.Space && y.Kind == VaultTokenKind.Space) return 0;
            if (x.Kind == VaultTokenKind.Tag && y.Kind == VaultTokenKind.Tag) return same ? 0 : MinorCost;
            if (x.IsContent && y.IsContent) return same ? 0 : WordCost;
            return double.PositiveInfinity;   // a word is never "changed into" a space or a tag
        }

        static double GapCost(VaultToken t) => t.IsContent ? WordCost : MinorCost;

        // Where two alignments cost the same, a word is paired with a word that starts the same way ("disable" with
        // "disabling"), never with a punctuation mark. Only a tie-break: far below MinorCost over 2 x 400 steps.
        const double KindTieBreak = 1e-6;
        static double Pairing(VaultToken x, VaultToken y, bool same, bool sameFirst)
        {
            if (!x.IsContent || !y.IsContent || same) return 0;
            if (x.Kind != y.Kind) return KindTieBreak;
            if (!sameFirst) return KindTieBreak / 2;   // nothing in common at the start (most pairs: no string work)
            int len = Math.Max(x.Text.Length, y.Text.Length), common = 0;
            while (common < x.Text.Length && common < y.Text.Length && char.ToLowerInvariant(x.Text[common]) == char.ToLowerInvariant(y.Text[common])) common++;
            return common == len ? 0 : KindTieBreak / 2 * (1 - (double)common / len);
        }

        static bool Same(double p, double q) => Math.Abs(p - q) < 1e-9;
    }
}
