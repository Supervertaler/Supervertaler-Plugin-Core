using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>
    /// Imports a TMX file into SuperVault's store. Streamed with an XmlReader, one &lt;tu&gt; at a time, so the size of
    /// the file doesn't matter. A TU with more than two languages gives one unit per pair of languages.
    ///
    /// Inline markup is kept as SuperVault's neutral codes: a &lt;bpt&gt;/&lt;ept&gt; pair becomes
    /// <c>&lt;g id="N"&gt;…&lt;/g id="N"&gt;</c>, a &lt;ph&gt;, &lt;x&gt;, &lt;it&gt; or &lt;ut&gt; becomes
    /// <c>&lt;x id="N"/&gt;</c>. The id is the TMX <c>x</c> attribute when there is one (it links a source tag to its
    /// target tag), else <c>i</c>. The native code inside those elements (RTF, HTML) is dropped. &lt;hi&gt; keeps its text.
    /// </summary>
    public static class VaultTmx
    {
        public static VaultImportResult Import(string store, string tmxPath, Action<string> progress, CancellationToken cancel)
        {
            if (!File.Exists(tmxPath)) throw new FileNotFoundException("The TMX file was not found: " + tmxPath, tmxPath);
            var origin = "tmx:" + Path.GetFileName(tmxPath);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
            using (var w = new VaultStore.Writer(store))
            using (var reader = XmlReader.Create(tmxPath, settings))
            {
                string headerLang = null;
                int tus = 0;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "header") headerLang = reader.GetAttribute("srclang");
                    if (reader.LocalName != "tu") continue;
                    var tuvs = ReadTu(reader.ReadSubtree());
                    if ((++tus & 255) == 0) cancel.ThrowIfCancellationRequested();
                    if (tus % 20000 == 0) progress?.Invoke(Path.GetFileName(tmxPath) + ": " + tus.ToString("N0", CultureInfo.InvariantCulture) + " TUs read");
                    // Every pair of languages in the TU. With a header source language, it comes first in each pair.
                    for (int i = 0; i < tuvs.Count; i++)
                        for (int j = i + 1; j < tuvs.Count; j++)
                        {
                            var (a, b) = string.Equals(tuvs[j].lang, headerLang, StringComparison.OrdinalIgnoreCase) ? (tuvs[j], tuvs[i]) : (tuvs[i], tuvs[j]);
                            w.Add(a.lang, a.text, b.lang, b.text, origin);
                        }
                }
                cancel.ThrowIfCancellationRequested();
                w.Commit();
                return w.Result;
            }
        }

        static List<(string lang, string text)> ReadTu(XmlReader tu)
        {
            var list = new List<(string, string)>();
            using (tu)
            {
                while (tu.Read())
                {
                    if (tu.NodeType != XmlNodeType.Element || tu.LocalName != "tuv") continue;
                    var lang = tu.GetAttribute("xml:lang") ?? tu.GetAttribute("lang") ?? "";
                    string text = null;
                    using (var tuv = tu.ReadSubtree())
                        while (tuv.Read())
                            if (tuv.NodeType == XmlNodeType.Element && tuv.LocalName == "seg") { text = ReadSeg(tuv.ReadSubtree()); break; }
                    if (text != null && lang.Length > 0) list.Add((lang, text));
                }
            }
            return list;
        }

        static string ReadSeg(XmlReader seg)
        {
            var sb = new StringBuilder();
            int auto = 0;
            var pairIds = new Dictionary<string, string>();
            using (seg)
            {
                if (!seg.Read() || seg.IsEmptyElement) return "";   // on <seg>
                seg.Read();                                          // its first child
                while (!seg.EOF)
                {
                    switch (seg.NodeType)
                    {
                        case XmlNodeType.Text:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                        case XmlNodeType.CDATA:
                            sb.Append(seg.Value);
                            seg.Read();
                            break;
                        case XmlNodeType.Element when seg.LocalName == "hi" || seg.LocalName == "sub":
                            seg.Read();   // step inside: its text is translatable
                            break;
                        case XmlNodeType.Element:
                            var i = seg.GetAttribute("i");
                            var id = seg.GetAttribute("x") ?? i ?? ("a" + (++auto).ToString(CultureInfo.InvariantCulture));
                            // An <ept> has only i; it closes the <bpt> with the same i, so it takes that one's id.
                            if (seg.LocalName == "ept" && i != null && pairIds.TryGetValue(i, out var opened)) id = opened;
                            if (seg.LocalName == "bpt") { if (i != null) pairIds[i] = id; sb.Append("<g id=\"").Append(id).Append("\">"); }
                            else if (seg.LocalName == "ept") sb.Append("</g id=\"").Append(id).Append("\">");
                            else sb.Append("<x id=\"").Append(id).Append("\"/>");   // ph, x, it, ut, bx, ex
                            seg.Skip();   // past the element and its native code
                            break;
                        default:
                            seg.Read();   // end tags, comments
                            break;
                    }
                }
            }
            return sb.ToString();
        }
    }
}
