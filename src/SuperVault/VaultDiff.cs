using System;
using System.Collections.Generic;
using System.Globalization;

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

    public enum VaultEditOp { Identity, Change, Insert, Delete }

    /// <summary>
    /// One step of the alignment: document token <see cref="A"/> against memory token <see cref="B"/>. For a Delete (a
    /// document token the memory lacks), B is the memory position it goes before; for an Insert (a memory token the
    /// document lacks), A is the document position it goes before: Studio's convention for its edit lists.
    /// </summary>
    public struct VaultEdit
    {
        public VaultEditOp Op;
        public int A, B;
        public double Cost;
    }

    /// <summary>How a document segment compares with a stored one: the match % and the token alignment behind it.</summary>
    public sealed class VaultDiffResult
    {
        public int Score;                       // 0..100; 100 only for identical normalised text
        public List<VaultEdit> Edits;           // null when the segments were too long to align (score from word overlap)
        public double Distance;                 // total cost of the alignment
    }

    /// <summary>
    /// Word-level comparison of a document segment with a stored unit: the match % SuperVault reports, and the
    /// alignment that lets a CAT tool show the differences. Our own, because the CAT tools' scorers are not public.
    ///
    /// Scale: tokenising is linear; the alignment is O(n·m) in tokens, so it is only run on the few best candidates of
    /// a lookup (see the callers), and segments over <see cref="MaxAlignTokens"/> tokens are scored on word overlap
    /// instead, without differences shown. A 400×400 alignment is 160,000 cells, well under a millisecond.
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
                        while (i < s.Length && (IsWordChar(s, i) || (IsJoiner(s[i]) && i + 1 < s.Length && IsWordChar(s, i + 1) && i > start))) i++;
                        kind = VaultTokenKind.Word;
                    }
                    else { i += char.IsSurrogatePair(s, i) ? 2 : 1; kind = VaultTokenKind.Punctuation; }
                    tokens.Add(new VaultToken { Kind = kind, Text = s.Substring(start, i - start), Piece = p, Offset = start, Length = i - start });
                }
            }
            return tokens;
        }

        static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == ' ' || c == ' ' || c == ' ';
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

        // Costs. A changed or missing word or punctuation mark costs 1 and counts toward the %. Whitespace and tags are
        // aligned too (a CAT tool shows them), but cost almost nothing and do not count: formatting is the tag
        // mapping's business, and a different space is not a different translation.
        const double WordCost = 1.0, MinorCost = 0.05;

        /// <summary>
        /// Compares document tokens <paramref name="a"/> with memory tokens <paramref name="b"/>. <paramref name="identical"/>
        /// says whether their normalised plain texts are equal: only then is the score 100.
        /// </summary>
        public static VaultDiffResult Compare(List<VaultToken> a, List<VaultToken> b, bool identical)
        {
            int na = 0, nb = 0;
            foreach (var t in a) if (t.IsContent) na++;
            foreach (var t in b) if (t.IsContent) nb++;
            if (identical) return new VaultDiffResult { Score = 100, Edits = Align(a, b, out _), Distance = 0 };
            if (na == 0 || nb == 0) return new VaultDiffResult { Score = 0 };

            if (a.Count > MaxAlignTokens || b.Count > MaxAlignTokens)
                return new VaultDiffResult { Score = Cap(OverlapScore(a, b, na, nb)) };

            var edits = Align(a, b, out double contentDistance);
            double s = 1.0 - contentDistance / Math.Max(na, nb);
            return new VaultDiffResult { Score = Cap((int)Math.Floor(s * 100 + 1e-9)), Edits = edits, Distance = contentDistance };
        }

        static int Cap(int score) => Math.Max(0, Math.Min(99, score));

        /// <summary>Dice coefficient on content tokens, for segments too long to align.</summary>
        static int OverlapScore(List<VaultToken> a, List<VaultToken> b, int na, int nb)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in a) if (t.IsContent) { counts.TryGetValue(t.Text, out int c); counts[t.Text] = c + 1; }
            int common = 0;
            foreach (var t in b)
                if (t.IsContent && counts.TryGetValue(t.Text, out int c) && c > 0) { common++; counts[t.Text] = c - 1; }
            return (int)Math.Floor(100.0 * 2 * common / (na + nb));
        }

        static double SubCost(VaultToken x, VaultToken y)
        {
            if (x.Kind == VaultTokenKind.Space && y.Kind == VaultTokenKind.Space) return 0;
            if (x.Kind == VaultTokenKind.Tag && y.Kind == VaultTokenKind.Tag) return string.Equals(x.Text, y.Text, StringComparison.Ordinal) ? 0 : MinorCost;
            if (x.IsContent && y.IsContent) return string.Equals(x.Text, y.Text, StringComparison.Ordinal) ? 0 : WordCost;
            return double.PositiveInfinity;   // a word is never "changed into" a space or a tag
        }

        static double GapCost(VaultToken t) => t.IsContent ? WordCost : MinorCost;

        // Where two alignments cost the same, prefer the one Studio would draw: a word paired with a word that starts
        // the same way ("disable" changed into "disabling", "determine" into "determining"), never a word with a
        // punctuation mark ("disable" changed into ","; 11 Nov 2026). Only a tie-break: costs are multiples of
        // MinorCost, and KindTieBreak times the longest alignment (2 x MaxAlignTokens) stays far below it, so the
        // cheapest alignment, and with it the score, is unchanged.
        const double KindTieBreak = 1e-6;
        static double Pairing(VaultToken x, VaultToken y)
        {
            if (!x.IsContent || !y.IsContent) return 0;
            if (x.Kind != y.Kind) return KindTieBreak;
            int len = Math.Max(x.Text.Length, y.Text.Length), common = 0;
            while (common < x.Text.Length && common < y.Text.Length && char.ToLowerInvariant(x.Text[common]) == char.ToLowerInvariant(y.Text[common])) common++;
            return len == 0 || common == len ? 0 : KindTieBreak / 2 * (1 - (double)common / len);
        }

        /// <summary>Minimum-cost alignment (Levenshtein with the costs above). Also returns the content-only distance.</summary>
        static List<VaultEdit> Align(List<VaultToken> a, List<VaultToken> b, out double contentDistance)
        {
            int n = a.Count, m = b.Count;
            var d = new double[n + 1, m + 1];
            for (int i = 1; i <= n; i++) d[i, 0] = d[i - 1, 0] + GapCost(a[i - 1]);
            for (int j = 1; j <= m; j++) d[0, j] = d[0, j - 1] + GapCost(b[j - 1]);
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                {
                    double best = d[i - 1, j - 1] + SubCost(a[i - 1], b[j - 1]) + Pairing(a[i - 1], b[j - 1]);
                    double del = d[i - 1, j] + GapCost(a[i - 1]);
                    double ins = d[i, j - 1] + GapCost(b[j - 1]);
                    if (del < best) best = del;
                    if (ins < best) best = ins;
                    d[i, j] = best;
                }

            var edits = new List<VaultEdit>(n + m);
            contentDistance = 0;
            int x = n, y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0)
                {
                    double sub = SubCost(a[x - 1], b[y - 1]);
                    if (Same(d[x, y], d[x - 1, y - 1] + sub + Pairing(a[x - 1], b[y - 1])))
                    {
                        var op = sub == 0 ? VaultEditOp.Identity : VaultEditOp.Change;
                        edits.Add(new VaultEdit { Op = op, A = x - 1, B = y - 1, Cost = sub });
                        if (a[x - 1].IsContent) contentDistance += sub;
                        x--; y--; continue;
                    }
                }
                if (x > 0 && Same(d[x, y], d[x - 1, y] + GapCost(a[x - 1])))
                {
                    // A document token the memory lacks. Studio calls this Delete; B is the position in the memory it
                    // goes BEFORE (the memory token that follows it; the memory's length at the end), as in Studio's own
                    // edit lists. Studio draws the inserted word there: one less put it a token too early (11 Nov 2026).
                    edits.Add(new VaultEdit { Op = VaultEditOp.Delete, A = x - 1, B = y, Cost = GapCost(a[x - 1]) });
                    if (a[x - 1].IsContent) contentDistance += WordCost;
                    x--; continue;
                }
                // A memory token the document lacks (Insert): A is the document position it goes before, likewise.
                edits.Add(new VaultEdit { Op = VaultEditOp.Insert, A = x, B = y - 1, Cost = GapCost(b[y - 1]) });
                if (b[y - 1].IsContent) contentDistance += WordCost;
                y--;
            }
            edits.Reverse();
            return edits;
        }

        static bool Same(double p, double q) => Math.Abs(p - q) < 1e-9;
    }
}
