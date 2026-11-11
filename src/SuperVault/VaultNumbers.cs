using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>
    /// Replacing a different number in EverTM's proposal by the document's, as Studio does with its own TMs (Michael's
    /// answer 2 of 11 Nov 2026), but only where it can't go wrong: a number put in the wrong place is a real translation
    /// error, and a missed replacement only costs the translator a look. So it is done only when ALL of these hold:
    ///
    ///  1. the document and the stored source have the same number of numbers, and the stored translation has exactly
    ///     the stored source's numbers, written the same way and in the same order;
    ///  2. each number that differs is in the stored source (and so in the translation) only once;
    ///  3. the document's number isn't one of the stored source's own numbers (that would be numbers moved around, not
    ///     changed);
    ///  4. the two are written the same way apart from their digits: the same separators in the same places ("3.5" and
    ///     "4.25" yes; "3.5" and "3,5" no; "1,000" and "999" no).
    ///
    /// Otherwise nothing is replaced, and the difference counts as a changed word, as before. Dates, ranges and the like
    /// are numbers separated by other characters ("11/11/2026" is three numbers), so they follow the same rules: a
    /// date whose day and month are the same number is never touched, for instance.
    ///
    /// A number is a run of digits, with "." or "," between digits ("1,000.5"), not glued to a letter ("M8", "2nd" and
    /// "H2O" are words). A replaced number costs nothing toward the score, as in Studio, and is marked as replaced
    /// (Studio doesn't draw it as a difference). The score of a segment that differs only in numbers stays 99%: EverTM
    /// gives 100% for identical text only.
    /// </summary>
    public static class VaultNumbers
    {
        static readonly Regex Number = new Regex(@"(?<![\p{L}\p{N}_])\d+(?:[.,]\d+)*(?![\p{L}\p{N}_])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>A number in a list of pieces: its text, and where it is (piece, characters).</summary>
        public struct Found { public string Text; public int Piece, Offset, Length; }

        public static List<Found> In(IList<VaultPiece> pieces)
        {
            var list = new List<Found>();
            if (pieces == null) return list;
            for (int p = 0; p < pieces.Count; p++)
            {
                if (pieces[p].IsTag || string.IsNullOrEmpty(pieces[p].Text)) continue;
                foreach (Match m in Number.Matches(pieces[p].Text))
                    list.Add(new Found { Text = m.Value, Piece = p, Offset = m.Index, Length = m.Length });
            }
            return list;
        }

        /// <summary>What to replace: for each number of the stored source that differs, the document's number; null when nothing may be replaced.</summary>
        public sealed class Plan
        {
            public List<(Found doc, Found source, Found target)> Swaps = new List<(Found, Found, Found)>();
        }

        /// <summary>The replacements, or null when the rules above don't all hold, or when nothing differs.</summary>
        public static Plan Make(IList<VaultPiece> document, IList<VaultPiece> source, IList<VaultPiece> target)
        {
            var d = In(document); var s = In(source); var t = In(target);
            if (d.Count == 0 || d.Count != s.Count || s.Count != t.Count) return null;   // rule 1: counts
            for (int i = 0; i < s.Count; i++) if (s[i].Text != t[i].Text) return null;    // rule 1: the translation has the source's numbers, in order
            var plan = new Plan();
            for (int i = 0; i < d.Count; i++)
            {
                if (d[i].Text == s[i].Text) continue;
                int inSource = 0;
                foreach (var x in s) { if (x.Text == s[i].Text) inSource++; if (x.Text == d[i].Text) return null; }   // rule 3: moved, not changed
                if (inSource != 1) return null;                                                                        // rule 2: once only
                if (Shape(d[i].Text) != Shape(s[i].Text)) return null;                                                  // rule 4: written alike
                plan.Swaps.Add((d[i], s[i], t[i]));
            }
            return plan.Swaps.Count == 0 ? null : plan;
        }

        // A number's separators in order, digits left out: "1,000.5" -> ",.".
        static string Shape(string number)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in number) if (!char.IsDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>The stored translation with the document's numbers in place of the stored source's.</summary>
        public static List<VaultPiece> Apply(Plan plan, IList<VaultPiece> target)
        {
            var result = new List<VaultPiece>(target.Count);
            for (int p = 0; p < target.Count; p++)
            {
                var piece = target[p];
                if (piece.IsTag) { result.Add(piece); continue; }
                var text = piece.Text;
                // From the end, so earlier offsets stay right.
                var here = plan.Swaps.FindAll(x => x.target.Piece == p);
                here.Sort((x, y) => y.target.Offset.CompareTo(x.target.Offset));
                foreach (var x in here) text = text.Substring(0, x.target.Offset) + x.doc.Text + text.Substring(x.target.Offset + x.target.Length);
                result.Add(new VaultPiece { Text = text, Kind = piece.Kind, Name = piece.Name, Key = piece.Key, Raw = piece.Raw });
            }
            return result;
        }

        /// <summary>The token positions (in the document's and the stored source's tokens) of the replaced numbers: their
        /// differences cost nothing and are marked as replaced.</summary>
        public static (HashSet<int> doc, HashSet<int> source) Tokens(Plan plan, List<VaultToken> docTokens, List<VaultToken> sourceTokens)
        {
            var d = new HashSet<int>(); var s = new HashSet<int>();
            foreach (var (doc, source, _) in plan.Swaps)
            {
                Mark(docTokens, doc, d);
                Mark(sourceTokens, source, s);
            }
            return (d, s);
        }

        static void Mark(List<VaultToken> tokens, Found f, HashSet<int> into)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Piece == f.Piece && t.Kind != VaultTokenKind.Tag && t.Offset >= f.Offset && t.Offset + t.Length <= f.Offset + f.Length) into.Add(i);
            }
        }
    }
}
