using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using static Supervertaler.Core.SuperVault.Tests.LibraryTests;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// Metadata, phase 1 (docs/design/evertm-metadata.md): fields and dates kept from TMX files, Workbench and
    /// confirmations; re-import fills them in; one translation per source, newest wins, the exact target variant first.
    /// Test data uses Acme / PROJ-001 only.
    /// </summary>
    static class MetadataTests
    {
        static VaultLibrary Lib() => VaultLibrary.For(Scratch.Folder("meta"));

        static string TmxFile(string body, string headerExtra = "", string name = "acme.tmx")
        {
            var path = Path.Combine(Scratch.Folder("tmx"), name);
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"x\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"" + headerExtra + "/><body>" + body + "</body></tmx>", new UTF8Encoding(false));
            return path;
        }

        static string T(string nl, string en, string attrs = "", string inner = "", string enLang = "en-GB") =>
            "<tu" + attrs + ">" + inner + "<tuv xml:lang=\"nl-NL\"><seg>" + nl + "</seg></tuv><tuv xml:lang=\"" + enLang + "\"><seg>" + en + "</seg></tuv></tu>";

        static VaultImportResult Import(VaultLibrary lib, string tmx) => lib.Import(Tmx(tmx), null, CancellationToken.None);

        static List<VaultMatch> Find(VaultLibrary lib, string text, string sl = "nl", string tl = "en", string culture = null)
        {
            lib.Refresh();
            return lib.Lookup(Doc(text), sl, tl, 50, 10, culture);
        }

        static VaultMatch Top(VaultLibrary lib, string text, string sl = "nl", string tl = "en", string culture = null)
        {
            var m = Find(lib, text, sl, tl, culture);
            A.True(m.Count > 0, "a match for " + text);
            return m[0];
        }

        static string Target(VaultMatch m) => VaultLookup.PlainOf(m.Target);
        static string F(VaultMatch m, string field) => string.Join("|", m.Candidate.Fields.Where(x => x.Field == field).Select(x => x.Value).OrderBy(v => v, StringComparer.Ordinal));
        static DateTime D(int y, int mo = 1, int d = 1, int h = 0, int mi = 0, int s = 0) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc);

        static object Sql(VaultLibrary lib, string sql)
        {
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; return cmd.ExecuteScalar(); }
            }
        }

        static long N(VaultLibrary lib, string sql) => Convert.ToInt64(Sql(lib, sql));

        // ---- keeping: TMX ----

        public static void TmxFieldsDatesAndUsersAreKeptOthersDropped()
        {
            var lib = Lib();
            var tmx = TmxFile(
                T("De pomp wordt gestart.", "The pump is started.",
                  " creationdate=\"20240102T030405Z\" creationid=\"ACME\\jan\" changedate=\"20250607T080910Z\" changeid=\"piet\"",
                  "<prop type=\"Project\">PROJ-001</prop><prop type=\"client\">Acme</prop><prop type=\"x-Domain:SinglePicklist\">Patents</prop>" +
                  "<prop type=\"x-Fachgebiet\">Chemistry</prop><prop type=\"x-PO-number:SingleString\">PO 123</prop><prop type=\"x-Origin\">TM</prop>" +
                  "<prop type=\"x-document\">pump.docx</prop><prop type=\"End_client\">Globex</prop><note>Checked   by the\nreviewer</note>"));
            var r = Import(lib, tmx);
            A.Eq(1L, r.Added, "imported: " + r);
            var m = Top(lib, "De pomp wordt gestart.");
            A.Eq("PROJ-001", F(m, VaultFields.Project), "Project");
            A.Eq("Acme", F(m, VaultFields.Client), "Client (any spelling)");
            A.Eq("Globex", F(m, VaultFields.EndClient), "End client");
            A.Eq("Chemistry|Patents", F(m, VaultFields.Subject), "Domain and Fachgebiet merged into Subject");
            A.Eq("pump.docx", F(m, VaultFields.Document), "Document");
            A.Eq("acme.tmx", F(m, VaultFields.Source), "Source: the file's name");
            A.Eq("Checked by the reviewer", F(m, VaultFields.Note), "the note, whitespace tidied");
            A.True(!m.Candidate.Fields.Any(f => f.Value == "PO 123" || f.Value == "TM"), "fields EverTM doesn't keep are dropped");
            A.Eq(D(2024, 1, 2, 3, 4, 5), m.Candidate.Created, "created");
            A.Eq(D(2025, 6, 7, 8, 9, 10), m.Candidate.Changed, "changed");
            A.Eq("ACME\\jan", m.Candidate.CreatedBy, "creationid kept (other people's names too)");
            A.Eq("piet", m.Candidate.ChangedBy, "changeid");
            A.Eq("unit", (string)Sql(lib, "SELECT date_from FROM units"), "a unit's own date");
        }

        public static void TuvDatesAndTheHeaderDateAreUsedWhenTheTuHasNone()
        {
            var lib = Lib();
            // DéjàVu: the dates on the <tuv>s. The earliest creation, the latest change.
            var body = "<tu><prop type=\"x-client\">Acme</prop>" +
                       "<tuv xml:lang=\"nl-NL\" creationdate=\"20200505T000000Z\" creationid=\"a\"><seg>De klep sluit.</seg></tuv>" +
                       "<tuv xml:lang=\"en-GB\" creationdate=\"20190101T000000Z\" creationid=\"b\" changedate=\"20210101T000000Z\"><seg>The valve closes.</seg></tuv></tu>" +
                       T("De motor draait.", "The motor is running.");   // no dates at all: the header's
            Import(lib, TmxFile(body, " creationdate=\"20221212T121212Z\""));
            var valve = Top(lib, "De klep sluit.");
            A.Eq(D(2019), valve.Candidate.Created, "earliest tuv creationdate");
            A.Eq("b", valve.Candidate.CreatedBy, "with its user");
            A.Eq(D(2021), valve.Candidate.Changed, "tuv changedate");
            var motor = Top(lib, "De motor draait.");
            A.Eq(D(2022, 12, 12, 12, 12, 12), motor.Candidate.Created, "the header's creationdate");
            A.Eq(1L, N(lib, "SELECT count(*) FROM units WHERE date_from = 'file'"), "marked as the file's date");
        }

        // ---- re-importing fills in, never duplicates ----

        public static void ReimportAddsMetadataToUnitsAlreadyThere()
        {
            var lib = Lib();
            var bare = TmxFile(T("De pomp wordt gestart.", "The pump is started.") + T("De klep sluit.", "The valve closes."), name: "old.tmx");
            Import(lib, bare);
            A.Eq("", F(Top(lib, "De klep sluit."), VaultFields.Project), "no project yet");
            A.True(Top(lib, "De klep sluit.").Candidate.Created == null, "no date yet");
            var rich = TmxFile(T("De pomp wordt gestart.", "The pump is started.", " creationdate=\"20230101T000000Z\"", "<prop type=\"Project\">PROJ-001</prop>") +
                               T("De klep sluit.", "The valve closes.", "", "<prop type=\"Project\">PROJ-001</prop>"), name: "acme.tmx");
            var r = Import(lib, rich);
            A.Eq(0L, r.Added, "nothing added: " + r);
            A.Eq(2L, r.Duplicates, "both already there");
            A.Eq(2L, r.Enriched, "both gained metadata");
            A.True(r.ToString().Contains("2 of them brought metadata or dates"), "the summary says so: " + r);
            A.Eq(2L, lib.StoreCount(), "no duplicates stored");
            var m = Top(lib, "De pomp wordt gestart.");
            A.Eq("PROJ-001", F(m, VaultFields.Project), "Project filled in (index updated)");
            A.Eq("acme.tmx|old.tmx", F(m, VaultFields.Source), "both sources");
            A.Eq(D(2023), m.Candidate.Created, "date filled in");
            var again = Import(lib, rich);
            A.Eq(0L, again.Enriched, "importing the same file again changes nothing: " + again);
        }

        public static void MergeRulesForDatesAndTheValueCap()
        {
            var lib = Lib();
            const string u = "De pomp wordt gestart.";
            Import(lib, TmxFile(T(u, "The pump is started.", "", "", "en-GB"), " creationdate=\"20300101T000000Z\""));
            A.Eq("file", (string)Sql(lib, "SELECT date_from FROM units"), "first: only a file date");
            Import(lib, TmxFile(T(u, "The pump is started.", " creationdate=\"20200101T000000Z\" creationid=\"x\" changedate=\"20200601T000000Z\"")));
            A.Eq(D(2020), Top(lib, u).Candidate.Created, "a unit's own date replaces a file date, even an older one");
            A.Eq(D(2020, 6), Top(lib, u).Candidate.Changed, "and its change date");
            Import(lib, TmxFile(T(u, "The pump is started.", " creationdate=\"20220101T000000Z\" changedate=\"20220101T000000Z\" changeid=\"y\"")));
            Import(lib, TmxFile(T(u, "The pump is started.", " creationdate=\"20180101T000000Z\" creationid=\"z\" changedate=\"20190101T000000Z\"")));
            Import(lib, TmxFile(T(u, "The pump is started."), " creationdate=\"20990101T000000Z\""));
            var c = Top(lib, u).Candidate;
            A.Eq(D(2018), c.Created, "the earliest created");
            A.Eq("z", c.CreatedBy, "with its user");
            A.Eq(D(2022), c.Changed, "the latest changed; a file date never overrides a unit's own");
            A.Eq("y", c.ChangedBy, "with its user");
            var body = string.Concat(Enumerable.Range(1, 25).Select(i => T(u, "The pump is started.", "", "<prop type=\"Project\">PROJ-" + i.ToString("000") + "</prop>")));
            Import(lib, TmxFile(body));
            A.Eq((long)VaultFields.MaxValuesPerField, N(lib, "SELECT count(*) FROM unit_vals uv JOIN vals v ON v.id = uv.val_id WHERE v.field = 'Project'"), "at most 20 projects per unit");
            A.Eq(1L, lib.StoreCount(), "still one unit");
        }

        // ---- one translation per source, newest wins ----

        public static void NewestWinsAndTheReverseDirectionKeepsItsOwn()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("Druk op de knop.", "Press the button.", " creationdate=\"20200101T000000Z\"") +
                                T("Druk op de knop.", "Push the button.", " changedate=\"20230101T000000Z\"")));
            var m = Find(lib, "Druk op de knop.");
            A.Eq(1, m.Count(x => x.Score == 100), "one 100% match, not two");
            A.Eq("Push the button.", Target(m[0]), "the newest");
            A.Eq(1, m[0].Candidate.Older, "one older translation hidden");
            A.Eq(2L, lib.StoreCount(), "nothing deleted");
            A.Eq("Druk op de knop.", Target(Top(lib, "Press the button.", "en", "nl")), "the older one still serves en > nl");

            // Undated counts as older than any dated unit, however recently imported.
            Import(lib, TmxFile(T("Druk op de knop.", "Hit the button.")));
            A.Eq("Push the button.", Target(Top(lib, "Druk op de knop.")), "a dated translation beats an undated import");
            A.Eq(2, Top(lib, "Druk op de knop.").Candidate.Older, "two hidden now");

            // Among undated units, the one imported last wins.
            Import(lib, TmxFile(T("De klep sluit.", "The valve closes.")));
            Import(lib, TmxFile(T("De klep sluit.", "The valve shuts.")));
            A.Eq("The valve shuts.", Target(Top(lib, "De klep sluit.")), "undated: imported last wins");
        }

        public static void TheProjectsExactTargetVariantComesFirst()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De kleur van het scherm.", "The colour of the screen.", " creationdate=\"20200101T000000Z\"", "", "en-GB") +
                                T("De kleur van het scherm.", "The color of the display.", " creationdate=\"20240101T000000Z\"", "", "en-US")));
            A.Eq("The colour of the screen.", Target(Top(lib, "De kleur van het scherm.", culture: "en-GB")), "en-GB project: the en-GB unit, though older");
            A.Eq("The color of the display.", Target(Top(lib, "De kleur van het scherm.", culture: "en-US")), "en-US project: the en-US unit");
            A.Eq("The color of the display.", Target(Top(lib, "De kleur van het scherm.", culture: "en")), "no variant asked: the newest");
            A.Eq("The color of the display.", Target(Top(lib, "De kleur van het scherm.", culture: "en-AU")), "no unit in that variant: the newest");
            A.Eq(1, Top(lib, "De kleur van het scherm.", culture: "en-GB").Candidate.Older, "the other one hidden");
        }

        public static void TheWinningTranslationsFormattingVariantThatFitsIsOffered()
        {
            var lib = Lib();
            Import(lib, TmxFile(
                T("Druk op <bpt i=\"1\">&lt;b&gt;</bpt>Start<ept i=\"1\">&lt;/b&gt;</ept>.", "Press <bpt i=\"1\">&lt;b&gt;</bpt>Start<ept i=\"1\">&lt;/b&gt;</ept>.") +
                T("Druk op Start.", "Press Start.", " creationdate=\"20250101T000000Z\"") +
                T("Druk op Start.", "Click Start.", " creationdate=\"20210101T000000Z\"")));
            var doc = new List<VaultPiece>
            {
                new VaultPiece { Text = "Druk op " }, new VaultPiece { Kind = VaultTagKind.Open, Name = "doc", Key = "d1" },
                new VaultPiece { Text = "Start" }, new VaultPiece { Kind = VaultTagKind.Close, Name = "doc", Key = "d1" }, new VaultPiece { Text = "." },
            };
            lib.Refresh();
            var m = lib.Lookup(doc, "nl", "en", 50, 10, "en-GB");
            A.Eq("Press Start.", Target(m[0]), "the newest translation");
            A.True(m[0].Tags != VaultTagOutcome.Unmapped, "in the formatting that fits the document: " + m[0].Tags);
            A.True(!m.Any(x => Target(x) == "Click Start."), "the older translation hidden");
        }

        public static void FuzzyAndConcordanceOfferOnlyTheNewest()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De rode pomp wordt elke ochtend automatisch gestart.", "The red pump is started automatically every morning.", " creationdate=\"20200101T000000Z\"") +
                                T("De rode pomp wordt elke ochtend automatisch gestart.", "Every morning the red pump starts automatically.", " creationdate=\"20240101T000000Z\"")));
            var fuzzy = Find(lib, "De rode pomp wordt elke avond automatisch gestart.");
            A.Eq(1, fuzzy.Count, "one fuzzy match");
            A.Eq("Every morning the red pump starts automatically.", Target(fuzzy[0]), "the newest");
            lib.Refresh();
            var conc = VaultLookup.Concordance(lib.Current, "rode pomp", "nl", "en", 50, 10);
            A.Eq(1, conc.Count, "concordance: one");
            A.Eq("Every morning the red pump starts automatically.", Target(conc[0]), "the newest");
        }

        // ---- confirmations ----

        static VaultLearnUnit U(string s, string t, DateTime? at = null, string project = null, string client = null, string user = null, string document = null) =>
            new VaultLearnUnit { SourceCulture = "nl-NL", Source = s, TargetCulture = "en-GB", Target = t, Origin = "studio", ConfirmedAt = at, Project = project, Client = client, User = user, Document = document };

        public static void ReconfirmingAnOlderTranslationMakesItTheNewest()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("Sluit het deksel.", "Close the lid.", " creationdate=\"20200101T000000Z\"") +
                                T("Sluit het deksel.", "Shut the lid.", " creationdate=\"20240101T000000Z\"")));
            A.Eq("Shut the lid.", Target(Top(lib, "Sluit het deksel.")), "before: the newer import");
            lib.Learn(new[] { U("Sluit het deksel.", "Close the lid.", user: "translator") });
            LearnTests.Settle(lib);
            A.Eq(2L, lib.StoreCount(), "nothing added");
            A.Eq(1L, lib.LearnedThisSession.Already, "counted as already in EverTM");
            var m = Top(lib, "Sluit het deksel.");
            A.Eq("Close the lid.", Target(m), "the re-confirmed translation is now the newest");
            A.Eq("translator", m.Candidate.ChangedBy, "changed by whoever confirmed it");
            A.Eq(D(2020), m.Candidate.Created, "its creation date kept");
        }

        public static void AConfirmationArrivingLateNeverBeatsANewerOne()
        {
            var lib = Lib();
            var t0 = DateTime.UtcNow.AddMinutes(-10);
            lib.Learn(new[] { U("Open de klep.", "Open the valve.", t0.AddMinutes(5)) });
            LearnTests.Settle(lib);
            // Confirmed earlier in another Studio, stored only now.
            lib.Learn(new[] { U("Open de klep.", "Open the valve now.", t0) });
            LearnTests.Settle(lib);
            A.Eq("Open the valve.", (string)Sql(lib, "SELECT text_a FROM units WHERE text_b = 'Open de klep.'"), "the learned unit keeps the newer translation");
            A.Eq(1L, lib.StoreCount(), "and nothing was added");
            // A late confirmation of a translation that exists as another unit doesn't remove the newer one either.
            Import(lib, TmxFile(T("Open de klep.", "Open the flap.")));
            lib.Learn(new[] { U("Open de klep.", "Open the flap.", t0.AddMinutes(1)) });
            LearnTests.Settle(lib);
            A.Eq(2L, lib.StoreCount(), "the newer learned unit is not removed");
            A.Eq("Open the valve.", Target(Top(lib, "Open de klep.")), "and still wins");
        }

        public static void ConfirmedSegmentsCarryProjectClientDocumentAndUser()
        {
            var lib = Lib();
            lib.Learn(new[] { U("De lamp brandt.", "The lamp is on.", null, "ACME_Globex (PROJ-001)", null, "translator", "manual.docx") });
            // Found at once (not yet stored), with its metadata.
            var now = Top(lib, "De lamp brandt.");
            A.Eq("ACME", F(now, VaultFields.Client), "client from the CLIENT_Endclient project name, before storing");
            LearnTests.Settle(lib);
            var m = Top(lib, "De lamp brandt.");
            A.Eq("ACME_Globex (PROJ-001)", F(m, VaultFields.Project), "Project: the Studio project's name");
            A.Eq("ACME", F(m, VaultFields.Client), "Client: its first part, as no client was set");
            A.Eq("Globex", F(m, VaultFields.EndClient), "End client");
            A.Eq("manual.docx", F(m, VaultFields.Document), "Document");
            A.Eq("Studio: ACME_Globex (PROJ-001)", F(m, VaultFields.Source), "Source");
            A.Eq("translator", m.Candidate.CreatedBy, "user");
            A.True(m.Candidate.Changed != null && (DateTime.UtcNow - m.Candidate.Changed.Value).TotalMinutes < 5, "dated now");
            lib.Learn(new[] { U("De lamp is uit.", "The lamp is off.", null, "ACME_Globex", "Acme Ltd") });
            LearnTests.Settle(lib);
            A.Eq("Acme Ltd", F(Top(lib, "De lamp is uit."), VaultFields.Client), "the project's own client wins over the name");
        }

        /// <summary>Michael, 11 Nov: every field on by default, a switch per field. A switched-off field is not stored at all.</summary>
        public static void SwitchedOffFieldsAreNotStored()
        {
            var lib = Lib();
            var u = U("De pomp loopt warm.", "The pump runs hot.", null, "ACME_Globex (PROJ-001)", null, "translator", "manual.docx");
            u.Omit = VaultLearnUnit.OmitOf(new[] { VaultFields.Project, VaultLearnUnit.UserField, VaultFields.Document });
            lib.Learn(new[] { u });
            var now = Top(lib, "De pomp loopt warm.");
            A.Eq("", F(now, VaultFields.Project), "not even in the unit just handed over");
            LearnTests.Settle(lib);
            var m = Top(lib, "De pomp loopt warm.");
            A.Eq("", F(m, VaultFields.Project), "no Project");
            A.Eq("", F(m, VaultFields.Document), "no Document");
            A.True(m.Candidate.CreatedBy == null && m.Candidate.ChangedBy == null, "no user");
            A.Eq("ACME", F(m, VaultFields.Client), "Client still (it was left on)");
            A.Eq("Globex", F(m, VaultFields.EndClient), "End client still");
            A.Eq("Studio", F(m, VaultFields.Source), "Source without the project's name");
            A.True(m.Candidate.Changed != null, "the confirm time is always kept");
            A.Eq(0L, N(lib, "SELECT count(*) FROM vals WHERE value LIKE '%PROJ-001%' OR value = 'translator' OR value = 'manual.docx'"), "the switched-off values are nowhere in the store");

            var all = U("De pomp loopt koel.", "The pump runs cool.", null, "PROJ-002", "Acme", "translator", "manual.docx");
            all.Omit = VaultLearnUnit.OmitOf(new string[0]);
            A.True(all.Omit == null, "nothing switched off: no list");
            lib.Learn(new[] { all });
            LearnTests.Settle(lib);
            var n = Top(lib, "De pomp loopt koel.");
            A.True(F(n, VaultFields.Project) == "PROJ-002" && F(n, VaultFields.Document) == "manual.docx" && n.Candidate.CreatedBy == "translator", "all on: all stored, as before");
        }

        /// <summary>The choice travels with the unit through the inbox (another Studio may store it, later).</summary>
        public static void TheChoiceTravelsThroughTheInbox()
        {
            var lib = Lib();
            var u = U("Het filter is vuil.", "The filter is dirty.", null, "PROJ-003", "Acme", "translator");
            u.Omit = VaultLearnUnit.OmitOf(new[] { VaultFields.Client });
            var inbox = Path.Combine(lib.Root, "inbox.db");
            Directory.CreateDirectory(lib.Root);
            typeof(VaultLibrary).Assembly.GetType("Supervertaler.Core.SuperVault.VaultInbox")
                .GetMethod("Append").Invoke(null, new object[] { inbox, new List<VaultLearnUnit> { u }, 10 });
            LearnTests.Settle(lib);
            var m = Top(lib, "Het filter is vuil.");
            A.Eq("PROJ-003", F(m, VaultFields.Project), "Project kept");
            A.Eq("", F(m, VaultFields.Client), "Client switched off, after the inbox");
        }

        public static void AnInboxFromAnOlderVersionIsUpgraded()
        {
            var lib = Lib();
            var inbox = Path.Combine(lib.Root, "inbox.db");
            using (var c = new SqliteConnection("Data Source=" + inbox + ";Pooling=False"))
            {
                c.Open();
                Scratch.Exec(c, "CREATE TABLE pending (id INTEGER PRIMARY KEY AUTOINCREMENT, src_lang TEXT, src TEXT, trg_lang TEXT, trg TEXT, origin TEXT)");
                Scratch.Exec(c, "INSERT INTO pending (src_lang, src, trg_lang, trg, origin) VALUES ('nl-NL', 'De deur gaat open.', 'en-GB', 'The door opens.', 'studio')");
            }
            lib.Learn(new[] { U("De deur gaat dicht.", "The door closes.", null, "PROJ-001") });
            LearnTests.Settle(lib);
            A.Eq(2L, lib.StoreCount(), "the old row and the new one stored");
            A.Eq("PROJ-001", F(Top(lib, "De deur gaat dicht."), VaultFields.Project), "the new row's metadata came through");
            A.True(Top(lib, "De deur gaat open.").Candidate.Changed != null, "the old row is dated when stored");
        }

        // ---- Workbench, schema upgrade, export ----

        /// <summary>CAT tools show the progress text as it is, as its own label ("Indexing: 10,000 of 20,000 translation units"): each starts with a capital.</summary>
        public static void ProgressTextsStartWithACapital()
        {
            var lib = Lib();
            var seen = new List<string>();
            var body = string.Concat(Enumerable.Range(0, 20001).Select(i => T("Zin nummer " + i + " van de reeks.", "Sentence number " + i + " of the series.")));
            lib.Import(Tmx(TmxFile(body)), p => seen.Add(p), CancellationToken.None);
            lib.Build(p => seen.Add(p), CancellationToken.None);
            var tmx = Path.Combine(Scratch.Folder("export"), "evertm.tmx");
            VaultExport.Export(lib.StorePath, tmx, null, null, p => seen.Add(p), CancellationToken.None);
            A.True(seen.Any(p => p.StartsWith("Importing acme.tmx: ")) && seen.Any(p => p.StartsWith("Indexing: ")) && seen.Any(p => p.StartsWith("Exporting: ")), "the kinds seen: " + string.Join(" / ", seen.Distinct().Take(8)));
            A.True(seen.All(p => p.Length > 0 && char.IsUpper(p[0])), "all capitalised: " + string.Join(" / ", seen.Where(p => p.Length == 0 || !char.IsUpper(p[0])).Distinct()));
        }

        /// <summary>The import summary counts translation units READ first, so "already in EverTM" can't look like more than EverTM holds.</summary>
        public static void TheImportSummaryStartsWithWhatWasRead()
        {
            string S(long read, long added, long dup, long skipped, long enriched = 0, long split = 0) =>
                new VaultImportResult { Read = read, Added = added, Duplicates = dup, Skipped = skipped, Enriched = enriched, NamesSplit = split }.ToString();
            A.Eq("1,553,018 translation units read: 0 new, all others already in EverTM (1,547,951 of them brought metadata or dates); 7 TM names split into Client and End client",
                 S(1553018, 0, 1553018, 0, 1547951, 7), "nothing new, nothing skipped: \"all others\"");
            A.Eq("1,554,005 translation units read: 0 new, 1,553,018 already in EverTM (1,547,951 of them brought metadata or dates); 987 skipped (no text, one language, or junk); 7 TM names split into Client and End client",
                 S(1554005, 0, 1553018, 987, 1547951, 7), "with skipped ones, the count (\"all others\" would include them)");
            A.Eq("1 translation unit read: 1 new, 0 already in EverTM", S(1, 1, 0, 0), "singular; empty parts left out");
            A.Eq("3 translation units read: 2 new, 1 already in EverTM; 1 TM name split into Client and End client", S(3, 2, 1, 0, 0, 1), "one name");
        }

        public static void TheWindowCountsProjectsClientsAndSubjects()
        {
            var lib = Lib();
            A.True(lib.MetadataSummary() == null, "nothing to say for an empty EverTM");
            Import(lib, TmxFile(T("De pomp start.", "The pump starts.", "", "<prop type=\"Project\">PROJ-001</prop><prop type=\"Project\">PROJ-002</prop><prop type=\"Client\">Acme</prop><prop type=\"Domain\">Patents</prop>")));
            A.Eq("Metadata: 2 projects, 1 client, 1 subject", lib.MetadataSummary(), "the window's line");
        }

        public static void WorkbenchTmNamesBecomeSourceAndSplitNarrowly()
        {
            var db = Scratch.Database(new[] { ("De pomp start.", "The pump starts.", "nl-NL", "en-GB") });
            using (var c = new SqliteConnection("Data Source=" + db + ";Pooling=False"))
            {
                c.Open();
                Scratch.Exec(c, "INSERT INTO translation_units (source_text, target_text, source_lang, target_lang, tm_id, source_hash) VALUES ('De klep sluit.', 'The valve closes.', 'nl-NL', 'en-GB', 'tm2', 'h2')");
                Scratch.Exec(c, "CREATE TABLE translation_memories (tm_id TEXT, name TEXT)");
                Scratch.Exec(c, "INSERT INTO translation_memories VALUES ('test', 'ACME_Globex'), ('tm2', 'Acme general')");
            }
            var lib = Lib();
            var r = lib.Import(Workbench(db), null, CancellationToken.None);
            A.Eq(1L, r.NamesSplit, "one name split: " + r);
            A.True(r.ToString().Contains("1 TM name split"), "said in the summary: " + r);
            var a = Top(lib, "De pomp start.");
            A.Eq("ACME_Globex", F(a, VaultFields.Source), "Source: the TM's name");
            A.Eq("ACME", F(a, VaultFields.Client), "Client");
            A.Eq("Globex", F(a, VaultFields.EndClient), "End client");
            A.True(a.Candidate.Created == null, "Workbench's own dates are not used");
            var b = Top(lib, "De klep sluit.");
            A.Eq("Acme general", F(b, VaultFields.Source), "Source");
            A.Eq("", F(b, VaultFields.Client), "a name not in the CLIENT_Endclient form is not split");
        }

        public static void SplitClientNameIsNarrow()
        {
            void Yes(string name, string client, string end)
            {
                A.True(VaultFields.SplitClientName(name, out var c, out var e), "split: " + name);
                A.Eq(client, c, name + " client"); A.Eq(end, e, name + " end client");
            }
            void No(string name) => A.True(!VaultFields.SplitClientName(name, out _, out _), "not split: " + name);
            Yes("ACME_Globex", "ACME", "Globex");
            Yes("ACME_Globex (PROJ-001)", "ACME", "Globex");
            Yes("A&B-C.2_End client", "A&B-C.2", "End client");
            No("Acme_Globex");
            No("ACME (PROJ-001)");
            No("ACME_");
            No("ACME__Globex");
            No("123_Globex");
            No("my ACME_Globex");
            No("");
        }

        public static void AStage2StoreIsUpgradedInPlace()
        {
            var lib = Lib();
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Pooling=False"))
            {
                c.Open();
                Scratch.Exec(c, "CREATE TABLE units (id INTEGER PRIMARY KEY, lang_a TEXT NOT NULL, text_a TEXT NOT NULL, lang_b TEXT NOT NULL, text_b TEXT NOT NULL, key TEXT NOT NULL UNIQUE, variant TEXT, origin TEXT, added TEXT)");
                Scratch.Exec(c, "CREATE TABLE meta (name TEXT PRIMARY KEY, value TEXT)");
                Scratch.Exec(c, "INSERT INTO meta VALUES ('schema', '2'), ('version', '1')");
                Scratch.Exec(c, "CREATE TABLE changes (seq INTEGER PRIMARY KEY AUTOINCREMENT, unit_id INTEGER NOT NULL)");
                Scratch.Exec(c, "CREATE TABLE learned (src_key TEXT PRIMARY KEY, unit_id INTEGER NOT NULL)");
                Scratch.Exec(c, "INSERT INTO units (lang_a, text_a, lang_b, text_b, key, variant, origin, added) VALUES ('en', 'The pump starts.', 'nl', 'De pomp start.', '" + VaultStore.Key("en", "The pump starts.", "nl", "De pomp start.") + "', 'nl-NL>en-GB', 'workbench', '2026-01-01T00:00:00.0000000Z')");
            }
            VaultStore.Create(lib.StorePath);
            A.Eq("3", (string)Sql(lib, "SELECT value FROM meta WHERE name = 'schema'"), "schema 3");
            A.Eq(1L, lib.StoreCount(), "the unit kept");
            A.True(Sql(lib, "SELECT created FROM units") is DBNull, "with no date");
            var r = Import(lib, TmxFile(T("De pomp start.", "The pump starts.", " creationdate=\"20210101T000000Z\"", "<prop type=\"Project\">PROJ-001</prop>")));
            A.Eq(1L, r.Enriched, "re-import fills it in: " + r);
            A.Eq("PROJ-001", F(Top(lib, "De pomp start."), VaultFields.Project), "and it is shown");
        }

        public static void ExportAndReimportKeepMetadataAndDates()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De pomp start.", "The pump starts.", " creationdate=\"20210101T000000Z\" creationid=\"jan\" changedate=\"20220101T000000Z\" changeid=\"piet\"",
                                  "<prop type=\"Project\">PROJ-001</prop><prop type=\"Client\">Acme</prop><prop type=\"End client\">Globex</prop><note>A note</note>") +
                                T("De klep sluit.", "The valve closes.")));
            var tmx = Path.Combine(Scratch.Folder("export"), "evertm.tmx");
            VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None);
            var copy = Lib();
            var r = Import(copy, tmx);
            A.Eq(2L, r.Added, "both back: " + r);
            var m = Top(copy, "De pomp start.");
            A.Eq("PROJ-001", F(m, VaultFields.Project), "Project");
            A.Eq("Acme", F(m, VaultFields.Client), "Client");
            A.Eq("Globex", F(m, VaultFields.EndClient), "End client");
            A.Eq("A note", F(m, VaultFields.Note), "Note");
            A.Eq("acme.tmx", F(m, VaultFields.Source), "the original Source, not the export's name");
            A.Eq(D(2021), m.Candidate.Created, "created");
            A.Eq(D(2022), m.Candidate.Changed, "changed");
            A.Eq("jan", m.Candidate.CreatedBy, "creationid"); A.Eq("piet", m.Candidate.ChangedBy, "changeid");
            A.True(Top(copy, "De klep sluit.").Candidate.Created == null, "an undated unit comes back undated");
            A.Eq(((string)Sql(lib, "SELECT added FROM units ORDER BY id LIMIT 1")).Substring(0, 19), ((string)Sql(copy, "SELECT added FROM units ORDER BY id LIMIT 1")).Substring(0, 19), "when it entered EverTM, kept (to the second, as TMX dates go)");
        }

        public static void AVersion1ExportIsStillReadAsBefore()
        {
            var body = "<tu srclang=\"nl-NL\" creationdate=\"20261101T100000Z\"><prop type=\"x-EverTM-Origin\">workbench</prop>" +
                       "<tuv xml:lang=\"nl-NL\"><seg>De pomp start.</seg></tuv><tuv xml:lang=\"en-GB\"><seg>The pump starts.</seg></tuv></tu>";
            var path = Path.Combine(Scratch.Folder("v1"), "old-export.tmx");
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header creationtool=\"" + VaultExport.CreationTool + "\" creationtoolversion=\"1\" srclang=\"*all*\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"EverTM\" adminlang=\"en-US\" creationdate=\"20261102T000000Z\"/><body>" + body + "</body></tmx>");
            var lib = Lib();
            Import(lib, path);
            A.True(Top(lib, "De pomp start.").Candidate.Created == null, "its creationdate was the added date, not a translation date");
            A.True(((string)Sql(lib, "SELECT added FROM units")).StartsWith("2026-11-01T10:00:00"), "kept as added");
            A.Eq("workbench", (string)Sql(lib, "SELECT origin FROM units"), "origin kept");
        }

        // ---- failure paths and where lookups read ----

        public static void AFailedImportLeavesNoMetadataBehind()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De pomp start.", "The pump starts.")));
            A.Throws<InvalidOperationException>(() => lib.Import((store, p, c) =>
            {
                using (var w = new VaultStore.Writer(store))
                {
                    var info = new VaultUnitInfo { Created = D(2020) };
                    info.Add(VaultFields.Project, "PROJ-001");
                    w.Add("nl-NL", "De pomp start.", "en-GB", "The pump starts.", "x", null, info);   // merged into the existing unit
                    w.Add("nl-NL", "De klep sluit.", "en-GB", "The valve closes.", "x", null, info);  // a new one
                    throw new InvalidOperationException("the file ends half-way");
                }
            }, null, CancellationToken.None), "the import fails");
            A.Eq(1L, lib.StoreCount(), "no unit added");
            A.Eq(0L, N(lib, "SELECT count(*) FROM vals WHERE value = 'PROJ-001'"), "no value stored");
            A.Eq(0L, N(lib, "SELECT count(*) FROM unit_vals uv JOIN vals v ON v.id = uv.val_id WHERE v.value = 'PROJ-001'"), "no value linked");
            A.True(Sql(lib, "SELECT created FROM units") is DBNull, "the existing unit's date untouched");
        }

        public static void LookupsReadOnlyTheIndex()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De pomp start.", "The pump starts.", " creationdate=\"20210101T000000Z\"", "<prop type=\"Project\">PROJ-001</prop>")));
            lib.Refresh();
            var idx = lib.Current;
            SqliteConnection.ClearAllPools();
            var moved = lib.StorePath + ".away";
            File.Move(lib.StorePath, moved);
            try
            {
                var m = VaultLookup.Search(idx, Doc("De pomp start."), "nl", "en", 50, 5, null, "en-GB");
                A.Eq("PROJ-001", F(m[0], VaultFields.Project), "metadata comes from the index");
                A.Eq(D(2021), m[0].Candidate.Created, "and the date");
            }
            finally { File.Move(moved, lib.StorePath); }
        }

        // ---- the other translations of a match (VaultLibrary.Translations: the pane's "Older translations") ----

        static List<string> Others(VaultLibrary lib, VaultMatch m, int page = 0, string culture = null, string sl = "nl", string tl = "en") =>
            lib.Translations(m, sl, tl, page, culture).Items.Select(c => VaultMarkup.Plain(c.TargetRaw)).ToList();

        public static void TheOtherTranslationsAreListedBestFirstWithoutTheMatchItself()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("Druk op de knop.", "Press the button.", " creationdate=\"20200101T000000Z\"", "<prop type=\"Project\">PROJ-001</prop>") +
                                T("Druk op de knop.", "Push the button.", " changedate=\"20230101T000000Z\"") +
                                T("Druk op de knop.", "Hit the button.") +
                                T("Druk op de knop.", "Press the <bpt i=\"1\">&lt;b&gt;</bpt>button<ept i=\"1\">&lt;/b&gt;</ept>.", " creationdate=\"20190101T000000Z\"")));
            var m = Top(lib, "Druk op de knop.");
            A.Eq("Push the button.", Target(m), "the newest is the match");
            var t = lib.Translations(m, "nl", "en", 0);
            A.Eq(m.Candidate.Older, t.Total, "as many as the match says are hidden");
            A.Eq("Press the button.|Hit the button.", string.Join("|", Others(lib, m)), "newest first, undated last, the match left out, a formatting variant counted once");
            A.Eq("PROJ-001", string.Join("|", t.Items[0].Fields.Where(x => x.Field == "Project").Select(x => x.Value)), "with its own fields");
            A.Eq(D(2020), t.Items[0].Created, "and dates: the best unit of that translation (2020, not the 2019 one with tags)");
            A.Eq("Druk op de knop.", Target(Top(lib, "Press the button.", "en", "nl")), "the reverse direction is untouched");
            A.Eq(0, lib.Translations(Top(lib, "Press the button.", "en", "nl"), "en", "nl", 0).Total, "and has no other translation");
        }

        public static void TheOtherTranslationsOfAFuzzyMatchAreThoseOfItsStoredSource()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De pomp wordt gestart als de druk daalt.", "The pump is started when the pressure drops.", " creationdate=\"20240101T000000Z\"") +
                                T("De pomp wordt gestart als de druk daalt.", "The pump starts when the pressure falls.", " creationdate=\"20190101T000000Z\"")));
            var m = Find(lib, "De pomp wordt gestart als de druk stijgt.").First();
            A.True(m.Score < 100, "a fuzzy match: " + m.Score);
            A.Eq("The pump starts when the pressure falls.", string.Join("|", Others(lib, m)), "the stored source's other translation");
        }

        public static void TheProjectsVariantComesFirstAmongTheOthersToo()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("De kleur van het scherm.", "The colour of the screen.", " creationdate=\"20200101T000000Z\"", "", "en-GB") +
                                T("De kleur van het scherm.", "The color of the display.", " creationdate=\"20240101T000000Z\"", "", "en-US") +
                                T("De kleur van het scherm.", "The colour of the display.", " creationdate=\"20210101T000000Z\"", "", "en-GB")));
            var m = Top(lib, "De kleur van het scherm.", culture: "en-GB");
            A.Eq("The colour of the display.", Target(m), "en-GB project: the newest en-GB one");
            A.Eq("The colour of the screen.|The color of the display.", string.Join("|", Others(lib, m, culture: "en-GB")), "the other en-GB one before the newer en-US one");
        }

        public static void NoOtherTranslationsAndNoEverTmGiveAnEmptyPage()
        {
            var lib = Lib();
            A.Eq(0, lib.Translations(null, "nl", "en", 0).Total, "no match");
            A.Eq(0, lib.Translations(new VaultMatch { Candidate = new VaultCandidate { SourceRaw = "Iets." } }, "nl", "en", 0).Total, "an empty EverTM");
            Import(lib, TmxFile(T("Alleen.", "Alone.")));
            var m = Top(lib, "Alleen.");
            A.Eq(0, lib.Translations(m, "nl", "en", 0).Items.Count, "one translation: no others");
            A.Eq(0, lib.Translations(m, "nl", "en", 7).Items.Count, "a page past the end: empty");
        }

        public static void AnOtherTranslationIsOfferedWithItsTagsPlacedAndNumbersPutRight()
        {
            var lib = Lib();
            Import(lib, TmxFile(T("Druk op Start.", "Press Start.", " creationdate=\"20250101T000000Z\"") +
                                T("Druk op <bpt i=\"1\">&lt;b&gt;</bpt>Start<ept i=\"1\">&lt;/b&gt;</ept>.", "Click <bpt i=\"1\">&lt;b&gt;</bpt>Start<ept i=\"1\">&lt;/b&gt;</ept>.", " creationdate=\"20210101T000000Z\"") +
                                T("Stap 2 is klaar.", "Step 2 is done.", " creationdate=\"20250101T000000Z\"") +
                                T("Stap 2 is klaar.", "Step 2 is finished.", " creationdate=\"20200101T000000Z\"")));
            var doc = new List<VaultPiece>
            {
                new VaultPiece { Text = "Druk op " }, new VaultPiece { Kind = VaultTagKind.Open, Name = "doc", Key = "d1" },
                new VaultPiece { Text = "Start" }, new VaultPiece { Kind = VaultTagKind.Close, Name = "doc", Key = "d1" }, new VaultPiece { Text = "." },
            };
            lib.Refresh();
            var m = lib.Lookup(doc, "nl", "en", 50, 10, "en-GB")[0];
            var other = lib.Translations(m, "nl", "en", 0).Items.Single();
            var o = VaultLookup.Offer(doc, m, other);
            A.Eq("Click Start.", Target(o), "the other translation");
            A.True(o.Tags != VaultTagOutcome.Unmapped, "its bold placed on the document's: " + o.Tags);
            A.Eq(2, o.TargetDocTag.Count(i => i >= 0), "both tags");
            A.Eq(m.Score, o.Score, "the match's score");

            var fuzzyDoc = LibraryTests.Doc("Stap 5 is klaar.");
            var f = lib.Lookup(fuzzyDoc, "nl", "en", 50, 10)[0];
            A.Eq("Step 5 is done.", Target(f), "the match has its number put right");
            var fo = VaultLookup.Offer(fuzzyDoc, f, lib.Translations(f, "nl", "en", 0).Items.Single());
            A.Eq("Step 5 is finished.", Target(fo), "and so does the other translation");
            A.Eq(1, fo.NumbersReplaced, "one number");
            A.Eq(null, VaultLookup.Offer(null, f, other), "no document: nothing");
        }

        /// <summary>"Figure 1" with 5,000 translations: paged, 50 at a time, each page quick.</summary>
        public static void ALargeGroupsOtherTranslationsComeAPageAtATime()
        {
            var lib = Lib();
            const int n = 5000;
            lib.Import((store, p, c) =>
            {
                using (var w = new VaultStore.Writer(store))
                {
                    for (int i = 0; i < n; i++)
                        w.Add("nl-NL", "Figuur 1", "en-GB", "Figure 1 (version " + i + ")", "x", null, new VaultUnitInfo { Created = D(2000).AddDays(i) });
                    w.Commit();
                    return w.Result;
                }
            }, null, CancellationToken.None);
            var m = Top(lib, "Figuur 1");
            A.Eq("Figure 1 (version 4999)", Target(m), "the newest is the match");
            var first = lib.Translations(m, "nl", "en", 0);
            A.Eq(n - 1, first.Total, "all the others");
            A.Eq(VaultLibrary.TranslationsPageSize, first.Items.Count, "one page");
            A.Eq("Figure 1 (version 4998)", VaultMarkup.Plain(first.Items[0].TargetRaw), "newest first");
            A.Eq("Figure 1 (version 4949)", VaultMarkup.Plain(first.Items[49].TargetRaw), "to the 50th");
            A.Eq("Figure 1 (version 4948)", Others(lib, m, 1)[0], "page 2 goes on from there");
            var last = lib.Translations(m, "nl", "en", (n - 1) / VaultLibrary.TranslationsPageSize);
            A.Eq((n - 1) % VaultLibrary.TranslationsPageSize, last.Items.Count, "the last page holds the rest");
            A.Eq("Figure 1 (version 0)", VaultMarkup.Plain(last.Items[last.Items.Count - 1].TargetRaw), "ending with the oldest");
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) lib.Translations(m, "nl", "en", i);
            double ms = sw.Elapsed.TotalMilliseconds / 20;
            Console.WriteLine($"      (a page of {VaultLibrary.TranslationsPageSize} from a group of {n:N0}: {ms:N1} ms)");
            A.True(ms < 100, "under 100 ms a page: " + ms);
        }

        /// <summary>A sentence with thousands of translations ("Figure 1" at 10M units): the exact lookup reads the group from doc values only.</summary>
        public static void ALargeGroupIsDecidedQuickly()
        {
            var lib = Lib();
            const int n = 5000;
            lib.Import((store, p, c) =>
            {
                using (var w = new VaultStore.Writer(store))
                {
                    for (int i = 0; i < n; i++)
                        w.Add("nl-NL", "Figuur 1", "en-GB", "Figure 1 (version " + i + ")", "x", null, new VaultUnitInfo { Created = D(2000).AddDays(i == 1234 ? 9000 : i) });
                    w.Commit();
                    return w.Result;
                }
            }, null, CancellationToken.None);
            A.Eq("Figure 1 (version 1234)", Target(Top(lib, "Figuur 1")), "the newest of 5,000");
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) Find(lib, "Figuur 1");
            double ms = sw.Elapsed.TotalMilliseconds / 50;
            Console.WriteLine($"      (exact lookup in a group of {n:N0}: {ms:N1} ms)");
            A.True(ms < 50, "under 50 ms: " + ms);
            A.Eq(n - 1, Top(lib, "Figuur 1").Candidate.Older, "all others counted as older");
        }
    }
}
