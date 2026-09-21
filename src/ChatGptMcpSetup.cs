using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Supervertaler.Core
{
    /// <summary>
    /// One-click setup of the Supervertaler MCP server for ChatGPT desktop.
    ///
    /// <para>Claude Desktop takes a <c>.mcpb</c> bundle and installs itself.
    /// ChatGPT has no equivalent - its bundles go through marketplaces, which is
    /// more work for the user than a config edit rather than less - so the
    /// product does the edit instead: fetch the server, put it somewhere
    /// permanent, and register it in the TOML file ChatGPT desktop shares with
    /// Codex CLI and the Codex IDE extension. One entry serves all three.</para>
    ///
    /// <para>This writes to another application's configuration file. So it backs
    /// the file up first, only ever touches its own named block, and passes
    /// everything else through untouched - including the other Supervertaler
    /// product's block, which lives in the same file under a different name.
    /// Both registered at once is the normal case for someone who owns both, and
    /// each product's tools then appear under its own name.</para>
    ///
    /// <para>Shared rather than copied because the fiddly parts - the staged
    /// install, the sweep, the text-level TOML edit - are exactly where two
    /// copies drift silently, and the drift shows up as a broken install on
    /// someone else's machine.</para>
    /// </summary>
    public static class ChatGptMcpSetup
    {
        /// <summary>The server executable, inside the release asset and on disk.</summary>
        private const string ExeName = "SupervertalerMcpServer.exe";

        /// <summary>
        /// The release asset holding the server.
        ///
        /// <para>LOAD-BEARING FOR BOTH PRODUCTS. One executable serves Trados and
        /// memoQ - an environment variable decides which handshake it looks for -
        /// and it is published only with the Trados releases, so a memoQ install
        /// depends on this asset being attached to them. Resolved by NAME against
        /// whatever the latest release is, never a pinned tag, so a new Trados
        /// release cannot strand memoQ on a version that has gone away. The real
        /// fix is the server having its own repository and releases; that is
        /// worth doing once memoQ ships.</para>
        /// </summary>
        private const string AssetName = "Supervertaler-MCP-Server-exe.zip";

        private const string LatestReleaseApi =
            "https://api.github.com/repos/Supervertaler/Supervertaler-for-Trados/releases/latest";

        /// <summary>
        /// What differs between the two products. Data only: every decision this
        /// class makes is the same for both, and anything that looked like
        /// product logic would belong in the product.
        /// </summary>
        public sealed class Options
        {
            /// <summary>"Trados" or "memoQ", for the messages shown to the user.</summary>
            public string ProductName;

            /// <summary>
            /// Our table in the config file, without brackets. Each product needs
            /// its own, or registering the second would overwrite the first.
            /// </summary>
            public string BlockName;

            /// <summary>
            /// Where the server is kept. Somewhere permanent and writable by an
            /// ordinary user - not a folder a plugin update or an installer run
            /// rewrites, since the path is stored in ChatGPT's config.
            /// </summary>
            public string ServerDir;

            /// <summary>
            /// Environment the server is launched with, or null for none. This is
            /// what tells one product's server from the other's.
            /// </summary>
            public IDictionary<string, string> Environment;

            /// <summary>The two comment lines written above the block.</summary>
            public string BlockComment;

            /// <summary>
            /// Given the version of the server already on disk, true to replace
            /// it. Never called when nothing is installed - that is "absent", and
            /// always downloads.
            ///
            /// <para>A delegate rather than a rule here because there is no rule
            /// that is true of both products. Trados can compare versions because
            /// its plugin and this server ship from one release and share a
            /// version tail; that is a property of how Trados is built, not
            /// something true of MCP servers, and encoding it here would make a
            /// local coincidence look general. A product with no such
            /// relationship passes a delegate that always downloads.</para>
            /// </summary>
            public Func<Version, bool> IsOutdated;

            /// <summary>Where to record what happened, or null to record nothing.</summary>
            public Action<string> Log;

            internal void Say(string message)
            {
                try { Log?.Invoke(message); } catch { }
            }
        }

        /// <summary>The config file ChatGPT desktop, Codex CLI and the Codex IDE extension share.</summary>
        public static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "config.toml");

        public static string ServerExePath(Options options)
            => Path.Combine(options.ServerDir, ExeName);

        /// <summary>
        /// True when ChatGPT desktop appears to be installed. On Windows it ships
        /// as a Store package, so the usual Program Files check finds nothing and
        /// the package folder is the reliable marker. An existing Codex config
        /// counts too, since the CLI and the IDE extension share it.
        /// </summary>
        public static bool IsChatGptInstalled()
        {
            try
            {
                var packages = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages");
                if (Directory.Exists(packages) &&
                    Directory.GetDirectories(packages, "OpenAI.Codex*").Length > 0)
                    return true;

                return File.Exists(ConfigPath);
            }
            catch { return false; }
        }

        /// <summary>True when the config already registers this product's server.</summary>
        public static bool IsConfigured(Options options)
        {
            try
            {
                return File.Exists(ConfigPath)
                    && File.ReadAllText(ConfigPath).Contains("[" + options.BlockName + "]");
            }
            catch { return false; }
        }

        /// <summary>Version of the server on disk, or null when absent or unreadable.</summary>
        public static Version InstalledServerVersion(Options options)
        {
            try
            {
                var path = ServerExePath(options);
                if (!File.Exists(path)) return null;
                var raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
                return Version.TryParse(raw, out var v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>What a run did, for reporting back to the user.</summary>
        public class Result
        {
            public bool Success;
            public string Message;
            public string BackupPath;
            public bool Downloaded;
            /// <summary>True when an existing server was replaced rather than first installed.</summary>
            public bool Updated;
        }

        /// <summary>
        /// Ensures the server exists on disk and is registered with ChatGPT.
        /// Never throws: failures come back on <see cref="Result"/>.
        /// </summary>
        /// <param name="progress">Called with short status lines for the UI.</param>
        public static async Task<Result> RunAsync(Options options, Action<string> progress = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var result = new Result();
            void Say(string s) { try { progress?.Invoke(s); } catch { } }

            try
            {
                var exePath = ServerExePath(options);
                var hadServer = File.Exists(exePath);

                // Never true when nothing is installed: that case is "absent" and
                // always downloads, which is why the delegate is not asked.
                var outdated = hadServer
                    && options.IsOutdated != null
                    && options.IsOutdated(InstalledServerVersion(options));

                if (!hadServer || outdated)
                {
                    Say(hadServer ? "Updating the MCP server…" : "Downloading the MCP server…");
                    var error = await DownloadServerAsync(options).ConfigureAwait(false);
                    if (error != null)
                    {
                        result.Message = error;
                        return result;
                    }
                    result.Downloaded = true;
                    result.Updated = hadServer;
                }

                if (!File.Exists(exePath))
                {
                    result.Message = "The server could not be downloaded. Check your internet "
                                   + "connection, or install it by hand – see the documentation.";
                    return result;
                }

                Say("Updating the ChatGPT configuration…");
                result.BackupPath = WriteConfig(options);

                result.Success = true;
                result.Message =
                    (result.Updated
                        ? "The MCP server has been updated and ChatGPT desktop is set up.\r\n\r\n"
                        : "ChatGPT desktop is set up.\r\n\r\n")
                    + "Quit ChatGPT completely – closing the window is not enough, it keeps "
                    + "running in the notification area – then start it again and ask:\r\n\r\n"
                    + "    What " + options.ProductName + " project is open?";
                return result;
            }
            catch (Exception ex)
            {
                options.Say("ChatGPT setup failed: " + ex);
                result.Message = "Setup failed: " + ex.Message
                    + (result.BackupPath != null
                        ? "\r\n\r\nYour original config was backed up to:\r\n" + result.BackupPath
                        : "");
                return result;
            }
        }

        /// <summary>
        /// Fetch the server and put it in place. Returns null on success, or a
        /// user-facing message explaining what stopped it.
        /// </summary>
        private static async Task<string> DownloadServerAsync(Options options)
        {
            var assetUrl = await ResolveAssetUrlAsync(options).ConfigureAwait(false);
            if (assetUrl == null)
                return "Could not find the MCP server download on the latest release. Check your "
                     + "internet connection, or install it by hand – see the documentation.";

            Directory.CreateDirectory(options.ServerDir);
            SweepReplacedServers(options);

            var zipPath = Path.Combine(options.ServerDir, AssetName);
            var staged = ServerExePath(options) + ".new";
            try
            {
                await DownloadFileAsync(assetUrl, zipPath, options).ConfigureAwait(false);

                // Unpack beside the target rather than onto it: extraction
                // truncates before it writes, so extracting straight onto a live
                // server would turn a failed download into no working server at
                // all.
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }

                using (var file = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, ExeName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        return "The downloaded package did not contain " + ExeName + ".";

                    using (var src = entry.Open())
                    using (var dst = new FileStream(staged, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 81920))
                    {
                        src.CopyTo(dst);
                    }
                }

                return InstallStagedServer(options, staged);
            }
            catch (Exception ex)
            {
                options.Say("ChatGPT setup download failed: " + ex);
                return "The MCP server could not be downloaded: " + ex.Message;
            }
            finally
            {
                // Cleanup for the paths that FAILED as much as the one that
                // worked. A half-written zip or a leftover staged exe would be
                // taken by the next run as if it were whole; on success both are
                // already gone or already moved, and deleting what is not there
                // is not an error.
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                try { File.Delete(zipPath); } catch { }
            }
        }

        /// <summary>
        /// Stream the asset to disk rather than holding it in memory: it is tens
        /// of megabytes today and there is no reason for it to be smaller later.
        /// </summary>
        private static async Task DownloadFileAsync(string url, string path, Options options)
        {
            using (var http = new HttpClient())
            {
                // The default hundred seconds is not enough for an asset this
                // size on a slow connection.
                http.Timeout = TimeSpan.FromMinutes(15);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent(options));

                using (var response = await http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 81920, useAsync: true))
                    {
                        await src.CopyToAsync(dst).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Swap the staged server into place. Returns null on success, or a
        /// user-facing message.
        /// </summary>
        private static string InstallStagedServer(Options options, string staged)
        {
            var exePath = ServerExePath(options);
            try
            {
                if (!File.Exists(exePath))
                {
                    File.Move(staged, exePath);
                    return null;
                }

                // Windows refuses to DELETE a running executable but is happy to
                // RENAME one. So retire the old server sideways and move the new
                // one into its place: a ChatGPT that is running right now keeps
                // using the renamed file until it restarts, and nothing has to be
                // quit first. Overwriting in place fails outright with "the
                // process cannot access the file".
                var retired = exePath + ".old";
                try { if (File.Exists(retired)) File.Delete(retired); } catch { }

                File.Move(exePath, retired);
                try
                {
                    File.Move(staged, exePath);
                }
                catch
                {
                    // Put the working server back rather than leaving none.
                    try { File.Move(retired, exePath); } catch { }
                    throw;
                }

                // Fails while the retired server is still running; harmless, and
                // the next run sweeps it.
                try { File.Delete(retired); } catch { }
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                options.Say("ChatGPT setup could not replace the server: " + ex);
                return "The MCP server file is locked and could not be replaced. Quit ChatGPT from "
                     + "the notification area (closing the window is not enough), then press this "
                     + "button again. Your existing server keeps working in the meantime.";
            }
        }

        /// <summary>Delete servers retired by an earlier run, once nothing holds them.</summary>
        private static void SweepReplacedServers(Options options)
        {
            try
            {
                foreach (var leftover in Directory.GetFiles(options.ServerDir, ExeName + ".old")
                    .Concat(Directory.GetFiles(options.ServerDir, ExeName + ".new")))
                {
                    try { File.Delete(leftover); } catch { /* still running – next time */ }
                }
            }
            catch { /* best effort */ }
        }

        /// <summary>Finds the server asset on the latest release, by name.</summary>
        private static async Task<string> ResolveAssetUrlAsync(Options options)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent(options));
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    var json = await http.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);

                    // Small, fixed shape - a regex avoids taking a JSON dependency
                    // into a plugin sandbox for one field.
                    var m = Regex.Match(json,
                        "\"browser_download_url\"\\s*:\\s*\"([^\"]*" + Regex.Escape(AssetName) + ")\"");
                    if (m.Success) return m.Groups[1].Value;

                    options.Say("No " + AssetName + " on the latest release");
                    return null;
                }
            }
            catch (Exception ex)
            {
                options.Say("Could not resolve the download URL: " + ex.Message);
                return null;
            }
        }

        private static string UserAgent(Options options)
            => "Supervertaler-for-" + (options.ProductName ?? "unknown");

        /// <summary>
        /// Adds or replaces our block, backing the file up first. Returns the
        /// backup path, or null when there was no file to back up.
        ///
        /// <para>Deliberately text-level rather than a TOML round trip: parsing
        /// and re-emitting would reformat the whole file and could drop comments
        /// or ordering the user cares about. One named block is a small enough
        /// edit to do exactly, and everything else passes through untouched.</para>
        /// </summary>
        private static string WriteConfig(Options options)
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string existing = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";

            string backup = null;
            if (File.Exists(ConfigPath))
            {
                backup = ConfigPath + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(ConfigPath, backup, overwrite: true);
            }

            var updated = RemoveOurBlock(existing, options.BlockName);
            if (updated.Length > 0 && !updated.EndsWith("\n")) updated += "\n";

            File.WriteAllText(ConfigPath, updated + BuildBlock(options), new UTF8Encoding(false));
            options.Say("Registered " + ServerExePath(options) + " in " + ConfigPath);
            return backup;
        }

        /// <summary>
        /// Our block, exactly as it is written into the file. Pure, and public,
        /// so a test can read it without a network, a download, or another
        /// application's configuration file.
        /// </summary>
        public static string BuildBlock(Options options)
        {
            var block = new StringBuilder();
            block.Append("\n").Append(options.BlockComment.TrimEnd('\n')).Append("\n");
            block.Append("[").Append(options.BlockName).Append("]\n");
            block.Append("type = \"stdio\"\n");
            // Single quotes make this a TOML literal string, so Windows
            // backslashes are taken exactly as written and need no escaping.
            block.Append("command = '").Append(ServerExePath(options)).Append("'\n");
            block.Append("args = []\n");

            if (options.Environment != null && options.Environment.Count > 0)
            {
                // An INLINE table, never a [<block>.env] section. A sub-table is
                // a table header, and the remover below ends our block at the
                // next one - so an env section would survive a rewrite and
                // reattach itself to whatever came after it.
                block.Append("env = { ");
                var first = true;
                foreach (var pair in options.Environment)
                {
                    if (!first) block.Append(", ");
                    block.Append(pair.Key).Append(" = \"").Append(pair.Value).Append("\"");
                    first = false;
                }
                block.Append(" }\n");
            }

            block.Append("enabled = true\n");
            block.Append("startup_timeout_sec = 60\n");
            return block.ToString();
        }

        /// <summary>
        /// Strips a previous copy of our block - from its header to the next
        /// top-level table header - so re-running replaces rather than
        /// duplicates. Comment lines immediately above it go too, since those are
        /// ours. Every other block, the other product's included, is passed
        /// through untouched.
        /// </summary>
        public static string RemoveOurBlock(string text, string blockName)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            bool skipping = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();

                if (skipping)
                {
                    // A new table header ends our block.
                    if (trimmed.StartsWith("[")) skipping = false;
                    else continue;
                }

                if (trimmed.StartsWith("[" + blockName + "]"))
                {
                    skipping = true;
                    // Drop the comment lines we wrote directly above it.
                    while (kept.Count > 0 && kept[kept.Count - 1].TrimStart().StartsWith("#"))
                        kept.RemoveAt(kept.Count - 1);
                    while (kept.Count > 0 && kept[kept.Count - 1].Trim().Length == 0)
                        kept.RemoveAt(kept.Count - 1);
                    continue;
                }

                kept.Add(lines[i]);
            }

            return string.Join("\n", kept.ToArray());
        }
    }
}
