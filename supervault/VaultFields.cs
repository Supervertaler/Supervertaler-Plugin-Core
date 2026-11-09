using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>
    /// What comes with a unit besides its text: its fields (Project, Client, ...) and its dates and users. Given to
    /// <see cref="VaultStore.Writer.Add"/> by every importer and by learning.
    /// </summary>
    public sealed class VaultUnitInfo
    {
        /// <summary>Canonical field names (<see cref="VaultFields"/>) and their values. Empty values are ignored.</summary>
        public readonly List<(string Field, string Value)> Fields = new List<(string, string)>();
        public DateTime? Created, Changed;        // UTC
        public string CreatedBy, ChangedBy;
        /// <summary>The dates are the file's (a TMX header's creationdate), not the unit's own: they rank below a unit's own date.</summary>
        public bool FileDate;

        public void Add(string field, string value)
        {
            if (field == null) return;
            value = VaultFields.Clean(value);
            if (value.Length > 0) Fields.Add((field, value));
        }

        public bool HasDates => Created != null || Changed != null;
        public bool IsEmpty => Fields.Count == 0 && !HasDates && CreatedBy == null && ChangedBy == null;
    }

    /// <summary>
    /// The fields EverTM keeps (design note docs/design/evertm-metadata.md, section 1) and the names they come under in
    /// TMX files and Studio TMs. Every other field is dropped.
    /// </summary>
    public static class VaultFields
    {
        public const string Project = "Project", Client = "Client", EndClient = "End client", Subject = "Subject",
                            Document = "Document", Source = "Source", Note = "Note";

        /// <summary>The order Studio shows them in (it shows the first three on a match).</summary>
        public static readonly string[] Order = { Project, Client, Subject, EndClient, Document, Source, Note };

        /// <summary>At most this many values per field per unit; very common segments ("Figure 1") would otherwise collect hundreds.</summary>
        public const int MaxValuesPerField = 20;
        internal const int MaxValueLength = 200;

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["project"] = Project, ["projekt"] = Project, ["job"] = Project,
            ["client"] = Client, ["customer"] = Client, ["kunde"] = Client,
            ["end client"] = EndClient, ["end_client"] = EndClient, ["endclient"] = EndClient, ["end-client"] = EndClient, ["endkunde"] = EndClient,
            ["subject"] = Subject, ["domain"] = Subject, ["fachgebiet"] = Subject,   // Michael: keep only Subject, Domain merged in
            ["document"] = Document, ["filename"] = Document, ["matecat-filename"] = Document, ["sourcefile"] = Document, ["datei"] = Document,
            ["source"] = Source,
            ["note"] = Note,
        };

        /// <summary>
        /// The canonical field for a name as a tool writes it ("x-Project:SingleString", "client", "Fachgebiet"), or null
        /// for a field EverTM doesn't keep. Case-insensitive; a leading "x-" and Studio's ":Type" suffix are ignored.
        /// </summary>
        public static string Canonical(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Trim();
            int colon = n.IndexOf(':');
            if (colon > 0) n = n.Substring(0, colon);
            n = n.ToLowerInvariant();
            if (n.StartsWith("x-", StringComparison.Ordinal)) n = n.Substring(2);
            return Names.TryGetValue(n, out var c) ? c : null;
        }

        /// <summary>A value as stored: whitespace runs to one space, trimmed, at most <see cref="MaxValueLength"/> characters.</summary>
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            bool space = false;
            foreach (var ch in value)
            {
                if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            var s = sb.ToString();
            return s.Length > MaxValueLength ? s.Substring(0, MaxValueLength) : s;
        }

        // One run of capitals, digits and . & ' - (with at least one capital), a single underscore, then the rest; an
        // optional trailing "(...)" (a job number) is dropped.
        static readonly Regex ClientName = new Regex(@"^(?<client>[A-Z0-9.&'\-]*[A-Z][A-Z0-9.&'\-]*)_(?<end>[^_].*?)\s*(\([^()]*\))?\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Splits Michael's own naming, an agency in capitals, an underscore, then the end client ("ACME_Globex"), into
        /// Client and End client. Deliberately narrow: only for Workbench TM names and Studio project names, and only
        /// when the name matches exactly. False (and nulls) otherwise.
        /// </summary>
        public static bool SplitClientName(string name, out string client, out string endClient)
        {
            client = endClient = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            var m = ClientName.Match(name.Trim());
            if (!m.Success) return false;
            var end = Clean(m.Groups["end"].Value);
            if (end.Length == 0) return false;
            client = m.Groups["client"].Value;
            endClient = end;
            return true;
        }

        // ---- the display form stored in the index ----

        /// <summary>
        /// A unit's fields as one string for the index's stored display field: "Field\tvalue" lines, in <see cref="Order"/>,
        /// then the values in the order they were added. Tabs and line breaks never occur in a cleaned value.
        /// </summary>
        internal static string Pack(IEnumerable<(string Field, string Value)> fields)
        {
            var sb = new StringBuilder();
            foreach (var (f, v) in fields.OrderBy(x => Rank(x.Field)))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(f).Append('\t').Append(v);
            }
            return sb.ToString();
        }

        internal static List<(string Field, string Value)> Unpack(string packed)
        {
            var list = new List<(string, string)>();
            if (string.IsNullOrEmpty(packed)) return list;
            foreach (var line in packed.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0) list.Add((line.Substring(0, tab), line.Substring(tab + 1)));
            }
            return list;
        }

        static int Rank(string field)
        {
            int i = Array.IndexOf(Order, field);
            return i < 0 ? Order.Length : i;
        }

        // ---- dates: seconds since 1970, UTC, as the store keeps them ----

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long ToSeconds(DateTime utc) => (long)(utc.ToUniversalTime() - Epoch).TotalSeconds;
        public static DateTime FromSeconds(long s) => Epoch.AddSeconds(s);

        /// <summary>A TMX date ("20261107T171036Z"; also without the Z or the time), or null.</summary>
        public static DateTime? ParseTmxDate(string tmx)
        {
            if (string.IsNullOrWhiteSpace(tmx)) return null;
            var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd" };
            if (DateTime.TryParseExact(tmx.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                && t.Year >= 1980 && t.Year <= 2100)
                return t;
            return null;
        }
    }
}
