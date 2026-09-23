using System;
using System.Runtime.Serialization;

namespace Supervertaler.Core
{
    /// <summary>
    /// The licence as stored on disk. Moved from the Trados plugin's
    /// <c>LicenseInfo</c> with its JSON names unchanged, so a Trados licence file
    /// copied to the shared location reads exactly as it did where it was.
    ///
    /// Persistence is <see cref="LicenceFile"/>'s job; this is only the data.
    /// Note that the JSON serializer runs no constructor and no initializer, so
    /// a field missing from the file comes back as null or its default.
    /// </summary>
    [DataContract]
    internal sealed class LicenceRecord
    {
        /// <summary>Trial length in days. The single source of truth.</summary>
        internal const int TrialDays = 14;

        /// <summary>The Lemon Squeezy licence key entered by the user.</summary>
        [DataMember(Name = "licenseKey")]
        public string LicenseKey { get; set; } = "";

        /// <summary>Instance ID from the activate call; ties this computer's activation to the key.</summary>
        [DataMember(Name = "instanceId")]
        public string InstanceId { get; set; } = "";

        /// <summary>What the customer bought, for display only. Grants nothing.</summary>
        [DataMember(Name = "variantName")]
        public string VariantName { get; set; } = "";

        [DataMember(Name = "activatedAt")]
        public DateTime ActivatedAt { get; set; } = DateTime.MinValue;

        /// <summary>Last successful online validation (UTC). Starts the offline window.</summary>
        [DataMember(Name = "lastValidatedAt")]
        public DateTime LastValidatedAt { get; set; } = DateTime.MinValue;

        [DataMember(Name = "expiresAt")]
        public DateTime? ExpiresAt { get; set; }

        /// <summary>Licence status from Lemon Squeezy: "active", "expired", "disabled".</summary>
        [DataMember(Name = "status")]
        public string Status { get; set; } = "";

        /// <summary>When the trial started (UTC). MinValue means it has not.</summary>
        [DataMember(Name = "trialStartedAt")]
        public DateTime TrialStartedAt { get; set; } = DateTime.MinValue;

        [DataMember(Name = "machineFingerprint")]
        public string MachineFingerprint { get; set; } = "";

        /// <summary>
        /// The "now" all trial arithmetic uses, from the trial anchor. Not
        /// stored: recomputed on every load.
        /// </summary>
        public DateTime EffectiveNow { get; set; } = DateTime.UtcNow;

        public bool HasLicenseKey => !string.IsNullOrWhiteSpace(LicenseKey);

        public bool IsActivated => !string.IsNullOrWhiteSpace(InstanceId);

        public bool IsTrialActive =>
            TrialStartedAt != DateTime.MinValue
            && (EffectiveNow - TrialStartedAt).TotalDays < TrialDays;

        /// <summary>Days left in the trial window, rounded up; 0 once it has ended.</summary>
        public int TrialDaysRemaining
        {
            get
            {
                if (TrialStartedAt == DateTime.MinValue) return 0;
                var remaining = TrialDays - (EffectiveNow - TrialStartedAt).TotalDays;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        /// <summary>Clears the activation. Keeps the trial start and the fingerprint.</summary>
        public void ClearActivation()
        {
            LicenseKey = "";
            InstanceId = "";
            VariantName = "";
            ActivatedAt = DateTime.MinValue;
            LastValidatedAt = DateTime.MinValue;
            ExpiresAt = null;
            Status = "";
        }
    }
}
