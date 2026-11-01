using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Supervertaler.Core
{
    /// <summary>What a key lookup found (<see cref="ApiKeyStore.Status"/>).</summary>
    public enum ApiKeyStatus
    {
        /// <summary>No key is stored for this provider.</summary>
        Absent,

        /// <summary>A key this computer can use.</summary>
        Present,

        /// <summary>
        /// A key is stored, but this Windows account cannot unlock it: saved under
        /// another sign-in, a password reset, a damaged file. Say so with
        /// <see cref="ApiKeyStore.MissingKeyMessage"/> or
        /// <see cref="ApiKeyStore.UnreadableKeyNote"/>; never show it as an empty
        /// box, and never send a provider a blank key in its place.
        /// </summary>
        Unreadable,
    }

    /// <summary>What <see cref="ApiKeyStore.SyncLocked"/> did.</summary>
    public sealed class LockedKeySync
    {
        /// <summary>Keys copied into the locked file: added, or replacing a different or unreadable one.</summary>
        public int Locked { get; internal set; }

        /// <summary>
        /// A key could not be locked, or the locked file not written. Nothing was
        /// removed, reads still take the plain file, and the next start tries again.
        /// </summary>
        public bool Failed { get; internal set; }

        /// <summary>
        /// For the settings window's status line when a damaged locked file was set
        /// aside (design note §7, M6); otherwise null. Already logged.
        /// </summary>
        public string Notice { get; internal set; }
    }

    /// <summary>
    /// The API keys every Supervertaler product shares (Trados #108), kept in two
    /// files during step 1 of docs/design/api-key-protection.md.
    ///
    /// <para><b>The plain file</b>, <c>&lt;data root&gt;\settings\api-keys.json</c>:
    /// one key per provider in clear text, hand-editable, read by Trados, memoQ and
    /// Sidekick (with its own parser). The data folder it sits in is portable -
    /// synced, backed up, copied whole - and every key in it travels with it. It is
    /// still written exactly as before, only so that products not yet updated keep
    /// working; step 2 imports it into the locked file and removes it.</para>
    ///
    /// <para><b>The locked file</b>, <see cref="LockedFilePath"/>: the same ids, each
    /// value locked by Windows to this Windows account (the contract every product
    /// keeps is on <see cref="DpapiSecret.Protect"/>), kept in the user's profile,
    /// outside the data folder. A copy of it opens for no other account.</para>
    ///
    /// <para><b>Step 1</b> (§5): a read takes the plain file's key when it has one,
    /// else the locked file's; a save writes the plain file, then the locked one; and
    /// <see cref="SyncLocked"/>, at start-up, copies the plain keys into the locked
    /// file - adding and replacing, never removing, so a damaged or half-written
    /// plain file cannot empty it. Always the plain file first: one order everywhere
    /// means no two products wait on each other, and a crash between the two writes
    /// leaves the plain file the newer one, which reads take and the next sync
    /// copies.</para>
    ///
    /// <para><b>Three answers, not two</b> (<see cref="ApiKeyStatus"/>). A key this
    /// account cannot unlock is reported by name, with what to do. It is never shown
    /// as an empty box, never sent to a provider blank, never replaced by an older
    /// key from a fallback, and it stays in the file until a new key replaces it -
    /// some causes pass (a profile loaded oddly, a domain controller not yet
    /// reachable).</para>
    ///
    /// <para>Nothing is cached. A lookup reads the files and unlocks one key, about a
    /// millisecond, once per action or batch run - never per segment - and a cache
    /// would go stale when another product changes a key.</para>
    ///
    /// <para>Keys are stored under the plugin's provider ids (claude, openai, gemini,
    /// grok, mistral, deepseek, openrouter). Common aliases are accepted (anthropic,
    /// xai, custom), so a file written by hand in either vocabulary works. An id with
    /// a colon (<see cref="PrivateId"/>) is one product's own key and lives in the
    /// locked file only.</para>
    /// </summary>
    public static class ApiKeyStore
    {
        public static string FilePath
        {
            get
            {
                try { return Path.Combine(SupervertalerPaths.Root, "settings", "api-keys.json"); }
                catch { return null; }
            }
        }

        /// <summary>
        /// The locked file: <c>%APPDATA%\Supervertaler\api-keys.locked.json</c>, beside
        /// config.json, in the roaming part of the profile - where Windows keeps the
        /// key that unlocks it, so a roaming profile carries both. Null when Windows
        /// names no such folder; the locked file is then not used.
        /// </summary>
        public static string LockedFilePath => _lockedFileRedirect ?? DefaultLockedFilePath;

        internal static string DefaultLockedFilePath
        {
            get
            {
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Supervertaler", "api-keys.locked.json");
                }
                catch { return null; }
            }
        }

        private static volatile string _lockedFileRedirect;

        /// <summary>
        /// Points the locked file somewhere else, for tests; null puts it back.
        ///
        /// <para>Redirecting the data root (<see cref="SupervertalerPaths.Set"/>) does
        /// not move this file: it is in the profile, not the data folder. A test or
        /// harness that saves a key or runs <see cref="SyncLocked"/> must redirect
        /// both, or it writes the user's own locked file - and the sync replaces
        /// entries there with the test's keys.</para>
        /// </summary>
        internal static void RedirectLockedFile(string path) => _lockedFileRedirect = path;

        /// <summary>
        /// Where core reports what the user is not shown: a key Windows would not
        /// unlock and its error code, a damaged locked file set aside, a locked write
        /// that failed. Never a key. Wire it to the product's log.
        /// </summary>
        public static Action<string> Log { get; set; }

        /// <summary>The fixed line under a key box (design note §7, M3).</summary>
        public const string LockedKeyLine = "Saved on this computer only, locked to your Windows sign-in.";

        private const string LockedComment =
            "Supervertaler API keys, locked by Windows to this Windows account. They cannot be read on another computer or by another account. Change them in any product's settings.";

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "anthropic", LlmModels.ProviderClaude },
            { "xai", LlmModels.ProviderGrok },
            { "x.ai", LlmModels.ProviderGrok },
            { "custom", LlmModels.ProviderCustomOpenAi },
        };

        private static readonly object Gate = new object();

        // A writer holds a key file exclusively for a few milliseconds; a second
        // reader or writer waits up to a second for it.
        private const int Retries = 20;
        private const int RetryMs = 50;

        public static string Canonical(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey)) return providerKey;
            var k = providerKey.Trim().ToLowerInvariant();
            return Aliases.TryGetValue(k, out var c) ? c : k;
        }

        /// <summary>
        /// The id for one product's own key, such as a named OpenAI-compatible
        /// profile: <c>trados:custom_openai:Acme server</c>. Quotes and backslashes
        /// are dropped from the name, as every product does (§3). Such an id is kept
        /// in the locked file only, never in the shared plain file.
        /// </summary>
        public static string PrivateId(string product, string kind, string name)
            => product + ":" + kind + ":" + (name ?? "").Replace("\"", "").Replace("\\", "");

        private static bool IsPrivate(string id) => id != null && id.IndexOf(':') >= 0;

        /// <summary>The id as stored and looked up: a provider's canonical id, a private id as given.</summary>
        private static string Id(string providerKey) => IsPrivate(providerKey) ? providerKey : Canonical(providerKey);

        // ─── Reading ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The key for a provider, or null when there is none this computer can use -
        /// absent or unreadable; <see cref="Status"/> tells which. Never throws.
        /// </summary>
        public static string Get(string providerKey)
        {
            var found = Find(providerKey);
            return found.Status == ApiKeyStatus.Present ? found.Key : null;
        }

        /// <summary>Present, absent or unreadable, by the step-1 read rule. Never throws.</summary>
        public static ApiKeyStatus Status(string providerKey) => Find(providerKey).Status;

        /// <summary>
        /// The sentence to show when an action needs a provider's key and has none to
        /// use (§7): M2 when one is stored but cannot be unlocked here, "No API key
        /// set ..." when there is none; null when the key is there.
        /// <paramref name="settingsPlace"/> is where the product's keys are entered:
        /// "AI Settings", "Supervertaler options".
        /// </summary>
        public static string MissingKeyMessage(string providerKey, string settingsPlace)
        {
            var found = Find(providerKey);
            return found.Status == ApiKeyStatus.Present ? null : WhenUsed(found, providerKey, settingsPlace);
        }

        /// <summary>
        /// The note beside a provider's key box when its key cannot be unlocked here
        /// (§7, M1); null when it can, or none is stored. The box itself stays empty,
        /// and the stored key is kept until a new one is pasted.
        /// </summary>
        public static string UnreadableKeyNote(string providerKey)
        {
            var found = Find(providerKey);
            if (found.Status != ApiKeyStatus.Unreadable) return null;
            var name = NameOf(Id(providerKey));
            switch (found.Why)
            {
                case Problem.FileDamaged:
                    return "Your " + name + " key can't be read – the file holding your locked API keys is damaged: "
                         + found.File + ". Paste the key in again; the damaged file will be set aside, not deleted.";
                case Problem.FileUnavailable:
                    return "Your " + name + " key can't be read just now – the file holding your locked API keys could not be opened: "
                         + found.File + ".";
                default:
                    return "Your " + name + " key can't be read on this computer – it was saved under another Windows sign-in, "
                         + "or your Windows password was reset. Paste the key in again; nothing else is affected.";
            }
        }

        /// <summary>For <c>LlmClient.ResolveApiKey</c>: the status, the key when present, and otherwise the sentence why not.</summary>
        internal static ApiKeyStatus Lookup(string providerKey, string settingsPlace, out string key, out string problem)
        {
            var found = Find(providerKey);
            key = found.Status == ApiKeyStatus.Present ? found.Key : null;
            problem = found.Status == ApiKeyStatus.Present ? null : WhenUsed(found, providerKey, settingsPlace);
            return found.Status;
        }

        /// <summary>"No API key set for ..." - for a key that no store has at all.</summary>
        internal static string NoKeyMessage(string providerKey, string settingsPlace)
            => WhenUsed(default(Found), providerKey, settingsPlace);

        private static string WhenUsed(Found found, string providerKey, string settingsPlace)
        {
            var name = NameOf(Id(providerKey));
            var place = string.IsNullOrWhiteSpace(settingsPlace) ? "the settings" : settingsPlace.Trim();
            switch (found.Why)
            {
                case Problem.EntryRefused:
                    return "Can't use your " + name + " key – it can't be read on this computer (saved under another Windows sign-in, "
                         + "or your Windows password was reset). Paste it in again in " + place + ".";
                case Problem.FileDamaged:
                    return "Can't use your " + name + " key – the file holding your locked API keys is damaged: "
                         + found.File + ". Paste the key in again in " + place + ".";
                case Problem.FileUnavailable:
                    return "Can't use your " + name + " key – the file holding your locked API keys could not be opened: "
                         + found.File + ". Try again in a moment.";
                default:
                    return "No API key set for " + name + ". Add one in " + place + ".";
            }
        }

        private static string DamagedNotice(string setAside) =>
            "The file holding your locked API keys was damaged and has been set aside as " + setAside
            + ". Please paste your keys in again.";

        private enum Problem { None, EntryRefused, FileDamaged, FileUnavailable }

        private struct Found
        {
            public ApiKeyStatus Status;   // default: Absent
            public string Key;
            public Problem Why;
            public string File;
        }

        private static Found Find(string providerKey)
        {
            try
            {
                var id = Id(providerKey);
                if (string.IsNullOrWhiteSpace(id)) return default(Found);

                // Step 1: the plain file decides whenever it has the key.
                if (!IsPrivate(id) && Load().TryGetValue(id, out var plain) && !string.IsNullOrWhiteSpace(plain))
                    return new Found { Status = ApiKeyStatus.Present, Key = plain };

                var path = LockedFilePath;
                var read = ReadLocked(path);
                if (read.Why != Problem.None)
                    return new Found { Status = ApiKeyStatus.Unreadable, Why = read.Why, File = path };

                if (!read.Map.TryGetValue(id, out var blob) || string.IsNullOrWhiteSpace(blob))
                    return default(Found);

                if (DpapiSecret.TryUnprotect(blob, out var key, out var error))
                    return string.IsNullOrEmpty(key) ? default(Found) : new Found { Status = ApiKeyStatus.Present, Key = key };

                Say(Path.GetFileName(path) + ": the " + id + " key cannot be unlocked by this Windows account ("
                    + (error == 0 ? "not base64" : "Windows error 0x" + error.ToString("X8", CultureInfo.InvariantCulture)) + ").");
                return new Found { Status = ApiKeyStatus.Unreadable, Why = Problem.EntryRefused, File = path };
            }
            catch (Exception ex)
            {
                // Nothing above should throw. If something does, "unreadable" is the
                // honest answer: there may well be a key, and "none set" would send
                // the user to paste one that is already there.
                Say("API key lookup failed: " + ex.GetType().Name + ": " + ex.Message);
                return new Found { Status = ApiKeyStatus.Unreadable, Why = Problem.FileUnavailable, File = LockedFilePath };
            }
        }

        private struct LockedRead
        {
            public Dictionary<string, string> Map;
            public Problem Why;
        }

        /// <summary>
        /// The locked file's entries, still locked. A file that is not valid JSON is
        /// damaged; one that cannot be opened after waiting out a writer is
        /// unavailable. Either way every key in it counts as unreadable, not absent.
        /// </summary>
        private static LockedRead ReadLocked(string path)
        {
            var none = new LockedRead { Map = NewMap() };
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return none;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    byte[] raw;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        raw = ReadAll(fs);
                    if (TryParseLocked(Decode(raw), out var map))
                        return new LockedRead { Map = map };
                    Say(Path.GetFileName(path) + " is damaged (not valid JSON): every key in it counts as unreadable until the next save sets it aside.");
                    return new LockedRead { Map = NewMap(), Why = Problem.FileDamaged };
                }
                catch (FileNotFoundException) { return none; }
                catch (DirectoryNotFoundException) { return none; }
                catch (IOException) when (attempt < Retries)
                {
                    Thread.Sleep(RetryMs);
                }
                catch (Exception ex)
                {
                    Say(Path.GetFileName(path) + " could not be opened: " + ex.GetType().Name + ": " + ex.Message);
                    return new LockedRead { Map = NewMap(), Why = Problem.FileUnavailable };
                }
            }
        }

        // ─── Writing ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Saves one key (empty removes it): the plain file, then the locked file.
        /// Returns false only when the save did not take.
        ///
        /// <para>A key already stored with this exact value needs no write, and that
        /// is a success: it is by far the commonest case, since a settings dialog
        /// saves every field whether or not it was touched. Reporting it as a
        /// failure made memoQ believe every ordinary OK had failed to save.</para>
        ///
        /// <para>In step 1 the plain file decides, so a key written there is saved
        /// even if the locked write fails: that is logged, and the next start's
        /// <see cref="SyncLocked"/> repeats it. A removal needs both files, because a
        /// key left in the locked file would still be read. A private id
        /// (<see cref="PrivateId"/>) is in the locked file only, so that write
        /// decides.</para>
        ///
        /// <para>An empty save never removes a key this account cannot unlock. The
        /// user was never shown that key - its box was empty - and a dialog saves
        /// every box on OK. It stays until a new key replaces it (§3).</para>
        /// </summary>
        public static bool Set(string providerKey, string key)
        {
            var id = Id(providerKey);
            if (string.IsNullOrWhiteSpace(id)) return false;
            var wanted = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            var shared = !IsPrivate(id);

            if (shared)
            {
                var plain = Update(map =>
                {
                    if (wanted == null) return map.Remove(id);
                    if (map.TryGetValue(id, out var have) && have == wanted) return false;
                    map[id] = wanted;
                    return true;
                });
                if (plain == null) return false;
            }

            if (SetLocked(id, wanted)) return true;
            if (!shared || wanted == null) return false;

            Say("The " + id + " key is saved in " + Path.GetFileName(FilePath)
                + " but not yet locked to this computer; the next start locks it.");
            return true;
        }

        /// <summary>The locked half of <see cref="Set"/>: true when the locked file now says what was asked.</summary>
        private static bool SetLocked(string id, string wanted)
        {
            var refused = false;
            var written = UpdateLocked(map =>
            {
                refused = false;
                string current = null;
                var readable = map.TryGetValue(id, out var blob) && DpapiSecret.TryUnprotect(blob, out current, out _);

                if (wanted == null) return readable && map.Remove(id);   // an unreadable entry is kept
                if (readable && current == wanted) return false;

                var locked = DpapiSecret.Protect(wanted);
                if (locked.Length == 0) { refused = true; return false; }
                map[id] = locked;
                return true;
            }, out _);

            if (refused) Say("Windows would not lock the " + id + " key.");
            return written != null && !refused;
        }

        /// <summary>
        /// Adds every non-empty key from a product's own settings that neither file
        /// has yet, to the plain file; <see cref="SyncLocked"/> then locks it. Never
        /// overwrites: once either file holds a key for a provider - readable here or
        /// not - that one is the newer. Without that, a product's old copy would be
        /// put back in the plain file after another product had replaced the key in
        /// the locked file only, and step 1 reads the plain file first. Returns how
        /// many were added.
        /// </summary>
        public static int MigrateFrom(AiApiKeys local)
        {
            if (local == null) return 0;

            // A locked file this account cannot read may hold newer keys than the
            // product's own copies, so nothing is migrated past it: the user is told
            // the key cannot be read, rather than handed an older one.
            var locked = ReadLocked(LockedFilePath);
            if (locked.Why != Problem.None) return 0;

            int added = 0;
            Update(map =>
            {
                added = 0;
                void Take(string provider, string key)
                {
                    if (string.IsNullOrWhiteSpace(key)) return;
                    if (map.TryGetValue(provider, out var have) && !string.IsNullOrWhiteSpace(have)) return;
                    if (locked.Map.ContainsKey(provider)) return;
                    map[provider] = key.Trim(); added++;
                }
                Take(LlmModels.ProviderOpenAi, local.OpenAi);
                Take(LlmModels.ProviderClaude, local.Claude);
                Take(LlmModels.ProviderGemini, local.Gemini);
                Take(LlmModels.ProviderGrok, local.Grok);
                Take(LlmModels.ProviderMistral, local.Mistral);
                Take(LlmModels.ProviderDeepSeek, local.DeepSeek);
                Take(LlmModels.ProviderOpenRouter, local.OpenRouter);
                return added > 0;
            });
            return added;
        }

        /// <summary>
        /// The step-1 start-up sync (§5): every key in the plain file is copied into
        /// the locked file where that lacks it or holds something else - a different
        /// key, or one this account cannot unlock. It adds and replaces, never
        /// removes, so a damaged, empty or half-written plain file cannot empty the
        /// locked one.
        ///
        /// <para>The plain file is held - readable by others, not writable - until the
        /// locked file is written. Otherwise a key another product saves in between
        /// (plain file, then locked file) could be overwritten in the locked file by
        /// the older value read here. Plain file first, then locked file: the order
        /// every writer takes, so none waits on another.</para>
        ///
        /// <para>Cost: one unlock per plain key to compare, about a millisecond each,
        /// and a write only when something changed.</para>
        /// </summary>
        public static LockedKeySync SyncLocked()
        {
            var result = new LockedKeySync();
            var plainPath = FilePath;
            if (string.IsNullOrEmpty(plainPath) || LockedFilePath == null) return result;

            // Gate first, as every write in this process takes it: a thread saving a
            // key here waits for the sync instead of timing out on the held file.
            lock (Gate)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(plainPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var plain = Parse(Decode(ReadAll(fs)));
                            if (plain.Count == 0) return result;
                            int locked = 0;
                            var refused = false;
                            var written = UpdateLocked(map =>
                            {
                                locked = 0;
                                refused = false;
                                foreach (var entry in plain)
                                {
                                    if (string.IsNullOrWhiteSpace(entry.Value)) continue;
                                    var want = entry.Value.Trim();
                                    if (map.TryGetValue(entry.Key, out var blob)
                                        && DpapiSecret.TryUnprotect(blob, out var have, out _) && have == want)
                                        continue;
                                    var sealedKey = DpapiSecret.Protect(want);
                                    if (sealedKey.Length == 0) { refused = true; continue; }
                                    map[entry.Key] = sealedKey;
                                    locked++;
                                }
                                return locked > 0;
                            }, out var setAside);

                            result.Locked = written == true ? locked : 0;
                            result.Failed = refused || written == null;
                            if (setAside != null) result.Notice = DamagedNotice(setAside);
                            if (result.Failed)
                                Say("Not every key in " + Path.GetFileName(plainPath) + " could be locked to this computer; the next start tries again.");
                            return result;
                        }
                    }
                    catch (FileNotFoundException) { return result; }
                    catch (DirectoryNotFoundException) { return result; }
                    catch (IOException) when (attempt < Retries)
                    {
                        Thread.Sleep(RetryMs);
                    }
                    catch (Exception ex)
                    {
                        Say("Keys not locked to this computer: " + ex.GetType().Name + ": " + ex.Message);
                        result.Failed = true;
                        return result;
                    }
                }
            }
        }

        /// <summary>
        /// The plain file as provider → key, canonical ids. Empty when absent. The
        /// locked file is not part of it: this is the whole-file read a product made
        /// before the locked file existed.
        /// </summary>
        public static Dictionary<string, string> Load()
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NewMap();
            // A writer holds the file exclusively for a few milliseconds; wait it out.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                        return Parse(reader.ReadToEnd());
                }
                catch (IOException) when (attempt < Retries)
                {
                    Thread.Sleep(RetryMs);
                }
                catch
                {
                    // "Empty when absent" is what the summary promises, and a file
                    // that cannot be read is as good as absent to every caller here:
                    // the key simply is not available. Without this, a locked file
                    // after the retries - or one the user has no rights to - throws
                    // out of a method documented never to.
                    return NewMap();
                }
            }
        }

        private static bool? Update(Func<Dictionary<string, string>, bool> mutate)
            => UpdateFile(FilePath, false, mutate, out _);

        private static bool? UpdateLocked(Func<Dictionary<string, string>, bool> mutate, out string setAside)
            => UpdateFile(LockedFilePath, true, mutate, out setAside);

        /// <summary>
        /// One read-modify-write under an exclusive open (#108 handoff): several
        /// products share these files and two of them saving at the same moment must
        /// not lose a write. A lock on the file, not the process; a second writer
        /// waits up to a second. <paramref name="mutate"/> returns false to leave the
        /// file untouched.
        ///
        /// <para>Three outcomes, not two: <c>true</c> written, <c>false</c> nothing
        /// needed writing, <c>null</c> could not be written. Collapsing the last two
        /// is what let a no-op save look like a failed one.</para>
        ///
        /// <para>The new file is written beside the old one and renamed over it
        /// (<see cref="AtomicFile"/>), never truncated and rewritten in place: a
        /// crash or a power cut between the two used to leave an empty file, and
        /// every key with it. So the file is the old keys or the new ones, never
        /// neither.</para>
        ///
        /// <para>The locked file is parsed strictly. One that is not valid JSON is
        /// never overwritten: its exact bytes are first copied to
        /// <c>api-keys.locked.damaged-&lt;yyyyMMdd-HHmmss&gt;.json</c> beside it
        /// (<paramref name="setAside"/>), and nothing is written unless that copy is.
        /// It holds only locked values, so keeping it costs nothing.</para>
        /// </summary>
        private static bool? UpdateFile(string path, bool locked, Func<Dictionary<string, string>, bool> mutate, out string setAside)
        {
            setAside = null;
            if (string.IsNullOrEmpty(path)) return null;
            lock (Gate)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); } catch { return null; }
                // Temporary files left by a process killed in the middle of a write.
                AtomicFile.SweepAbandoned(path, TimeSpan.FromMinutes(5));
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        // FileShare.Delete alone: still exclusive against every reader and
                        // writer, and it lets the empty-file case below delete while the
                        // handle is held, so no other product can open the file in between.
                        using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete))
                        {
                            var raw = ReadAll(fs);
                            var existing = Decode(raw);
                            Dictionary<string, string> map;
                            var damaged = false;
                            if (!locked) map = Parse(existing);
                            else if (!TryParseLocked(existing, out map)) { damaged = true; map = NewMap(); }

                            if (!mutate(map))
                            {
                                // Nothing to write. An empty key file - the one OpenOrCreate just
                                // made on a machine that had none, or any other - is removed so it
                                // cannot be mistaken for a failed write. Deleted BEFORE the handle
                                // closes: Windows marks it delete-pending and nobody can open it
                                // in the meantime, so a key another product writes in that window
                                // cannot be lost (memoQ session, 2026-09-06).
                                if (raw.Length == 0) { try { File.Delete(path); } catch { } }
                                return false;
                            }

                            if (damaged)
                            {
                                setAside = SetAside(path, raw);
                                if (setAside == null) return null;
                                Say(DamagedNotice(setAside));
                            }

                            var bytes = new UTF8Encoding(false).GetBytes(locked ? RenderLocked(map) : Render(map));

                            // Renamed over the file while this handle still holds it:
                            // a POSIX rename (NTFS) may replace a file whose handles all
                            // share delete, and this one does. Until the new file is in
                            // place no other product can open the old one, so the lock
                            // still covers the whole read-modify-write.
                            if (AtomicFile.Write(path, bytes, replaceExisting: true, log: Say) == AtomicFile.Outcome.Written)
                                return true;

                            // A volume without POSIX renames (FAT, some network shares)
                            // cannot replace a file that is open, even by this process.
                            // Let go of it and try once more. The lock is gone for the
                            // few milliseconds that takes, which matters only if another
                            // product saves a key in that same moment; the file is never
                            // left half-written either way.
                            fs.Dispose();
                            return AtomicFile.Write(path, bytes, replaceExisting: true, log: Say) == AtomicFile.Outcome.Written
                                ? true : (bool?)null;
                        }
                    }
                    catch (IOException) when (attempt < Retries)
                    {
                        Thread.Sleep(RetryMs);
                    }
                    catch (Exception ex)
                    {
                        Say(Path.GetFileName(path) + " not written: " + ex.GetType().Name + ": " + ex.Message);
                        return null;
                    }
                }
            }
        }

        /// <summary>
        /// Copies a damaged locked file's exact bytes beside it, under a name no
        /// writer uses. Returns that path, or null when the copy could not be made.
        /// </summary>
        private static string SetAside(string path, byte[] raw)
        {
            var stem = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path)
                + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            for (int n = 1; n < 10; n++)
            {
                var aside = stem + (n == 1 ? "" : "-" + n.ToString(CultureInfo.InvariantCulture)) + ".json";
                var outcome = AtomicFile.Write(aside, raw, replaceExisting: false, log: Say);
                if (outcome == AtomicFile.Outcome.Written) return aside;
                if (outcome == AtomicFile.Outcome.Failed) return null;
            }
            return null;
        }

        // ─── The two formats ─────────────────────────────────────────────────────

        private static Dictionary<string, string> NewMap() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static byte[] ReadAll(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>UTF-8 unless a byte-order mark says otherwise, which it then consumes.</summary>
        private static string Decode(byte[] raw)
        {
            using (var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        private static Dictionary<string, string> Parse(string json)
        {
            var map = NewMap();
            // A flat object of strings; nothing else is expected, and anything else is ignored.
            foreach (Match m in Regex.Matches(json ?? "", "\"(?<k>[^\"\\\\]+)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\""))
            {
                var k = m.Groups["k"].Value;
                if (k.StartsWith("_")) continue;   // "_comment" and the like
                map[Canonical(k)] = Unescape(m.Groups["v"].Value);
            }
            return map;
        }

        private static string Render(Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"_comment\": \"API keys shared by Supervertaler for Trados, Supervertaler for memoQ and Supervertaler Sidekick. One key per provider; edit by hand or in any product's settings.\",");
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append("  \"").Append(Escape(keys[i])).Append("\": \"").Append(Escape(map[keys[i]] ?? "")).Append("\"");
                sb.AppendLine(i < keys.Count - 1 ? "," : "");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Unescape(string s) => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");

        /// <summary>
        /// The locked file, read strictly - unlike the plain file's forgiving parser,
        /// which serves hand edits. Other products write this file too, so a real
        /// JSON parser (the framework's), not a pattern: a truncated or garbled file
        /// must be recognised as damaged, never read as fewer keys. Empty or blank
        /// counts as no entries. Ids starting with "_" are ignored; an id another
        /// product wrote is kept exactly as spelt, so it survives a rewrite.
        /// </summary>
        private static bool TryParseLocked(string json, out Dictionary<string, string> map)
        {
            map = NewMap();
            if (string.IsNullOrWhiteSpace(json)) return true;
            try
            {
                if (!(NewJson().DeserializeObject(json) is IDictionary<string, object> entries)) return false;
                foreach (var entry in entries)
                {
                    if (entry.Key.StartsWith("_", StringComparison.Ordinal) || !(entry.Value is string value)) continue;
                    map[Aliases.TryGetValue(entry.Key.Trim(), out var alias) ? alias : entry.Key] = value;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string RenderLocked(Dictionary<string, string> map)
        {
            var json = NewJson();
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.Append("  \"_comment\": \"").Append(LockedComment).Append("\"");
            var ids = new List<string>(map.Keys);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                sb.AppendLine(",");
                sb.Append("  ").Append(json.Serialize(id)).Append(": ").Append(json.Serialize(map[id] ?? ""));
            }
            sb.AppendLine();
            sb.AppendLine("}");
            return sb.ToString();
        }

        // The default cap is 2 MB; a file over it would be taken for damaged.
        private static JavaScriptSerializer NewJson() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static void Say(string message)
        {
            try { Log?.Invoke(message); }
            catch { /* logging must never break a lookup or a save */ }
        }

        // ─── Does this look like a key for that provider? ─────────────────────────────

        /// <summary>
        /// A sentence when the key plainly belongs to another service, else null. Only
        /// the prefixes that are stable and public are checked; an unknown shape passes.
        /// </summary>
        public static string CheckShape(string providerKey, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;

            var k = key.Trim();
            var wanted = Canonical(providerKey);
            var prefix = PrefixFor(wanted);
            if (prefix == null) return null;                  // no known shape: anything goes

            var name = NameOf(wanted);

            // The service the key belongs to is decided first, not the prefix the
            // box wants. Every OpenRouter key also starts with OpenAI's "sk-", so
            // testing the wanted prefix first would wave one straight through.
            //
            // Naming the service it DOES belong to is the useful half: "this does
            // not look like an Anthropic key" leaves someone staring at a key that
            // looks perfectly fine to them; "this is your OpenAI key" ends it.
            var actual = ServiceOf(k);
            if (actual != null)
                return string.Equals(actual, name, StringComparison.Ordinal)
                    ? null
                    : "This is " + Article(actual) + " " + actual + " key, not " + Article(name) + " " + name + " one.";

            return "This does not look like " + Article(name) + " " + name
                 + " key - they start with " + prefix + ".";
        }

        /// <summary>The prefix a provider's keys are known to start with, or null.</summary>
        private static string PrefixFor(string providerKey)
        {
            switch (providerKey)
            {
                case LlmModels.ProviderClaude: return "sk-ant-";
                case LlmModels.ProviderOpenAi: return "sk-";
                case LlmModels.ProviderGemini: return "AIza";
                case LlmModels.ProviderGrok: return "xai-";
                case LlmModels.ProviderOpenRouter: return "sk-or-";
                default: return null;
            }
        }

        /// <summary>The name a user knows a provider by; for a private id, the profile's own name.</summary>
        private static string NameOf(string providerKey)
        {
            if (IsPrivate(providerKey))
            {
                var parts = providerKey.Split(new[] { ':' }, 3);
                return parts.Length == 3 ? parts[2] : providerKey;
            }
            switch (providerKey)
            {
                case LlmModels.ProviderClaude: return "Anthropic";
                case LlmModels.ProviderOpenAi: return "OpenAI";
                case LlmModels.ProviderGemini: return "Gemini";
                case LlmModels.ProviderGrok: return "xAI";
                case LlmModels.ProviderOpenRouter: return "OpenRouter";
                case LlmModels.ProviderMistral: return "Mistral";
                case LlmModels.ProviderDeepSeek: return "DeepSeek";
                case LlmModels.ProviderCustomOpenAi: return "Custom OpenAI";
                case MachineTranslation.DeepL:
                case MachineTranslation.Google:
                case MachineTranslation.Microsoft: return MachineTranslation.DisplayName(providerKey);
                default: return providerKey;
            }
        }

        /// <summary>
        /// Which service a key plainly belongs to, or null. Longest prefix first:
        /// every OpenRouter key also starts with "sk-".
        /// </summary>
        private static string ServiceOf(string key)
        {
            if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return "Anthropic";
            if (key.StartsWith("sk-or-", StringComparison.Ordinal)) return "OpenRouter";
            if (key.StartsWith("sk-", StringComparison.Ordinal)) return "OpenAI";
            if (key.StartsWith("AIza", StringComparison.Ordinal)) return "Gemini";
            if (key.StartsWith("xai-", StringComparison.Ordinal)) return "xAI";
            return null;
        }

        private static string Article(string name)
        {
            if (string.IsNullOrEmpty(name)) return "a";
            return "AEIOUaeiou".IndexOf(name[0]) >= 0 ? "an" : "a";
        }
    }
}
