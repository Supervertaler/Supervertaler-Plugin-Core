using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// A segment's inline tags, as the plugins write them for an AI -
    /// <c>&lt;t1&gt;…&lt;/t1&gt;</c> for a pair, <c>&lt;t2/&gt;</c> for a standalone tag - turned
    /// into the markup a machine-translation engine keeps, and back
    /// (docs/design/mt-engines.md §3.1, in the Trados repository).
    ///
    /// <para>Two notations, chosen by what the engines did with real text on
    /// 1 Nov 2026 (the spike's replies are in the Trados repo's .dev\mt-spike):</para>
    /// <list type="bullet">
    /// <item><b>XML (DeepL)</b>: the placeholders as they are - valid XML elements -
    /// with DeepL's <c>tag_handling: "xml"</c>.</item>
    /// <item><b>HTML (Google, Microsoft)</b>: a pair becomes <c>&lt;span id="t1"&gt;…&lt;/span&gt;</c>,
    /// a standalone tag <c>&lt;img id="t2"&gt;</c>. Google kept every tag in all three
    /// notations tried, but with its own element names (<c>&lt;t1&gt;</c>) it translated
    /// as if the tags were words: "Press &lt;t1&gt;Start&lt;/t1&gt;" came back "Pers Begin",
    /// against "Druk op Start" with spans. Spans cost about a third more characters;
    /// the translation is the point.</item>
    /// </list>
    ///
    /// <para>The text between tags is escaped for the markup and decoded on the way
    /// back. Markup an engine adds that the source did not have is dropped, never
    /// written into a target as text. A segment whose tags are not properly nested
    /// is sent without them; the plugin then writes the reply as plain text, as it
    /// does whenever a reply's tags cannot be placed.</para>
    /// </summary>
    internal static class MtMarkup
    {
        internal enum Notation { Xml, Html }

        private static readonly Regex Placeholder = new Regex(@"<(/?)t(\d+)(/?)>", RegexOptions.Compiled);

        // Any tag in a reply. Text "<" comes back escaped, so a raw "<" starts markup.
        private static readonly Regex AnyTag = new Regex(@"<[^<>]*>", RegexOptions.Compiled);

        private static readonly Regex XmlTag = new Regex(@"^<\s*(/?)\s*t(\d+)\s*(/?)\s*>$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SpanOpen = new Regex(@"^<\s*span\b[^>]*\bid\s*=\s*[""']?t(\d+)[""']?[^>]*>$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SpanClose = new Regex(@"^<\s*/\s*span\s*>$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Img = new Regex(@"^<\s*img\b[^>]*\bid\s*=\s*[""']?t(\d+)[""']?[^>]*>$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The segment as the engine is sent it. <paramref name="tagsSent"/> is false
        /// when it had tags that were left out because they were not properly nested.
        /// </summary>
        internal static string ToEngine(string text, Notation notation, out bool tagsSent)
        {
            text = text ?? "";
            tagsSent = true;
            if (!WellFormed(text))
            {
                tagsSent = false;
                return Escape(Placeholder.Replace(text, ""));
            }

            var sb = new StringBuilder(text.Length + 16);
            int at = 0;
            foreach (Match m in Placeholder.Matches(text))
            {
                sb.Append(Escape(text.Substring(at, m.Index - at)));
                at = m.Index + m.Length;
                if (notation == Notation.Xml) { sb.Append(m.Value); continue; }

                var n = m.Groups[2].Value;
                if (m.Groups[3].Value == "/") sb.Append("<img id=\"t").Append(n).Append("\">");
                else if (m.Groups[1].Value == "/") sb.Append("</span>");
                else sb.Append("<span id=\"t").Append(n).Append("\">");
            }
            sb.Append(Escape(text.Substring(at)));
            return sb.ToString();
        }

        /// <summary>
        /// An engine's reply in the plugins' notation again. <paramref name="source"/>
        /// is the segment as the plugin gave it, which says which numbers were
        /// standalone tags (an engine may return <c>&lt;t2&gt;&lt;/t2&gt;</c> for <c>&lt;t2/&gt;</c>).
        /// </summary>
        internal static string FromEngine(string reply, Notation notation, string source)
        {
            if (string.IsNullOrEmpty(reply)) return reply;

            var standalone = new HashSet<string>();
            foreach (Match m in Placeholder.Matches(source ?? ""))
                if (m.Groups[3].Value == "/") standalone.Add(m.Groups[2].Value);

            var sb = new StringBuilder(reply.Length);
            var openSpans = new Stack<string>();   // HTML: which number each open span carries
            int at = 0;
            foreach (Match m in AnyTag.Matches(reply))
            {
                sb.Append(WebUtility.HtmlDecode(reply.Substring(at, m.Index - at)));
                at = m.Index + m.Length;
                sb.Append(Translate(m.Value, notation, standalone, openSpans));
            }
            sb.Append(WebUtility.HtmlDecode(reply.Substring(at)));

            var result = sb.ToString();
            // <t2></t2> for a standalone tag: one placeholder, as it was sent.
            result = Regex.Replace(result, @"<t(\d+)></t\1>", mm =>
                standalone.Contains(mm.Groups[1].Value) ? "<t" + mm.Groups[1].Value + "/>" : mm.Value);
            return notation == Notation.Html ? RepairSpacing(result) : result;
        }

        /// <summary>One tag of a reply in the plugins' notation, or "" for markup the source did not have.</summary>
        private static string Translate(string tag, Notation notation, HashSet<string> standalone, Stack<string> openSpans)
        {
            if (notation == Notation.Xml)
            {
                var x = XmlTag.Match(tag);
                if (!x.Success) return "";
                var n = x.Groups[2].Value;
                if (x.Groups[3].Value == "/" || (standalone.Contains(n) && x.Groups[1].Value != "/")) return "<t" + n + "/>";
                if (standalone.Contains(n)) return "";   // the closing half of <t2></t2>
                return x.Groups[1].Value == "/" ? "</t" + n + ">" : "<t" + n + ">";
            }

            var img = Img.Match(tag);
            if (img.Success) return "<t" + img.Groups[1].Value + "/>";
            var open = SpanOpen.Match(tag);
            if (open.Success)
            {
                openSpans.Push(open.Groups[1].Value);
                return "<t" + open.Groups[1].Value + ">";
            }
            if (SpanClose.IsMatch(tag)) return openSpans.Count > 0 ? "</t" + openSpans.Pop() + ">" : "";
            return "";
        }

        /// <summary>
        /// Google puts a space between a bracket or a full stop and the tag beside it:
        /// "( &lt;t2&gt;10&lt;/t2&gt; )", "&lt;/t1&gt; .". Taken out where no language wants one.
        /// Not before ; : ! ?, which French spaces.
        /// </summary>
        private static string RepairSpacing(string text)
        {
            text = Regex.Replace(text, @"([(\[]) +(?=<t\d+/?>)", "$1");
            text = Regex.Replace(text, @"(</t\d+>|<t\d+/>) +(?=[.,)\]])", "$1");
            return text;
        }

        /// <summary>Every pair closes in the order it opened, and no number is used twice.</summary>
        internal static bool WellFormed(string text)
        {
            var open = new Stack<string>();
            var seen = new HashSet<string>();
            foreach (Match m in Placeholder.Matches(text ?? ""))
            {
                var n = m.Groups[2].Value;
                if (m.Groups[3].Value == "/")
                {
                    if (m.Groups[1].Value == "/" || !seen.Add(n)) return false;
                }
                else if (m.Groups[1].Value == "/")
                {
                    if (open.Count == 0 || open.Pop() != n) return false;
                }
                else
                {
                    if (!seen.Add(n)) return false;
                    open.Push(n);
                }
            }
            return open.Count == 0;
        }

        /// <summary>The segment without its tags, for DeepL's context.</summary>
        internal static string PlainText(string text) => Placeholder.Replace(text ?? "", "");

        // Text content needs only these three escaped, in XML and HTML alike. A
        // framework encoder would also turn every apostrophe into an entity, which
        // costs characters on every engine and which Google's HTML mode does not
        // always turn back.
        private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
