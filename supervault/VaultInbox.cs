using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>
    /// Confirmed segments waiting to be learned: <c>inbox.db</c> beside the store. A confirmation is written here first,
    /// in a transaction of its own that takes a millisecond or two, and moved into the store and the index later, under
    /// the build lock (<see cref="VaultLibrary"/>).
    ///
    /// Why a separate file: an import holds the store's write transaction for minutes, and every CAT tool on the PC
    /// shares the store. Here a confirmation never waits for an import, and once it is here it survives Studio being
    /// closed or crashing; whichever Studio next holds the build lock moves it, its own and every other's.
    ///
    /// Only small transactions touch this file, so its locking is SQLite's own: a writer waits at most
    /// <see cref="VaultLibrary.InboxBusySeconds"/> for another. Rows are deleted once they are in the store; a row moved
    /// twice (a crash between the two steps) is harmless, because learning the same unit again changes nothing.
    /// </summary>
    internal static class VaultInbox
    {
        public const int ReadBatch = 5000;
        static readonly string[] Extra = { "confirmed_at", "user", "project", "client", "subject", "document", "omit", "from_batch" };

        static SqliteConnection Open(string path, int busySeconds)
        {
            var c = VaultStore.Open(path, readOnly: false, busySeconds);
            try
            {
                VaultStore.Exec(c, "PRAGMA journal_mode=WAL");
                VaultStore.Exec(c, "CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY AUTOINCREMENT, src_lang TEXT, src TEXT, trg_lang TEXT, trg TEXT, origin TEXT)");
                // Schema 3: when and where a segment was confirmed. Added to an inbox made by an older version; rows it
                // wrote have them empty (stored as confirmed when stored, with no metadata).
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(pending)";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) have.Add(r.GetString(1));
                }
                foreach (var col in Extra)
                    if (!have.Contains(col)) VaultStore.Exec(c, "ALTER TABLE pending ADD COLUMN " + col + (col == "confirmed_at" || col == "from_batch" ? " INTEGER" : " TEXT"));
                // Removing and restoring a translation (schema 4): queued like confirmations, applied after them.
                VaultStore.Exec(c, "CREATE TABLE IF NOT EXISTS removals (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, pair TEXT NOT NULL, " +
                                   "hash_a TEXT NOT NULL, hash_b TEXT NOT NULL, at INTEGER, by_user TEXT)");
                // Edits (kinds "edit" and "undo-edit"): the texts and fields, added to an inbox made before them.
                var removalCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(removals)";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) removalCols.Add(r.GetString(1));
                }
                foreach (var col in EditColumns)
                    if (!removalCols.Contains(col)) VaultStore.Exec(c, "ALTER TABLE removals ADD COLUMN " + col + (col == "old_changed" ? " INTEGER" : " TEXT"));
                return c;
            }
            catch { c.Dispose(); throw; }
        }

        public static void Append(string path, IList<VaultLearnUnit> units, int busySeconds)
        {
            if (units.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var c = Open(path, busySeconds))
            using (var tx = c.BeginTransaction())
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO pending (src_lang, src, trg_lang, trg, origin, confirmed_at, user, project, client, subject, document, omit, from_batch) VALUES ($sl, $s, $tl, $t, $o, $ca, $u, $p, $c, $sj, $d, $om, $fb)";
                var fb = cmd.Parameters.Add("$fb", SqliteType.Integer);
                var ca = cmd.Parameters.Add("$ca", SqliteType.Integer); var us = cmd.Parameters.Add("$u", SqliteType.Text);
                var pr = cmd.Parameters.Add("$p", SqliteType.Text); var cl = cmd.Parameters.Add("$c", SqliteType.Text);
                var sj = cmd.Parameters.Add("$sj", SqliteType.Text); var dc = cmd.Parameters.Add("$d", SqliteType.Text);
                var om = cmd.Parameters.Add("$om", SqliteType.Text);
                var sl = cmd.Parameters.Add("$sl", SqliteType.Text); var s = cmd.Parameters.Add("$s", SqliteType.Text);
                var tl = cmd.Parameters.Add("$tl", SqliteType.Text); var t = cmd.Parameters.Add("$t", SqliteType.Text);
                var o = cmd.Parameters.Add("$o", SqliteType.Text);
                foreach (var u in units)
                {
                    sl.Value = (object)u.SourceCulture ?? DBNull.Value; s.Value = (object)u.Source ?? DBNull.Value;
                    tl.Value = (object)u.TargetCulture ?? DBNull.Value; t.Value = (object)u.Target ?? DBNull.Value;
                    o.Value = (object)u.Origin ?? DBNull.Value;
                    ca.Value = u.ConfirmedAt != null ? (object)VaultFields.ToSeconds(u.ConfirmedAt.Value) : DBNull.Value;
                    us.Value = (object)u.User ?? DBNull.Value; pr.Value = (object)u.Project ?? DBNull.Value; cl.Value = (object)u.Client ?? DBNull.Value;
                    sj.Value = (object)u.Subject ?? DBNull.Value; dc.Value = (object)u.Document ?? DBNull.Value;
                    om.Value = (object)u.Omit ?? DBNull.Value;
                    fb.Value = u.FromBatch ? 1 : 0;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        /// <summary>The oldest waiting units, at most <see cref="ReadBatch"/>, with the id of the last one.</summary>
        public static List<VaultLearnUnit> Read(string path, int busySeconds, out long lastId)
        {
            lastId = 0;
            var list = new List<VaultLearnUnit>();
            if (!File.Exists(path)) return list;
            using (var c = Open(path, busySeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, src_lang, src, trg_lang, trg, origin, confirmed_at, user, project, client, subject, document, omit, from_batch FROM pending ORDER BY id LIMIT " + ReadBatch.ToString(CultureInfo.InvariantCulture);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        lastId = r.GetInt64(0);
                        list.Add(new VaultLearnUnit
                        {
                            SourceCulture = r.IsDBNull(1) ? "" : r.GetString(1), Source = r.IsDBNull(2) ? "" : r.GetString(2),
                            TargetCulture = r.IsDBNull(3) ? "" : r.GetString(3), Target = r.IsDBNull(4) ? "" : r.GetString(4),
                            Origin = r.IsDBNull(5) ? null : r.GetString(5),
                            ConfirmedAt = r.IsDBNull(6) ? (DateTime?)null : VaultFields.FromSeconds(r.GetInt64(6)),
                            User = Text(r, 7), Project = Text(r, 8), Client = Text(r, 9), Subject = Text(r, 10), Document = Text(r, 11), Omit = Text(r, 12),
                            FromBatch = !r.IsDBNull(13) && r.GetInt64(13) != 0,
                        });
                    }
            }
            return list;
        }

        static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        /// <summary>Removes the units up to and including <paramref name="lastId"/>: those just moved into the store.</summary>
        public static void Delete(string path, long lastId, int busySeconds)
        {
            using (var c = Open(path, busySeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM pending WHERE id <= $id";
                cmd.Parameters.AddWithValue("$id", lastId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>A removal or restore waiting in the inbox.</summary>
        public sealed class Removal
        {
            public long Id;
            public bool Restore;
            public VaultRemovalKey Key;
            public DateTime At;
            public string By;
            /// <summary>"edit" or "undo-edit" (with <see cref="Edit"/>); null: a removal or restore.</summary>
            public string EditKind;
            public VaultTranslationEdit Edit;
        }

        /// <summary>Packed fields; "-" for null (unchanged), "" for none.</summary>
        static string PackOrNull(List<(string Field, string Value)> fields) => fields == null ? "-" : VaultFields.Pack(fields);
        static List<(string Field, string Value)> UnpackOrNull(string s) => s == null || s == "-" ? null : VaultFields.Unpack(s);

        static readonly string[] EditColumns = { "e_sl", "e_tl", "e_shown_src", "e_shown_trg", "e_new_src", "e_new_trg", "e_fields", "e_old_fields", "old_changed", "e_old_changed_by" };

        public static void AppendRemovals(string path, IList<Removal> removals, int busySeconds)
        {
            if (removals.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var c = Open(path, busySeconds))
            using (var tx = c.BeginTransaction())
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO removals (kind, pair, hash_a, hash_b, at, by_user, " + string.Join(", ", EditColumns) + ") VALUES ($k, $p, $a, $b, $t, $u, " +
                                  string.Join(", ", EditColumns.Select(x => "$" + x)) + ")";
                var k = cmd.Parameters.Add("$k", SqliteType.Text); var p = cmd.Parameters.Add("$p", SqliteType.Text);
                var a = cmd.Parameters.Add("$a", SqliteType.Text); var b = cmd.Parameters.Add("$b", SqliteType.Text);
                var t = cmd.Parameters.Add("$t", SqliteType.Integer); var u = cmd.Parameters.Add("$u", SqliteType.Text);
                var ep = EditColumns.ToDictionary(x => x, x => cmd.Parameters.Add("$" + x, x == "old_changed" ? SqliteType.Integer : SqliteType.Text));
                foreach (var r in removals)
                {
                    k.Value = r.EditKind ?? (r.Restore ? "restore" : "remove"); p.Value = r.Key.Pair; a.Value = r.Key.HashA; b.Value = r.Key.HashB;
                    t.Value = VaultFields.ToSeconds(r.At); u.Value = (object)r.By ?? DBNull.Value;
                    var e = r.Edit;
                    ep["e_sl"].Value = (object)e?.SourceLang ?? DBNull.Value; ep["e_tl"].Value = (object)e?.TargetLang ?? DBNull.Value;
                    ep["e_shown_src"].Value = (object)e?.ShownSource ?? DBNull.Value; ep["e_shown_trg"].Value = (object)e?.ShownTarget ?? DBNull.Value;
                    ep["e_new_src"].Value = (object)e?.NewSource ?? DBNull.Value; ep["e_new_trg"].Value = (object)e?.NewTarget ?? DBNull.Value;
                    ep["e_fields"].Value = e == null ? (object)DBNull.Value : PackOrNull(e.Fields);
                    ep["e_old_fields"].Value = e == null ? (object)DBNull.Value : PackOrNull(e.OldFields);
                    ep["old_changed"].Value = e?.OldChanged == null ? (object)DBNull.Value : VaultFields.ToSeconds(e.OldChanged.Value);
                    ep["e_old_changed_by"].Value = (object)e?.OldChangedBy ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        /// <summary>The waiting removals and restores, oldest first (a user's few: no batching needed).</summary>
        public static List<Removal> ReadRemovals(string path, int busySeconds)
        {
            var list = new List<Removal>();
            if (!File.Exists(path)) return list;
            using (var c = Open(path, busySeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, kind, pair, hash_a, hash_b, at, by_user, " + string.Join(", ", EditColumns) + " FROM removals ORDER BY id LIMIT 1000";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var pair = r.GetString(2).Split('|');
                        list.Add(new Removal
                        {
                            Id = r.GetInt64(0), Restore = r.GetString(1) == "restore",
                            Key = new VaultRemovalKey(pair[0], r.GetString(3), pair.Length > 1 ? pair[1] : "", r.GetString(4)),
                            At = r.IsDBNull(5) ? DateTime.UtcNow : VaultFields.FromSeconds(r.GetInt64(5)),
                            By = Text(r, 6),
                            EditKind = r.GetString(1) == "edit" || r.GetString(1) == "undo-edit" ? r.GetString(1) : null,
                            Edit = r.GetString(1) == "edit" || r.GetString(1) == "undo-edit" ? new VaultTranslationEdit
                            {
                                SourceLang = Text(r, 7), TargetLang = Text(r, 8), ShownSource = Text(r, 9), ShownTarget = Text(r, 10),
                                NewSource = Text(r, 11), NewTarget = Text(r, 12), Fields = UnpackOrNull(Text(r, 13)),
                                OldFields = UnpackOrNull(Text(r, 14)) ?? new List<(string, string)>(),
                                OldChanged = r.IsDBNull(15) ? (DateTime?)null : VaultFields.FromSeconds(r.GetInt64(15)), OldChangedBy = Text(r, 16),
                            } : null,
                        });
                    }
            }
            return list;
        }

        public static void DeleteRemoval(string path, long id, int busySeconds)
        {
            using (var c = Open(path, busySeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM removals WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public static long CountRemovals(string path, int busySeconds)
        {
            if (!File.Exists(path)) return 0;
            using (var c = Open(path, busySeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM removals";
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public static long Count(string path, int busySeconds)
        {
            if (!File.Exists(path)) return 0;
            using (var c = Open(path, busySeconds))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM pending";
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }
}
