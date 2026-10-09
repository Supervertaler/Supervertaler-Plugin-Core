using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>What a bank-file operation did, or why it did not.</summary>
    public sealed class BankFileResult
    {
        public bool Ok { get; internal set; }
        public string Error { get; internal set; }

        /// <summary>
        /// The file changed since the caller read it, or a file to be created
        /// already exists. <see cref="Content"/> and <see cref="Version"/> are
        /// then the file as it is now, so the caller can merge and try again.
        /// </summary>
        public bool Conflict { get; internal set; }

        /// <summary>The bank's folder name as it is on disk.</summary>
        public string Bank { get; internal set; }

        /// <summary>Bank-relative, with forward slashes: <c>terminology.md</c>, <c>reference/x.md</c>.</summary>
        public string Path { get; internal set; }

        /// <summary>The file's version as it now stands: what was read, or what was written.</summary>
        public string Version { get; internal set; }

        /// <summary>The file's text, for a read and for a conflict.</summary>
        public string Content { get; internal set; }

        public bool Created { get; internal set; }

        /// <summary>The write would not have changed a byte, so nothing was written.</summary>
        public bool Unchanged { get; internal set; }

        /// <summary>Where the previous version was saved, when a write replaced one.</summary>
        public string Backup { get; internal set; }

        /// <summary>The terminology row written, or the one already there.</summary>
        public string Row { get; internal set; }

        /// <summary>The heading a terminology row sits under, or the section written.</summary>
        public string Section { get; internal set; }

        /// <summary>Anything else the caller should pass on, in words.</summary>
        public string Note { get; internal set; }
    }

    /// <summary>
    /// Reads and writes memory-bank files: for an AI agent through the MCP
    /// tools (read_supermemory_file, write_supermemory_file,
    /// update_supermemory_section, append_terminology_row), and for the
    /// plugin's own Quick Add and bank-file editor. One implementation, so the
    /// three cannot disagree about how a bank file is written - and in core,
    /// so the memoQ plugin's tools behave exactly like these.
    ///
    /// <para><b>Every write:</b></para>
    /// <list type="bullet">
    /// <item>Checks a version: a hash of the file's bytes as the caller last
    /// read them. A file changed since - by Obsidian, by a colleague in the team
    /// folder, by the other plugin - is not overwritten; the caller gets the
    /// current content and version back.</item>
    /// <item>Keeps the file's own line endings, final newline and byte-order
    /// mark. The banks disagree - most files CRLF, many LF-only - and a writer
    /// that normalises turns a one-row edit into a whole-file diff.</item>
    /// <item>Saves the previous version to <see cref="BackupsDir"/>, in the
    /// person's own data folder. Never beside the bank file: that would put
    /// backups in Obsidian, in git, and in a team folder everyone shares. One
    /// version per file, overwritten by the next write.</item>
    /// <item>Goes through <see cref="AtomicFile"/>: anyone reading sees the old
    /// file or the new one, never half of either.</item>
    /// </list>
    ///
    /// <para><b>The version check has a gap of milliseconds.</b> Looking at the
    /// file and replacing it are two steps, and a write from another process
    /// that lands between them is overwritten. Closing that would need a lock
    /// file on the share, which goes stale when a computer drops off the network
    /// and then blocks the whole team; the gap was accepted instead (9 Oct
    /// 2026), with the backup as the way back. Inside one process a single lock
    /// closes it: the bridge serves requests on several threads at once.</para>
    /// </summary>
    public sealed class BankFileStore
    {
        /// <summary>The ifVersion that creates a file, and refuses if one is already there.</summary>
        public const string NewFile = "new";

        /// <summary>
        /// The largest file <see cref="Read"/> hands back. The biggest bank file
        /// in real use is about 50 KB; a file ten times that size would already
        /// fill most of an AI client's context, and a larger one is better read
        /// in an editor than poured into a conversation.
        /// </summary>
        internal const int MaxReadBytes = 512 * 1024;

        // One lock for every bank write in the process, not one per file: writes
        // are rare and small, and a single lock cannot deadlock or leak.
        private static readonly object WriteLock = new object();

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly string[] Scopes = { "project", "client", "domain" };

        private readonly string _banksRoot;
        private readonly string _backupsRoot;
        private readonly string _writeRefusal;

        internal BankFileStore(string banksRoot, string backupsRoot, string writeRefusal)
        {
            _banksRoot = banksRoot;
            _backupsRoot = backupsRoot;
            _writeRefusal = writeRefusal;
        }

        /// <summary>Where the previous version of a changed bank file is kept:
        /// <c>&lt;data folder&gt;\backups\memory-banks\&lt;bank&gt;\&lt;file&gt;.bak</c>.
        /// The person's own data folder even when the banks are in a team folder.</summary>
        public static string BackupsDir => System.IO.Path.Combine(SupervertalerPaths.Root, "backups", "memory-banks");

        /// <summary>For the plugin's own UI: the person chose the bank and the file.</summary>
        public static BankFileStore ForUser() =>
            new BankFileStore(SupervertalerPaths.MemoryBanksDir, BackupsDir, null);

        /// <summary>
        /// For an AI agent (the MCP tools). Refuses every write while the team
        /// folder set in config.json is not in use this session: the banks are
        /// then the person's own copies, and a change would land where the rest
        /// of the team never sees it.
        /// </summary>
        public static BankFileStore ForAgent()
        {
            var problem = SupervertalerPaths.TeamFolderProblem;
            return new BankFileStore(SupervertalerPaths.MemoryBanksDir, BackupsDir,
                problem == null ? null
                    : "Memory banks cannot be changed this session. " + problem
                      + " A change now would go into that copy, which the rest of the team never sees.");
        }

        // ── Agent operations ─────────────────────────────────────────────────

        /// <summary>A file at the bank root or in <c>reference/</c>, with its version.</summary>
        public BankFileResult Read(string bank, string path)
        {
            var r = new BankFileResult();
            if (!Resolve(bank, path, false, true, r, out var t)) return r;

            var snap = Snapshot.Take(t.FullPath, r.Path, out var error);
            if (error != null) return Fail(r, error);
            if (!snap.Exists) return Fail(r, NoSuchFile(t, r.Path));
            if (snap.Bytes.Length > MaxReadBytes)
            {
                // The version still comes back: one section of a large file can
                // be changed without pouring the whole file into the conversation.
                r.Version = snap.Version;
                return Fail(r, r.Path + " is " + (snap.Bytes.Length / 1024) + " KB, more than the "
                    + (MaxReadBytes / 1024) + " KB this tool returns. Its version is in 'version', for "
                    + "update_supermemory_section or append_terminology_row; to see it whole, ask the user to open it.");
            }

            r.Ok = true;
            r.Content = snap.Text;
            r.Version = snap.Version;
            return r;
        }

        /// <summary>
        /// Creates (<paramref name="ifVersion"/> = "new") or replaces one file at
        /// the bank root. The content takes on the file's own line endings.
        /// </summary>
        public BankFileResult Write(string bank, string path, string content, string ifVersion, bool allowShared)
        {
            var r = new BankFileResult();
            if (!Resolve(bank, path, true, allowShared, r, out var t)) return r;
            if (string.IsNullOrWhiteSpace(ifVersion)) return Fail(r, NeedVersion);
            if (string.IsNullOrWhiteSpace(content))
                return Fail(r, "'content' is empty. These tools never empty a bank file.");

            lock (WriteLock)
            {
                if (!TakeChecked(t, ifVersion, r, out var snap)) return r;
                return Commit(t, snap, Conform(content, snap.Lines.Newline, snap.Lines.TrailingNewline), r);
            }
        }

        /// <summary>
        /// Replaces the body of one <c>## heading</c> and leaves every other line
        /// of the file exactly as it was. Adds the section at the end of the file
        /// when there is no such heading yet.
        /// </summary>
        public BankFileResult ReplaceSection(string bank, string path, string heading, string content,
            string ifVersion, bool allowShared)
        {
            var r = new BankFileResult();
            if (!Resolve(bank, path, true, allowShared, r, out var t)) return r;
            if (string.IsNullOrWhiteSpace(ifVersion)) return Fail(r, NeedVersion);

            var wanted = (heading ?? "").Trim();
            var hashes = wanted.TakeWhile(c => c == '#').Count();
            if (hashes > 0 && hashes != 2)
                return Fail(r, "Only a level-2 heading (## ...) can be replaced; '" + wanted + "' is level " + hashes + ".");
            wanted = wanted.Substring(hashes).Trim();
            if (wanted.Length == 0) return Fail(r, "Pass 'heading': the text of the ## heading whose section to replace.");
            r.Section = wanted;

            var body = TrimBlankLines(SplitText(content));
            var stray = FindHeadings(body, false).FirstOrDefault(h => h.Level <= 2);
            if (stray != null)
                return Fail(r, "'content' has a heading of its own ('" + body[stray.Index].Trim()
                    + "'), which would split the section in two. Pass only the body; ### subheadings are fine.");

            lock (WriteLock)
            {
                if (!TakeChecked(t, ifVersion, r, out var snap)) return r;
                var doc = snap.Lines;

                var headings = FindHeadings(doc.Text, true);
                var matches = headings
                    .Where(h => h.Level == 2 && string.Equals(h.Text, wanted, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count > 1)
                    return Fail(r, r.Path + " has " + matches.Count + " sections headed '## " + wanted
                        + "', so which one to replace is not clear. Rewrite the file with write_supermemory_file.");

                if (matches.Count == 1)
                {
                    var at = matches[0].Index;
                    var end = headings.Where(h => h.Index > at && h.Level <= 2)
                                      .Select(h => h.Index).DefaultIfEmpty(doc.Count).First();
                    var lines = new List<string> { "" };
                    lines.AddRange(body);
                    if (end < doc.Count) lines.Add("");
                    doc.Splice(at + 1, end - at - 1, lines);
                }
                else
                {
                    var lines = new List<string>();
                    if (doc.Count > 0 && doc.Text[doc.Count - 1].Trim().Length > 0) lines.Add("");
                    lines.Add("## " + wanted);
                    lines.Add("");
                    lines.AddRange(body);
                    doc.Splice(doc.Count, 0, lines);
                    r.Note = "There was no section headed '## " + wanted + "', so it was added at the end of the file.";
                }

                return Commit(t, snap, doc.Render(), r);
            }
        }

        /// <summary>
        /// Adds one row to <c>terminology.md</c>: to the first table with a Scope
        /// column, so a row never lands in a table of rejected variants or TM
        /// comparisons further down. Refuses a source term that any table in the
        /// file already has, and hands back that row: one row per decision.
        /// </summary>
        public BankFileResult AppendTerminologyRow(string bank, string source, string target, string scope,
            string note, bool allowShared)
        {
            var r = new BankFileResult();
            if (!Resolve(bank, MemoryBankReader.TerminologyFile, true, allowShared, r, out var t)) return r;

            source = Cell(source);
            target = Cell(target);
            note = Cell(note);
            var sc = (scope ?? "").Trim().ToLowerInvariant();
            if (source.Length == 0 || target.Length == 0) return Fail(r, "Pass both 'source' and 'target'.");
            if (Array.IndexOf(Scopes, sc) < 0)
                return Fail(r, "'scope' must be project, client or domain, not '" + scope + "'.");

            lock (WriteLock)
            {
                var snap = Snapshot.Take(t.FullPath, r.Path, out var error);
                if (error != null) return Fail(r, error);
                r.Version = snap.Version;

                // A bank without the file gets the skeleton every new bank starts
                // with, whose one table is exactly where the row belongs.
                var doc = snap.Exists
                    ? snap.Lines
                    : Lines.Parse(MemoryBanks.SkeletonBody(MemoryBankReader.TerminologyFile, t.BankName));

                var tables = FindTables(doc);
                var key = Key(source);
                foreach (var table in tables)
                {
                    for (int i = table.Header + 2; i <= table.LastRow; i++)
                    {
                        var cells = SplitRow(doc.Text[i]);
                        if (cells.Count == 0 || !string.Equals(Key(cells[0]), key, StringComparison.OrdinalIgnoreCase))
                            continue;

                        r.Row = doc.Text[i].Trim();
                        r.Section = table.Heading;
                        return Fail(r, "'" + source + "' is already in terminology.md"
                            + (table.Heading != null ? ", under '" + table.Heading + "'" : "") + ": " + r.Row
                            + "  One row per decision: to change it, edit that row instead of adding another.");
                    }
                }

                var into = tables.FirstOrDefault(x => x.ScopeColumn >= 2);
                if (into == null) return Fail(r, NoScopeTable(doc, tables));
                if (note.Length > 0 && into.NoteColumn < 0)
                    return Fail(r, "The table" + (into.Heading != null ? " under '" + into.Heading + "'" : "")
                        + " has no column for a note. Leave 'note' out, or add the row by hand.");

                var row = Enumerable.Repeat("", into.Columns.Count).ToArray();
                row[0] = source;
                row[1] = target;
                row[into.ScopeColumn] = sc;
                if (into.NoteColumn >= 0) row[into.NoteColumn] = note;
                var line = "| " + string.Join(" | ", row) + " |";

                // The skeleton ships an empty placeholder row: fill it, rather than
                // leave a blank row in the middle of the table.
                var last = into.LastRow;
                if (last > into.Header + 1 && SplitRow(doc.Text[last]).All(c => c.Length == 0))
                    doc.Splice(last, 1, new[] { line });
                else
                    doc.Splice(last + 1, 0, new[] { line });

                r.Row = line;
                r.Section = into.Heading;
                return Commit(t, snap, doc.Render(), r);
            }
        }

        // ── The plugin's bank-file editor ────────────────────────────────────

        /// <summary>Any file inside a bank, <c>reference/</c> included, with its version.</summary>
        public BankFileResult Load(string fullPath)
        {
            var r = new BankFileResult();
            if (!Locate(fullPath, r, out var t)) return r;

            var snap = Snapshot.Take(t.FullPath, r.Path, out var error);
            if (error != null) return Fail(r, error);
            if (!snap.Exists) return Fail(r, r.Path + " no longer exists.");

            r.Ok = true;
            r.Content = snap.Text;
            r.Version = snap.Version;
            return r;
        }

        /// <summary>
        /// Replaces a file the person has open in the editor, with the same
        /// version check, backup and care for line endings as the agent tools -
        /// but none of their rules about where in the bank, because the person
        /// chose the file.
        /// </summary>
        public BankFileResult Save(string fullPath, string content, string ifVersion)
        {
            var r = new BankFileResult();
            if (!Locate(fullPath, r, out var t)) return r;
            if (string.IsNullOrWhiteSpace(ifVersion)) return Fail(r, NeedVersion);

            lock (WriteLock)
            {
                if (!TakeChecked(t, ifVersion, r, out var snap)) return r;
                return Commit(t, snap, Conform(content ?? "", snap.Lines.Newline, snap.Lines.TrailingNewline), r);
            }
        }

        // ── Where ────────────────────────────────────────────────────────────

        private sealed class Target
        {
            public string BankName;
            public string FullPath;
            public string BackupPath;
        }

        private const string NeedVersion =
            "Pass 'ifVersion': the version read_supermemory_file returned, or \"new\" to create a file. "
            + "Nothing is overwritten without one.";

        private bool Resolve(string bank, string path, bool write, bool allowShared, BankFileResult r, out Target t)
        {
            t = null;
            if (write && _writeRefusal != null) { Fail(r, _writeRefusal); return false; }

            var wanted = (bank ?? "").Trim();
            if (wanted.Length == 0)
            {
                Fail(r, "Name the bank in 'bank' (see list_supermemory_banks). It never defaults to the active bank.");
                return false;
            }

            // An exact folder name, never a sanitised guess: a name that resolves
            // to a different bank than the one asked for writes one client's
            // decisions into another's.
            var banks = MemoryBanks.List(_banksRoot);
            var name = banks.FirstOrDefault(b => string.Equals(b, wanted, StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                Fail(r, "No memory bank named '" + wanted + "'. Banks: "
                    + (banks.Count > 0 ? string.Join(", ", banks) : "(none)")
                    + ". These tools do not create banks.");
                return false;
            }
            r.Bank = name;

            if (write && MemoryBanks.IsSharedName(name) && !allowShared)
            {
                Fail(r, "'" + name + "' holds the house defaults loaded under every bank, so writing to it "
                    + "needs allowShared: true. Ask the user first.");
                return false;
            }

            var problem = CheckPath(path, write, out var rel);
            if (problem != null) { Fail(r, problem); return false; }
            r.Path = rel;

            var bankDir = System.IO.Path.Combine(_banksRoot, name);
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(bankDir, rel.Replace('/', '\\')));
            if (!full.StartsWith(System.IO.Path.GetFullPath(bankDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Fail(r, "'" + path + "' is not inside the bank.");
                return false;
            }

            t = new Target
            {
                BankName = name,
                FullPath = full,
                BackupPath = System.IO.Path.Combine(_backupsRoot, name, rel.Replace('/', '\\')) + ".bak",
            };
            return true;
        }

        /// <summary>
        /// A bank-relative path, or why not. Reads may reach <c>reference/</c>;
        /// writes go to the bank root only, which is all a prompt ever reads, so
        /// a file anywhere else would be knowledge nothing uses.
        /// </summary>
        internal static string CheckPath(string path, bool write, out string rel)
        {
            rel = null;
            var p = (path ?? "").Trim().Replace('\\', '/');
            if (p.Length == 0) return "Pass 'path': a file name such as terminology.md.";
            if (p.StartsWith("/") || p.IndexOf(':') >= 0 || p.StartsWith("~"))
                return "'" + path + "' is not a path inside the bank. Use a name such as terminology.md.";

            var parts = p.Split('/');
            if (parts.Any(s => s.Length == 0 || s == "." || s == ".."))
                return "'" + path + "' is not a plain path inside the bank. Use a name such as terminology.md.";
            if (!p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                return "Only Markdown (.md) files can be " + (write ? "written" : "read") + " here; '" + path + "' is not one.";

            var name = parts[parts.Length - 1];
            if (name.StartsWith(".") || name.Length <= 3 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                return "'" + name + "' is not a usable file name.";

            var inReference = parts.Length == 2 &&
                string.Equals(parts[0], MemoryBankReader.ReferenceFolder, StringComparison.OrdinalIgnoreCase);
            if (write && inReference)
                return MemoryBankReader.ReferenceFolder + "/ holds unmodified source material and is never written by these tools.";
            if (parts.Length > 1 && !inReference)
                return write
                    ? "Write files at the bank root: only those are read into prompts, so a file in a folder would never be seen."
                    : "Only files at the bank root and in " + MemoryBankReader.ReferenceFolder + "/ can be read.";

            rel = inReference ? MemoryBankReader.ReferenceFolder + "/" + name : name;
            return null;
        }

        /// <summary>The bank and bank-relative path of a file the editor opened.</summary>
        private bool Locate(string fullPath, BankFileResult r, out Target t)
        {
            t = null;
            string full, root;
            try
            {
                full = System.IO.Path.GetFullPath(fullPath ?? "");
                root = System.IO.Path.GetFullPath(_banksRoot).TrimEnd('\\') + "\\";
            }
            catch (Exception ex) { Fail(r, "Not a usable path: " + ex.Message); return false; }

            var slash = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.IndexOf('\\', root.Length) : -1;
            if (slash <= root.Length)
            {
                Fail(r, fullPath + " is not a file inside a memory bank.");
                return false;
            }

            var bank = full.Substring(root.Length, slash - root.Length);
            var rel = full.Substring(slash + 1);
            r.Bank = bank;
            r.Path = rel.Replace('\\', '/');
            t = new Target
            {
                BankName = bank,
                FullPath = full,
                BackupPath = System.IO.Path.Combine(_backupsRoot, bank, rel) + ".bak",
            };
            return true;
        }

        private static string NoSuchFile(Target t, string path)
        {
            string files = "";
            try
            {
                var dir = System.IO.Path.GetDirectoryName(t.FullPath);
                if (Directory.Exists(dir))
                    files = string.Join(", ", Directory.GetFiles(dir, "*.md")
                        .Select(System.IO.Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch { /* the list is a courtesy */ }
            return "Bank '" + t.BankName + "' has no " + path + "."
                + (files.Length > 0 ? " Files in that folder: " + files + "." : "");
        }

        // ── Version check and write ──────────────────────────────────────────

        /// <summary>Takes the file as it is and checks it against the caller's version.</summary>
        private static bool TakeChecked(Target t, string ifVersion, BankFileResult r, out Snapshot snap)
        {
            snap = Snapshot.Take(t.FullPath, r.Path, out var error);
            if (error != null) { Fail(r, error); return false; }

            var wanted = ifVersion.Trim();
            if (string.Equals(wanted, NewFile, StringComparison.OrdinalIgnoreCase))
            {
                if (!snap.Exists) return true;
                Conflict(r, snap, r.Path + " already exists.");
                return false;
            }

            if (!snap.Exists)
            {
                Fail(r, r.Path + " does not exist (any more). Pass ifVersion \"new\" to create it.");
                r.Conflict = true;
                return false;
            }

            if (string.Equals(wanted, snap.Version, StringComparison.OrdinalIgnoreCase)) return true;
            Conflict(r, snap, r.Path + " has changed since version " + wanted + " was read.");
            return false;
        }

        private BankFileResult Commit(Target t, Snapshot before, string newText, BankFileResult r)
        {
            var bytes = Encode(newText, before.Bom);
            if (before.Exists && bytes.SequenceEqual(before.Bytes))
            {
                r.Ok = true;
                r.Unchanged = true;
                r.Version = before.Version;
                r.Note = "Nothing to change: the file already reads exactly like that.";
                return r;
            }

            var log = new List<string>();

            // The backup first, and no write without it: a change that cannot be
            // undone is not made.
            if (before.Exists &&
                AtomicFile.Write(t.BackupPath, before.Bytes, true, log.Add) != AtomicFile.Outcome.Written)
            {
                return Fail(r, "The previous version could not be saved to " + t.BackupPath
                    + ", so nothing was written. " + string.Join(" ", log));
            }

            AtomicFile.SweepAbandoned(t.FullPath, TimeSpan.FromMinutes(10));
            switch (AtomicFile.Write(t.FullPath, bytes, before.Exists, log.Add))
            {
                case AtomicFile.Outcome.Written:
                    r.Ok = true;
                    r.Version = VersionOf(bytes);
                    r.Created = !before.Exists;
                    r.Backup = before.Exists ? t.BackupPath : null;
                    return r;

                case AtomicFile.Outcome.AlreadyExists:
                    var now = Snapshot.Take(t.FullPath, r.Path, out var error);
                    if (error != null || !now.Exists) return Fail(r, r.Path + " appeared while it was being created; try again.");
                    return Conflict(r, now, r.Path + " was created by someone else a moment ago.");

                default:
                    return Fail(r, (log.Count > 0 ? string.Join(" ", log) : r.Path + " was not written.")
                        + " If it is open in another program, close it and try again.");
            }
        }

        private static BankFileResult Fail(BankFileResult r, string error)
        {
            r.Ok = false;
            r.Error = error;
            return r;
        }

        private static BankFileResult Conflict(BankFileResult r, Snapshot now, string why)
        {
            r.Ok = false;
            r.Conflict = true;
            r.Content = now.Text;
            r.Version = now.Version;
            r.Error = why + " Nothing was written. Its current content is in 'content': merge your change "
                    + "into that, then call again with ifVersion \"" + now.Version + "\".";
            return r;
        }

        /// <summary>A short hash of the file's exact bytes: a version for change
        /// detection, not a security measure.</summary>
        internal static string VersionOf(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static byte[] Encode(string text, bool bom)
        {
            var body = Utf8NoBom.GetBytes(text);
            if (!bom) return body;
            var all = new byte[body.Length + 3];
            all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
            Buffer.BlockCopy(body, 0, all, 3, body.Length);
            return all;
        }

        /// <summary>
        /// Whole-file content in the file's own conventions: its newline, and a
        /// final newline exactly when the file had one (a new file gets one).
        /// </summary>
        private static string Conform(string content, string newline, bool trailingNewline)
        {
            var s = content.Replace("\r\n", "\n").Replace('\r', '\n');
            if (trailingNewline && !s.EndsWith("\n")) s += "\n";
            if (!trailingNewline) s = s.TrimEnd('\n');
            return newline == "\n" ? s : s.Replace("\n", newline);
        }

        /// <summary>A file as read: bytes, version, and its text split into lines.</summary>
        private sealed class Snapshot
        {
            public bool Exists;
            public byte[] Bytes;
            public bool Bom;
            public string Text;
            public string Version;
            public Lines Lines = new Lines();

            /// <summary>Null and an error when the file is there but unusable;
            /// a snapshot with Exists false when it is not there.</summary>
            public static Snapshot Take(string fullPath, string shownAs, out string error)
            {
                error = null;
                byte[] bytes;
                try { bytes = AtomicFile.ReadAllBytes(fullPath); }
                catch (FileNotFoundException) { return new Snapshot(); }
                catch (DirectoryNotFoundException) { return new Snapshot(); }
                catch (Exception ex)
                {
                    error = shownAs + " could not be read: " + ex.Message;
                    return null;
                }

                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text;
                try
                {
                    if (!bom && bytes.Length >= 2 &&
                        ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                        throw new DecoderFallbackException();
                    text = bom ? StrictUtf8.GetString(bytes, 3, bytes.Length - 3) : StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    error = shownAs + " is not UTF-8 text, so it is left as it is: rewriting it here would "
                          + "garble every character outside UTF-8. Save it as UTF-8 in an editor first.";
                    return null;
                }

                return new Snapshot
                {
                    Exists = true,
                    Bytes = bytes,
                    Bom = bom,
                    Text = text,
                    Version = VersionOf(bytes),
                    Lines = Lines.Parse(text),
                };
            }
        }

        // ── Lines ────────────────────────────────────────────────────────────

        /// <summary>
        /// A file's lines, each with its own line ending, so an edit touches only
        /// the lines it changes: every other line goes back byte for byte, even
        /// in a file whose endings are mixed.
        /// </summary>
        private sealed class Lines
        {
            public readonly List<string> Text = new List<string>();
            public readonly List<string> Ends = new List<string>();

            /// <summary>The ending new lines get: whichever the file uses most.</summary>
            public string Newline = "\r\n";

            public int Count => Text.Count;

            /// <summary>Whether the file ends with a line break. An empty file counts as
            /// one that does, so a file created from nothing gets one.</summary>
            public bool TrailingNewline => Count == 0 || Ends[Count - 1].Length > 0;

            public static Lines Parse(string s)
            {
                var l = new Lines();
                int i = 0, crlf = 0, lf = 0;
                while (i < s.Length)
                {
                    var j = s.IndexOf('\n', i);
                    if (j < 0)
                    {
                        l.Text.Add(s.Substring(i));
                        l.Ends.Add("");
                        break;
                    }
                    if (j > i && s[j - 1] == '\r')
                    {
                        l.Text.Add(s.Substring(i, j - 1 - i));
                        l.Ends.Add("\r\n");
                        crlf++;
                    }
                    else
                    {
                        l.Text.Add(s.Substring(i, j - i));
                        l.Ends.Add("\n");
                        lf++;
                    }
                    i = j + 1;
                }

                // Counted rather than taken from the first line: a file edited by
                // two tools can be mixed, and the majority is the safer guess. A
                // file with no line break yet gets CRLF, like the bank skeletons.
                l.Newline = crlf > 0 && crlf >= lf ? "\r\n" : lf > 0 ? "\n" : "\r\n";
                return l;
            }

            public string Render()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Count; i++) sb.Append(Text[i]).Append(Ends[i]);
                return sb.ToString();
            }

            /// <summary>
            /// Replaces <paramref name="count"/> lines at <paramref name="start"/>
            /// with <paramref name="lines"/>, which get the file's newline. The
            /// file keeps, or keeps lacking, its final line break.
            /// </summary>
            public void Splice(int start, int count, IList<string> lines)
            {
                var trailing = TrailingNewline;
                Text.RemoveRange(start, count);
                Ends.RemoveRange(start, count);
                Text.InsertRange(start, lines);
                Ends.InsertRange(start, lines.Select(_ => Newline));

                for (int i = 0; i < Count - 1; i++)
                    if (Ends[i].Length == 0) Ends[i] = Newline;
                if (Count > 0)
                    Ends[Count - 1] = !trailing ? "" : Ends[Count - 1].Length > 0 ? Ends[Count - 1] : Newline;
            }
        }

        private static List<string> SplitText(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        private static List<string> TrimBlankLines(List<string> lines)
        {
            int a = 0, b = lines.Count;
            while (a < b && lines[a].Trim().Length == 0) a++;
            while (b > a && lines[b - 1].Trim().Length == 0) b--;
            return lines.GetRange(a, b - a);
        }

        // ── Markdown structure ───────────────────────────────────────────────
        //
        // Only as much as these operations need: ATX headings, pipe tables, and
        // enough of code fences and frontmatter not to mistake their contents for
        // either. Nothing is reformatted; lines are only located.

        private sealed class Heading
        {
            public int Index;
            public int Level;
            public string Text;
        }

        private static List<Heading> FindHeadings(IList<string> lines, bool frontmatter)
        {
            var found = new List<Heading>();
            string fence = null;
            for (int i = frontmatter ? FrontmatterEnd(lines) : 0; i < lines.Count; i++)
            {
                if (InFence(lines[i], ref fence)) continue;
                var level = HeadingLevel(lines[i], out var text);
                if (level > 0) found.Add(new Heading { Index = i, Level = level, Text = text });
            }
            return found;
        }

        /// <summary>The first line after a YAML frontmatter block, or 0 when there is none.</summary>
        private static int FrontmatterEnd(IList<string> lines)
        {
            if (lines.Count == 0 || lines[0].Trim() != "---") return 0;
            for (int i = 1; i < lines.Count; i++)
                if (lines[i].Trim() == "---" || lines[i].Trim() == "...") return i + 1;
            return 0;
        }

        /// <summary>True for a fence line or a line inside a fenced code block.</summary>
        private static bool InFence(string line, ref string fence)
        {
            var t = line.TrimStart();
            if (fence != null)
            {
                if (t.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                return true;
            }
            if (t.StartsWith("```", StringComparison.Ordinal)) { fence = "```"; return true; }
            if (t.StartsWith("~~~", StringComparison.Ordinal)) { fence = "~~~"; return true; }
            return false;
        }

        /// <summary>1-6 for an ATX heading, 0 for anything else.</summary>
        private static int HeadingLevel(string line, out string text)
        {
            text = null;
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            int h = 0;
            while (i + h < line.Length && line[i + h] == '#') h++;
            if (h == 0 || h > 6) return 0;
            var rest = i + h;
            if (rest < line.Length && line[rest] != ' ' && line[rest] != '\t') return 0;

            // A closing run of #s is decoration only when a space separates it,
            // so "## C#" keeps its #.
            var t = line.Substring(rest).Trim();
            var k = t.Length;
            while (k > 0 && t[k - 1] == '#') k--;
            if (k == 0) t = "";
            else if (k < t.Length && (t[k - 1] == ' ' || t[k - 1] == '\t')) t = t.Substring(0, k).Trim();
            text = t;
            return h;
        }

        private sealed class Table
        {
            public int Header;
            /// <summary>The last row; the separator line when there are none.</summary>
            public int LastRow;
            public List<string> Columns;
            public string Heading;
            public int ScopeColumn;
            public int NoteColumn;
        }

        private static List<Table> FindTables(Lines doc)
        {
            var tables = new List<Table>();
            string heading = null, fence = null;
            for (int i = FrontmatterEnd(doc.Text); i < doc.Count; i++)
            {
                var line = doc.Text[i];
                if (InFence(line, ref fence)) continue;
                if (HeadingLevel(line, out var text) > 0) { heading = text; continue; }
                if (!IsRow(line) || i + 1 >= doc.Count || !IsSeparator(doc.Text[i + 1])) continue;

                var columns = SplitRow(line).Select(Key).ToList();
                var table = new Table { Header = i, LastRow = i + 1, Columns = columns, Heading = heading };
                for (int j = i + 2; j < doc.Count && IsRow(doc.Text[j]); j++) table.LastRow = j;

                table.ScopeColumn = columns.FindIndex(c => c.Equals("scope", StringComparison.OrdinalIgnoreCase));
                table.NoteColumn = columns.FindIndex(c =>
                    c.Equals("note", StringComparison.OrdinalIgnoreCase) || c.Equals("notes", StringComparison.OrdinalIgnoreCase));
                // "Why it is here and not just in the termbase" is a note column
                // too: the last one, when it is not source, target or scope.
                if (table.NoteColumn < 0 && columns.Count - 1 > Math.Max(1, table.ScopeColumn))
                    table.NoteColumn = columns.Count - 1;

                tables.Add(table);
                i = table.LastRow;
            }
            return tables;
        }

        private static bool IsRow(string line) => line.TrimStart().StartsWith("|", StringComparison.Ordinal);

        private static bool IsSeparator(string line)
        {
            var t = line.Trim();
            return t.StartsWith("|", StringComparison.Ordinal) && t.IndexOf('-') >= 0
                && t.All(c => c == '|' || c == '-' || c == ':' || c == ' ' || c == '\t');
        }

        /// <summary>A row's cells, trimmed, with escaped pipes left escaped.</summary>
        private static List<string> SplitRow(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal) && !t.EndsWith("\\|", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 1);

            var cells = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append("\\|"); i++; continue; }
                if (t[i] == '|') { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(t[i]);
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        /// <summary>A cell as compared: unescaped, without emphasis around it, composed.</summary>
        private static string Key(string cell) =>
            (cell ?? "").Replace("\\|", "|").Trim().Trim('*', '_', '`').Trim().Normalize(NormalizationForm.FormC);

        /// <summary>Text made safe for one table cell: a raw pipe would end the cell
        /// early and shift every column after it, and a line break would end the row.</summary>
        private static string Cell(string text) =>
            (text ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

        private static string NoScopeTable(Lines doc, List<Table> tables)
        {
            var sb = new StringBuilder("terminology.md has no table with a Scope column, so there is no table this row clearly belongs in.");
            if (tables.Count == 0)
            {
                sb.Append(" It has no table at all.");
                var headings = FindHeadings(doc.Text, true).Select(h => h.Text).Where(h => h.Length > 0).ToList();
                if (headings.Count > 0) sb.Append(" Its headings: ").Append(string.Join("; ", headings)).Append('.');
            }
            else
            {
                sb.Append(" Its tables: ").Append(string.Join("; ", tables.Select(x =>
                    (x.Heading != null ? "under '" + x.Heading + "' " : "") + "(" + string.Join(" | ", x.Columns) + ")"))).Append('.');
            }
            sb.Append(" Add the row by hand where it belongs, or give that table a Scope column.");
            return sb.ToString();
        }
    }
}
