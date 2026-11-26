using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// Editing a translation (evertm-edit-plan.md): a text edit replaces every copy of the old translation with one unit
    /// and marks the old text removed ("edited"); a fields-only edit changes the fields only; Undo puts everything back;
    /// an edit that fails halfway changes nothing. Test data uses Acme / PROJ-001 only.
    /// </summary>
    static class EditTests
    {
        static VaultLibrary Lib() => VaultLibrary.For(Scratch.Folder("edit"));

        static string TmxFile(string body, string name = "acme.tmx")
        {
            var path = Path.Combine(Scratch.Folder("tmx"), name);
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"x\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" + body + "</body></tmx>", new UTF8Encoding(false));
            return path;
        }

        static string T(string nl, string en, string attrs = "", string inner = "", string nlLang = "nl-NL", string enLang = "en-GB") =>
            "<tu" + attrs + ">" + inner + "<tuv xml:lang=\"" + nlLang + "\"><seg>" + nl + "</seg></tuv><tuv xml:lang=\"" + enLang + "\"><seg>" + en + "</seg></tuv></tu>";

        static VaultImportResult Import(VaultLibrary lib, string tmx) => lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);

        static List<VaultMatch> Find(VaultLibrary lib, string text, string sl = "nl", string tl = "en")
        {
            lib.Refresh();
            return lib.Lookup(LibraryTests.Doc(text), sl, tl, 50, 10);
        }

        static string Target(VaultMatch m) => VaultLookup.PlainOf(m.Target);

        static void Settle(VaultLibrary lib, int seconds = 30)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (lib.LearnOnce() && lib.LearnWaiting() == 0 && lib.RemovalsWaiting() == 0) { lib.Refresh(); return; }
                Thread.Sleep(50);
            }
            throw new AssertFailed("editing did not finish: " + lib.LearnError);
        }

        static long N(VaultLibrary lib, string sql)
        {
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            }
        }

        const string Pump = "De pomp wordt gestart.";
        const string Typo = "The pmup is started.";
        const string Fixed = "The pump is switched on.";
        static DateTime D(int y, int mo = 1, int d = 1) => new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The typo in three copies (plain with Client Acme, bold, nl-BE>en-US with a tag), and an older good translation.</summary>
        static VaultLibrary WithTypo()
        {
            var lib = Lib();
            Import(lib, TmxFile(
                T(Pump, "The pump is started.", " creationdate=\"20200101T000000Z\"", "<prop type=\"x-Project\">PROJ-001</prop>") +
                T(Pump, Typo, " creationdate=\"20250101T000000Z\" creationid=\"jdoe\"", "<prop type=\"x-Client\">Acme</prop><prop type=\"x-Project\">PROJ-002</prop>") +
                T("De <bpt i=\"1\">&lt;b&gt;</bpt>pomp<ept i=\"1\">&lt;/b&gt;</ept> wordt gestart.", "The <bpt i=\"1\">&lt;b&gt;</bpt>pmup<ept i=\"1\">&lt;/b&gt;</ept> is started.", " creationdate=\"20240101T000000Z\"") +
                T(Pump + "<ph x=\"1\">&lt;br/&gt;</ph>", Typo + "<ph x=\"1\">&lt;br/&gt;</ph>", " creationdate=\"20230101T000000Z\"", "", "nl-BE", "en-US") +
                T("De klep sluit.", "The valve closes.")));
            return lib;
        }

        static VaultTranslationEdit EditOf(VaultMatch m, string newTarget, List<(string, string)> fields = null) => new VaultTranslationEdit
        {
            SourceLang = "nl", TargetLang = "en", ShownSource = m.Candidate.SourceRaw, ShownTarget = m.Candidate.TargetRaw,
            NewSource = m.Candidate.SourceRaw, NewTarget = newTarget, Fields = fields,
            OldFields = m.Candidate.Fields.ToList(), OldChanged = m.Candidate.Changed, OldChangedBy = m.Candidate.ChangedBy,
        };

        static VaultMatch Typoed(VaultLibrary lib)
        {
            var m = Find(lib, Pump).First();
            A.Eq(Typo, Target(m), "the typo is the match");
            return m;
        }

        // ---- a text edit ----

        public static void ATextEditReplacesEveryCopyWithOneAndMarksTheOldText()
        {
            var lib = WithTypo();
            var m = Typoed(lib);
            var edit = lib.Edit(EditOf(m, Fixed), "mbeijer");
            A.True(edit != null && edit.TextChanged, "queued as a text edit");
            A.True(!Find(lib, Pump).Any(x => Target(x) == Typo), "the old text is hidden here at once");
            Settle(lib);
            A.Eq(3L, lib.StoreCount(), "the typo's three units became one (plus the two others)");
            var after = Find(lib, Pump).First();
            A.Eq(Fixed, Target(after), "the edited translation is the newest, so it is the match");
            A.Eq(D(2025), after.Candidate.Created, "created as the shown unit was");
            A.Eq("jdoe", after.Candidate.CreatedBy, "by whom");
            A.Eq("mbeijer", after.Candidate.ChangedBy, "changed by the editor");
            A.True(DateTime.UtcNow - after.Candidate.Changed.Value < TimeSpan.FromMinutes(1), "just now");
            A.True(after.Candidate.Fields.Contains(("Client", "Acme")) && after.Candidate.Fields.Contains(("Project", "PROJ-002")), "its fields kept: " + string.Join("|", after.Candidate.Fields));
            A.Eq("The pump is switched on.", Target(Find(lib, Pump).First()), "and in nl > en");
            A.Eq(1, Find(lib, Fixed, "en", "nl").Count(x => x.Score == 100 && Target(x) == Pump), "and in en > nl (one unit serves both)");
            var removed = lib.Removed(0, out _);
            A.True(removed.Count == 1 && removed[0].Edited && removed[0].Units == 3, "the old text is in Removed translations, as edited");
            var again = Import(lib, TmxFile(T(Pump, Typo), "again.tmx"));
            A.Eq(1L, again.Removed, "re-importing the old text skips it");
        }

        public static void UndoPutsTheOldTranslationBackExactly()
        {
            var lib = WithTypo();
            var m = Typoed(lib);
            var edit = lib.Edit(EditOf(m, Fixed), "mbeijer");
            Settle(lib);
            lib.UndoEdit(edit, "mbeijer");
            Settle(lib);
            A.Eq(5L, lib.StoreCount(), "the three old units back, the edited one gone");
            var back = Find(lib, Pump).First();
            A.Eq(Typo, Target(back), "the typo is the match again");
            A.Eq(m.Candidate.Created, back.Candidate.Created, "its dates as they were");
            A.Eq(string.Join("|", m.Candidate.Fields), string.Join("|", back.Candidate.Fields), "its fields as they were");
            A.Eq(0L, N(lib, "SELECT count(*) FROM removed"), "no mark left behind");
            A.True(!Find(lib, Pump).Any(x => Target(x) == Fixed), "the edited text is not offered");
        }

        public static void EditingIntoAnExistingTranslationMergesAndUndoKeepsIt()
        {
            var lib = WithTypo();
            var m = Typoed(lib);
            var edit = lib.Edit(EditOf(m, "The pump is started."), "mbeijer");
            Settle(lib);
            A.Eq(2L, lib.StoreCount(), "no second copy of the good translation");
            A.Eq("The pump is started.", Target(Find(lib, Pump).Single()), "the good translation is the match");
            lib.UndoEdit(edit, "mbeijer");
            Settle(lib);
            A.Eq(5L, lib.StoreCount(), "undo: the typo's units back, and the good translation (there before the edit) kept");
        }

        // ---- a fields-only edit ----

        public static void AFieldsOnlyEditChangesTheFieldsOnly()
        {
            var lib = WithTypo();
            var m = Typoed(lib);
            var fields = new List<(string, string)> { ("Client", "Acme Two"), ("Subject", "Pumps") };
            var edit = lib.Edit(EditOf(m, m.Candidate.TargetRaw, fields), "mbeijer");
            A.True(edit != null && !edit.TextChanged, "queued as a fields-only edit");
            Settle(lib);
            A.Eq(5L, lib.StoreCount(), "no unit added or removed");
            A.Eq(0L, N(lib, "SELECT count(*) FROM removed"), "no mark");
            var after = Find(lib, Pump).First();
            A.Eq(Typo, Target(after), "the same translation");
            A.Eq("Client=Acme Two|Subject=Pumps", string.Join("|", after.Candidate.Fields.Where(f => f.Field != "Source").OrderBy(f => f.Field).Select(f => f.Field + "=" + f.Value)),
                 "its fields are exactly the edited ones (Project PROJ-002 taken out)");
            A.Eq("mbeijer", after.Candidate.ChangedBy, "changed by the editor");
            lib.UndoEdit(edit, "mbeijer");
            Settle(lib);
            var back = Find(lib, Pump).First();
            A.Eq(string.Join("|", m.Candidate.Fields.OrderBy(f => f.Field)), string.Join("|", back.Candidate.Fields.OrderBy(f => f.Field)), "undo: the old fields");
            A.Eq(m.Candidate.Changed, back.Candidate.Changed, "and the old changed date");
        }

        // ---- marks and learning ----

        public static void EditingIntoARemovedTextClearsItsMark()
        {
            var lib = WithTypo();
            lib.Remove(Find(lib, "De klep sluit.").First().Candidate, "nl", "en", null);
            Settle(lib);
            var m = Typoed(lib);
            lib.Edit(new VaultTranslationEdit { SourceLang = "nl", TargetLang = "en", ShownSource = m.Candidate.SourceRaw, ShownTarget = m.Candidate.TargetRaw, NewSource = "De klep sluit.", NewTarget = "The valve closes." }, null);
            Settle(lib);
            A.Eq(1L, N(lib, "SELECT count(*) FROM removed"), "only the edited typo is marked: the removed text's mark was cleared by the edit");
            A.Eq("The valve closes.", Target(Find(lib, "De klep sluit.").First()), "and it is offered again");
        }

        public static void AnEditedLearnedUnitStillTakesTheNextCorrection()
        {
            var lib = WithTypo();
            lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-NL", Source = "De klep opent.", TargetCulture = "en-GB", Target = "The valve opnes.", Origin = "studio" } });
            Settle(lib);
            var m = Find(lib, "De klep opent.").First();
            lib.Edit(EditOf(m, "The valve opens."), null);
            Settle(lib);
            long units = lib.StoreCount();
            A.Eq(1L, N(lib, "SELECT count(*) FROM learned"), "the learned row moved to the edited unit");
            lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-NL", Source = "De klep opent.", TargetCulture = "en-GB", Target = "The valve is opening.", Origin = "studio" } });
            Settle(lib);
            A.Eq(units, lib.StoreCount(), "a later correction in the editor updates it, rather than adding a near-copy");
            A.Eq("The valve is opening.", Target(Find(lib, "De klep opent.").First()), "the correction is the match");
        }

        /// <summary>
        /// A confirmation that brings a field value EverTM hasn't seen yet (a new document's first segment) must still
        /// remember the right unit as learned: "the last inserted row" is then the new value's, not the unit's. Before
        /// the fix, the learned row pointed at a vals id, and a later correction overwrote whichever unit had that id.
        /// </summary>
        public static void ACorrectionAfterANewFieldValueUpdatesTheRightUnit()
        {
            var lib = WithTypo();
            var before = Find(lib, "De klep sluit.").First();
            lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-NL", Source = "De klep opent.", TargetCulture = "en-GB", Target = "The valve opnes.", Origin = "studio", Document = "brand-new-document.docx", Project = "PROJ-NEW" } });
            Settle(lib);
            long learnedId = N(lib, "SELECT unit_id FROM learned");
            A.Eq(1L, N(lib, "SELECT count(*) FROM units WHERE id = " + learnedId + " AND (text_b = 'The valve opnes.' OR text_a = 'The valve opnes.')"), "the learned row points at the learned unit");
            lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-NL", Source = "De klep opent.", TargetCulture = "en-GB", Target = "The valve opens.", Origin = "studio", Document = "brand-new-document.docx" } });
            Settle(lib);
            A.Eq("The valve opens.", Target(Find(lib, "De klep opent.").First()), "the correction replaced the learned translation");
            A.Eq("The valve closes.", Target(Find(lib, "De klep sluit.").First()), "and no other unit was touched");
            A.Eq(before.Candidate.TargetRaw, Find(lib, "De klep sluit.").First().Candidate.TargetRaw, "exactly");
        }

        /// <summary>Schema 6 drops learned rows that point at the wrong unit (left by the bug above), and keeps the right ones.</summary>
        public static void TheUpgradeDropsWrongLearnedRows()
        {
            var lib = WithTypo();
            lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-NL", Source = "De klep opent.", TargetCulture = "en-GB", Target = "The valve opens.", Origin = "studio" } });
            Settle(lib);
            long good = N(lib, "SELECT unit_id FROM learned");
            long other = N(lib, "SELECT min(id) FROM units");
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO learned (src_key, unit_id) VALUES ('wrong-key', " + other + "), ('gone-key', 999999); UPDATE meta SET value = '5' WHERE name = 'schema'";
                    cmd.ExecuteNonQuery();
                }
            }
            VaultStore.Create(lib.StorePath);
            A.Eq(1L, N(lib, "SELECT count(*) FROM learned"), "the wrong and the dangling rows are gone");
            A.Eq(good, N(lib, "SELECT unit_id FROM learned"), "the right one is kept");
            A.Eq(VaultStore.Schema.ToString(), N(lib, "SELECT value FROM meta WHERE name = 'schema'").ToString(), "and the store is at the current schema");
            A.Eq(2, VaultStore.RepairedLearned, "and it says how many it dropped");
        }

        // ---- failure paths ----

        public static void AnEditThatFailsHalfwayChangesNothing()
        {
            var lib = WithTypo();
            var m = Typoed(lib);
            try
            {
                // After the old units are removed and the new one added, before the commit.
                VaultLibrary.FailPoint = p => { if (p == "removal-commit") throw new IOException("disk full (simulated)"); };
                lib.Edit(EditOf(m, Fixed), null);
                for (int i = 0; i < 3; i++) lib.LearnOnce();
                A.Eq(5L, lib.StoreCount(), "the store is exactly as before: old units there, no new one");
                A.Eq(0L, N(lib, "SELECT count(*) FROM removed"), "no mark");
                A.Eq(1L, N(lib, "SELECT count(*) FROM units WHERE text_b = '" + Typo + "' OR text_a = '" + Typo + "'"), "the typo is still stored");
                A.Eq(1L, lib.RemovalsWaiting(), "the edit waits in the inbox");
            }
            finally { VaultLibrary.FailPoint = null; }
            Settle(lib);
            A.Eq(Fixed, Target(Find(lib, Pump).First()), "applied once the store can be written");
            A.Eq(3L, lib.StoreCount(), "as a whole");
        }

        public static void ASecondProcessSeesTheEdit()
        {
            var lib = WithTypo();
            var other = VaultLibrary.ForTests(lib.Root);
            lib.Edit(EditOf(Typoed(lib), Fixed), null);
            Settle(lib);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500 && Target(Find(other, Pump).First()) != Fixed) Thread.Sleep(100);
            A.Eq(Fixed, Target(Find(other, Pump).First()), "the other process offers the edited text (" + sw.ElapsedMilliseconds + " ms)");
        }

        public static void NothingToEditIsHarmless()
        {
            var lib = WithTypo();
            var m = Typoed(lib);
            A.Eq(null, lib.Edit(null, null), "no edit");
            A.Eq(null, lib.Edit(EditOf(m, m.Candidate.TargetRaw), null), "the same text and no fields: nothing to do");
            A.Eq(null, lib.Edit(EditOf(m, "   "), null), "an empty target: refused");
        }
    }
}
