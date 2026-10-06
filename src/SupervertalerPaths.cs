using System;
using System.IO;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// The one folder every Supervertaler product shares.
    ///
    /// Extracted from the Trados plugin's <c>UserDataPath</c>, which is 1,400
    /// lines of paths specific to that plugin — settings, termbases, memory banks,
    /// runtime handshake files — and does not belong here. Only the shared root
    /// does: it is resolved from a pointer file that Supervertaler Workbench also
    /// writes, so a user who moved their data folder once has moved it for
    /// everything, and a <c>pricing.json</c> dropped there re-prices every product
    /// at once.
    ///
    /// The Trados plugin's <c>UserDataPath.Root</c> delegates here rather than
    /// keeping its own copy, so the two can never disagree about where a user's
    /// data lives.
    /// </summary>
    public static class SupervertalerPaths
    {
        private static string _root;
        private static readonly object _lock = new object();

        /// <summary>
        /// Pointer file, shared with Supervertaler Workbench:
        /// <c>%APPDATA%\Supervertaler\config.json</c>.
        /// </summary>
        private static string ConfigFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Supervertaler", "config.json");

        /// <summary>Where the data folder lives when nothing has pointed elsewhere.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Supervertaler");

        /// <summary>
        /// Root of the shared Supervertaler user-data folder: whatever
        /// <c>config.json</c> points at, or <see cref="DefaultRoot"/>.
        ///
        /// Cached after the first read. A user who relocates their data folder
        /// mid-session is not a case worth re-statting the disk for on every
        /// lookup; <see cref="Reset"/> exists for the code that does the moving.
        /// </summary>
        public static string Root
        {
            get
            {
                lock (_lock)
                {
                    if (_root == null) _root = Resolve();
                    return _root;
                }
            }
        }

        /// <summary>
        /// Prompt library: <c>.md</c> files with YAML frontmatter, one per prompt,
        /// under the shared root - or in the team folder when one is set.
        ///
        /// Shared deliberately and from the start — Workbench and the Trados
        /// plugin already read the same folder, and the memoQ plugin now makes
        /// three. A plain folder of Markdown is also why a prompt picker can be
        /// written in something other than C# without agreeing a format first.
        /// </summary>
        public static string PromptLibraryDir => Path.Combine(ContentRoot, "prompt_library");

        /// <summary>Memory banks: one folder per bank, plus <c>_shared</c>. In the
        /// team folder when one is set.</summary>
        public static string MemoryBanksDir => Path.Combine(ContentRoot, "memory-banks");

        /// <summary>Shared resources folder (the Supervertaler database lives here).</summary>
        public static string ResourcesDir => Path.Combine(Root, "resources");

        // ── Team folder ──────────────────────────────────────────────────────
        //
        // A team shares memory banks and prompts by pointing everyone's team
        // folder at the same place. Only those two live there: the licence,
        // settings, API keys (encrypted for one Windows user), the termbase
        // database (SQLite, not for several writers on a network share), logs and
        // usage stay in each person's own data folder. Set in config.json as
        // "team_folder", next to "user_data_path", so every product on the
        // computer follows it. Read once per session, like the root: switching
        // halfway would leave parts of a product on the old banks.

        private static string _contentRoot;
        private static string _teamFolder;
        private static string _teamFolderProblem;

        /// <summary>How long a team folder on a network share gets to answer
        /// before the session falls back to the user's own data folder. A dead
        /// share can otherwise hold a file check for tens of seconds.</summary>
        internal static readonly TimeSpan TeamFolderTimeout = TimeSpan.FromSeconds(3);

        /// <summary>The team folder set in config.json, or null when none is.</summary>
        public static string TeamFolder
        {
            get { lock (_lock) { EnsureContentRoot(); return _teamFolder; } }
        }

        /// <summary>Why the team folder set in config.json is not in use this
        /// session (unreachable, missing), or null when it is in use or none is
        /// set. Show it: falling back without saying so would hand the AI the
        /// user's own banks while they believe they are using the team's.</summary>
        public static string TeamFolderProblem
        {
            get { lock (_lock) { EnsureContentRoot(); return _teamFolderProblem; } }
        }

        /// <summary>Where memory banks and the prompt library live this session:
        /// the team folder when one is set and answers, otherwise <see cref="Root"/>.</summary>
        public static string ContentRoot
        {
            get { lock (_lock) { EnsureContentRoot(); return _contentRoot; } }
        }

        private static void EnsureContentRoot()
        {
            if (_contentRoot != null) return;
            string configured = null;
            try
            {
                if (File.Exists(ConfigFile))
                    configured = ExtractJsonString(File.ReadAllText(ConfigFile, Encoding.UTF8), "team_folder");
            }
            catch { configured = null; }

            var decided = DecideContentRoot(Root, configured, FolderAnswers);
            _contentRoot = decided.ContentRoot;
            _teamFolder = decided.TeamFolder;
            _teamFolderProblem = decided.Problem;
        }

        /// <summary>The decision on its own, for tests: which folder banks and
        /// prompts come from, given the root, the configured team folder (null or
        /// blank = none) and a way to ask whether a folder exists.</summary>
        internal static (string ContentRoot, string TeamFolder, string Problem) DecideContentRoot(
            string root, string configuredTeamFolder, Func<string, bool?> folderExists)
        {
            var team = string.IsNullOrWhiteSpace(configuredTeamFolder) ? null : configuredTeamFolder.Trim();
            if (team == null) return (root, null, null);
            if (!Path.IsPathRooted(team))
                return (root, team, "The team folder \"" + team + "\" is not a full path, so your own data folder is used.");
            var exists = folderExists(team);
            if (exists == true) return (team, team, null);
            return (root, team, exists == null
                ? "The team folder \"" + team + "\" did not answer within " + (int)TeamFolderTimeout.TotalSeconds +
                  " seconds, so your own data folder is used until Trados Studio is restarted."
                : "The team folder \"" + team + "\" cannot be found, so your own data folder is used until Trados Studio is restarted.");
        }

        /// <summary>True or false, or null when the folder did not answer in time.</summary>
        private static bool? FolderAnswers(string path)
        {
            try
            {
                var check = System.Threading.Tasks.Task.Run(() => Directory.Exists(path));
                return check.Wait(TeamFolderTimeout) ? check.Result : (bool?)null;
            }
            catch { return false; }
        }

        /// <summary>Forgets the cached root and team folder. For code that has just relocated the folder.</summary>
        public static void Reset()
        {
            lock (_lock) { _root = null; _contentRoot = null; _teamFolder = null; _teamFolderProblem = null; }
        }

        /// <summary>Overrides the root, for a caller that has just chosen or moved it.</summary>
        public static void Set(string path)
        {
            lock (_lock) { _root = path; _contentRoot = null; }
        }

        private static string Resolve()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile, Encoding.UTF8);
                    var path = ExtractJsonString(json, "user_data_path");
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch
            {
                // An unreadable or malformed pointer falls back to the default
                // rather than failing: losing a custom location costs the user a
                // re-pick, whereas throwing here would take down whatever asked.
            }

            return DefaultRoot;
        }

        /// <summary>
        /// Pulls one string value out of a flat JSON file.
        ///
        /// Hand-rolled rather than pulled from a serializer: this runs before
        /// anything else is initialised, in two different plugin sandboxes, and
        /// the file has two flat string keys worth reading. Carried across from the
        /// Trados implementation unchanged so the two cannot diverge on a
        /// malformed file.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            var searchKey = "\"" + key + "\"";
            var idx = json.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var valStart = json.IndexOf('"', idx + searchKey.Length + 1);
            if (valStart < 0) return null;

            var valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return null;

            return json.Substring(valStart + 1, valEnd - valStart - 1).Replace("\\\\", "\\");
        }
    }
}
