using System;
using System.Collections.Generic;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>What a piece of a source segment is, as the match key sees it.</summary>
    public enum RepetitionTokenKind
    {
        Text,
        TagOpen,
        TagClose,
        TagStandalone,
    }

    /// <summary>
    /// One piece of a source segment: a run of text, or a tag by its kind and
    /// role. A tag never carries its id - two segments whose tags differ only in
    /// id are the same sentence, and a host that put an id in would split every
    /// such pair (see <see cref="RepetitionKey"/>).
    /// </summary>
    public sealed class RepetitionToken
    {
        private RepetitionToken(RepetitionTokenKind kind, string value)
        {
            Kind = kind;
            Value = value ?? "";
        }

        public RepetitionTokenKind Kind { get; }

        /// <summary>The text itself, or the tag's kind ("b", "inline_tag", …).</summary>
        public string Value { get; }

        public static RepetitionToken Text(string text) => new RepetitionToken(RepetitionTokenKind.Text, text);
        public static RepetitionToken Open(string tagKind) => new RepetitionToken(RepetitionTokenKind.TagOpen, tagKind);
        public static RepetitionToken Close(string tagKind) => new RepetitionToken(RepetitionTokenKind.TagClose, tagKind);
        public static RepetitionToken Standalone(string tagKind) => new RepetitionToken(RepetitionTokenKind.TagStandalone, tagKind);
    }

    /// <summary>
    /// The match key Batch Translate (Trados) and Pre-translate (memoQ) group
    /// repetitions by, so a run never delivers two translations for one source
    /// (docs/design/consistent-repetitions.md, approved 22 Oct 2026). One function
    /// for both products: a repetition in one must be a repetition in the other.
    ///
    /// <para>Exact on purpose. Text is kept as it stands - case, non-breaking and
    /// narrow non-breaking spaces, tabs, line breaks, soft returns (U+2028/9),
    /// Unicode subscripts - because each of those can make two sentences
    /// translate differently, and a copied translation would carry the wrong one
    /// across. Tags count by kind, role and order, never by id, so the same
    /// sentence with its tags numbered differently still matches, and two
    /// segments share a key only if their tags pair off one to one by
    /// position.</para>
    ///
    /// <para>Each token is written as its role, its length and its value, so no
    /// text can be mistaken for a tag or for the edge between two tokens,
    /// whatever characters it contains. Adjacent text tokens are joined first:
    /// how a host happens to split a run of text decides nothing.</para>
    /// </summary>
    public static class RepetitionKey
    {
        private const char Separator = '\u001F';

        /// <summary>
        /// The key of a segment given as tokens, in order. Empty when there is
        /// nothing at all; a segment of tags alone still has a key - whether to
        /// group it is the host's decision.
        /// </summary>
        public static string Of(IEnumerable<RepetitionToken> tokens)
        {
            if (tokens == null) return "";

            var key = new StringBuilder();
            var text = new StringBuilder();

            foreach (var token in tokens)
            {
                if (token == null) continue;

                if (token.Kind == RepetitionTokenKind.Text)
                {
                    text.Append(token.Value);
                    continue;
                }

                Flush(key, text);
                Append(key, Role(token.Kind), token.Value.Length == 0 ? "?" : token.Value);
            }

            Flush(key, text);
            return key.ToString();
        }

        private static void Flush(StringBuilder key, StringBuilder text)
        {
            if (text.Length == 0) return;
            Append(key, 'T', text.ToString());
            text.Clear();
        }

        private static void Append(StringBuilder key, char role, string value)
        {
            if (key.Length > 0) key.Append(Separator);
            key.Append(role).Append(value.Length).Append(':').Append(value);
        }

        private static char Role(RepetitionTokenKind kind)
        {
            switch (kind)
            {
                case RepetitionTokenKind.TagOpen: return 'O';
                case RepetitionTokenKind.TagClose: return 'C';
                case RepetitionTokenKind.TagStandalone: return 'S';
                default: return 'T';
            }
        }
    }
}
