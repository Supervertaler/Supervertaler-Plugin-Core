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
    ///
    /// A TMX that SuperVault exported (<see cref="VaultExport"/>; its header names <see cref="VaultExport.CreationTool"/>)
    /// is read back exactly: each tag's native code is SuperVault's own stored code, so it is kept as it was, and the
    /// unit keeps its origin (<c>x-SuperVault-Origin</c>) and the date it was added (<c>creationdate</c>). Other files
    /// are read as before: their origin is the file's name, and their date the day of the import.
    ///
    /// Metadata (schema 3; docs/design/evertm-metadata.md): each unit's dates and users (<c>creationdate</c>,
    /// <c>creationid</c>, <c>changedate</c>, <c>changeid</c> on the &lt;tu&gt;, or on its &lt;tuv&gt;s as DéjàVu writes
    /// them; else the header's <c>creationdate</c>, ranked below a unit's own), the fields EverTM keeps
    /// (<see cref="VaultFields.Canonical"/>: Project, Client, Subject with Domain merged in, ...), its &lt;note&gt;s, and the
    /// file's name as Source. A unit already in EverTM gets them merged in, so re-importing a file fills in the
    /// metadata of units imported before it had any.
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
                bool ours = false, oursV1 = false;
                DateTime? headerDate = null;
                var fileName = Path.GetFileName(tmxPath);
                int tus = 0;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "header")
                    {
                        headerLang = reader.GetAttribute("srclang");
                        ours = reader.GetAttribute("creationtool") == VaultExport.CreationTool;
                        // Version 1 of EverTM's export wrote the date a unit was added as its creationdate, and no metadata.
                        oursV1 = ours && reader.GetAttribute("creationtoolversion") == "1";
                        headerDate = VaultFields.ParseTmxDate(reader.GetAttribute("creationdate"));
                    }
                    if (reader.LocalName != "tu") continue;
                    var tuLang = reader.GetAttribute("srclang") ?? headerLang;
                    var tu = new Tu
                    {
                        Created = VaultFields.ParseTmxDate(reader.GetAttribute("creationdate")), CreatedBy = reader.GetAttribute("creationid"),
                        Changed = VaultFields.ParseTmxDate(reader.GetAttribute("changedate")), ChangedBy = reader.GetAttribute("changeid"),
                    };
                    var added = oursV1 ? VaultExport.FromTmxDate(reader.GetAttribute("creationdate")) : null;
                    ReadTu(reader.ReadSubtree(), ours, tu);
                    if (ours && !oursV1) added = tu.Added;
                    var info = Info(tu, ours ? null : headerDate, ours ? null : fileName, oursV1);   // our export's header date is when it was exported: never a unit's date
                    if ((++tus & 255) == 0) cancel.ThrowIfCancellationRequested();
                    if (tus % 20000 == 0) progress?.Invoke(Path.GetFileName(tmxPath) + ": " + tus.ToString("N0", CultureInfo.InvariantCulture) + " TUs read");
                    var unitOrigin = ours ? tu.Origin : origin;   // our own file: the origin as stored, none included
                    var tuvs = tu.Tuvs;
                    // Every pair of languages in the TU. With a source language (the TU's own, else the header's), it comes first in each pair.
                    for (int i = 0; i < tuvs.Count; i++)
                        for (int j = i + 1; j < tuvs.Count; j++)
                        {
                            var (a, b) = string.Equals(tuvs[j].lang, tuLang, StringComparison.OrdinalIgnoreCase) ? (tuvs[j], tuvs[i]) : (tuvs[i], tuvs[j]);
                            w.Add(a.lang, a.text, b.lang, b.text, unitOrigin, added, info);
                        }
                }
                cancel.ThrowIfCancellationRequested();
                w.Commit();
                return w.Result;
            }
        }

        sealed class Tu
        {
            public readonly List<(string lang, string text)> Tuvs = new List<(string, string)>();
            public readonly List<(string type, string value)> Props = new List<(string, string)>();
            public readonly List<string> Notes = new List<string>();
            public string Origin, Added;   // EverTM's own export only
            public DateTime? Created, Changed;
            public string CreatedBy, ChangedBy;
            public bool TuvDates;          // the dates came from the <tuv>s, not the <tu>
        }

        /// <summary>
        /// A TU's metadata: its kept fields, its notes, <paramref name="source"/> (the file's name; null for EverTM's own
        /// export, whose Source values are props) and its dates, the header's when it has none of its own.
        /// </summary>
        static VaultUnitInfo Info(Tu tu, DateTime? headerDate, string source, bool noDates)
        {
            var info = new VaultUnitInfo();
            foreach (var (type, value) in tu.Props) info.Add(VaultFields.Canonical(type), value);
            foreach (var note in tu.Notes) info.Add(VaultFields.Note, note);
            if (source != null) info.Add(VaultFields.Source, source);
            if (noDates) return info;
            if (tu.Created != null || tu.Changed != null)
            {
                info.Created = tu.Created; info.Changed = tu.Changed;
                info.CreatedBy = Clean(tu.CreatedBy); info.ChangedBy = Clean(tu.ChangedBy);
            }
            else if (headerDate != null) { info.Created = info.Changed = headerDate; info.FileDate = true; }
            return info;
        }

        static string Clean(string user) { var u = VaultFields.Clean(user); return u.Length == 0 ? null : u; }

        static void ReadTu(XmlReader tu, bool ours, Tu result)
        {
            using (tu)
            {
                tu.Read();   // on <tu>
                while (!tu.EOF)
                {
                    if (tu.NodeType != XmlNodeType.Element) { tu.Read(); continue; }
                    if (tu.LocalName == "prop" && tu.Depth == 1)
                    {
                        var type = tu.GetAttribute("type");
                        var value = tu.ReadElementContentAsString();   // leaves the reader on the next node
                        if (ours && VaultExport.IsOriginProp(type)) result.Origin = value;
                        else if (ours && VaultExport.IsAddedProp(type)) result.Added = VaultExport.FromTmxDate(value);
                        else if (result.Props.Count < 50) result.Props.Add((type, value));
                        continue;
                    }
                    if (tu.LocalName == "note" && tu.Depth == 1)
                    {
                        var note = tu.ReadElementContentAsString();
                        if (result.Notes.Count < 5) result.Notes.Add(note);
                        continue;
                    }
                    if (tu.LocalName != "tuv") { tu.Read(); continue; }
                    var lang = tu.GetAttribute("xml:lang") ?? tu.GetAttribute("lang") ?? "";
                    // Dates on the <tuv> (DéjàVu): the earliest creation and the latest change, when the <tu> has none.
                    var tc = VaultFields.ParseTmxDate(tu.GetAttribute("creationdate"));
                    if (tc != null && (result.Created == null || (result.TuvDates && tc < result.Created))) { result.Created = tc; result.CreatedBy = tu.GetAttribute("creationid"); result.TuvDates = true; }
                    var tch = VaultFields.ParseTmxDate(tu.GetAttribute("changedate"));
                    if (tch != null && (result.Changed == null || (result.TuvDates && tch > result.Changed))) { result.Changed = tch; result.ChangedBy = tu.GetAttribute("changeid"); result.TuvDates = true; }
                    string text = null;
                    using (var tuv = tu.ReadSubtree())
                        while (tuv.Read())
                            if (tuv.NodeType == XmlNodeType.Element && tuv.LocalName == "seg") { text = ReadSeg(tuv.ReadSubtree(), ours); break; }
                    if (text != null && lang.Length > 0) result.Tuvs.Add((lang, text));
                    tu.Read();   // past the </tuv>
                }
            }
        }

        static string ReadSeg(XmlReader seg, bool ours)
        {
            var sb = new StringBuilder();
            // SuperVault's own export: the same text with each tag's native code (the code exactly as stored), and
            // the shape it must parse back to. Used only if it does, so a hand-edited file can't smuggle in a broken unit.
            var native = ours ? new StringBuilder() : null;
            var shape = ours ? new StringBuilder() : null;
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
                            native?.Append(seg.Value);
                            shape?.Append(seg.Value);
                            seg.Read();
                            break;
                        case XmlNodeType.Element when seg.LocalName == "hi" || seg.LocalName == "sub":
                            seg.Read();   // step inside: its text is translatable
                            break;
                        case XmlNodeType.Element:
                            var name = seg.LocalName;
                            var i = seg.GetAttribute("i");
                            var pos = seg.GetAttribute("pos");
                            var id = seg.GetAttribute("x") ?? i ?? ("a" + (++auto).ToString(CultureInfo.InvariantCulture));
                            // An <ept> has only i; it closes the <bpt> with the same i, so it takes that one's id.
                            if (name == "ept" && i != null && pairIds.TryGetValue(i, out var opened)) id = opened;
                            if (name == "bpt" && i != null) pairIds[i] = id;
                            if (ours)
                            {
                                native.Append(NativeCode(seg));   // reads past the element
                                shape.Append('\u0001').Append(name == "bpt" || (name == "it" && pos != "end") ? 'o' : name == "ept" || name == "it" ? 'c' : 's');
                            }
                            else seg.Skip();                      // past the element and its native code
                            if (name == "bpt") sb.Append("<g id=\"").Append(id).Append("\">");
                            else if (name == "ept") sb.Append("</g id=\"").Append(id).Append("\">");
                            else sb.Append("<x id=\"").Append(id).Append("\"/>");   // ph, x, it, ut, bx, ex
                            break;
                        default:
                            seg.Read();   // end tags, comments
                            break;
                    }
                }
            }
            if (ours && Shape(native.ToString()) == shape.ToString()) return native.ToString();
            return sb.ToString();
        }

        /// <summary>A stored text's structure: its text, with each tag as \u0001 plus o(pen), c(lose) or s(tandalone).</summary>
        static string Shape(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var p in VaultMarkup.Parse(raw))
                if (p.IsTag) sb.Append('\u0001').Append(p.Kind == VaultTagKind.Open ? 'o' : p.Kind == VaultTagKind.Close ? 'c' : 's');
                else sb.Append(p.Text);
            return sb.ToString();
        }

        /// <summary>The text inside an inline element (its native code), leaving the reader just past the element.</summary>
        static string NativeCode(XmlReader r)
        {
            if (r.IsEmptyElement) { r.Read(); return ""; }
            int depth = r.Depth;
            var sb = new StringBuilder();
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA || r.NodeType == XmlNodeType.Whitespace || r.NodeType == XmlNodeType.SignificantWhitespace)
                    sb.Append(r.Value);
                r.Read();
            }
            r.Read();   // past the end tag
            return sb.ToString();
        }
    }
}
