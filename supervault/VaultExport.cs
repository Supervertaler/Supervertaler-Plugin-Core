using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>How many units SuperVault holds for one language pair and one origin: a line of the export's choice list.</summary>
    public sealed class VaultHolding
    {
        public string LangA, LangB;   // base languages, in the store's (alphabetical) order
        public string Origin;         // "workbench", "studio", "tmx:file.tmx", "sdltm:file.sdltm"; "" when unknown
        public long Units;

        public string Pair => VaultIndex.PairKey(LangA, LangB);

        /// <summary>"Confirmed in Studio", "Workbench", "TMX file: x.tmx", "Studio TM: y.sdltm".</summary>
        public static string OriginText(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return "(origin not recorded)";
            if (origin == "studio") return "Confirmed in Studio";
            if (origin == "workbench") return "Workbench";
            if (origin.StartsWith("tmx:", StringComparison.Ordinal)) return "TMX file: " + origin.Substring(4);
            if (origin.StartsWith("sdltm:", StringComparison.Ordinal)) return "Studio TM: " + origin.Substring(6);
            return origin;
        }
    }

    public sealed class VaultExportResult
    {
        public long Units;
        public long Bytes;
        public string Path;
        public override string ToString() =>
            VaultLibrary.Units(Units) + " exported to " + System.IO.Path.GetFileName(Path) + " (" + (Bytes / 1048576.0).ToString("N1", CultureInfo.CurrentCulture) + " MB)";
    }

    /// <summary>
    /// Writes SuperVault's units to a TMX 1.4 file.
    ///
    /// What is written:
    ///  - each unit in the direction and language variants it was first stored with (column <c>variant</c>, e.g.
    ///    "nl-BE>en-US"), source first; the TU's own <c>srclang</c> says which, since the header can only say
    ///    <c>*all*</c> for a memory that holds both directions;
    ///  - its own dates and users (<c>creationdate</c>/<c>creationid</c>, <c>changedate</c>/<c>changeid</c>), left out
    ///    when EverTM doesn't know them; its fields as props named as EverTM names them ("Project", "End client"), its
    ///    notes as &lt;note&gt;; <c>x-EverTM-Origin</c> (where it came from) and <c>x-EverTM-Added</c> (when it entered
    ///    EverTM), which only EverTM reads back. Version 1 of this export (creationtoolversion="1") wrote the added date
    ///    as the creationdate; <see cref="VaultTmx"/> still reads such files as before;
    ///  - tags as TMX inline elements: a pair as &lt;bpt&gt;/&lt;ept&gt;, a standalone as &lt;ph&gt;, an opening or closing
    ///    tag whose partner isn't in the segment as &lt;it&gt;. The same tag in source and target gets the same <c>x</c>.
    ///    The code exactly as SuperVault stores it (<c>&lt;g id="1"&gt;</c>, <c>&lt;b&gt;</c>, memoQ's <c>[1}</c>) is the
    ///    element's native code, so SuperVault's own import gives back exactly the same unit (<see cref="VaultTmx"/>
    ///    reads it back for files whose header names <see cref="CreationTool"/>; other tools ignore it).
    ///
    /// Scale: streamed. One SQLite reader goes through the units in id order and an XmlWriter writes each TU as it is
    /// read, so memory stays flat (a few MB) whatever the size of SuperVault; nothing is collected first.
    ///
    /// A snapshot: the units are read inside one read transaction. SQLite in WAL mode gives that transaction the store
    /// as it was when it began, while learning and imports go on writing (they write to the WAL, and nothing waits for
    /// anyone). So an export never blocks a lookup (lookups read only the index), a confirm or an import, and a unit
    /// stored meanwhile is simply not in this file. The export takes no lock. The one cost: while it runs, SQLite can't
    /// fold the WAL back into the store past the export's starting point, so the WAL grows by whatever is written
    /// meanwhile and shrinks again at the next checkpoint after the export.
    ///
    /// All or nothing: the file is written under a temporary name in the same folder and renamed when complete. A
    /// stopped or failed export deletes the temporary file; an existing file of the chosen name is only replaced by a
    /// complete export.
    /// </summary>
    public static class VaultExport
    {
        public const string CreationTool = "Supervertaler EverTM";
        /// <summary>
        /// The prop with a unit's origin. Read back with or without a Studio-style type suffix ("x-SuperVault-Origin:SingleString").
        /// Studio's TMX import keeps x- props as fields only in files Studio itself wrote (checked offline, 9 Nov 2026):
        /// in Studio the origin is not kept; in SuperVault and in tools that read props it is.
        /// </summary>
        public const string OriginProp = "x-EverTM-Origin";
        /// <summary>The prop with the date a unit entered EverTM (column added), a TMX date.</summary>
        public const string AddedProp = "x-EverTM-Added";
        /// <summary>2: dates are the unit's own, metadata included. 1 (before schema 3): creationdate was the added date.</summary>
        public const string ToolVersion = "2";

        internal static bool IsAddedProp(string type) =>
            type == AddedProp || (type != null && type.StartsWith(AddedProp + ":", StringComparison.Ordinal));

        internal static bool IsOriginProp(string type) =>
            type == "x-EverTM-Origin" || (type != null && type.StartsWith("x-EverTM-Origin:", StringComparison.Ordinal));

        /// <summary>
        /// What SuperVault holds, per language pair and origin: one GROUP BY over the store, a single pass (seconds for
        /// millions of units), so call it off the UI thread. Empty without a store.
        /// </summary>
        public static List<VaultHolding> Holdings(string store, CancellationToken cancel)
        {
            var list = new List<VaultHolding>();
            if (!File.Exists(store)) return list;
            using (var c = VaultStore.Open(store, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT lang_a, lang_b, COALESCE(origin, ''), count(*) FROM units GROUP BY 1, 2, 3";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        cancel.ThrowIfCancellationRequested();
                        list.Add(new VaultHolding { LangA = r.GetString(0), LangB = r.GetString(1), Origin = r.GetString(2), Units = r.GetInt64(3) });
                    }
            }
            return list;
        }

        /// <summary>
        /// Exports to <paramref name="tmxPath"/>. <paramref name="pair"/> limits it to one base language pair
        /// ("en|nl", as <see cref="VaultHolding.Pair"/>), null for all; <paramref name="origins"/> to those origins
        /// ("" for units without one), null for all. Throws on failure or when cancelled, leaving no file behind.
        /// </summary>
        public static VaultExportResult Export(string store, string tmxPath, string pair, ICollection<string> origins, Action<string> progress, CancellationToken cancel)
        {
            if (!File.Exists(store)) throw new FileNotFoundException("EverTM is empty: there is nothing to export.", store);
            tmxPath = System.IO.Path.GetFullPath(tmxPath);
            var folder = System.IO.Path.GetDirectoryName(tmxPath);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The folder " + folder + " does not exist.");
            var temp = tmxPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                long units;
                using (var c = VaultStore.Open(store, readOnly: true))
                using (var tx = c.BeginTransaction())   // the snapshot: the count, and every row, as of this moment
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    var where = new List<string>();
                    if (origins != null)
                    {
                        // The choice goes into a temporary table (allowed on a read-only connection): no origin ever becomes
                        // part of the SQL text, and there is no limit on how many.
                        cmd.CommandText = "CREATE TEMP TABLE chosen (origin TEXT PRIMARY KEY)";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "INSERT OR IGNORE INTO chosen VALUES ($o)";
                        var p = cmd.Parameters.Add("$o", SqliteType.Text);
                        foreach (var o in origins) { p.Value = o ?? ""; cmd.ExecuteNonQuery(); }
                        cmd.Parameters.Remove(p);
                        where.Add("COALESCE(origin, '') IN (SELECT origin FROM temp.chosen)");
                    }
                    if (pair != null)
                    {
                        var langs = pair.Split('|');
                        if (langs.Length != 2) throw new ArgumentException("A language pair is two base languages, as \"en|nl\".", nameof(pair));
                        where.Add("lang_a = $a AND lang_b = $b");
                        cmd.Parameters.AddWithValue("$a", langs[0]);
                        cmd.Parameters.AddWithValue("$b", langs[1]);
                    }
                    var filter = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
                    cmd.CommandText = "SELECT count(*) FROM units" + filter;
                    long total = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    cmd.CommandText = "SELECT lang_a, text_a, lang_b, text_b, variant, origin, added, created, changed, " +
                                      "(SELECT value FROM vals WHERE id = u.created_by), (SELECT value FROM vals WHERE id = u.changed_by), " +
                                      "(SELECT group_concat(v.field || char(9) || v.value, char(10)) FROM unit_vals uv JOIN vals v ON v.id = uv.val_id WHERE uv.unit_id = u.id) " +
                                      "FROM units u" + filter + " ORDER BY id";

                    var settings = new XmlWriterSettings
                    {
                        Encoding = new UTF8Encoding(false),
                        Indent = false,                                 // a newline between elements is written by hand; never inside a <seg>
                        NewLineHandling = NewLineHandling.Entitize,     // a line break in a unit survives as &#xD;/&#xA;
                        CheckCharacters = true,
                        CloseOutput = true,
                    };
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    using (var w = XmlWriter.Create(stream, settings))
                    {
                        WriteHeader(w);
                        units = 0;
                        var xs = new Dictionary<string, int>(StringComparer.Ordinal);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                if ((units & 1023) == 0)
                                {
                                    cancel.ThrowIfCancellationRequested();
                                    if (units % 50000 == 0) progress?.Invoke("Exporting: " + units.ToString("N0", CultureInfo.InvariantCulture) + " of " + VaultLibrary.Units(total, CultureInfo.InvariantCulture));
                                }
                                var unit = new VaultExportUnit
                                {
                                    LangA = r.GetString(0), TextA = r.GetString(1), LangB = r.GetString(2), TextB = r.GetString(3),
                                    Variant = r.IsDBNull(4) ? null : r.GetString(4),
                                    Created = r.IsDBNull(7) ? null : Iso(r.GetInt64(7)),
                                    Changed = r.IsDBNull(8) ? null : Iso(r.GetInt64(8)),
                                    CreatedBy = r.IsDBNull(9) ? null : r.GetString(9),
                                    ChangedBy = r.IsDBNull(10) ? null : r.GetString(10),
                                };
                                if (!r.IsDBNull(11))
                                    foreach (var (field, value) in VaultFields.Unpack(Sorted(r.GetString(11))))
                                        if (field == VaultFields.Note) unit.Notes.Add(value); else unit.Props.Add((field, value));
                                if (!r.IsDBNull(5)) unit.Props.Add((OriginProp, r.GetString(5)));
                                if (!r.IsDBNull(6) && DateTime.TryParse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var added))
                                    unit.Props.Add((AddedProp, TmxDate(added)));
                                WriteTu(w, unit, xs);
                                units++;
                            }
                        cancel.ThrowIfCancellationRequested();
                        w.WriteWhitespace("\n");
                        w.WriteEndElement();   // body
                        w.WriteWhitespace("\n");
                        w.WriteEndElement();   // tmx
                        w.WriteWhitespace("\n");
                        w.WriteEndDocument();
                        w.Flush();
                        stream.Flush(true);    // on disk before it gets the real name
                    }
                }
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke("Saving " + System.IO.Path.GetFileName(tmxPath));
                FailPoint?.Invoke("rename");
                if (File.Exists(tmxPath)) File.Replace(temp, tmxPath, null);
                else File.Move(temp, tmxPath);
                temp = null;
                return new VaultExportResult { Units = units, Bytes = new FileInfo(tmxPath).Length, Path = tmxPath };
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { }   // stopped or failed: no half file
                SqliteConnection.ClearAllPools();
            }
        }

        /// <summary>Tests only: called at named points ("rename"); throw to simulate a failure there.</summary>
        internal static Action<string> FailPoint = null;

        static void WriteHeader(XmlWriter w)
        {
            w.WriteStartDocument();
            w.WriteWhitespace("\n");
            w.WriteStartElement("tmx");
            w.WriteAttributeString("version", "1.4");
            w.WriteWhitespace("\n");
            w.WriteStartElement("header");
            w.WriteAttributeString("creationtool", CreationTool);
            w.WriteAttributeString("creationtoolversion", ToolVersion);
            w.WriteAttributeString("segtype", "sentence");
            w.WriteAttributeString("o-tmf", "EverTM");
            w.WriteAttributeString("adminlang", "en-US");
            w.WriteAttributeString("srclang", "*all*");   // each TU says its own source language
            w.WriteAttributeString("datatype", "plaintext");
            w.WriteAttributeString("creationdate", TmxDate(DateTime.UtcNow));
            w.WriteEndElement();
            w.WriteWhitespace("\n");
            w.WriteStartElement("body");
        }

        /// <summary>
        /// One unit as written to TMX: the store's columns, its own dates and its props. Everything per unit comes from
        /// here, so more of SuperVault's own metadata (a change date, project, subject) is one more field or prop, not a
        /// rewrite. A date the unit doesn't have is left out, never filled in with the time of the export.
        /// </summary>
        internal sealed class VaultExportUnit
        {
            public string LangA, TextA, LangB, TextB, Variant;
            public string Created;   // ISO 8601 UTC (column added); null: no creationdate is written
            public string Changed;   // ISO 8601 UTC; null: no changedate
            public string CreatedBy, ChangedBy;
            public readonly List<(string Type, string Value)> Props = new List<(string, string)>();
            public readonly List<string> Notes = new List<string>();
        }

        static void WriteDate(XmlWriter w, string attribute, string iso)
        {
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                w.WriteAttributeString(attribute, TmxDate(when));
        }

        /// <summary>One unit as a &lt;tu&gt;, in the direction and variants it was first stored with.</summary>
        internal static void WriteTu(XmlWriter w, VaultExportUnit u, Dictionary<string, int> xs)
        {
            var (srcCulture, srcText, trgCulture, trgText) = Orient(u.LangA, u.TextA, u.LangB, u.TextB, u.Variant);
            w.WriteWhitespace("\n");
            w.WriteStartElement("tu");
            w.WriteAttributeString("srclang", srcCulture);
            WriteDate(w, "creationdate", u.Created);
            if (u.Created != null && !string.IsNullOrEmpty(u.CreatedBy)) w.WriteAttributeString("creationid", XmlSafe(u.CreatedBy));
            WriteDate(w, "changedate", u.Changed);
            if (u.Changed != null && !string.IsNullOrEmpty(u.ChangedBy)) w.WriteAttributeString("changeid", XmlSafe(u.ChangedBy));
            foreach (var (type, value) in u.Props)
            {
                if (string.IsNullOrEmpty(value)) continue;
                w.WriteStartElement("prop");
                w.WriteAttributeString("type", type);
                w.WriteString(XmlSafe(value));
                w.WriteEndElement();
            }
            foreach (var note in u.Notes)
            {
                if (string.IsNullOrEmpty(note)) continue;
                w.WriteStartElement("note");
                w.WriteString(XmlSafe(note));
                w.WriteEndElement();
            }
            xs.Clear();   // x links a tag in the source to the same tag in the target: numbered per TU
            WriteTuv(w, srcCulture, srcText, xs);
            WriteTuv(w, trgCulture, trgText, xs);
            w.WriteEndElement();
        }

        static void WriteTuv(XmlWriter w, string culture, string raw, Dictionary<string, int> xs)
        {
            w.WriteStartElement("tuv");
            w.WriteAttributeString("xml", "lang", null, culture);
            w.WriteStartElement("seg");
            WriteSeg(w, raw, xs);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        /// <summary>
        /// The source and target as first stored: the variant's source culture picks the side. A variant that doesn't
        /// fit the unit's languages (it never should) falls back to the store's order and base languages.
        /// </summary>
        internal static (string srcCulture, string srcText, string trgCulture, string trgText) Orient(string langA, string textA, string langB, string textB, string variant)
        {
            string vs = null, vt = null;
            int gt = variant?.IndexOf('>') ?? -1;
            if (gt >= 0) { vs = Culture(variant.Substring(0, gt)); vt = Culture(variant.Substring(gt + 1)); }
            if (vs != null && vt != null)
            {
                string bs = VaultMarkup.BaseLanguage(vs), bt = VaultMarkup.BaseLanguage(vt);
                if (bs == langA && bt == langB) return (vs, textA, vt, textB);
                if (bs == langB && bt == langA) return (vs, textB, vt, textA);
            }
            return (langA, textA, langB, textB);
        }

        /// <summary>"nl_BE" → "nl-BE"; null when empty or not a plausible language code.</summary>
        static string Culture(string c)
        {
            c = c?.Trim().Replace('_', '-');
            if (string.IsNullOrEmpty(c) || c.Length > 35) return null;
            foreach (var ch in c) if (!(ch == '-' || (ch < 128 && char.IsLetterOrDigit(ch)))) return null;
            return c;
        }

        /// <summary>
        /// A stored text as TMX seg content. Text is escaped by the XmlWriter; each tag code becomes an inline element
        /// with the code itself as its native code.
        /// </summary>
        internal static void WriteSeg(XmlWriter w, string raw, Dictionary<string, int> xs)
        {
            var pieces = VaultMarkup.Parse(raw);
            // Pair each closing tag with the latest unpaired opening tag of the same identity.
            var partner = new int[pieces.Count];
            for (int k = 0; k < partner.Length; k++) partner[k] = -1;
            var open = new List<int>();
            for (int k = 0; k < pieces.Count; k++)
            {
                var p = pieces[k];
                if (!p.IsTag) continue;
                if (p.Kind == VaultTagKind.Open) open.Add(k);
                else if (p.Kind == VaultTagKind.Close)
                {
                    var key = OpenKey(p.Key);
                    for (int j = open.Count - 1; j >= 0; j--)
                        if (pieces[open[j]].Key == key) { partner[k] = open[j]; partner[open[j]] = k; open.RemoveAt(j); break; }
                }
            }
            var iOf = new int[pieces.Count];
            int nextI = 0;
            for (int k = 0; k < pieces.Count; k++)
            {
                var p = pieces[k];
                if (!p.IsTag) { w.WriteString(XmlSafe(p.Text)); continue; }
                if (p.Kind == VaultTagKind.Open && partner[k] >= 0)
                {
                    iOf[k] = ++nextI;
                    w.WriteStartElement("bpt");
                    w.WriteAttributeString("i", iOf[k].ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("x", X(xs, p.Key));
                }
                else if (p.Kind == VaultTagKind.Close && partner[k] >= 0)
                {
                    w.WriteStartElement("ept");
                    w.WriteAttributeString("i", iOf[partner[k]].ToString(CultureInfo.InvariantCulture));
                }
                else if (p.Kind == VaultTagKind.Standalone)
                {
                    w.WriteStartElement("ph");
                    w.WriteAttributeString("x", X(xs, p.Key));
                }
                else
                {
                    // An opening tag closed in another segment, or a closing tag opened in one.
                    w.WriteStartElement("it");
                    w.WriteAttributeString("pos", p.Kind == VaultTagKind.Open ? "begin" : "end");
                    w.WriteAttributeString("x", X(xs, p.Key));
                }
                w.WriteString(XmlSafe(p.Raw));
                w.WriteEndElement();
            }
        }

        static string OpenKey(string closeKey) => closeKey != null && closeKey.EndsWith("/close", StringComparison.Ordinal) ? closeKey.Substring(0, closeKey.Length - 6) : closeKey;

        static string X(Dictionary<string, int> xs, string key)
        {
            key = key ?? "";
            if (!xs.TryGetValue(key, out var x)) xs[key] = x = xs.Count + 1;
            return x.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Drops the few characters XML 1.0 can't carry at all (control characters, a lone surrogate half).</summary>
        internal static string XmlSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int bad = -1;
            for (int i = 0; i < s.Length; i++)
                if (!Ok(s, i)) { bad = i; break; }
            if (bad < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(s[i]).Append(s[i + 1]); i++; continue; }
                if (Ok(s, i)) sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static bool Ok(string s, int i)
        {
            char c = s[i];
            if (c == '\t' || c == '\n' || c == '\r') return true;
            if (c < 0x20 || c == '￾' || c == '￿') return false;
            if (char.IsHighSurrogate(c)) return i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            if (char.IsLowSurrogate(c)) return i > 0 && char.IsHighSurrogate(s[i - 1]);
            return true;
        }

        static string Iso(long seconds) => VaultFields.FromSeconds(seconds).ToString("o", CultureInfo.InvariantCulture);

        /// <summary>The store's field lines (group_concat, in no set order) in a fixed order, so an export is the same every time.</summary>
        static string Sorted(string lines) => VaultFields.Pack(VaultFields.Unpack(lines).OrderBy(x => x.Field, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal));

        internal static string TmxDate(DateTime utc) => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        /// <summary>A TMX date ("20261107T171036Z") as the store keeps <c>added</c> (ISO 8601, UTC); null if it isn't one.</summary>
        internal static string FromTmxDate(string tmx)
        {
            return tmx != null && DateTime.TryParseExact(tmx.Trim(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToString("o", CultureInfo.InvariantCulture) : null;
        }
    }
}
