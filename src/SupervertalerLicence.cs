using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supervertaler.Core
{
    /// <summary>
    /// The one licence every Supervertaler product shares: trial, activation,
    /// validation and the offline window, for this computer.
    ///
    /// Moved here from the Trados plugin's <c>LicenseManager</c> so that the
    /// memoQ plugin reads the same licence rather than a second one. The logic
    /// is the Trados plugin's, unchanged, apart from what sharing requires:
    ///
    ///   - One licence covers every product, and an activation is a computer,
    ///     not a product install. Entering a key in a second product on a
    ///     computer that is already activated with it adopts that activation
    ///     rather than spending another one.
    ///   - The file and the trial anchor are shared, and written as two-process
    ///     state (see <see cref="LicenceFile"/> and <see cref="TrialAnchor"/>).
    ///     Every change re-reads the file first and applies itself to what is
    ///     there, so one product does not undo what the other just wrote.
    ///   - The first product to start copies the Trados plugin's licence file
    ///     into the shared location. It is a copy: the Trados-named file stays
    ///     where it was and is never written again.
    ///
    /// No UI: every message box stays in the products. And no permission
    /// either - see <see cref="LicenceState"/> for the two rules every product
    /// holds to.
    ///
    /// State is read when the product starts and changed by the calls below.
    /// A change made by the other product while both are open is picked up at
    /// the next of those calls or the next start, not before.
    /// </summary>
    public sealed class SupervertalerLicence
    {
        private static readonly Lazy<SupervertalerLicence> _instance =
            new Lazy<SupervertalerLicence>(() => new SupervertalerLicence(
                null, null, TrialAnchor.SharedSubKey, TrialAnchor.LegacySubKey));

        /// <summary>
        /// This computer's licence. The first use reads it, so set
        /// <see cref="Log"/> before touching this.
        /// </summary>
        public static SupervertalerLicence Instance => _instance.Value;

        /// <summary>
        /// Where diagnostics go - write failures and unreadable files. Set by
        /// the product before the first use of <see cref="Instance"/>. Never
        /// passed a licence key.
        /// </summary>
        public static Action<string> Log { get; set; }

        internal static void WriteLog(string message)
        {
            try { Log?.Invoke("[Licence] " + message); }
            catch { /* logging must never throw into licence code */ }
        }

        // ─── Constants ──────────────────────────────────────────────

        private const string BaseUrl = "https://api.lemonsqueezy.com/v1/licenses";

        /// <summary>
        /// The Lemon Squeezy store that sells Supervertaler licences. A key is a
        /// Supervertaler licence only if the licence server says it was issued
        /// by this store. Every product in the store is a licence, so the store
        /// rather than the product is the test, and renaming or adding a product
        /// needs no change here.
        /// </summary>
        internal const long SupervertalerStoreId = 307062;

        private const string NotOurKeyStatus = "not-supervertaler";
        private const string NotOurKeyMessage =
            "This licence key is not a Supervertaler licence. Please check you entered the right key.";
        private const int OfflineCacheDays = 30;
        private static readonly TimeSpan AbandonedTempAge = TimeSpan.FromMinutes(10);
        private static readonly HttpClient Http = new HttpClient();

        // ─── State ──────────────────────────────────────────────────

        // Null means the live locations, resolved on every use so that a data
        // folder moved mid-session is written to where it now is. The tests
        // set them, and must never touch the real ones.
        private readonly string _sharedPathOverride;
        private readonly string _legacyPathOverride;
        private readonly string _anchorKey;
        private readonly string _legacyAnchorKey;

        private readonly object _lock = new object();
        private LicenceRecord _rec;

        // This session found a licence file it could not read. State is
        // Unknown until a successful activation or deactivation replaces it.
        private bool _unreadable;

        // This session found the file damaged, as opposed to merely unopenable:
        // re-reading the fresh record written in its place must not end the
        // Unknown session.
        private bool _damagedThisSession;

        private string SharedPath => _sharedPathOverride ?? LicenceFile.SharedPath;
        private string LegacyPath => _legacyPathOverride ?? LicenceFile.LegacyTradosPath;

        // A damaged licence file is kept here before a fresh record replaces it,
        // so a key it held can still be recovered by hand. Its presence is also
        // what keeps DamagedFileFound true until a key is activated.
        private string DamagedPath => SharedPath + ".damaged";

        /// <summary>
        /// Raised when the state changes: activation, deactivation, or a
        /// validation that changed it. Raised on whichever thread made the
        /// change, so a UI subscriber marshals to its own thread.
        /// </summary>
        public event EventHandler StateChanged;

        internal SupervertalerLicence(
            string sharedPath, string legacyPath, string anchorKey, string legacyAnchorKey)
        {
            _sharedPathOverride = sharedPath;
            _legacyPathOverride = legacyPath;
            _anchorKey = anchorKey;
            _legacyAnchorKey = legacyAnchorKey;
            Load();
        }

        // ─── The interface ──────────────────────────────────────────

        /// <summary>What is known about the licence. Unknown is never a refusal.</summary>
        public LicenceState State
        {
            get { lock (_lock) return Resolve(); }
        }

        /// <summary>Days left in the trial, rounded up. 0 when not on trial.</summary>
        public int TrialDaysRemaining
        {
            get { lock (_lock) return Resolve() == LicenceState.Trial ? _rec.TrialDaysRemaining : 0; }
        }

        /// <summary>
        /// When the trial ends (UTC), whether or not it is still running.
        /// <see cref="DateTime.MinValue"/> if no trial has started. For display;
        /// <see cref="State"/> and <see cref="TrialDaysRemaining"/> are measured
        /// against a clock that cannot be wound back, so use those for logic.
        /// </summary>
        public DateTime TrialEndsUtc
        {
            get
            {
                lock (_lock)
                    return _rec.TrialStartedAt == DateTime.MinValue
                        ? DateTime.MinValue
                        : _rec.TrialStartedAt.AddDays(LicenceRecord.TrialDays);
            }
        }

        /// <summary>Whether a licence key has been entered.</summary>
        public bool HasKey
        {
            get { lock (_lock) return _rec.HasLicenseKey; }
        }

        /// <summary>The key for display: first 8 and last 4 characters.</summary>
        public string MaskedKey
        {
            get
            {
                lock (_lock)
                {
                    var key = _rec.LicenseKey ?? "";
                    if (key.Length <= 12) return key;
                    return key.Substring(0, 8) + "..." + key.Substring(key.Length - 4);
                }
            }
        }

        /// <summary>
        /// Last time the licence server confirmed the licence (UTC), or
        /// <see cref="DateTime.MinValue"/>. The offline window runs from here.
        /// </summary>
        public DateTime LastValidatedUtc
        {
            get { lock (_lock) return _rec.LastValidatedAt; }
        }

        /// <summary>What the customer bought, for display. Grants nothing.</summary>
        public string VariantName
        {
            get { lock (_lock) return _rec.VariantName ?? ""; }
        }

        /// <summary>
        /// True when a licence file was found that could not be made sense of,
        /// and no key has been activated since. The damaged file was set aside
        /// and a fresh record written, so a key it held needs entering again,
        /// and the product should say so - at every start until it is done, not
        /// only the first.
        ///
        /// The session that finds the damage is Unknown to the end, a failed
        /// attempt to re-enter the key included. Later sessions read the fresh
        /// record like any other: Trial, or Expired once the trial is over.
        /// </summary>
        public bool DamagedFileFound { get; private set; }

        // For the Trados plugin's trial registration, which reports the start it has.
        internal DateTime TrialStartedUtc
        {
            get { lock (_lock) return _rec.TrialStartedAt; }
        }

        internal bool IsActivated
        {
            get { lock (_lock) return _rec.IsActivated; }
        }

        // ─── Loading ────────────────────────────────────────────────

        private void Load()
        {
            try
            {
                LoadFrom(SharedPath);
            }
            catch (Exception ex)
            {
                // Anything unforeseen - a data folder path Windows rejects, say.
                // This runs inside a lazy singleton, so an exception here would
                // be cached and every later use would throw: a hard lockout on
                // an absence of information. Fail open instead.
                _rec = new LicenceRecord();
                _unreadable = true;
                WriteLog("The licence could not be loaded; carrying on with the licence state unknown. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void LoadFrom(string path)
        {
            LicenceFile.SweepAbandonedTemporaryFiles(path, AbandonedTempAge);

            var fingerprint = MachineId.GetFingerprint();

            var status = LicenceFile.TryRead(path, out var rec, out var bytes);
            var legacyDamaged = false;
            if (status == LicenceFile.ReadStatus.Missing)
                status = CreateSharedFile(path, fingerprint, out rec, out legacyDamaged);

            switch (status)
            {
                case LicenceFile.ReadStatus.Ok:
                    _rec = Prepare(rec, fingerprint);
                    if (legacyDamaged)
                    {
                        _unreadable = true;
                        _damagedThisSession = true;
                        WriteLog("The Trados licence file could not be read as a licence; a fresh shared record was written.");
                    }
                    break;

                case LicenceFile.ReadStatus.Damaged:
                    // Set aside, not overwritten: it may hold the only copy of a
                    // paying customer's key. A fresh record takes its place so
                    // the next session knows where it stands; this one does not.
                    SetAside(bytes);
                    _rec = FreshRecord(fingerprint);
                    _unreadable = true;
                    _damagedThisSession = true;
                    LicenceFile.Write(path, LicenceFile.Serialise(_rec), replaceExisting: true);
                    WriteLog("The licence file could not be read as a licence; it was set aside and a fresh record written.");
                    break;

                default:
                    // There, but could not be opened. Nothing is written, so
                    // nothing is lost; the next start tries again.
                    _rec = FreshRecord(fingerprint);
                    _unreadable = true;
                    WriteLog("The licence file could not be opened; carrying on with the licence state unknown.");
                    break;
            }

            DamagedFileFound = File.Exists(DamagedPath);
        }

        /// <summary>
        /// Keeps the bytes of a damaged licence file beside the shared one. Only
        /// the first: while a set-aside file exists no key has been activated
        /// since, so any later damaged file holds no key worth keeping, and
        /// there is only ever one of these.
        /// </summary>
        private void SetAside(byte[] damaged)
        {
            if (damaged == null || File.Exists(DamagedPath)) return;
            LicenceFile.Write(DamagedPath, damaged, replaceExisting: false);
        }

        /// <summary>A key was activated: the damaged file has served its purpose.</summary>
        private void ClearDamage()
        {
            try { File.Delete(DamagedPath); } catch { }
            DamagedFileFound = File.Exists(DamagedPath);
            _damagedThisSession = false;
        }

        /// <summary>
        /// Reads the Trados plugin's licence file for the copy. Older Trados
        /// builds still write it in place, so a read that lands while one is
        /// saving sees an empty or partial file - and a fresh shared file written
        /// on the strength of that would hide the customer's activation for
        /// good. So a damaged read is retried for about a second before it is
        /// believed. Only here: the shared file is never written that way.
        /// </summary>
        private LicenceFile.ReadStatus ReadTradosFile(out LicenceRecord rec, out byte[] bytes)
        {
            for (int attempt = 0; ; attempt++)
            {
                var status = LicenceFile.TryRead(LegacyPath, out rec, out bytes);
                if (status != LicenceFile.ReadStatus.Damaged || attempt >= 5)
                    return status;
                Thread.Sleep(200);
            }
        }

        /// <summary>
        /// The shared file does not exist yet. Creates it - a byte-for-byte copy
        /// of the Trados plugin's file if that can be read, otherwise a fresh
        /// record - but only if it is still absent. If another product created
        /// it first, reads theirs instead.
        /// </summary>
        private LicenceFile.ReadStatus CreateSharedFile(
            string path, string fingerprint, out LicenceRecord rec, out bool legacyDamaged)
        {
            legacyDamaged = false;

            byte[] first;
            switch (ReadTradosFile(out rec, out var legacyBytes))
            {
                case LicenceFile.ReadStatus.Ok:
                    first = legacyBytes;
                    break;

                case LicenceFile.ReadStatus.Unreadable:
                    // A fresh shared file would hide this one for good. Leave
                    // it for the next start to copy.
                    return LicenceFile.ReadStatus.Unreadable;

                case LicenceFile.ReadStatus.Damaged:
                    // Copied aside, since the Trados file itself is never written.
                    legacyDamaged = true;
                    SetAside(legacyBytes);
                    rec = FreshRecord(fingerprint);
                    first = LicenceFile.Serialise(rec);
                    break;

                default:
                    rec = FreshRecord(fingerprint);
                    first = LicenceFile.Serialise(rec);
                    break;
            }

            switch (LicenceFile.Write(path, first, replaceExisting: false))
            {
                case LicenceFile.WriteStatus.AlreadyExists:
                    legacyDamaged = false;
                    return LicenceFile.TryRead(path, out rec, out _);

                default:
                    // Written, or not - in which case the write was logged and
                    // this session runs on the record in hand, as it always has.
                    return LicenceFile.ReadStatus.Ok;
            }
        }

        /// <summary>A record for a computer with no readable licence file: the trial, from the anchor.</summary>
        private LicenceRecord FreshRecord(string fingerprint)
        {
            var now = DateTime.UtcNow;
            var anchored = TrialAnchor.Reconcile(_anchorKey, _legacyAnchorKey, fingerprint, now, now);
            return new LicenceRecord
            {
                TrialStartedAt = anchored.Start,
                EffectiveNow = anchored.EffectiveNow,
                MachineFingerprint = fingerprint,
            };
        }

        /// <summary>
        /// Readies a record read from disk: reconciles its trial start with the
        /// anchor, which can only ever pull it earlier, and fixes the clock the
        /// trial is measured against. Trial arithmetic uses the time of loading,
        /// so a trial never ends in the middle of a session.
        /// </summary>
        private LicenceRecord Prepare(LicenceRecord rec, string fingerprint)
        {
            var now = DateTime.UtcNow;
            rec.EffectiveNow = now;

            if (rec.TrialStartedAt > now)
                rec.TrialStartedAt = now;

            if (string.IsNullOrWhiteSpace(rec.MachineFingerprint))
                rec.MachineFingerprint = fingerprint;

            if (rec.TrialStartedAt != DateTime.MinValue)
            {
                var anchored = TrialAnchor.Reconcile(
                    _anchorKey, _legacyAnchorKey, fingerprint, rec.TrialStartedAt, now);
                rec.EffectiveNow = anchored.EffectiveNow;
                if (anchored.Start < rec.TrialStartedAt)
                {
                    rec.TrialStartedAt = anchored.Start;
                    LicenceFile.Write(SharedPath, LicenceFile.Serialise(rec), replaceExisting: true);
                }
            }

            return rec;
        }

        /// <summary>
        /// Replaces the record in memory with what is on disk now, if it can be
        /// read. Every change starts here, so it applies to what the other
        /// product may have just written rather than to this one's old copy.
        /// Caller holds the lock.
        /// </summary>
        private void RefreshFromDisk()
        {
            try
            {
                if (LicenceFile.TryRead(SharedPath, out var rec, out _) != LicenceFile.ReadStatus.Ok)
                    return;

                _rec = Prepare(rec, MachineId.GetFingerprint());

                // A file that could not be opened at startup and now can be:
                // the state is known again. A damaged one is different - the
                // record read now is the fresh one written over it, and the
                // session stays Unknown so a customer looking for their key is
                // not locked out by a failed attempt to enter it.
                if (!_damagedThisSession)
                    _unreadable = false;
            }
            catch (Exception ex)
            {
                WriteLog("The licence file could not be re-read: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Save()
        {
            try
            {
                LicenceFile.Write(SharedPath, LicenceFile.Serialise(_rec), replaceExisting: true);
            }
            catch (Exception ex)
            {
                WriteLog("The licence file could not be saved: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ─── State ──────────────────────────────────────────────────

        private LicenceState Resolve()
        {
            if (_unreadable)
                return LicenceState.Unknown;

            if (_rec.IsActivated && IsStatusActive() && IsCacheValid())
                return LicenceState.Licensed;

            // Activated, but not confirmed within the offline window.
            if (_rec.IsActivated && !IsCacheValid())
                return LicenceState.Expired;

            if (!_rec.HasLicenseKey && _rec.IsTrialActive)
                return LicenceState.Trial;

            return LicenceState.Expired;
        }

        private bool IsStatusActive()
        {
            // Empty counts as active: the activation succeeded (there is an
            // instance ID) but the reply may not have carried a status.
            if (string.IsNullOrEmpty(_rec.Status))
                return _rec.IsActivated;

            return string.Equals(_rec.Status, "active", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCacheValid()
        {
            if (_rec.LastValidatedAt == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - _rec.LastValidatedAt).TotalDays < OfflineCacheDays;
        }

        // ─── Activation ─────────────────────────────────────────────

        /// <summary>Activates a licence key on this computer.</summary>
        public async Task<(bool Ok, string Message)> ActivateAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return (false, "Please enter a licence key.");
            key = key.Trim();

            // Another product on this computer may already have activated this
            // key. Activating again would spend a second activation on the same
            // computer and orphan the first, so adopt it instead.
            if (IsActivatedHereWith(key))
            {
                // Read, activated and holding the key: known again, and any
                // damaged file set aside earlier has nothing left to recover.
                lock (_lock)
                {
                    _unreadable = false;
                    ClearDamage();
                }
                OnStateChanged();
                var (_, message) = await ValidateOnlineAsync().ConfigureAwait(false);
                return State == LicenceState.Licensed
                    ? (true, "This computer is already activated with this licence key.")
                    : (false, message);
            }

            try
            {
                var fingerprint = MachineId.GetFingerprint();

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("license_key", key),
                    new KeyValuePair<string, string>("instance_name", fingerprint),
                });

                var response = await Http.PostAsync(BaseUrl + "/activate", content).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = ParseLemonSqueezyResponse(json);

                if (!result.Activated)
                    return (false, result.Error ?? "Activation failed. Please check your licence key.");

                if (result.FromAnotherStore)
                {
                    // Hand back the activation the server has just made, so it
                    // does not hold a seat on someone else's key.
                    await ReleaseAsync(key, result.InstanceId).ConfigureAwait(false);
                    return (false, NotOurKeyMessage);
                }

                lock (_lock)
                {
                    RefreshFromDisk();
                    _rec.LicenseKey = key;
                    _rec.InstanceId = result.InstanceId;
                    _rec.VariantName = result.VariantName;
                    _rec.Status = result.Status;
                    _rec.ActivatedAt = DateTime.UtcNow;
                    _rec.LastValidatedAt = DateTime.UtcNow;
                    _rec.ExpiresAt = result.ExpiresAt;
                    _rec.MachineFingerprint = fingerprint;
                    _unreadable = false;
                    ClearDamage();
                    Save();
                }

                OnStateChanged();
                return (true, "Licence activated successfully.");
            }
            catch (HttpRequestException ex)
            {
                return (false, "Could not reach the licence server. Please check your internet connection.\n\n" + ex.Message);
            }
            catch (Exception ex)
            {
                return (false, "An error occurred during activation: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether this computer is already activated with <paramref name="key"/>,
        /// by either product. Re-reads the file, so it sees the other's activation.
        /// </summary>
        internal bool IsActivatedHereWith(string key)
        {
            lock (_lock)
            {
                RefreshFromDisk();
                return _rec.IsActivated
                    && string.Equals(_rec.LicenseKey, key?.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Deactivates the licence on this computer, freeing the activation.</summary>
        public async Task<(bool Ok, string Message)> DeactivateAsync()
        {
            string key, instance;
            lock (_lock)
            {
                RefreshFromDisk();
                if (!_rec.IsActivated)
                    return (false, "No active licence to deactivate.");
                key = _rec.LicenseKey;
                instance = _rec.InstanceId;
            }

            // Even if the server call fails, local state is cleared.
            await ReleaseAsync(key, instance).ConfigureAwait(false);

            lock (_lock)
            {
                RefreshFromDisk();
                // Clear the activation this call deactivated, and only that one.
                if (string.Equals(_rec.InstanceId, instance, StringComparison.Ordinal))
                {
                    _rec.ClearActivation();
                    _unreadable = false;
                    Save();
                }
            }

            OnStateChanged();
            return (true, "Licence deactivated. This machine's activation slot has been freed.");
        }

        // ─── Validation ─────────────────────────────────────────────

        /// <summary>
        /// Confirms the licence with the licence server. Each product calls it
        /// in the background at startup; it also backs a manual "verify" button.
        /// </summary>
        public async Task<(bool Ok, string Message)> ValidateOnlineAsync()
        {
            LicenceState before, now;
            string key = null, instance = null;
            lock (_lock)
            {
                before = Resolve();
                RefreshFromDisk();
                now = Resolve();
                if (_rec.IsActivated)
                {
                    key = _rec.LicenseKey;
                    instance = _rec.InstanceId;
                }
            }

            // Never raised under the lock: a subscriber that waits on its UI
            // thread while that thread waits on the lock would deadlock.
            if (instance == null)
            {
                if (now != before) OnStateChanged();
                return (false, "No active licence to validate.");
            }

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("license_key", key),
                    new KeyValuePair<string, string>("instance_id", instance),
                });

                var response = await Http.PostAsync(BaseUrl + "/validate", content).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = ParseLemonSqueezyResponse(json);

                // An answer we cannot read is not an answer. A reply that does
                // not parse, or that arrives in an unexpected shape, is treated
                // exactly like a transport failure: nothing stored changes, the
                // cached state stands, and the offline window runs down as it
                // would with no network at all. Only a reply we understood may
                // change licence state, and only one that says active may renew
                // the window. Do not relax either half; both directions of
                // getting this wrong are bad, one for the user and one for us.
                if (!result.Understood)
                    return (false, "Could not read the licence server's reply. Using cached licence state.");

                var after = ApplyValidationReply(result, key, instance);

                if (after != before)
                    OnStateChanged();

                if (result.FromAnotherStore)
                    return (false, NotOurKeyMessage);

                return result.Valid
                    ? (true, "Licence is valid.")
                    : (false, result.Error ?? "Licence validation failed.");
            }
            catch (HttpRequestException)
            {
                return (false, "Could not reach the licence server. Using cached licence state.");
            }
            catch (Exception ex)
            {
                return (false, "Validation error: " + ex.Message);
            }
        }

        /// <summary>
        /// Applies a validation reply the licence code understood to the
        /// activation it was about, and returns the resulting state. Separate
        /// from the network call so the rules below can be tested without one.
        /// </summary>
        internal LicenceState ApplyValidationReply(LemonSqueezyResult result, string key, string instance)
        {
            lock (_lock)
            {
                RefreshFromDisk();

                // A reply about a different activation - the other product
                // changed it while this request was out - is not applied.
                if (string.Equals(_rec.InstanceId, instance, StringComparison.Ordinal)
                    && string.Equals(_rec.LicenseKey, key, StringComparison.Ordinal))
                {
                    // A key from another store is known not to be a Supervertaler
                    // licence, so like a disabled one it takes effect at once.
                    _rec.Status = result.FromAnotherStore ? NotOurKeyStatus : result.Status;
                    _rec.VariantName = result.VariantName;
                    _rec.ExpiresAt = result.ExpiresAt;

                    // Only a reply that says ACTIVE renews the window. A
                    // reply that says disabled or expired still takes effect
                    // at once through Status above - it just does not buy
                    // another 30 days of offline grace.
                    if (IsStatusActive())
                        _rec.LastValidatedAt = DateTime.UtcNow;

                    Save();
                }
                return Resolve();
            }
        }

        /// <summary>Gives back an activation. Failures are ignored: there is nothing to do about them here.</summary>
        private static async Task ReleaseAsync(string key, string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("license_key", key),
                    new KeyValuePair<string, string>("instance_id", instanceId),
                });
                await Http.PostAsync(BaseUrl + "/deactivate", content).ConfigureAwait(false);
            }
            catch
            {
                // Offline or refused: the seat stays taken on the server.
            }
        }

        /// <summary>
        /// Tells every subscriber, each on its own. A subscriber that throws -
        /// a UI touching a control that has no window yet, say - is logged and
        /// skipped: it must not turn an activation that succeeded into a
        /// reported failure, nor keep the other subscribers from hearing of it.
        /// </summary>
        private void OnStateChanged()
        {
            var handlers = StateChanged;
            if (handlers == null) return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    WriteLog("A StateChanged subscriber failed and was skipped: " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ─── Lemon Squeezy response parsing ─────────────────────────

        /// <summary>
        /// Parses a reply from Lemon Squeezy's licence API:
        /// <c>{ "valid", "activated", "error", "license_key": { "status",
        /// "expires_at" }, "meta": { "variant_name", "store_id" }, "instance": { "id" } }</c>.
        /// </summary>
        internal static LemonSqueezyResult ParseLemonSqueezyResponse(string json)
        {
            var result = new LemonSqueezyResult();

            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(LsResponse));
                    var response = (LsResponse)serializer.ReadObject(stream);

                    result.Valid = response.Valid;
                    result.Activated = response.Activated;
                    result.Error = response.Error;

                    // "Understood" means the body parsed AND carried a
                    // recognisable licence block - which every real reply from
                    // this endpoint does, verified against the live API on
                    // 2026-09-19. It is the test ValidateOnlineAsync uses to
                    // decide whether the reply may change stored state at all.
                    result.Understood = response.LicenseKey != null
                        && !string.IsNullOrEmpty(response.LicenseKey.Status);

                    if (response.LicenseKey != null)
                    {
                        result.Status = response.LicenseKey.Status ?? "";

                        if (!string.IsNullOrWhiteSpace(response.LicenseKey.ExpiresAt)
                            && DateTime.TryParse(response.LicenseKey.ExpiresAt, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
                        {
                            result.ExpiresAt = expires;
                        }
                    }

                    if (response.Meta != null)
                    {
                        result.VariantName = response.Meta.VariantName ?? "";

                        // Only a store that is named, and is not ours, counts.
                        // A reply without one proves nothing either way, and
                        // an absence of information never locks anyone out.
                        result.FromAnotherStore = response.Meta.StoreId.HasValue
                            && response.Meta.StoreId.Value != SupervertalerStoreId;
                    }

                    if (response.Instance != null)
                        result.InstanceId = response.Instance.Id ?? "";
                }
            }
            catch
            {
                result.Error = "Failed to parse licence server response.";
            }

            return result;
        }

        internal class LemonSqueezyResult
        {
            /// <summary>The server named the store that issued the key, and it is not ours.</summary>
            public bool FromAnotherStore;

            /// <summary>
            /// The body parsed and carried a recognisable licence block. False
            /// for a non-JSON body, a parse failure, or a reply of an
            /// unexpected shape - all of which mean "we did not reach the
            /// server", never "the licence is fine".
            /// </summary>
            public bool Understood;
            public bool Valid;
            public bool Activated;
            public string Error;
            public string Status;
            public string VariantName;
            public string InstanceId;
            public DateTime? ExpiresAt;
        }

        [DataContract]
        private class LsResponse
        {
            [DataMember(Name = "valid")]
            public bool Valid { get; set; }

            [DataMember(Name = "activated")]
            public bool Activated { get; set; }

            [DataMember(Name = "error")]
            public string Error { get; set; }

            [DataMember(Name = "license_key")]
            public LsLicenseKey LicenseKey { get; set; }

            [DataMember(Name = "meta")]
            public LsMeta Meta { get; set; }

            [DataMember(Name = "instance")]
            public LsInstance Instance { get; set; }
        }

        [DataContract]
        private class LsLicenseKey
        {
            [DataMember(Name = "status")]
            public string Status { get; set; }

            [DataMember(Name = "expires_at")]
            public string ExpiresAt { get; set; }
        }

        [DataContract]
        private class LsMeta
        {
            [DataMember(Name = "variant_name")]
            public string VariantName { get; set; }

            [DataMember(Name = "store_id")]
            public long? StoreId { get; set; }
        }

        [DataContract]
        private class LsInstance
        {
            [DataMember(Name = "id")]
            public string Id { get; set; }
        }
    }
}
