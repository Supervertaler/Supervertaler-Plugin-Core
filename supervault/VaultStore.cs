using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault
{
    public enum VaultAddOutcome
    {
        Added, Duplicate, Skipped,
        /// <summary>Not added: the user removed this translation from EverTM (<see cref="VaultLibrary.Remove"/>).</summary>
        Removed,
    }

    /// <summary>What learning one confirmed segment did to the store.</summary>
    public enum VaultLearnOutcome
    {
        /// <summary>A new unit.</summary>
        Added,
        /// <summary>The unit learned earlier for the same source now has the new translation.</summary>
        Updated,
        /// <summary>The new translation was already in SuperVault, so the unit learned earlier for this source was removed.</summary>
        Replaced,
        /// <summary>Already in SuperVault, unchanged.</summary>
        Duplicate,
        /// <summary>No text, one language, or junk.</summary>
        Skipped,
        /// <summary>The user removed this translation from EverTM, and this was not a confirmation of it in the editor
        /// (Studio's batch task "Update Main Translation Memories"): not stored.</summary>
        Removed,
    }

    /// <summary>A segment confirmed in a CAT tool, waiting to be learned.</summary>
    public sealed class VaultLearnUnit
    {
        public string SourceCulture, Source, TargetCulture, Target, Origin;
        /// <summary>When it was confirmed (UTC). It travels with the unit through the inbox, so two Studios' confirmations
        /// are ordered by when they were made, not by when they were stored. Null: when it is stored.</summary>
        public DateTime? ConfirmedAt;
        /// <summary>Who confirmed it, and where: the CAT tool's user, its project's name and client, the project's subject, the document's file name.</summary>
        public string User, Project, Client, Subject, Document;
        /// <summary>The document's path, for <see cref="VaultLibrary.ProjectOf"/> to find its project's name and client on
        /// the learning thread (not on the CAT tool's UI thread). Not stored.</summary>
        public string DocumentPath;
        /// <summary>
        /// The fields the user switched off (Michael, 11 Nov: every field on by default, a switch per field): any of
        /// <see cref="VaultFields.Project"/>, Client, End client, Subject, Document, and <see cref="VaultLearnUnit.UserField"/>,
        /// separated by <see cref="OmitSeparator"/>. Null or empty: all of them stored. The confirm time is not a choice:
        /// newest-wins needs it. Stored in the inbox with the unit, so a unit stored later, by another Studio, keeps the
        /// choice made when it was confirmed.
        /// </summary>
        public string Omit;

        /// <summary>
        /// It came from a batch over a whole document (Studio's "Update Main Translation Memories", which sets
        /// ImportSettings.IsDocumentImport), not from confirming the segment in the editor. Such a unit never brings back
        /// a translation the user removed from EverTM; a confirmation in the editor does (Michael, 9 Nov 2026). False
        /// (the default) for anything else, so a caller that can't tell brings it back: a removed translation that comes
        /// back can be removed again; one that silently never comes back can't be seen.
        /// </summary>
        public bool FromBatch;

        /// <summary>The user who confirmed it, as a field name for <see cref="Omit"/>.</summary>
        public const string UserField = "User";
        public const char OmitSeparator = '|';

        /// <summary>The fields a user can switch off, in the order a settings window shows them.</summary>
        public static readonly string[] Switchable = { VaultFields.Project, VaultFields.Client, VaultFields.EndClient, VaultFields.Subject, VaultFields.Document, UserField };

        /// <summary><see cref="Omit"/> for these switched-off fields; null when none is.</summary>
        public static string OmitOf(IEnumerable<string> off)
        {
            var list = off?.Where(f => Switchable.Contains(f)).Distinct().ToList();
            return list == null || list.Count == 0 ? null : string.Join(OmitSeparator.ToString(), list);
        }

        bool Omitted(string field) =>
            !string.IsNullOrEmpty(Omit) && Omit.Split(OmitSeparator).Contains(field);

        /// <summary>
        /// The metadata stored with it: confirmed at = created and changed, by <see cref="User"/>; Project, Client (and
        /// End client, when the project's name is CLIENT_Endclient; its first part is the Client when none is set),
        /// Subject, Document, and Source "Studio: &lt;project&gt;". Fields in <see cref="Omit"/> are left out; with
        /// Project left out, Source says only "Studio", so the project's name is not stored at all.
        /// </summary>
        public VaultUnitInfo Info(DateTime now)
        {
            var info = new VaultUnitInfo { Created = ConfirmedAt ?? now, Changed = ConfirmedAt ?? now };
            if (!Omitted(UserField))
                info.CreatedBy = info.ChangedBy = string.IsNullOrWhiteSpace(User) ? null : VaultFields.Clean(User);
            if (!Omitted(VaultFields.Project)) info.Add(VaultFields.Project, Project);
            var client = Client;
            if (VaultFields.SplitClientName(Project, out var c, out var end))
            {
                if (string.IsNullOrWhiteSpace(client)) client = c;
                if (!Omitted(VaultFields.EndClient)) info.Add(VaultFields.EndClient, end);
            }
            if (!Omitted(VaultFields.Client)) info.Add(VaultFields.Client, client);
            if (!Omitted(VaultFields.Subject)) info.Add(VaultFields.Subject, Subject);
            if (!Omitted(VaultFields.Document)) info.Add(VaultFields.Document, Document);
            var tool = Origin == "studio" ? "Studio" : Origin ?? "";
            if (!string.IsNullOrWhiteSpace(Project))
                info.Add(VaultFields.Source, Omitted(VaultFields.Project) ? tool : tool + ": " + Project.Trim());
            return info;
        }
    }

    /// <summary>What an import did.</summary>
    /// <summary>One translation memory in Supervertaler Workbench's database, for choosing what to import.</summary>
    public sealed class WorkbenchTm
    {
        public string TmId;        // translation_units.tm_id
        public string Name;        // translation_memories.name, or the tm_id when Workbench has no name for it
        public long Units;         // 0 for a TM Workbench lists but holds no units for
        public List<(string Source, string Target, long Units)> Pairs = new List<(string, string, long)>();   // largest first

        /// <summary>"nl > en, en > nl", largest first; empty for a TM with no units.</summary>
        public string PairsText => string.Join(", ", Pairs.Select(p => p.Source + " > " + p.Target));
    }

    /// <summary>An import or rebuild refused because SuperVault would be too large for this (32-bit) process. Nothing was changed.</summary>
    public sealed class VaultTooLargeException : InvalidOperationException
    {
        public VaultTooLargeException(string message) : base(message) { }
    }

    public sealed class VaultImportResult
    {
        public long Read, Added, Duplicates, Skipped;
        /// <summary>Units not added because the user removed that translation from EverTM.</summary>
        public long Removed;
        /// <summary>
        /// Of the duplicates: how many brought metadata or dates the unit in EverTM didn't have yet. Counted per unit read,
        /// like <see cref="Duplicates"/>: a unit found in two of the file's TMs counts twice. (Counting distinct units
        /// would mean remembering every id touched: 10M units would not fit a 32-bit Studio.)
        /// </summary>
        public long Enriched;
        /// <summary>Workbench only: TM names split into Client and End client (<see cref="VaultFields.SplitClientName"/>).</summary>
        public long NamesSplit;
        /// <summary>
        /// "1,554,005 translation units read: 0 new, 1,553,018 already in EverTM (1,547,951 of them brought metadata or
        /// dates); 987 skipped (no text, one language, or junk); 7 TM names split into Client and End client".
        /// Every count is of units READ, which is why it starts with that number: a unit found in two TMs is read twice,
        /// so "already in EverTM" can be more than EverTM holds. "All others" stands for the already-there count when
        /// nothing was new and nothing skipped. The parts for metadata, skipped and split names are left out when zero.
        /// </summary>
        public override string ToString()
        {
            var f = CultureInfo.CurrentCulture;
            var s = VaultLibrary.Units(Read, f) + " read: " + Added.ToString("N0", f) + " new, ";
            s += Added == 0 && Skipped == 0 && Duplicates > 0 ? "all others already in EverTM" : Duplicates.ToString("N0", f) + " already in EverTM";
            if (Enriched > 0) s += " (" + Enriched.ToString("N0", f) + " of them brought metadata or dates)";
            if (Skipped > 0) s += "; " + Skipped.ToString("N0", f) + " skipped (no text, one language, or junk)";
            if (Removed > 0) s += "; " + Removed.ToString("N0", f) + " skipped: you removed " + (Removed == 1 ? "it" : "them") + " from EverTM";
            if (NamesSplit > 0) s += "; " + NamesSplit.ToString("N0", f) + " TM " + (NamesSplit == 1 ? "name" : "names") + " split into Client and End client";
            return s;
        }
    }

    /// <summary>
    /// SuperVault's own memory: one SQLite file, <c>supervault.db</c>, owned by SuperVault and by nothing else. It needs
    /// no other program: it is filled by importing TMX files, Studio TMs and (optionally) a Workbench database, and the
    /// search index is built from it.
    ///
    /// One row per unit and base-language pair, whatever the variant and direction it came in: an nl-BE>en-US unit and
    /// the same unit as en-GB>nl-NL are one row. Each row keeps its texts with their tags, as neutral codes
    /// (<c>&lt;g id="1"&gt;…&lt;/g id="1"&gt;</c>, <c>&lt;x id="2"/&gt;</c>), and the variant pair it first came with.
    ///
    /// Duplicates are recognised by text plus tag shape (<see cref="Key"/>): the same sentence with the same formatting
    /// is one unit even when two tools numbered its tags differently.
    ///
    /// Scale: inserts go through one prepared statement in one transaction per import; the uniqueness check is the
    /// UNIQUE index on <c>key</c>. Measured: about 8,000 units a second; the 1.55M-row Workbench TM imports in about 3 minutes, as 871,121 distinct units.
    ///
    /// Schema 2 (learning as you work) adds two small tables:
    ///   changes   one row per unit added, changed or removed, written by triggers on <c>units</c>, so no write path
    ///             can forget it (stage 1's code included). The index applies them in order and remembers the last
    ///             <c>seq</c> it applied; applied rows are then deleted. AUTOINCREMENT, so a seq is never reused.
    ///   learned   for units SuperVault learned from a confirmed segment: which unit belongs to which source text (in
    ///             the direction it was confirmed). A changed translation of the same source updates that unit
    ///             instead of adding a near-duplicate. Imported units are never in it, so a confirmation never
    ///             changes or removes an imported unit.
    ///
    /// Schema 4 (removing a wrong translation, docs/design/evertm-pane.md section 8) adds:
    ///   removed        one mark per translation the user removed: the base pair and a hash of each side's plain,
    ///                  normalised text (so every formatting and every variant of it), when and by whom. Adding a
    ///                  unit that matches a mark is refused here, in the one place units are added (<see cref="Writer"/>):
    ///                  so a re-import never brings it back, and nothing that reads units has to know about marks.
    ///   removed_units  the units that were deleted for a mark, exactly as they were (texts, variant, origin, dates,
    ///                  users, fields), so a removal can be undone or restored.
    /// </summary>
    public static class VaultStore
    {
        public const int Schema = 4;

        /// <summary>
        /// Set by <see cref="VaultLibrary.Import"/> for the length of one import, on its thread: the most units the store
        /// may hold after it (0: no limit). A <see cref="Writer"/> created meanwhile throws <see cref="VaultTooLargeException"/>
        /// as soon as its additions would pass it, so the import's transaction is rolled back. Only imports: confirmed
        /// segments are always stored.
        /// </summary>
        [ThreadStatic] internal static long ImportLimit;

        /// <summary>Creates the store if it doesn't exist yet. Safe to call while other processes have it open.</summary>
        public static void Create(string path, int busyTimeoutSeconds = 30)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var c = Open(path, readOnly: false, busyTimeoutSeconds))
            {
                if (CurrentSchema(c) >= Schema) return;   // the usual case: nothing to write, so no write lock taken
                Exec(c, "PRAGMA journal_mode=WAL");
                Exec(c, @"CREATE TABLE IF NOT EXISTS units (
                            id INTEGER PRIMARY KEY,
                            lang_a TEXT NOT NULL, text_a TEXT NOT NULL,
                            lang_b TEXT NOT NULL, text_b TEXT NOT NULL,
                            key TEXT NOT NULL UNIQUE,
                            variant TEXT, origin TEXT, added TEXT)");
                Exec(c, "CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT)");
                Exec(c, "INSERT OR IGNORE INTO meta (name, value) VALUES ('schema', '" + Schema + "')");
                Exec(c, "INSERT OR IGNORE INTO meta (name, value) VALUES ('version', '0')");
                // Schema 2. Each statement is idempotent, so an older store is upgraded in place, in milliseconds
                // whatever its size: nothing here reads the existing units.
                Exec(c, "CREATE TABLE IF NOT EXISTS changes (seq INTEGER PRIMARY KEY AUTOINCREMENT, unit_id INTEGER NOT NULL)");
                Exec(c, "CREATE TABLE IF NOT EXISTS learned (src_key TEXT PRIMARY KEY, unit_id INTEGER NOT NULL)");
                Exec(c, "CREATE TRIGGER IF NOT EXISTS units_ai AFTER INSERT ON units BEGIN INSERT INTO changes (unit_id) VALUES (new.id); END");
                Exec(c, "CREATE TRIGGER IF NOT EXISTS units_au AFTER UPDATE ON units BEGIN INSERT INTO changes (unit_id) VALUES (new.id); END");
                Exec(c, "CREATE TRIGGER IF NOT EXISTS units_ad AFTER DELETE ON units BEGIN INSERT INTO changes (unit_id) VALUES (old.id); END");
                // Schema 3: metadata (docs/design/evertm-metadata.md, section 2). Again nothing reads the existing units:
                // the new columns start empty (no date known), and re-importing fills them in.
                //   created, changed     seconds since 1970, UTC; NULL = not known
                //   created_by, ...      vals.id of the user (field '@user')
                //   date_from            'unit' (the unit's own date, or a confirmation), 'file' (a TMX header's), NULL
                //   vals / unit_vals     each field value stored once; units refer to them
                var cols = Columns(c, "units");
                foreach (var col in new[] { "created INTEGER", "changed INTEGER", "created_by INTEGER", "changed_by INTEGER", "date_from TEXT" })
                    if (!cols.Contains(col.Split(' ')[0])) Exec(c, "ALTER TABLE units ADD COLUMN " + col);
                Exec(c, "CREATE TABLE IF NOT EXISTS vals (id INTEGER PRIMARY KEY, field TEXT NOT NULL, value TEXT NOT NULL, UNIQUE (field, value))");
                Exec(c, "CREATE TABLE IF NOT EXISTS unit_vals (unit_id INTEGER NOT NULL, val_id INTEGER NOT NULL, added INTEGER, PRIMARY KEY (unit_id, val_id)) WITHOUT ROWID");
                Exec(c, "CREATE INDEX IF NOT EXISTS unit_vals_val ON unit_vals (val_id)");
                Exec(c, "CREATE TRIGGER IF NOT EXISTS unit_vals_ai AFTER INSERT ON unit_vals BEGIN INSERT INTO changes (unit_id) VALUES (new.unit_id); END");
                Exec(c, "CREATE TRIGGER IF NOT EXISTS unit_vals_ad AFTER DELETE ON unit_vals BEGIN INSERT INTO changes (unit_id) VALUES (old.unit_id); END");
                // A removed unit takes its values with it (a corrected confirmation replaced by an existing unit).
                Exec(c, "CREATE TRIGGER IF NOT EXISTS units_ad_vals AFTER DELETE ON units BEGIN DELETE FROM unit_vals WHERE unit_id = old.id; END");
                // Schema 4: removed translations. Two new, empty tables: nothing reads the existing units.
                Exec(c, "CREATE TABLE IF NOT EXISTS removed (id INTEGER PRIMARY KEY, pair TEXT NOT NULL, hash_a TEXT NOT NULL, hash_b TEXT NOT NULL, " +
                        "removed_at INTEGER NOT NULL, removed_by TEXT, UNIQUE (pair, hash_a, hash_b))");
                Exec(c, "CREATE TABLE IF NOT EXISTS removed_units (removal_id INTEGER NOT NULL, lang_a TEXT, text_a TEXT, lang_b TEXT, text_b TEXT, " +
                        "variant TEXT, origin TEXT, added TEXT, created INTEGER, changed INTEGER, created_by TEXT, changed_by TEXT, date_from TEXT, fields TEXT)");
                Exec(c, "CREATE INDEX IF NOT EXISTS removed_units_removal ON removed_units (removal_id)");
                Exec(c, "UPDATE meta SET value = '" + Schema + "' WHERE name = 'schema' AND CAST(value AS INTEGER) < " + Schema);
            }
        }

        static HashSet<string> Columns(SqliteConnection c, string table)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + table + ")";
                using (var r = cmd.ExecuteReader()) while (r.Read()) set.Add(r.GetString(1));
            }
            return set;
        }

        /// <summary>
        /// The store's columns and subqueries the index is built from, in this order: id, lang_a, text_a, lang_b,
        /// text_b, variant, created, changed, the creator's and changer's user names, added, and the unit's fields as
        /// "field TAB value" lines (unordered). One correlated lookup per unit on unit_vals' primary key: linear in the
        /// number of units. For "SELECT ... FROM units u".
        /// </summary>
        internal const string IndexColumns =
            "u.id, u.lang_a, u.text_a, u.lang_b, u.text_b, u.variant, u.created, u.changed, " +
            "(SELECT value FROM vals WHERE id = u.created_by), (SELECT value FROM vals WHERE id = u.changed_by), u.added, " +
            "(SELECT group_concat(v.field || char(9) || v.value, char(10)) FROM unit_vals uv JOIN vals v ON v.id = uv.val_id WHERE uv.unit_id = u.id)";

        /// <summary>The metadata columns of a row read with <see cref="IndexColumns"/>, starting at column <paramref name="at"/> (created).</summary>
        internal static VaultIndexMeta ReadIndexMeta(SqliteDataReader r, int at)
        {
            var m = new VaultIndexMeta
            {
                Created = r.IsDBNull(at) ? (long?)null : r.GetInt64(at),
                Changed = r.IsDBNull(at + 1) ? (long?)null : r.GetInt64(at + 1),
                CreatedBy = r.IsDBNull(at + 2) ? null : r.GetString(at + 2),
                ChangedBy = r.IsDBNull(at + 3) ? null : r.GetString(at + 3),
                Added = r.IsDBNull(at + 4) ? null : r.GetString(at + 4),
            };
            if (!r.IsDBNull(at + 5)) m.Fields = VaultFields.Unpack(r.GetString(at + 5));
            return m;
        }

        static int CurrentSchema(SqliteConnection c)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;
                cmd.CommandText = "SELECT value FROM meta WHERE name = 'schema'";
                return int.TryParse(Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture), out var v) ? v : 0;
            }
        }

        /// <summary>The last change seq ever written (0 if none). Read inside the caller's transaction when given one.</summary>
        internal static long LastChange(SqliteConnection c, SqliteTransaction tx = null)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT seq FROM sqlite_sequence WHERE name = 'changes'";
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Number of units changed after <paramref name="seq"/>, i.e. not yet in an index that has applied up to it. 0 without a store.</summary>
        public static long ChangesAfter(string path, long seq)
        {
            if (!File.Exists(path)) return 0;
            using (var c = Open(path, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'changes'";
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;
                cmd.CommandText = "SELECT count(DISTINCT unit_id) FROM changes WHERE seq > $s";   // a unit and its values: one change
                cmd.Parameters.AddWithValue("$s", seq);
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Deletes the changes the index has applied (up to and including <paramref name="seq"/>). Caller holds the build lock.</summary>
        internal static void PruneChanges(string path, long seq, int busyTimeoutSeconds)
        {
            if (seq <= 0 || !File.Exists(path)) return;
            using (var c = Open(path, readOnly: false, busyTimeoutSeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM changes WHERE seq <= $s";
                cmd.Parameters.AddWithValue("$s", seq);
                cmd.ExecuteNonQuery();
            }
        }

        internal static SqliteConnection Open(string path, bool readOnly, int busyTimeoutSeconds = 30)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,                 // never keep the file open after we're done with it
                DefaultTimeout = busyTimeoutSeconds,
            }.ToString();
            var c = new SqliteConnection(cs);
            c.Open();
            return c;
        }

        internal static void Exec(SqliteConnection c, string sql)
        {
            using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }

        /// <summary>Number of units, or 0 when there is no store yet.</summary>
        public static long Count(string path)
        {
            if (!File.Exists(path)) return 0;
            using (var c = Open(path, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM units";
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// How many distinct values EverTM holds per field ("Project" -> 285): for its window. One GROUP BY over the
        /// small vals table (each value stored once), not over the units. Empty without a store or before schema 3.
        /// </summary>
        public static Dictionary<string, long> FieldCounts(string path)
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!File.Exists(path)) return d;
            using (var c = Open(path, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'vals'";
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return d;
                cmd.CommandText = "SELECT field, count(*) FROM vals GROUP BY field";
                using (var r = cmd.ExecuteReader()) while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
            }
            return d;
        }

        /// <summary>
        /// The translations the user removed, newest first, <paramref name="take"/> from <paramref name="skip"/>: for the
        /// EverTM window's "Removed translations". Each with one stored copy's texts (the first), how many units were
        /// removed, when and by whom. One indexed query; empty without a store or before schema 4.
        /// </summary>
        public static List<VaultRemoved> Removed(string path, int skip, int take, out int total)
        {
            total = 0;
            var list = new List<VaultRemoved>();
            if (!File.Exists(path)) return list;
            using (var c = Open(path, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'removed'";
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return list;
                cmd.CommandText = "SELECT count(*) FROM removed";
                total = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                cmd.CommandText = "SELECT m.pair, m.hash_a, m.hash_b, m.removed_at, m.removed_by, " +
                                  "(SELECT count(*) FROM removed_units WHERE removal_id = m.id), " +
                                  "(SELECT text_a FROM removed_units WHERE removal_id = m.id ORDER BY rowid LIMIT 1), " +
                                  "(SELECT text_b FROM removed_units WHERE removal_id = m.id ORDER BY rowid LIMIT 1), " +
                                  "(SELECT variant FROM removed_units WHERE removal_id = m.id ORDER BY rowid LIMIT 1) " +
                                  "FROM removed m ORDER BY m.removed_at DESC, m.id DESC LIMIT $take OFFSET $skip";
                cmd.Parameters.AddWithValue("$take", Math.Max(0, take));
                cmd.Parameters.AddWithValue("$skip", Math.Max(0, skip));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var pair = r.GetString(0).Split('|');
                        list.Add(new VaultRemoved
                        {
                            Key = new VaultRemovalKey(pair[0], r.GetString(1), pair.Length > 1 ? pair[1] : "", r.GetString(2)),
                            RemovedAt = VaultFields.FromSeconds(r.GetInt64(3)),
                            RemovedBy = r.IsDBNull(4) ? null : r.GetString(4),
                            Units = r.GetInt32(5),
                            TextA = r.IsDBNull(6) ? "" : r.GetString(6),
                            TextB = r.IsDBNull(7) ? "" : r.GetString(7),
                            Variant = r.IsDBNull(8) ? "" : r.GetString(8),
                        });
                    }
            }
            return list;
        }

        /// <summary>Goes up by one with every import that added something; the index records the version it was built from.</summary>
        public static long Version(string path)
        {
            if (!File.Exists(path)) return 0;
            using (var c = Open(path, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM meta WHERE name = 'version'";
                return long.TryParse(Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture), out var v) ? v : 0;
            }
        }

        /// <summary>
        /// The identity of a unit for duplicate detection: base languages (in alphabetical order) plus, per side, the
        /// normalised text with each tag reduced to its kind. Hashed, so the UNIQUE index stays small.
        /// </summary>
        public static string Key(string langA, string rawA, string langB, string rawB) =>
            VaultMarkup.Hash(langA + "|" + langB + "|" + Shape(rawA) + "\u0002" + Shape(rawB));

        static string Shape(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var p in VaultMarkup.Parse(raw))
                sb.Append(p.IsTag ? (p.Kind == VaultTagKind.Open ? "\u0001o" : p.Kind == VaultTagKind.Close ? "\u0001c" : "\u0001s") : p.Text);
            return VaultMarkup.Normalize(sb.ToString());
        }

        /// <summary>
        /// Adds units in one transaction: <see cref="Commit"/> makes them all permanent, disposing without it (an error,
        /// a cancelled import) leaves the store exactly as it was.
        ///
        /// Metadata (schema 3): a unit's fields and dates come as a <see cref="VaultUnitInfo"/>. A new unit gets them; a
        /// unit already there (a duplicate) has them merged in (<see cref="Merge"/>), so re-importing a file fills in
        /// what an earlier import, or the Workbench import, didn't bring.
        /// </summary>
        public sealed class Writer : IDisposable
        {
            readonly SqliteConnection _c;
            readonly SqliteTransaction _tx;
            readonly SqliteCommand _insert;
            readonly SqliteParameter _la, _ta, _lb, _tb, _key, _variant, _origin, _added, _pCreated, _pChanged, _pCreatedBy, _pChangedBy, _pDateFrom;
            public VaultImportResult Result { get; } = new VaultImportResult();
            bool _done, _changed;
            readonly DateTime _nowTime = DateTime.UtcNow;
            readonly string _now;
            readonly long _nowSeconds;
            SqliteCommand _findLearned, _findKey, _update, _delete, _forget, _remember, _readDates, _writeDates, _findVal, _addVal, _linkVal, _lastId;
            // field|value -> vals.id, for this transaction. Bounded by the number of distinct values an import brings
            // (Michael's 890k TMX units: a few hundred projects and documents), not by the number of units.
            readonly Dictionary<string, long> _valIds = new Dictionary<string, long>(StringComparer.Ordinal);

            public Writer(string path, int busyTimeoutSeconds = 30)
            {
                _now = _nowTime.ToString("o", CultureInfo.InvariantCulture);
                _nowSeconds = VaultFields.ToSeconds(_nowTime);
                Create(path, busyTimeoutSeconds);
                _c = Open(path, readOnly: false, busyTimeoutSeconds);
                _tx = _c.BeginTransaction();
                _insert = _c.CreateCommand();
                _insert.Transaction = _tx;
                _insert.CommandText = "INSERT OR IGNORE INTO units (lang_a, text_a, lang_b, text_b, key, variant, origin, added, created, changed, created_by, changed_by, date_from) " +
                                      "VALUES ($la, $ta, $lb, $tb, $key, $v, $o, $d, $cr, $ch, $cb, $hb, $df)";
                _la = _insert.Parameters.Add("$la", SqliteType.Text); _ta = _insert.Parameters.Add("$ta", SqliteType.Text);
                _lb = _insert.Parameters.Add("$lb", SqliteType.Text); _tb = _insert.Parameters.Add("$tb", SqliteType.Text);
                _key = _insert.Parameters.Add("$key", SqliteType.Text); _variant = _insert.Parameters.Add("$v", SqliteType.Text);
                _origin = _insert.Parameters.Add("$o", SqliteType.Text); _added = _insert.Parameters.Add("$d", SqliteType.Text);
                _pCreated = _insert.Parameters.Add("$cr", SqliteType.Integer); _pChanged = _insert.Parameters.Add("$ch", SqliteType.Integer);
                _pCreatedBy = _insert.Parameters.Add("$cb", SqliteType.Integer); _pChangedBy = _insert.Parameters.Add("$hb", SqliteType.Integer);
                _pDateFrom = _insert.Parameters.Add("$df", SqliteType.Text);
                _added.Value = _now;
                _insert.Prepare();
                // Marks are checked per added unit only when there are any (one indexed lookup each); an EverTM nobody
                // removed anything from pays nothing.
                using (var cmd = _c.CreateCommand())
                {
                    cmd.Transaction = _tx;
                    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM removed)";
                    _anyMarks = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
                }
                _limit = ImportLimit;
                if (_limit > 0)
                    using (var cmd = _c.CreateCommand())
                    {
                        cmd.Transaction = _tx;
                        cmd.CommandText = "SELECT count(*) FROM units";
                        _start = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }
            }

            readonly long _limit, _start;
            bool _anyMarks;
            SqliteCommand _findMark;

            /// <summary>Whether this pair of texts (in either order) was removed. One indexed lookup, and none while no
            /// translation is removed.</summary>
            bool Marked(string langX, string rawX, string langY, string rawY) =>
                _anyMarks && MarkOf(VaultRemovalKey.For(langX, rawX, langY, rawY)) != null;

            long? MarkOf(VaultRemovalKey k)
            {
                if (_findMark == null)
                {
                    _findMark = _c.CreateCommand(); _findMark.Transaction = _tx;
                    _findMark.CommandText = "SELECT id FROM removed WHERE pair = $p AND hash_a = $a AND hash_b = $b";
                    foreach (var n in new[] { "$p", "$a", "$b" }) _findMark.Parameters.Add(new SqliteParameter { ParameterName = n });
                }
                _findMark.Parameters[0].Value = k.Pair; _findMark.Parameters[1].Value = k.HashA; _findMark.Parameters[2].Value = k.HashB;
                var v = _findMark.ExecuteScalar();
                return v == null || v is DBNull ? (long?)null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }

            /// <summary>Adds one unit, given in whatever direction and variants it came in.</summary>
            /// <param name="added">When the unit was first added (ISO 8601, UTC), for a unit coming back from EverTM's own export; null for now.</param>
            /// <param name="info">Its fields and dates, if any: stored with a new unit, merged into one already there.</param>
            public VaultAddOutcome Add(string sourceCulture, string sourceRaw, string targetCulture, string targetRaw, string origin, string added = null, VaultUnitInfo info = null)
            {
                Result.Read++;
                string a = VaultMarkup.BaseLanguage(sourceCulture), b = VaultMarkup.BaseLanguage(targetCulture);
                sourceRaw = sourceRaw?.Trim() ?? ""; targetRaw = targetRaw?.Trim() ?? "";
                if (a.Length == 0 || b.Length == 0 || a == b || VaultMarkup.IsJunk(VaultMarkup.Plain(sourceRaw), VaultMarkup.Plain(targetRaw)))
                { Result.Skipped++; return VaultAddOutcome.Skipped; }
                if (string.CompareOrdinal(a, b) > 0) { (a, b) = (b, a); (sourceRaw, targetRaw) = (targetRaw, sourceRaw); }
                if (Marked(a, sourceRaw, b, targetRaw)) { Result.Removed++; return VaultAddOutcome.Removed; }
                var key = Key(a, sourceRaw, b, targetRaw);
                _la.Value = a; _ta.Value = sourceRaw; _lb.Value = b; _tb.Value = targetRaw;
                _key.Value = key;
                _variant.Value = (sourceCulture ?? "") + ">" + (targetCulture ?? "");
                _origin.Value = (object)origin ?? DBNull.Value;
                _added.Value = added ?? _now;
                bool dated = info != null && info.HasDates;
                _pCreated.Value = dated ? (object)Seconds(info.Created ?? info.Changed) : DBNull.Value;
                _pChanged.Value = dated ? (object)Seconds(info.Changed ?? info.Created) : DBNull.Value;
                _pCreatedBy.Value = dated && info.CreatedBy != null ? (object)ValId("@user", info.CreatedBy) : DBNull.Value;
                _pChangedBy.Value = dated && (info.ChangedBy ?? info.CreatedBy) != null ? (object)ValId("@user", info.ChangedBy ?? info.CreatedBy) : DBNull.Value;
                _pDateFrom.Value = dated ? (info.FileDate ? "file" : "unit") : (object)DBNull.Value;
                if (_insert.ExecuteNonQuery() == 1)
                {
                    Result.Added++; _changed = true;
                    if (_limit > 0 && _start + Result.Added > _limit)
                        throw new VaultTooLargeException(VaultLibrary.TooLargeFor32Bit(_start + Result.Added, importing: true) ?? "EverTM would be too large.");
                    if (info != null && info.Fields.Count > 0) AddValues(LastId(), info);
                    return VaultAddOutcome.Added;
                }
                Result.Duplicates++;
                if (info != null && !info.IsEmpty)
                {
                    long? id = Scalar(ref _findKey, "SELECT id FROM units WHERE key = $p", key);
                    if (id != null && Merge(id.Value, info)) Result.Enriched++;
                }
                return VaultAddOutcome.Duplicate;
            }

            static long Seconds(DateTime? t) => VaultFields.ToSeconds(t.Value);

            long LastId()
            {
                if (_lastId == null) { _lastId = _c.CreateCommand(); _lastId.Transaction = _tx; _lastId.CommandText = "SELECT last_insert_rowid()"; }
                return Convert.ToInt64(_lastId.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// Merges <paramref name="info"/> into unit <paramref name="id"/>. True if anything changed.
            ///  - Fields: the union of values, at most <see cref="VaultFields.MaxValuesPerField"/> per field (the first ones kept).
            ///  - Dates: a unit's own date beats a TMX header's, which beats none. Between dates of the same kind, the
            ///    earliest created and the latest changed win, each with its user. So re-confirming a unit, or importing
            ///    a newer copy of it, makes it newer; an older copy never makes it older.
            /// </summary>
            internal bool Merge(long id, VaultUnitInfo info)
            {
                bool changed = info.HasDates && MergeDates(id, info);
                if (info.Fields.Count > 0 && AddValues(id, info)) changed = true;
                if (changed) _changed = true;
                return changed;
            }

            bool MergeDates(long id, VaultUnitInfo info)
            {
                if (_readDates == null)
                {
                    _readDates = _c.CreateCommand(); _readDates.Transaction = _tx;
                    _readDates.CommandText = "SELECT created, changed, created_by, changed_by, date_from FROM units WHERE id = $id";
                    _readDates.Parameters.Add("$id", SqliteType.Integer);
                }
                _readDates.Parameters[0].Value = id;
                long? oc, och, ocb, ochb; string from;
                using (var r = _readDates.ExecuteReader())
                {
                    if (!r.Read()) return false;
                    oc = r.IsDBNull(0) ? (long?)null : r.GetInt64(0); och = r.IsDBNull(1) ? (long?)null : r.GetInt64(1);
                    ocb = r.IsDBNull(2) ? (long?)null : r.GetInt64(2); ochb = r.IsDBNull(3) ? (long?)null : r.GetInt64(3);
                    from = r.IsDBNull(4) ? null : r.GetString(4);
                }
                int oldRank = from == "unit" ? 2 : from == "file" ? 1 : 0, newRank = info.FileDate ? 1 : 2;
                if (newRank < oldRank) return false;
                long nc = Seconds(info.Created ?? info.Changed), nch = Seconds(info.Changed ?? info.Created);
                long? ncb = info.CreatedBy != null ? ValId("@user", info.CreatedBy) : (long?)null;
                var changer = info.ChangedBy ?? info.CreatedBy;
                long? nchb = changer != null ? ValId("@user", changer) : (long?)null;
                long? c = oc, ch = och, cb = ocb, chb = ochb;
                if (newRank > oldRank || oc == null) { c = nc; cb = ncb; ch = nch; chb = nchb; }
                else
                {
                    if (nc < oc) { c = nc; cb = ncb; }
                    if (och == null || nch > och) { ch = nch; chb = nchb; }
                }
                var newFrom = newRank == 2 ? "unit" : "file";
                if (c == oc && ch == och && cb == ocb && chb == ochb && newFrom == from) return false;
                if (_writeDates == null)
                {
                    _writeDates = _c.CreateCommand(); _writeDates.Transaction = _tx;
                    _writeDates.CommandText = "UPDATE units SET created = $c, changed = $ch, created_by = $cb, changed_by = $hb, date_from = $f WHERE id = $id";
                    foreach (var n in new[] { "$c", "$ch", "$cb", "$hb", "$f", "$id" }) _writeDates.Parameters.Add(new SqliteParameter { ParameterName = n });
                }
                var ps = _writeDates.Parameters;
                ps[0].Value = c; ps[1].Value = ch; ps[2].Value = (object)cb ?? DBNull.Value; ps[3].Value = (object)chb ?? DBNull.Value;
                ps[4].Value = newFrom; ps[5].Value = id;
                _writeDates.ExecuteNonQuery();
                return true;
            }

            /// <summary>Links the info's field values to the unit. True if any was new for it.</summary>
            bool AddValues(long unitId, VaultUnitInfo info)
            {
                if (_linkVal == null)
                {
                    _linkVal = _c.CreateCommand(); _linkVal.Transaction = _tx;
                    // Only a value the unit doesn't have yet, and only while the unit has fewer than the maximum for that field.
                    _linkVal.CommandText = "INSERT OR IGNORE INTO unit_vals (unit_id, val_id, added) SELECT $u, $v, $t " +
                                           "WHERE (SELECT count(*) FROM unit_vals uv JOIN vals x ON x.id = uv.val_id WHERE uv.unit_id = $u AND x.field = $f) < " +
                                           VaultFields.MaxValuesPerField.ToString(CultureInfo.InvariantCulture);
                    foreach (var n in new[] { "$u", "$v", "$t", "$f" }) _linkVal.Parameters.Add(new SqliteParameter { ParameterName = n });
                }
                bool any = false;
                foreach (var (field, value) in info.Fields)
                {
                    var ps = _linkVal.Parameters;
                    ps[0].Value = unitId; ps[1].Value = ValId(field, value); ps[2].Value = _nowSeconds; ps[3].Value = field;
                    if (_linkVal.ExecuteNonQuery() == 1) any = true;
                }
                return any;
            }

            long ValId(string field, string value)
            {
                var k = field + "\t" + value;
                if (_valIds.TryGetValue(k, out var id)) return id;
                if (_addVal == null)
                {
                    _addVal = _c.CreateCommand(); _addVal.Transaction = _tx;
                    _addVal.CommandText = "INSERT OR IGNORE INTO vals (field, value) VALUES ($f, $v)";
                    _addVal.Parameters.Add(new SqliteParameter { ParameterName = "$f" }); _addVal.Parameters.Add(new SqliteParameter { ParameterName = "$v" });
                    _findVal = _c.CreateCommand(); _findVal.Transaction = _tx;
                    _findVal.CommandText = "SELECT id FROM vals WHERE field = $f AND value = $v";
                    _findVal.Parameters.Add(new SqliteParameter { ParameterName = "$f" }); _findVal.Parameters.Add(new SqliteParameter { ParameterName = "$v" });
                }
                _addVal.Parameters[0].Value = field; _addVal.Parameters[1].Value = value;
                _addVal.ExecuteNonQuery();
                _findVal.Parameters[0].Value = field; _findVal.Parameters[1].Value = value;
                id = Convert.ToInt64(_findVal.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (_valIds.Count < 100000) _valIds[k] = id;   // a cache, bounded; beyond it each value is looked up
                return id;
            }

            /// <summary>Learns one confirmed segment, with what came with it (<see cref="VaultLearnUnit.Info"/>).</summary>
            public VaultLearnOutcome Learn(VaultLearnUnit u) =>
                Learn(u.SourceCulture, u.Source, u.TargetCulture, u.Target, u.Origin, u.Info(_nowTime), u.FromBatch);

            /// <summary>
            /// Learns one confirmed segment. "The same source" is: the same base languages in the same direction
            /// (nl>en; nl-BE>en-US and nl-NL>en-GB alike) and the same source text and tag shape, as in <see cref="Key"/>.
            /// If EverTM learned a unit for that source before, its translation is replaced; otherwise the unit is
            /// added like an imported one. Imported units are never changed or removed: a confirmation that differs from
            /// an imported unit adds a second translation, which is then the newest (lookups show only the newest).
            ///
            /// With <paramref name="info"/> (schema 3), confirming makes the unit the newest: a confirmation of a unit
            /// already there (imported, or learned) moves its changed date to the time of confirming. A confirmation
            /// that arrives after a newer one for the same source (two Studios, stored out of order) changes nothing.
            /// </summary>
            /// <param name="fromBatch">See <see cref="VaultLearnUnit.FromBatch"/>: a removed translation stays removed;
            /// otherwise confirming it brings it back, its old metadata with it.</param>
            public VaultLearnOutcome Learn(string sourceCulture, string sourceRaw, string targetCulture, string targetRaw, string origin, VaultUnitInfo info = null, bool fromBatch = false)
            {
                string a = VaultMarkup.BaseLanguage(sourceCulture), b = VaultMarkup.BaseLanguage(targetCulture);
                string src = sourceRaw?.Trim() ?? "", trg = targetRaw?.Trim() ?? "";
                if (a.Length == 0 || b.Length == 0 || a == b || VaultMarkup.IsJunk(VaultMarkup.Plain(src), VaultMarkup.Plain(trg)))
                { Result.Read++; Result.Skipped++; return VaultLearnOutcome.Skipped; }
                if (Marked(a, src, b, trg))
                {
                    if (fromBatch) { Result.Read++; Result.Removed++; return VaultLearnOutcome.Removed; }
                    Restore(VaultRemovalKey.For(a, src, b, trg));   // confirmed in the editor: a deliberate act, so it comes back, old metadata and all
                }
                var srcKey = VaultMarkup.Hash(a + ">" + b + "|" + Shape(src));
                long? learnedId = Scalar(ref _findLearned, "SELECT unit_id FROM learned WHERE src_key = $p", srcKey);
                if (learnedId == null)
                {
                    var added = Add(sourceCulture, src, targetCulture, trg, origin, null, info);   // a duplicate gets the info merged: it is now the newest
                    if (added != VaultAddOutcome.Added) return added == VaultAddOutcome.Skipped ? VaultLearnOutcome.Skipped : VaultLearnOutcome.Duplicate;
                    Exec2(ref _remember, "INSERT OR REPLACE INTO learned (src_key, unit_id) VALUES ($p, last_insert_rowid())", srcKey);
                    return VaultLearnOutcome.Added;
                }

                string la = a, ta = src, lb = b, tb = trg;
                if (string.CompareOrdinal(la, lb) > 0) { (la, lb) = (lb, la); (ta, tb) = (tb, ta); }
                var key = Key(la, ta, lb, tb);
                long? existing = Scalar(ref _findKey, "SELECT id FROM units WHERE key = $p", key);
                if (existing == learnedId)
                {
                    Result.Read++; Result.Duplicates++;
                    if (info != null) Merge(existing.Value, info);
                    return VaultLearnOutcome.Duplicate;
                }
                // A confirmation older than what was learned for this source since (it waited in another Studio's
                // inbox): the newer one stays. Its translation still counts as seen: merged where it already exists.
                if (info?.Changed != null)
                {
                    long? learnedChanged = Scalar(ref _readChanged, "SELECT changed FROM units WHERE id = $p", learnedId.Value);
                    if (learnedChanged != null && learnedChanged.Value > Seconds(info.Changed))
                    {
                        Result.Read++; Result.Duplicates++;
                        if (existing != null) Merge(existing.Value, info);
                        return VaultLearnOutcome.Duplicate;
                    }
                }
                if (existing != null)
                {
                    // The new translation is already a unit (imported, or learned for another source): the one learned
                    // earlier for this source is superseded, so it goes, rather than staying as a near-duplicate.
                    Exec2(ref _delete, "DELETE FROM units WHERE id = $p", learnedId.Value);
                    Exec2(ref _forget, "DELETE FROM learned WHERE src_key = $p", srcKey);
                    if (info != null) Merge(existing.Value, info);
                    Result.Read++; Result.Duplicates++; _changed = true;
                    return VaultLearnOutcome.Replaced;
                }
                if (_update == null)
                {
                    _update = _c.CreateCommand();
                    _update.Transaction = _tx;
                    _update.CommandText = "UPDATE units SET lang_a = $la, text_a = $ta, lang_b = $lb, text_b = $tb, key = $key, variant = $v, origin = $o, added = $d WHERE id = $id";
                }
                _update.Parameters.Clear();
                _update.Parameters.AddWithValue("$la", la); _update.Parameters.AddWithValue("$ta", ta);
                _update.Parameters.AddWithValue("$lb", lb); _update.Parameters.AddWithValue("$tb", tb);
                _update.Parameters.AddWithValue("$key", key);
                _update.Parameters.AddWithValue("$v", (sourceCulture ?? "") + ">" + (targetCulture ?? ""));
                _update.Parameters.AddWithValue("$o", (object)origin ?? DBNull.Value);
                _update.Parameters.AddWithValue("$d", _now);
                _update.Parameters.AddWithValue("$id", learnedId.Value);
                if (_update.ExecuteNonQuery() == 1)
                {
                    if (info != null) Merge(learnedId.Value, info);
                    Result.Read++; Result.Added++; _changed = true; return VaultLearnOutcome.Updated;
                }
                // The learned unit is gone (removed by hand, say): forget it and learn this one afresh.
                Exec2(ref _forget, "DELETE FROM learned WHERE src_key = $p", srcKey);
                return Learn(sourceCulture, sourceRaw, targetCulture, targetRaw, origin, info);
            }

            SqliteCommand _readChanged;

            // ---- removing a translation, and bringing it back ----

            /// <summary>
            /// Removes units <paramref name="unitIds"/> (every stored unit of the translation <paramref name="key"/>, found
            /// by the caller in the index), keeping an exact copy of each, and marks the translation removed, so it is
            /// never added again (<see cref="Add"/>, <see cref="Learn(VaultLearnUnit)"/>). A unit learned from a
            /// confirmation loses its learned row too, so a later correction of its source is learned afresh. Ids no
            /// longer in the store are ignored. Returns the number of units removed. Part of this writer's transaction.
            /// </summary>
            public int Remove(VaultRemovalKey key, IEnumerable<long> unitIds, DateTime removedAt, string removedBy)
            {
                if (key == null) return 0;
                using (var cmd = _c.CreateCommand())
                {
                    cmd.Transaction = _tx;
                    cmd.CommandText = "INSERT INTO removed (pair, hash_a, hash_b, removed_at, removed_by) VALUES ($p, $a, $b, $t, $u) " +
                                      "ON CONFLICT (pair, hash_a, hash_b) DO UPDATE SET removed_at = excluded.removed_at, removed_by = excluded.removed_by";
                    cmd.Parameters.AddWithValue("$p", key.Pair); cmd.Parameters.AddWithValue("$a", key.HashA); cmd.Parameters.AddWithValue("$b", key.HashB);
                    cmd.Parameters.AddWithValue("$t", VaultFields.ToSeconds(removedAt));
                    cmd.Parameters.AddWithValue("$u", string.IsNullOrWhiteSpace(removedBy) ? (object)DBNull.Value : VaultFields.Clean(removedBy));
                    cmd.ExecuteNonQuery();
                }
                long markId = MarkOf(key).Value;
                _anyMarks = true;
                int n = 0;
                using (var copy = _c.CreateCommand())
                using (var forget = _c.CreateCommand())
                using (var delete = _c.CreateCommand())
                {
                    copy.Transaction = forget.Transaction = delete.Transaction = _tx;
                    copy.CommandText = "INSERT INTO removed_units (removal_id, lang_a, text_a, lang_b, text_b, variant, origin, added, created, changed, created_by, changed_by, date_from, fields) " +
                                       "SELECT $m, u.lang_a, u.text_a, u.lang_b, u.text_b, u.variant, u.origin, u.added, u.created, u.changed, " +
                                       "(SELECT value FROM vals WHERE id = u.created_by), (SELECT value FROM vals WHERE id = u.changed_by), u.date_from, " +
                                       "(SELECT group_concat(v.field || char(9) || v.value, char(10)) FROM unit_vals uv JOIN vals v ON v.id = uv.val_id WHERE uv.unit_id = u.id) " +
                                       "FROM units u WHERE u.id = $id";
                    copy.Parameters.AddWithValue("$m", markId);
                    var cid = copy.Parameters.Add("$id", SqliteType.Integer);
                    forget.CommandText = "DELETE FROM learned WHERE unit_id = $id";
                    var fid = forget.Parameters.Add("$id", SqliteType.Integer);
                    delete.CommandText = "DELETE FROM units WHERE id = $id";   // the triggers log it and take its field values with it
                    var did = delete.Parameters.Add("$id", SqliteType.Integer);
                    foreach (var id in unitIds.Distinct())
                    {
                        cid.Value = id;
                        if (copy.ExecuteNonQuery() == 0) continue;   // gone already
                        fid.Value = id; forget.ExecuteNonQuery();
                        did.Value = id; delete.ExecuteNonQuery();
                        n++;
                    }
                }
                _changed = true;
                return n;
            }

            /// <summary>
            /// Brings back a removed translation: its mark goes, and the units kept with it are added again exactly as
            /// they were (texts, variant, origin, when added, dates and users, fields). Units that are back already are
            /// merged, as on any import. Returns the number of units restored; 0 when nothing was removed under that key.
            /// Part of this writer's transaction.
            /// </summary>
            public int Restore(VaultRemovalKey key)
            {
                var markId = key == null ? null : MarkOf(key);
                if (markId == null) return 0;
                var copies = new List<(string la, string ta, string lb, string tb, string variant, string origin, string added, VaultUnitInfo info)>();
                using (var cmd = _c.CreateCommand())
                {
                    cmd.Transaction = _tx;
                    cmd.CommandText = "SELECT lang_a, text_a, lang_b, text_b, variant, origin, added, created, changed, created_by, changed_by, date_from, fields FROM removed_units WHERE removal_id = $m ORDER BY rowid";
                    cmd.Parameters.AddWithValue("$m", markId.Value);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var info = new VaultUnitInfo
                            {
                                Created = r.IsDBNull(7) ? (DateTime?)null : VaultFields.FromSeconds(r.GetInt64(7)),
                                Changed = r.IsDBNull(8) ? (DateTime?)null : VaultFields.FromSeconds(r.GetInt64(8)),
                                CreatedBy = r.IsDBNull(9) ? null : r.GetString(9),
                                ChangedBy = r.IsDBNull(10) ? null : r.GetString(10),
                                FileDate = !r.IsDBNull(11) && r.GetString(11) == "file",
                            };
                            if (!r.IsDBNull(12)) foreach (var (f, v) in VaultFields.Unpack(r.GetString(12))) info.Fields.Add((f, v));
                            copies.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4),
                                        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), info));
                        }
                }
                using (var cmd = _c.CreateCommand())
                {
                    cmd.Transaction = _tx;
                    cmd.CommandText = "DELETE FROM removed_units WHERE removal_id = $m; DELETE FROM removed WHERE id = $m";
                    cmd.Parameters.AddWithValue("$m", markId.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _c.CreateCommand())
                {
                    cmd.Transaction = _tx;
                    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM removed)";
                    _anyMarks = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
                }
                _changed = true;
                int n = 0;
                foreach (var c in copies)
                {
                    // In the direction and variants it was stored with ("nl-BE>en-GB"), so its variant comes back as it was.
                    var parts = c.variant.Split('>');
                    string first = parts[0], second = parts.Length > 1 ? parts[1] : "";
                    VaultAddOutcome outcome;
                    if (first.Length > 0 && VaultMarkup.BaseLanguage(first) == c.la) outcome = Add(first, c.ta, second.Length > 0 ? second : c.lb, c.tb, c.origin, c.added, c.info);
                    else if (first.Length > 0 && VaultMarkup.BaseLanguage(first) == c.lb) outcome = Add(first, c.tb, second.Length > 0 ? second : c.la, c.ta, c.origin, c.added, c.info);
                    else outcome = Add(c.la, c.ta, c.lb, c.tb, c.origin, c.added, c.info);
                    if (outcome == VaultAddOutcome.Added) n++;
                }
                return n;
            }


            long? Scalar(ref SqliteCommand cmd, string sql, object p)
            {
                Prepare(ref cmd, sql);
                cmd.Parameters[0].Value = p;
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? (long?)null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }

            void Exec2(ref SqliteCommand cmd, string sql, object p)
            {
                Prepare(ref cmd, sql);
                cmd.Parameters[0].Value = p;
                cmd.ExecuteNonQuery();
            }

            void Prepare(ref SqliteCommand cmd, string sql)
            {
                if (cmd != null) return;
                cmd = _c.CreateCommand();
                cmd.Transaction = _tx;
                cmd.CommandText = sql;
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "$p" });
            }

            public void Commit()
            {
                if (_changed)
                    using (var cmd = _c.CreateCommand())
                    {
                        cmd.Transaction = _tx;
                        cmd.CommandText = "UPDATE meta SET value = CAST(value AS INTEGER) + 1 WHERE name = 'version'";
                        cmd.ExecuteNonQuery();
                    }
                _tx.Commit();
                _done = true;
            }

            public void Dispose()
            {
                try { if (!_done) _tx.Rollback(); } catch { }
                _insert.Dispose();
                foreach (var cmd in new[] { _findLearned, _findKey, _update, _delete, _forget, _remember, _readDates, _writeDates, _findVal, _addVal, _linkVal, _lastId, _readChanged, _findMark }) cmd?.Dispose();
                _tx.Dispose();
                _c.Dispose();
            }
        }

        /// <summary>
        /// Imports a Supervertaler Workbench database, if you have one: read-only, never changed. Optional: SuperVault
        /// works without Workbench.
        /// </summary>
        /// <summary>
        /// The translation memories in Workbench's database (opened read-only), sorted by name: each with its name, its
        /// language pairs and its number of units. The counts come from one GROUP BY over translation_units, a single
        /// pass (about 3 s for 1.55 million units), so run it off the UI thread. Names come from
        /// translation_memories when Workbench has that table; a TM without a name is shown by its tm_id, and a named
        /// TM with no units is listed with 0.
        /// </summary>
        public static List<WorkbenchTm> ListWorkbenchTms(string workbenchDb, CancellationToken cancel, int busyTimeoutSeconds = 30)
        {
            if (!File.Exists(workbenchDb)) throw new FileNotFoundException("The Workbench database was not found at " + workbenchDb + ".", workbenchDb);
            var tms = new Dictionary<string, WorkbenchTm>(StringComparer.Ordinal);
            using (var src = Open(workbenchDb, readOnly: true, busyTimeoutSeconds))
            using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = "SELECT tm_id, source_lang, target_lang, count(*) FROM translation_units GROUP BY tm_id, source_lang, target_lang";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        cancel.ThrowIfCancellationRequested();
                        var id = r.IsDBNull(0) ? "" : r.GetString(0);
                        if (!tms.TryGetValue(id, out var tm)) tms[id] = tm = new WorkbenchTm { TmId = id, Name = id };
                        long n = r.GetInt64(3);
                        tm.Units += n;
                        tm.Pairs.Add((r.IsDBNull(1) ? "?" : r.GetString(1), r.IsDBNull(2) ? "?" : r.GetString(2), n));
                    }
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'translation_memories'";
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                {
                    try
                    {
                        cmd.CommandText = "SELECT tm_id, name FROM translation_memories";
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                if (r.IsDBNull(0)) continue;
                                var id = r.GetString(0);
                                if (!tms.TryGetValue(id, out var tm)) tms[id] = tm = new WorkbenchTm { TmId = id, Name = id };   // named, no units
                                var name = r.IsDBNull(1) ? null : r.GetString(1)?.Trim();
                                if (!string.IsNullOrEmpty(name)) tm.Name = name;
                            }
                    }
                    catch (SqliteException) { }   // an older Workbench without these columns: tm_ids it is
                }
            }
            foreach (var tm in tms.Values) tm.Pairs.Sort((a, b) => b.Units.CompareTo(a.Units));
            return tms.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.TmId, StringComparer.Ordinal).ToList();
        }

        /// <summary>tm_id -> name, from Workbench's translation_memories table; empty when it has none.</summary>
        static Dictionary<string, string> WorkbenchNames(SqliteCommand cmd)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'translation_memories'";
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return names;
            try
            {
                cmd.CommandText = "SELECT tm_id, name FROM translation_memories";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var name = r.IsDBNull(1) ? null : r.GetString(1)?.Trim();
                        if (!r.IsDBNull(0) && !string.IsNullOrEmpty(name)) names[r.GetString(0)] = name;
                    }
            }
            catch (SqliteException) { }   // an older Workbench without these columns: tm_ids it is
            return names;
        }

        /// <summary>
        /// Imports Workbench's units into the store (Workbench's database is only read). <paramref name="tmIds"/> limits
        /// it to those translation memories; null imports every unit, and an empty choice imports nothing. A tm_id
        /// Workbench doesn't have matches nothing.
        /// </summary>
        public static VaultImportResult ImportWorkbench(string store, string workbenchDb, Action<string> progress, CancellationToken cancel, int busyTimeoutSeconds = 30, ICollection<string> tmIds = null)
        {
            if (!File.Exists(workbenchDb)) throw new FileNotFoundException("The Workbench database was not found at " + workbenchDb + ".", workbenchDb);
            if (tmIds != null && tmIds.Count == 0) return new VaultImportResult();
            using (var w = new Writer(store))
            using (var src = Open(workbenchDb, readOnly: true, busyTimeoutSeconds))
            using (var cmd = src.CreateCommand())
            {
                if (tmIds != null)
                {
                    // The choice goes into a temporary table of this connection (allowed on a read-only database): no
                    // limit on how many TMs, and no tm_id ever becomes part of the SQL text.
                    cmd.CommandText = "CREATE TEMP TABLE chosen (tm_id TEXT PRIMARY KEY)";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "INSERT OR IGNORE INTO chosen VALUES ($id)";
                    var p = cmd.Parameters.Add("$id", SqliteType.Text);
                    foreach (var id in tmIds) { p.Value = id ?? ""; cmd.ExecuteNonQuery(); }
                    cmd.Parameters.Clear();
                }
                // Each unit's metadata is its TM's name, as Source, and Client and End client when the name is
                // CLIENT_Endclient. Workbench's own dates are when a unit entered Workbench, not when it was
                // translated, so they are not used.
                var names = WorkbenchNames(cmd);
                var infos = new Dictionary<string, VaultUnitInfo>(StringComparer.Ordinal);
                cmd.CommandText = "SELECT source_text, target_text, source_lang, target_lang, tm_id FROM translation_units" +
                                  (tmIds == null ? "" : " WHERE tm_id IN (SELECT tm_id FROM chosen)") + " ORDER BY id";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        if ((w.Result.Read & 1023) == 0) cancel.ThrowIfCancellationRequested();
                        if (w.Result.Read % 20000 == 0) progress?.Invoke("Importing from Workbench: " + VaultLibrary.Units(w.Result.Read, CultureInfo.InvariantCulture) + " read");
                        var tm = r.IsDBNull(4) ? "" : r.GetString(4);
                        if (!infos.TryGetValue(tm, out var info))
                        {
                            infos[tm] = info = new VaultUnitInfo();
                            var name = names.TryGetValue(tm, out var n) ? n : tm;
                            info.Add(VaultFields.Source, name);
                            if (VaultFields.SplitClientName(name, out var client, out var end))
                            {
                                info.Add(VaultFields.Client, client);
                                info.Add(VaultFields.EndClient, end);
                                w.Result.NamesSplit++;
                            }
                        }
                        w.Add(r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(1) ? "" : r.GetString(1), "workbench", null, info);
                    }
                cancel.ThrowIfCancellationRequested();
                w.Commit();
                return w.Result;
            }
        }
    }

    /// <summary>
    /// Which translation a removal is about: a base language pair (alphabetical: "en", "nl") and a hash of each side's
    /// plain, normalised text, the same normalisation exact matches use. So it covers every formatting of the sentence
    /// and every variant (nl-BE and nl-NL, en-GB and en-US), in both directions.
    /// </summary>
    public sealed class VaultRemovalKey
    {
        public readonly string LangA, HashA, LangB, HashB;

        public VaultRemovalKey(string langA, string hashA, string langB, string hashB)
        {
            LangA = langA ?? ""; HashA = hashA ?? ""; LangB = langB ?? ""; HashB = hashB ?? "";
        }

        public string Pair => LangA + "|" + LangB;

        /// <summary>The key of a pair of texts, given in either order (base languages, raw texts with tag codes).</summary>
        public static VaultRemovalKey For(string langX, string rawX, string langY, string rawY)
        {
            string hx = VaultMarkup.Hash(VaultMarkup.Plain(rawX)), hy = VaultMarkup.Hash(VaultMarkup.Plain(rawY));
            return string.CompareOrdinal(langX, langY) <= 0 ? new VaultRemovalKey(langX, hx, langY, hy) : new VaultRemovalKey(langY, hy, langX, hx);
        }

        public override bool Equals(object obj) => obj is VaultRemovalKey k && k.LangA == LangA && k.HashA == HashA && k.LangB == LangB && k.HashB == HashB;
        public override int GetHashCode() => (LangA + "|" + HashA + "|" + LangB + "|" + HashB).GetHashCode();
        public override string ToString() => LangA + ":" + HashA + "|" + LangB + ":" + HashB;
    }

    /// <summary>A translation the user removed (<see cref="VaultStore.Removed"/>).</summary>
    public sealed class VaultRemoved
    {
        public VaultRemovalKey Key;
        public DateTime RemovedAt;   // UTC
        public string RemovedBy;
        public int Units;
        /// <summary>One removed unit's texts (tag codes included), in <see cref="VaultRemovalKey.LangA"/> and LangB.</summary>
        public string TextA, TextB, Variant;
    }
}
