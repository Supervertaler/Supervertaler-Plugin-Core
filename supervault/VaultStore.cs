using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault
{
    public enum VaultAddOutcome { Added, Duplicate, Skipped }

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
    /// </summary>
    public static class VaultStore
    {
        public const int Schema = 1;

        /// <summary>Creates the store if it doesn't exist yet. Safe to call while other processes have it open.</summary>
        public static void Create(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var c = Open(path, readOnly: false))
            {
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
            bool _done;

            public Writer(string path, int busyTimeoutSeconds = 30)
            {
                Create(path);
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
                if (_insert.ExecuteNonQuery() == 1) { Result.Added++; return VaultAddOutcome.Added; }
                Result.Duplicates++;
                return VaultAddOutcome.Duplicate;
            }

            public void Commit()
            {
                if (Result.Added > 0)
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
