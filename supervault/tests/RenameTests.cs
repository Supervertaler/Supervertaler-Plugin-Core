using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// The rename from SuperVault to EverTM on disk: <c>supervault\</c> becomes <c>evertm\</c>
    /// (<see cref="VaultLibrary.MoveToNewName"/>) and <c>supervault.db</c> becomes <c>evertm.db</c>. Every test works in
    /// its own scratch "data folder"; none touches the real one.
    /// </summary>
    static class RenameTests
    {
        static readonly (string, string, string, string, string)[] Units =
        {
            ("nl-NL", "De pomp wordt gestart.", "en-GB", "The pump is started.", "workbench"),
            ("nl-NL", "De klep is gesloten.", "en-GB", "The valve is closed.", "studio"),
        };

        /// <summary>A data folder with an old-style store: <c>supervault\supervault.db</c> and a built index.</summary>
        static string OldStyle(out string old, out string now)
        {
            var data = Scratch.Folder("data");
            old = Path.Combine(data, VaultLibrary.OldFolderName);
            now = Path.Combine(data, VaultLibrary.FolderName);
            System.IO.Directory.CreateDirectory(old);
            var store = Path.Combine(old, "supervault.db");
            VaultStore.Create(store);
            using (var w = new VaultStore.Writer(store))
            {
                foreach (var (sl, src, tl, trg, origin) in Units) w.Add(sl, src, tl, trg, origin);
                w.Commit();
            }
            var lib = VaultLibrary.ForTests(old);
            lib.Build(null, CancellationToken.None);
            VaultLibrary.CloseAllForTests();   // as when Studio is closed: nothing in the folder is open
            SqliteConnection.ClearAllPools();
            A.True(!File.Exists(store + "-wal") || new FileInfo(store + "-wal").Length == 0, "the old store was closed cleanly");
            return data;
        }

        static List<string> Logged(Action a)
        {
            // Only this thread's lines: everything tested here logs on the calling thread, and another test's
            // background thread (learning, a rebuild) can log at any moment, which made these counts flaky.
            var lines = new List<string>();
            var was = VaultLibrary.Log;
            int me = Thread.CurrentThread.ManagedThreadId;
            VaultLibrary.Log = m => { if (Thread.CurrentThread.ManagedThreadId == me) lock (lines) lines.Add(m); else was?.Invoke(m); };
            try { a(); } finally { VaultLibrary.Log = was; }
            return lines;
        }

        static void FindsThePump(string root)
        {
            var lib = VaultLibrary.ForTests(root);
            var m = lib.Lookup(LibraryTests.Doc("De pomp wordt gestart."), "nl", "en", 70, 5);
            A.True(m.Count == 1 && VaultLookup.PlainOf(m[0].Target) == "The pump is started.", "the moved index finds its unit (" + m.Count + " matches)");
            A.Eq(2L, lib.StoreCount(), "units in the store");
        }

        static string Snapshot(string folder) =>
            string.Join("\n", System.IO.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => f.Substring(folder.Length) + " " + new FileInfo(f).Length + " " + Convert.ToBase64String(System.Security.Cryptography.SHA1.Create().ComputeHash(File.ReadAllBytes(f)))));

        public static void NoOldFolderMovesNothing()
        {
            var data = Scratch.Folder("data");
            var lines = Logged(() => A.Eq(Path.Combine(data, "evertm"), VaultLibrary.MoveToNewName(data), "the new folder"));
            A.True(!System.IO.Directory.Exists(Path.Combine(data, "evertm")) && !System.IO.Directory.Exists(Path.Combine(data, "supervault")), "no folder made");
            A.Eq(0, lines.Count, "nothing logged");
            A.Eq(Path.Combine(data, "evertm"), VaultLibrary.MoveToNewName(Path.Combine(data, "not-there")).Replace("not-there" + Path.DirectorySeparatorChar, ""), "a data folder that doesn't exist yet");
        }

        public static void TheOldFolderIsMovedWhole()
        {
            var data = OldStyle(out var old, out var now);
            var before = Snapshot(old);
            var lines = Logged(() => A.Eq(now, VaultLibrary.MoveToNewName(data), "uses the new folder"));
            A.True(!System.IO.Directory.Exists(old), "the old folder is gone");
            A.Eq(before, Snapshot(now), "every file moved unchanged (store, index, pointer)");
            A.True(lines.Count == 1 && lines[0].StartsWith("Moved "), "logged: " + string.Join(" / ", lines));
            A.Eq(Path.Combine(now, "supervault.db"), VaultLibrary.ForTests(now).StorePath, "the store keeps its old name until the build lock is taken");
            FindsThePump(now);
            A.Eq(now, VaultLibrary.MoveToNewName(data), "a second start: nothing to do");
        }

        public static void TheStoreFileIsRenamedUnderTheBuildLock()
        {
            var data = OldStyle(out var old, out var now);
            VaultLibrary.MoveToNewName(data);
            var lib = VaultLibrary.ForTests(now);
            File.WriteAllText(Path.Combine(now, "supervault.db-shm"), "");   // a leftover, as SQLite can leave one
            var lines = Logged(() => lib.Build(null, CancellationToken.None));   // takes the build lock
            A.Eq(Path.Combine(now, "evertm.db"), lib.StorePath, "renamed");
            A.True(!File.Exists(Path.Combine(now, "supervault.db")) && !File.Exists(Path.Combine(now, "supervault.db-shm")), "nothing left under the old name");
            A.True(lines.Any(l => l.StartsWith("Renamed ")), "logged: " + string.Join(" / ", lines));
            A.Eq(2L, lib.StoreCount(), "units after the rename");
            lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-NL", Source = "Een nieuwe zin.", TargetCulture = "en-GB", Target = "A new sentence.", Origin = "studio" } });
            LearnTests.Settle(lib);
            A.Eq(3L, lib.StoreCount(), "learned unit stored");
            A.True(!File.Exists(Path.Combine(now, "supervault.db")), "no new store under the old name");
        }

        public static void AFolderInUseIsLeftAndTriedAgainNextStart()
        {
            var data = OldStyle(out var old, out var now);
            var before = Snapshot(old);
            foreach (var (what, open) in new (string, Func<IDisposable>)[]
            {
                ("another Studio holding build.lock", () => new FileStream(Path.Combine(old, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)),
                ("another Studio reading the store", () => { var c = new SqliteConnection("Data Source=" + Path.Combine(old, "supervault.db") + ";Mode=ReadOnly;Pooling=False"); c.Open(); return c; }),
                ("a file deep inside open, even shared for delete", () => new FileStream(System.IO.Directory.EnumerateFiles(System.IO.Directory.EnumerateDirectories(old, "gen-*").First()).First(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)),
            })
            {
                List<string> lines;
                using (open()) lines = Logged(() => A.Eq(old, VaultLibrary.MoveToNewName(data), what + ": keeps using the old folder"));
                A.True(!System.IO.Directory.Exists(now), what + ": no new folder");
                A.Eq(before, Snapshot(old), what + ": nothing changed");
                A.True(lines.Count == 1 && lines[0].StartsWith("Could not move ") && lines[0].Contains("trying again next time"), what + ": logged why: " + string.Join(" / ", lines));
                FindsThePump(old);
                VaultLibrary.CloseAllForTests();
                SqliteConnection.ClearAllPools();
            }
            var stillOld = VaultLibrary.ForTests(old);
            stillOld.Build(null, CancellationToken.None);   // takes the build lock in the old folder
            A.True(File.Exists(Path.Combine(old, "supervault.db")) && !File.Exists(Path.Combine(old, "evertm.db")),
                   "inside a folder still called supervault the store keeps its name (an older Supervertaler elsewhere knows only that one)");
            VaultLibrary.CloseAllForTests();
            Thread.Sleep(500);   // the old generation's clean-up (after 200 ms in tests) has run
            before = Snapshot(old);
            A.Eq(now, VaultLibrary.MoveToNewName(data), "the next start, with nothing open, moves it");
            A.Eq(before, Snapshot(now), "whole");
            FindsThePump(now);
        }

        /// <summary>
        /// A Studio that had to keep the old folder holds it until it ends, so a Studio started later can't move it from
        /// under it (with only a memory-mapped index open, nothing else would stop the rename).
        /// </summary>
        public static void AStudioUsingTheOldFolderKeepsItThere()
        {
            var data = OldStyle(out var old, out var now);
            var before = Snapshot(old);
            FileStream pin, pin2;
            using (var blocker = new FileStream(Path.Combine(old, "current.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                A.Eq(old, VaultLibrary.ResolveInUse(data, out pin), "first Studio: can't move, uses the old folder");
            A.True(pin != null && File.Exists(Path.Combine(old, VaultLibrary.PinFileName)), "and marks it as in use");
            var other = VaultLibrary.ResolveInUse(data, out pin2);   // a second Studio starting later
            A.Eq(old, other, "the second Studio can't move it either while the first uses it");
            A.True(pin2 != null, "both hold the mark");
            pin.Dispose();
            A.Eq(old, VaultLibrary.MoveToNewName(data), "still held by the second");
            pin2.Dispose();
            A.True(!File.Exists(Path.Combine(old, VaultLibrary.PinFileName)), "the mark is gone with the last Studio that held it");
            A.Eq(now, VaultLibrary.ResolveInUse(data, out var pin3), "the next start moves it");
            A.True(pin3 == null, "nothing held when the new folder is used");
            A.Eq(before, Snapshot(now), "whole");

            var vanished = Scratch.Folder("data");   // the old folder moved by another Studio between our try and the mark
            System.IO.Directory.CreateDirectory(Path.Combine(vanished, VaultLibrary.FolderName));
            A.Eq(Path.Combine(vanished, VaultLibrary.FolderName), VaultLibrary.ResolveInUse(vanished, out var pin4), "no old folder: the new one");
            A.True(pin4 == null, "nothing held");
        }

        public static void AFolderWeMayNotRenameIsLeft()
        {
            var data = OldStyle(out var old, out var now);
            var before = Snapshot(old);
            // Renaming needs Delete on the folder or Delete-child on its parent: deny both.
            var me = WindowsIdentity.GetCurrent().User;
            var deny = new FileSystemAccessRule(me, FileSystemRights.Delete, AccessControlType.Deny);
            var denyChild = new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
            var dir = new DirectoryInfo(old);
            var parent = new DirectoryInfo(data);
            var sec = dir.GetAccessControl(AccessControlSections.Access);
            var psec = parent.GetAccessControl(AccessControlSections.Access);
            sec.AddAccessRule(deny);
            psec.AddAccessRule(denyChild);
            dir.SetAccessControl(sec);
            parent.SetAccessControl(psec);
            try
            {
                var lines = Logged(() => A.Eq(old, VaultLibrary.MoveToNewName(data), "no right to rename: the old folder"));
                A.True(lines.Count == 1 && lines[0].StartsWith("Could not move "), "logged: " + string.Join(" / ", lines));
                A.True(!System.IO.Directory.Exists(now), "no new folder");
            }
            finally
            {
                psec.RemoveAccessRule(denyChild);
                parent.SetAccessControl(psec);
                sec.RemoveAccessRule(deny);
                dir.SetAccessControl(sec);
            }
            A.Eq(before, Snapshot(old), "nothing changed");
            FindsThePump(old);
            VaultLibrary.CloseAllForTests();   // in 32-bit an open index holds its files, which (rightly) blocks the rename

            new DirectoryInfo(old).Attributes |= FileAttributes.ReadOnly;   // the read-only attribute: Windows still allows the rename
            foreach (var f in System.IO.Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.ReadOnly);
            A.Eq(now, VaultLibrary.MoveToNewName(data), "a read-only folder of read-only files moves");
            A.Eq(before, Snapshot(now), "whole");
            new DirectoryInfo(now).Attributes &= ~FileAttributes.ReadOnly;
            foreach (var f in System.IO.Directory.EnumerateFiles(now, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        }

        public static void BothFoldersThereKeepsBoth()
        {
            var data = OldStyle(out var old, out var now);
            System.IO.Directory.CreateDirectory(now);
            File.WriteAllText(Path.Combine(now, "evertm.db"), "stands in for a store");
            var beforeOld = Snapshot(old); var beforeNew = Snapshot(now);
            var lines = Logged(() => A.Eq(now, VaultLibrary.MoveToNewName(data), "both there: the new one"));
            A.Eq(beforeOld, Snapshot(old), "the old one is left whole");
            A.Eq(beforeNew, Snapshot(now), "the new one is untouched");
            A.True(lines.Count == 1 && lines[0].StartsWith("Both ") && lines[0].Contains("nothing in it is deleted"), "logged: " + string.Join(" / ", lines));
        }

        public static void AnEmptyNewFolderDoesNotHideTheStore()
        {
            var data = OldStyle(out var old, out var now);
            var before = Snapshot(old);
            System.IO.Directory.CreateDirectory(now);
            A.Eq(now, VaultLibrary.MoveToNewName(data), "moved over the empty folder");
            A.Eq(before, Snapshot(now), "whole");
            A.True(!System.IO.Directory.Exists(old), "old gone");
        }

        public static void AnEmptyOldFolderNextToTheNewOneIsRemoved()
        {
            var data = OldStyle(out var old, out var now);
            VaultLibrary.MoveToNewName(data);
            System.IO.Directory.CreateDirectory(old);   // what an older Supervertaler in another Studio leaves after trying its lock
            var lines = Logged(() => A.Eq(now, VaultLibrary.MoveToNewName(data), "the new folder"));
            A.True(!System.IO.Directory.Exists(old), "the empty old folder is removed");
            A.Eq(0, lines.Count, "nothing to report");
        }

        public static void TwoStudiosStartingAtOnceBothGetTheNewFolder()
        {
            for (int round = 0; round < 5; round++)
            {
                var data = OldStyle(out var old, out var now);
                var before = Snapshot(old);
                var go = new ManualResetEventSlim();
                var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { go.Wait(); return VaultLibrary.MoveToNewName(data); })).ToArray();
                go.Set();
                Task.WaitAll(tasks);
                A.True(tasks.All(t => t.Result == now), "round " + round + ": every caller uses the new folder: " + string.Join(", ", tasks.Select(t => t.Result)));
                A.Eq(before, Snapshot(now), "round " + round + ": whole");
                A.True(!System.IO.Directory.Exists(old), "round " + round + ": no old folder");
            }
        }

        /// <summary>
        /// A store whose -wal still holds changes (Studio was killed with a write not yet folded in): renaming only the
        /// main file would lose them, so it keeps its old name until SQLite has folded them in.
        /// </summary>
        public static void AStoreWithPendingChangesKeepsItsNameUntilTheyAreIn()
        {
            var data = OldStyle(out var old, out var now);
            var store = Path.Combine(old, "supervault.db");
            var crashed = Path.Combine(Scratch.Folder("crashed"), VaultLibrary.OldFolderName);
            System.IO.Directory.CreateDirectory(crashed);
            using (var w = new VaultStore.Writer(store))
            {
                w.Add("nl-NL", "Nog niet in het hoofdbestand.", "en-GB", "Not in the main file yet.", "studio");
                w.Commit();
                // Copy the files as they are on disk while the writer is still open (as when Studio is killed): the
                // main file plus a -wal holding the new unit.
                foreach (var f in System.IO.Directory.EnumerateFiles(old, "supervault.db*"))
                    using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = File.Create(Path.Combine(crashed, Path.GetFileName(f))))
                        src.CopyTo(dst);
            }
            A.True(new FileInfo(Path.Combine(crashed, "supervault.db-wal")).Length > 0, "the copy has a -wal with changes");
            var data2 = Path.GetDirectoryName(crashed);
            var moved = VaultLibrary.MoveToNewName(data2);
            A.Eq(Path.Combine(data2, "evertm"), moved, "the folder moves");
            var lib = VaultLibrary.ForTests(moved);
            var lines = Logged(() => { using (var l = (IDisposable)typeof(VaultLibrary).GetMethod("TryTakeLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(lib, null)) { } });
            A.Eq(Path.Combine(moved, "supervault.db"), lib.StorePath, "not renamed while its -wal holds changes");
            A.True(lines.Any(l => l.Contains("was not renamed") && l.Contains("-wal")), "logged: " + string.Join(" / ", lines));
            A.Eq(3L, lib.StoreCount(), "the change in the -wal is there (read through the old name)");
            SqliteConnection.ClearAllPools();
            VaultStore.Create(lib.StorePath);   // a write-mode open and close, as the next import or learn does: SQLite folds the -wal in
            SqliteConnection.ClearAllPools();
            A.True(!File.Exists(Path.Combine(moved, "supervault.db-wal")) || new FileInfo(Path.Combine(moved, "supervault.db-wal")).Length == 0, "folded in");
            var next = VaultLibrary.ForTests(moved);   // the next start
            next.Build(null, CancellationToken.None);
            A.Eq(Path.Combine(moved, "evertm.db"), next.StorePath, "renamed at the next start");
            A.Eq(3L, next.StoreCount(), "nothing lost");
        }

        public static void AReaderHoldingTheStoreOpenMakesTheFileRenameWaitHarmlessly()
        {
            var data = OldStyle(out var old, out var now);
            VaultLibrary.MoveToNewName(data);
            var lib = VaultLibrary.ForTests(now);
            List<string> lines;
            using (var c = new SqliteConnection("Data Source=" + Path.Combine(now, "supervault.db") + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                lines = Logged(() => lib.Build(null, CancellationToken.None));
            }
            A.Eq(Path.Combine(now, "supervault.db"), lib.StorePath, "still the old name");
            A.True(lines.Any(l => l.StartsWith("Could not rename ")), "logged: " + string.Join(" / ", lines));
            FindsThePump(now);
            A.Eq(2L, lib.StoreCount(), "nothing lost");
        }

        public static void TheRealFoldersAreNeverUsedByTests()
        {
            A.True(Scratch.IsReal(VaultLibrary.DefaultRoot) && Scratch.IsReal(VaultLibrary.OldDefaultRoot), "both names count as real");
            A.True(Scratch.IsReal(Path.Combine(VaultLibrary.OldDefaultRoot, "gen-1")), "and anything inside");
            A.True(!Scratch.IsReal(Scratch.Folder("x")), "a scratch folder doesn't");
            A.True(!VaultLibrary.DefaultRoot.EndsWith("supervault"), "DefaultRoot is the new name and moves nothing");
        }
    }
}
