using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        static SqliteConnection Open(string path, int busySeconds)
        {
            var c = VaultStore.Open(path, readOnly: false, busySeconds);
            try
            {
                VaultStore.Exec(c, "PRAGMA journal_mode=WAL");
                VaultStore.Exec(c, "CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY AUTOINCREMENT, src_lang TEXT, src TEXT, trg_lang TEXT, trg TEXT, origin TEXT)");
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
                cmd.CommandText = "INSERT INTO pending (src_lang, src, trg_lang, trg, origin) VALUES ($sl, $s, $tl, $t, $o)";
                var sl = cmd.Parameters.Add("$sl", SqliteType.Text); var s = cmd.Parameters.Add("$s", SqliteType.Text);
                var tl = cmd.Parameters.Add("$tl", SqliteType.Text); var t = cmd.Parameters.Add("$t", SqliteType.Text);
                var o = cmd.Parameters.Add("$o", SqliteType.Text);
                foreach (var u in units)
                {
                    sl.Value = (object)u.SourceCulture ?? DBNull.Value; s.Value = (object)u.Source ?? DBNull.Value;
                    tl.Value = (object)u.TargetCulture ?? DBNull.Value; t.Value = (object)u.Target ?? DBNull.Value;
                    o.Value = (object)u.Origin ?? DBNull.Value;
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
                cmd.CommandText = "SELECT id, src_lang, src, trg_lang, trg, origin FROM pending ORDER BY id LIMIT " + ReadBatch.ToString(CultureInfo.InvariantCulture);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        lastId = r.GetInt64(0);
                        list.Add(new VaultLearnUnit
                        {
                            SourceCulture = r.IsDBNull(1) ? "" : r.GetString(1), Source = r.IsDBNull(2) ? "" : r.GetString(2),
                            TargetCulture = r.IsDBNull(3) ? "" : r.GetString(3), Target = r.IsDBNull(4) ? "" : r.GetString(4),
                            Origin = r.IsDBNull(5) ? null : r.GetString(5),
                        });
                    }
            }
            return list;
        }

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
