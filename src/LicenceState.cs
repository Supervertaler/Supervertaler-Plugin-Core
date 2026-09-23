namespace Supervertaler.Core
{
    /// <summary>
    /// What is known about this computer's Supervertaler licence. One licence
    /// covers every product, and an activation is a computer, not a product
    /// install - so every product reads the same answer.
    ///
    /// Two rules for anything that reads it:
    ///
    ///   - <see cref="Unknown"/> is never a refusal. It means the licence could
    ///     not be read, not that there is none, and the product carries on. A
    ///     paying customer must never be locked out by an absence of information.
    ///   - It is a state, not a permission. What a product does about an expired
    ///     trial is the product's decision; the dates behind the state are on
    ///     <see cref="SupervertalerLicence"/>. There is deliberately no
    ///     "IsAllowed" anywhere in the licence code.
    /// </summary>
    public enum LicenceState
    {
        /// <summary>
        /// The licence could not be read this session. Never a refusal. The
        /// default value, so anything that forgets to set a state fails open.
        /// </summary>
        Unknown = 0,

        /// <summary>An activated licence the licence server has confirmed within the offline window.</summary>
        Licensed,

        /// <summary>No licence key yet, and the free trial is still running.</summary>
        Trial,

        /// <summary>
        /// Known to be over: the trial has ended with no key entered, the
        /// licence server has said the licence is no longer active, or it has
        /// not been confirmed within the offline window.
        /// </summary>
        Expired,
    }
}
