using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault
{
    public enum VaultAddOutcome { Added, Duplicate, Skipped }

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
    }

    /// <summary>A segment confirmed in a CAT tool, waiting to be learned.</summary>
    public sealed class VaultLearnUnit
    {
        public string SourceCulture, Source, TargetCulture, Target, Origin;
    }

    /// <summary>What an import did.</summary>
    public sealed class VaultImportResult
    {
        public long Read, Added, Duplicates, Skipped;
        public override string ToString() =>
            $"{Added:N0} units added, {Duplicates:N0} already in SuperVault, {Skipped:N0} skipped (no text, one language, or junk)";
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
    /// </summary>
    public static class VaultStore
    {
        public const int Schema = 2;

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
                Exec(c, "UPDATE meta SET value = '" + Schema + "' WHERE name = 'schema' AND CAST(value AS INTEGER) < " + Schema);
            }
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

        /// <summary>Number of changes after <paramref name="seq"/>, i.e. not yet in an index that has applied up to it. 0 without a store.</summary>
        public static long ChangesAfter(string path, long seq)
        {
            if (!File.Exists(path)) return 0;
            using (var c = Open(path, readOnly: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'changes'";
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;
                cmd.CommandText = "SELECT count(*) FROM changes WHERE seq > $s";
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
        /// </summary>
        public sealed class Writer : IDisposable
        {
            readonly SqliteConnection _c;
            readonly SqliteTransaction _tx;
            readonly SqliteCommand _insert;
            readonly SqliteParameter _la, _ta, _lb, _tb, _key, _variant, _origin, _added;
            public VaultImportResult Result { get; } = new VaultImportResult();
            bool _done, _changed;
            SqliteCommand _findLearned, _findKey, _update, _delete, _forget, _remember;

            public Writer(string path, int busyTimeoutSeconds = 30)
            {
                Create(path, busyTimeoutSeconds);
                _c = Open(path, readOnly: false, busyTimeoutSeconds);
                _tx = _c.BeginTransaction();
                _insert = _c.CreateCommand();
                _insert.Transaction = _tx;
                _insert.CommandText = "INSERT OR IGNORE INTO units (lang_a, text_a, lang_b, text_b, key, variant, origin, added) VALUES ($la, $ta, $lb, $tb, $key, $v, $o, $d)";
                _la = _insert.Parameters.Add("$la", SqliteType.Text); _ta = _insert.Parameters.Add("$ta", SqliteType.Text);
                _lb = _insert.Parameters.Add("$lb", SqliteType.Text); _tb = _insert.Parameters.Add("$tb", SqliteType.Text);
                _key = _insert.Parameters.Add("$key", SqliteType.Text); _variant = _insert.Parameters.Add("$v", SqliteType.Text);
                _origin = _insert.Parameters.Add("$o", SqliteType.Text); _added = _insert.Parameters.Add("$d", SqliteType.Text);
                _added.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                _insert.Prepare();
            }

            /// <summary>Adds one unit, given in whatever direction and variants it came in.</summary>
            public VaultAddOutcome Add(string sourceCulture, string sourceRaw, string targetCulture, string targetRaw, string origin)
            {
                Result.Read++;
                string a = VaultMarkup.BaseLanguage(sourceCulture), b = VaultMarkup.BaseLanguage(targetCulture);
                sourceRaw = sourceRaw?.Trim() ?? ""; targetRaw = targetRaw?.Trim() ?? "";
                if (a.Length == 0 || b.Length == 0 || a == b || VaultMarkup.IsJunk(VaultMarkup.Plain(sourceRaw), VaultMarkup.Plain(targetRaw)))
                { Result.Skipped++; return VaultAddOutcome.Skipped; }
                if (string.CompareOrdinal(a, b) > 0) { (a, b) = (b, a); (sourceRaw, targetRaw) = (targetRaw, sourceRaw); }
                _la.Value = a; _ta.Value = sourceRaw; _lb.Value = b; _tb.Value = targetRaw;
                _key.Value = Key(a, sourceRaw, b, targetRaw);
                _variant.Value = (sourceCulture ?? "") + ">" + (targetCulture ?? "");
                _origin.Value = (object)origin ?? DBNull.Value;
                if (_insert.ExecuteNonQuery() == 1) { Result.Added++; _changed = true; return VaultAddOutcome.Added; }
                Result.Duplicates++;
                return VaultAddOutcome.Duplicate;
            }

            /// <summary>
            /// Learns one confirmed segment. "The same source" is: the same base languages in the same direction
            /// (nl>en; nl-BE>en-US and nl-NL>en-GB alike) and the same source text and tag shape, as in <see cref="Key"/>.
            /// If SuperVault learned a unit for that source before, its translation is replaced; otherwise the unit is
            /// added like an imported one (a duplicate of any existing unit adds nothing). Imported units are never
            /// changed: a confirmation that differs from an imported unit adds a second translation, as a TM would.
            /// </summary>
            public VaultLearnOutcome Learn(string sourceCulture, string sourceRaw, string targetCulture, string targetRaw, string origin)
            {
                string a = VaultMarkup.BaseLanguage(sourceCulture), b = VaultMarkup.BaseLanguage(targetCulture);
                string src = sourceRaw?.Trim() ?? "", trg = targetRaw?.Trim() ?? "";
                if (a.Length == 0 || b.Length == 0 || a == b || VaultMarkup.IsJunk(VaultMarkup.Plain(src), VaultMarkup.Plain(trg)))
                { Result.Read++; Result.Skipped++; return VaultLearnOutcome.Skipped; }
                var srcKey = VaultMarkup.Hash(a + ">" + b + "|" + Shape(src));
                long? learnedId = Scalar(ref _findLearned, "SELECT unit_id FROM learned WHERE src_key = $p", srcKey);
                if (learnedId == null)
                {
                    var added = Add(sourceCulture, src, targetCulture, trg, origin);
                    if (added != VaultAddOutcome.Added) return added == VaultAddOutcome.Skipped ? VaultLearnOutcome.Skipped : VaultLearnOutcome.Duplicate;
                    Exec2(ref _remember, "INSERT OR REPLACE INTO learned (src_key, unit_id) VALUES ($p, last_insert_rowid())", srcKey);
                    return VaultLearnOutcome.Added;
                }

                string la = a, ta = src, lb = b, tb = trg;
                if (string.CompareOrdinal(la, lb) > 0) { (la, lb) = (lb, la); (ta, tb) = (tb, ta); }
                var key = Key(la, ta, lb, tb);
                long? existing = Scalar(ref _findKey, "SELECT id FROM units WHERE key = $p", key);
                if (existing == learnedId) { Result.Read++; Result.Duplicates++; return VaultLearnOutcome.Duplicate; }
                if (existing != null)
                {
                    // The new translation is already a unit (imported, or learned for another source): the one learned
                    // earlier for this source is superseded, so it goes, rather than staying as a near-duplicate.
                    Exec2(ref _delete, "DELETE FROM units WHERE id = $p", learnedId.Value);
                    Exec2(ref _forget, "DELETE FROM learned WHERE src_key = $p", srcKey);
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
                _update.Parameters.AddWithValue("$d", _added.Value);
                _update.Parameters.AddWithValue("$id", learnedId.Value);
                if (_update.ExecuteNonQuery() == 1) { Result.Read++; Result.Added++; _changed = true; return VaultLearnOutcome.Updated; }
                // The learned unit is gone (removed by hand, say): forget it and learn this one afresh.
                Exec2(ref _forget, "DELETE FROM learned WHERE src_key = $p", srcKey);
                return Learn(sourceCulture, sourceRaw, targetCulture, targetRaw, origin);
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
                foreach (var cmd in new[] { _findLearned, _findKey, _update, _delete, _forget, _remember }) cmd?.Dispose();
                _tx.Dispose();
                _c.Dispose();
            }
        }

        /// <summary>
        /// Imports a Supervertaler Workbench database, if you have one: read-only, never changed. Optional: SuperVault
        /// works without Workbench.
        /// </summary>
        public static VaultImportResult ImportWorkbench(string store, string workbenchDb, Action<string> progress, CancellationToken cancel, int busyTimeoutSeconds = 30)
        {
            if (!File.Exists(workbenchDb)) throw new FileNotFoundException("The Workbench database was not found at " + workbenchDb + ".", workbenchDb);
            using (var w = new Writer(store))
            using (var src = Open(workbenchDb, readOnly: true, busyTimeoutSeconds))
            using (var cmd = src.CreateCommand())
            {
                cmd.CommandText = "SELECT source_text, target_text, source_lang, target_lang FROM translation_units ORDER BY id";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        if ((w.Result.Read & 1023) == 0) cancel.ThrowIfCancellationRequested();
                        if (w.Result.Read % 20000 == 0) progress?.Invoke("Workbench: " + w.Result.Read.ToString("N0", CultureInfo.InvariantCulture) + " units read");
                        w.Add(r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(1) ? "" : r.GetString(1), "workbench");
                    }
                cancel.ThrowIfCancellationRequested();
                w.Commit();
                return w.Result;
            }
        }
    }
}
