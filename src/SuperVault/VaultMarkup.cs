using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>What an inline tag does: opens a formatting run, closes one, or stands alone (an image, a field, a tab).</summary>
    public enum VaultTagKind { Open, Close, Standalone }

    /// <summary>
    /// One piece of a stored translation unit: a run of text, or an inline tag code. Workbench (and the CAT tools it
    /// imported from) store formatting as codes inside the text, e.g. <c>&lt;b&gt;Save&lt;/b&gt;</c>,
    /// <c>&lt;pt x="1"&gt;</c>, <c>&lt;ph x="1"/&gt;</c>, <c>&lt;g id="14"&gt;</c>, or memoQ's <c>[1}</c>, <c>{1]</c>, <c>{1}</c> and <c>{}</c>.
    /// </summary>
    public sealed class VaultPiece
    {
        public string Text;          // the text run; null for a tag
        public VaultTagKind Kind;    // for a tag
        public string Name;          // tag name ("b", "pt", "g", "mq" for memoQ codes)
        public string Key;           // identity used to pair a source tag with its target counterpart
        public string Raw;           // the code exactly as stored

        public bool IsTag => Text == null;
        public override string ToString() => IsTag ? Raw : Text;
    }

    /// <summary>
    /// Reads the tag codes in stored units, and gives the plain-text form every SuperVault comparison uses.
    /// Shared by every product that serves SuperVault, so a unit is matched the same way in Trados and memoQ.
    /// </summary>
    public static class VaultMarkup
    {
        // An XML-like tag: <name ...>, </name ...> or <name .../>. The name must start with a letter, so "a < b" and
        // "x<5" are text. Whether "<Enter>" is a tag is decided in Parse (it needs context).
        static readonly Regex XmlTag = new Regex(
            @"<(/?)([A-Za-z][\w:.\-]*)((?:\s+[^<>]*?)?)\s*(/?)>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // memoQ inline codes: [1} opens, {1] closes, {1} and {} stand alone.
        static readonly Regex MemoQTag = new Regex(@"\[(\d+)\}|\{(\d+)\]|\{(\d*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly Regex IdAttribute = new Regex(@"\b(?:id|x|rid|i)\s*=\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Splits a stored unit into text runs and tags. A string with no recognisable codes is one text piece.
        ///
        /// An XML-like code counts as a tag when it closes itself (<c>&lt;ph/&gt;</c>), carries attributes
        /// (<c>&lt;pt x="1"&gt;</c>), is closed later in the same text (<c>&lt;b&gt;...&lt;/b&gt;</c>), or closes an
        /// earlier one. A lone <c>&lt;Enter&gt;</c> with none of these is text: it is far more likely to be a key name
        /// in a manual than an unclosed formatting tag.
        /// </summary>
        public static List<VaultPiece> Parse(string raw)
        {
            var pieces = new List<VaultPiece>();
            if (string.IsNullOrEmpty(raw)) return pieces;

            // Collect candidate tags from both syntaxes, in position order.
            var found = new List<(int start, int length, VaultPiece tag)>();
            var xml = XmlTag.Matches(raw);
            var openNames = new Dictionary<string, int>(StringComparer.Ordinal);
            var closeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in xml) if (m.Groups[1].Length > 0) closeNames.Add(m.Groups[2].Value);
            var openedSoFar = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in xml)
            {
                bool closing = m.Groups[1].Length > 0, selfClosing = m.Groups[4].Length > 0;
                string name = m.Groups[2].Value, attrs = m.Groups[3].Value;
                bool hasAttrs = attrs.Trim().Length > 0;
                bool isTag;
                if (closing) isTag = openedSoFar.Contains(name) || hasAttrs;
                else if (selfClosing || hasAttrs) isTag = true;
                else isTag = closeNames.Contains(name);
                if (!isTag) continue;
                if (!closing && !selfClosing) openedSoFar.Add(name);
                var idm = IdAttribute.Match(attrs);
                found.Add((m.Index, m.Length, new VaultPiece
                {
                    Kind = closing ? VaultTagKind.Close : selfClosing ? VaultTagKind.Standalone : VaultTagKind.Open,
                    Name = name,
                    Key = idm.Success ? name + "#" + idm.Groups[1].Value : null,   // filled in by occurrence below if null
                    Raw = m.Value,
                }));
            }
            foreach (Match m in MemoQTag.Matches(raw))
            {
                if (Overlaps(found, m.Index, m.Length)) continue;
                VaultTagKind kind; string num;
                if (m.Groups[1].Success) { kind = VaultTagKind.Open; num = m.Groups[1].Value; }
                else if (m.Groups[2].Success) { kind = VaultTagKind.Close; num = m.Groups[2].Value; }
                else { kind = VaultTagKind.Standalone; num = m.Groups[3].Value; }
                found.Add((m.Index, m.Length, new VaultPiece { Kind = kind, Name = "mq", Key = num.Length > 0 ? "mq#" + num : null, Raw = m.Value }));
            }
            found.Sort((a, b) => a.start.CompareTo(b.start));

            // Tags without an id are paired by order of appearance: the 2nd <b> in the source with the 2nd <b> in the
            // target. Close tags without an id take the key of the open tag they close (innermost open of that name).
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var openStacks = new Dictionary<string, Stack<string>>(StringComparer.Ordinal);
            int pos = 0;
            foreach (var f in found)
            {
                if (f.start < pos) continue;   // overlapping candidate
                if (f.start > pos) pieces.Add(new VaultPiece { Text = raw.Substring(pos, f.start - pos) });
                var t = f.tag;
                if (!openStacks.TryGetValue(t.Name, out var stack)) openStacks[t.Name] = stack = new Stack<string>();
                if (t.Key == null)
                {
                    if (t.Kind == VaultTagKind.Close && stack.Count > 0) t.Key = stack.Peek();
                    else
                    {
                        string slot = t.Name + (t.Kind == VaultTagKind.Standalone ? "/" : "");
                        occurrences.TryGetValue(slot, out int n);
                        occurrences[slot] = n + 1;
                        t.Key = slot + "@" + (n + 1);
                    }
                }
                if (t.Kind == VaultTagKind.Open) stack.Push(t.Key);
                else if (t.Kind == VaultTagKind.Close && stack.Count > 0) stack.Pop();
                if (t.Kind == VaultTagKind.Close) t.Key += "/close";
                pieces.Add(t);
                pos = f.start + f.length;
            }
            if (pos < raw.Length) pieces.Add(new VaultPiece { Text = raw.Substring(pos) });
            return pieces;
        }

        static bool Overlaps(List<(int start, int length, VaultPiece tag)> found, int start, int length)
        {
            foreach (var f in found)
                if (start < f.start + f.length && f.start < start + length) return true;
            return false;
        }

        /// <summary>The text of a stored unit with its tag codes removed, normalised (see <see cref="Normalize"/>).</summary>
        public static string Plain(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.IndexOf('<') < 0 && raw.IndexOf('{') < 0 && raw.IndexOf('[') < 0) return Normalize(raw);
            var sb = new StringBuilder(raw.Length);
            foreach (var p in Parse(raw)) if (!p.IsTag) sb.Append(p.Text);
            return Normalize(sb.ToString());
        }

        static readonly Regex Spaces = new Regex(@"[\s   ]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The form text is compared in: invisible characters (soft hyphen, zero-width space/joiners, BOM) removed,
        /// every run of whitespace (non-breaking included) made one space, trimmed. Case and punctuation are kept:
        /// "Save" and "save" are different translations as far as a TM is concerned.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                if (c != '­' && c != '​' && c != '‌' && c != '‍' && c != '﻿') sb.Append(c);
            return Spaces.Replace(sb.ToString(), " ").Trim();
        }

        /// <summary>Hash of a normalised text, for exact lookups. Hex MD5 of its UTF-8 bytes.</summary>
        public static string Hash(string normalized)
        {
            using (var md5 = MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(normalized ?? ""));
                var sb = new StringBuilder(32);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// "nl-BE" → "nl", "EN_us" → "en". Language variants fold into the base language: SuperVault serves one memory
        /// per language pair, whatever variants the project uses. Script variants are kept apart, because they are
        /// different writing systems, not spellings: "zh-Hant" → "zh-hant", "sr-Latn-RS" → "sr-latn".
        /// </summary>
        public static string BaseLanguage(string culture)
        {
            if (string.IsNullOrWhiteSpace(culture)) return "";
            var parts = culture.Trim().Replace('_', '-').ToLowerInvariant().Split('-');
            if (parts.Length > 1 && parts[1].Length == 4) return parts[0] + "-" + parts[1];   // a script subtag
            if (parts[0] == "zh" && parts.Length > 1 && (parts[1] == "tw" || parts[1] == "hk" || parts[1] == "mo")) return "zh-hant";
            if (parts[0] == "zh") return "zh-hans";
            return parts[0];
        }

        /// <summary>
        /// Units not worth serving. A minimal filter for now (the full clean-up is a later stage):
        ///  - nothing left once tag codes and whitespace are gone;
        ///  - the GNOME/OPUS glue artefact, where a menu label ran into the next string:
        ///    <c>AndereSearch in:" "All Categories</c>.
        /// </summary>
        public static bool IsJunk(string sourcePlain, string targetPlain)
        {
            if (string.IsNullOrEmpty(sourcePlain) || string.IsNullOrEmpty(targetPlain)) return true;
            return Glued(sourcePlain) || Glued(targetPlain);
        }

        // "Search in:" glued between two quoted strings, as GNOME/KDE menu labels were exported. Not just 'in:" "':
        // KDE's own command lines legitimately have 'naam in:" "Uw naam'.
        static readonly Regex GluedSearch = new Regex(@"Search""? ""?in:"" """, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static bool Glued(string s) => s.IndexOf("in:", StringComparison.OrdinalIgnoreCase) >= 0 && GluedSearch.IsMatch(s);
    }
}
