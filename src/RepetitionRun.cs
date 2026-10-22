using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>Where the translation a segment gets came from.</summary>
    public enum RepetitionSource
    {
        /// <summary>Nothing: its own translation failed and its group has none yet.</summary>
        None,

        /// <summary>Its own: the first in its group, or not in a group at all.</summary>
        Own,

        /// <summary>Its group's, in place of its own or of a failure.</summary>
        Group,
    }

    /// <summary>What to deliver for one segment, and why.</summary>
    public sealed class RepetitionDelivery
    {
        internal RepetitionDelivery(RepetitionSource source, string text, bool ownFailed, bool differed)
        {
            Source = source;
            Text = text;
            OwnFailed = ownFailed;
            Differed = differed;
        }

        public RepetitionSource Source { get; }

        /// <summary>The translation to write, in the notation the host sends; null for <see cref="RepetitionSource.None"/>.</summary>
        public string Text { get; }

        /// <summary>The segment's own translation had failed; the group's fills it.</summary>
        public bool OwnFailed { get; }

        /// <summary>The segment's own translation was worded differently from the group's.</summary>
        public bool Differed { get; }
    }

    /// <summary>A group whose kept, existing translations disagree. Listed; nothing is changed.</summary>
    public sealed class RepetitionConflict
    {
        internal RepetitionConflict(string firstLabel, IEnumerable<string> otherLabels)
        {
            FirstLabel = firstLabel;
            OtherLabels = new List<string>(otherLabels).AsReadOnly();
        }

        /// <summary>The segment whose translation the group took, or null when a write in the run fixed it.</summary>
        public string FirstLabel { get; }

        /// <summary>The kept segments whose translation differs from it.</summary>
        public IReadOnlyList<string> OtherLabels { get; }
    }

    /// <summary>
    /// The repetitions of one Batch Translate (Trados) or Pre-translate (memoQ)
    /// run, so that every occurrence of a source gets one translation
    /// (docs/design/consistent-repetitions.md, approved 22 Oct 2026). Nothing sent
    /// to the AI changes: the AI still translates every segment, and a later
    /// repetition is given its group's translation instead of its own.
    ///
    /// <para>A group's translation is fixed once, by the first of:
    /// an existing target the run keeps (<see cref="Keep"/>, Trados, before any
    /// write - a TM match on the first occurrence, say), or the first translation
    /// the run delivers for any of its segments. Every later delivery in the group
    /// gets it, including one whose own translation failed. A segment the run does
    /// not write is never changed; if its existing target differs from the
    /// group's, the group is listed as a conflict.</para>
    ///
    /// <para>Two ways to deliver. Trados writes one segment at a time on one
    /// thread and Studio may decline a write, so it asks <see cref="Decide"/>,
    /// writes, and calls <see cref="Commit"/> only when the write went through - a
    /// declined write must not fix its group. memoQ calls in from parallel
    /// sessions and nothing can decline, so it calls <see cref="Settle"/>, which
    /// decides and commits as one step: two occurrences answered at once cannot
    /// both become their group's first.</para>
    ///
    /// <para>One lock around every read and write, held for a dictionary lookup at
    /// most - never across a model call. Every key seen takes one small entry, up
    /// to <see cref="DefaultMaxGroups"/>; past that, new sources are delivered as
    /// they are and counted as not checked, rather than letting a run that never
    /// ends grow without bound.</para>
    /// </summary>
    public sealed class RepetitionRun
    {
        public const int DefaultMaxGroups = 50000;

        private readonly object _lock = new object();
        private readonly Dictionary<string, Group> _groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        private readonly List<Group> _conflicts = new List<Group>();
        private readonly int _maxGroups;

        private int _delivered, _aligned, _filled, _differed, _untracked;

        public RepetitionRun() : this(DefaultMaxGroups) { }

        /// <summary>A smaller cap, for tests.</summary>
        internal RepetitionRun(int maxGroups)
        {
            _maxGroups = Math.Max(1, maxGroups);
        }

        private sealed class Group
        {
            public string Translation;
            public string KeptBy;

            /// <summary>Kept segments whose target differs from <see cref="Translation"/>; null until there is one.</summary>
            public List<string> Disagreeing;
        }

        /// <summary>Segments delivered a translation, own or group.</summary>
        public int Delivered { get { lock (_lock) return _delivered; } }

        /// <summary>Later repetitions given their group's translation.</summary>
        public int Aligned { get { lock (_lock) return _aligned; } }

        /// <summary>Of <see cref="Aligned"/>: the segment's own translation had failed.</summary>
        public int Filled { get { lock (_lock) return _filled; } }

        /// <summary>Of <see cref="Aligned"/>: the AI had worded the repetition differently.</summary>
        public int Differed { get { lock (_lock) return _differed; } }

        /// <summary>Segments delivered as they were because the run was already tracking its maximum of sources.</summary>
        public int Untracked { get { lock (_lock) return _untracked; } }

        /// <summary>The conflicts so far, in the order they were found; a snapshot, safe to keep.</summary>
        public List<RepetitionConflict> Conflicts
        {
            get
            {
                lock (_lock)
                {
                    var list = new List<RepetitionConflict>(_conflicts.Count);
                    foreach (var group in _conflicts) list.Add(new RepetitionConflict(group.KeptBy, group.Disagreeing));
                    return list;
                }
            }
        }

        /// <summary>
        /// A segment this run will not write - locked, outside what the run
        /// overwrites - with the target it already has. Called before any
        /// delivery, in document order. Its target becomes its group's
        /// translation if the group has none; if it has one and this differs,
        /// the group is a conflict. An empty or blank target is no target.
        /// </summary>
        public void Keep(string key, string existingTarget, string label)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(existingTarget)) return;

            lock (_lock)
            {
                var group = GroupFor(key);
                if (group == null) return;

                if (group.Translation == null)
                {
                    group.Translation = existingTarget;
                    group.KeptBy = label;
                    return;
                }

                if (string.Equals(group.Translation, existingTarget, StringComparison.Ordinal)) return;

                if (group.Disagreeing == null)
                {
                    group.Disagreeing = new List<string>();
                    _conflicts.Add(group);
                }
                group.Disagreeing.Add(label);
            }
        }

        /// <summary>
        /// What a segment gets, without recording anything: its group's
        /// translation when the group has one, otherwise its own
        /// (<paramref name="own"/>, null when it failed). Follow with
        /// <see cref="Commit"/> once the delivery is written.
        /// </summary>
        public RepetitionDelivery Decide(string key, string own)
        {
            lock (_lock) return DecideLocked(key, own);
        }

        /// <summary>
        /// Records a delivery that was written. The first <see cref="RepetitionSource.Own"/>
        /// one in a group fixes the group's translation.
        /// </summary>
        public void Commit(string key, RepetitionDelivery delivery)
        {
            if (delivery == null) return;
            lock (_lock) CommitLocked(key, delivery);
        }

        /// <summary><see cref="Decide"/> and <see cref="Commit"/> as one step, for callers on several threads.</summary>
        public RepetitionDelivery Settle(string key, string own)
        {
            lock (_lock)
            {
                var delivery = DecideLocked(key, own);
                CommitLocked(key, delivery);
                return delivery;
            }
        }

        private RepetitionDelivery DecideLocked(string key, string own)
        {
            if (!string.IsNullOrEmpty(key)
                && _groups.TryGetValue(key, out var group)
                && group.Translation != null)
            {
                return new RepetitionDelivery(RepetitionSource.Group, group.Translation,
                    ownFailed: own == null,
                    differed: own != null && !string.Equals(own, group.Translation, StringComparison.Ordinal));
            }

            return own == null
                ? new RepetitionDelivery(RepetitionSource.None, null, ownFailed: true, differed: false)
                : new RepetitionDelivery(RepetitionSource.Own, own, ownFailed: false, differed: false);
        }

        private void CommitLocked(string key, RepetitionDelivery delivery)
        {
            if (delivery.Source == RepetitionSource.None) return;

            _delivered++;

            if (delivery.Source == RepetitionSource.Group)
            {
                _aligned++;
                if (delivery.OwnFailed) _filled++;
                if (delivery.Differed) _differed++;
                return;
            }

            if (string.IsNullOrEmpty(key)) return;

            var group = GroupFor(key);
            if (group == null)
            {
                _untracked++;
                return;
            }

            if (group.Translation == null) group.Translation = delivery.Text;
        }

        /// <summary>The key's group, made if need be; null when the run is full.</summary>
        private Group GroupFor(string key)
        {
            if (_groups.TryGetValue(key, out var group)) return group;
            if (_groups.Count >= _maxGroups) return null;

            group = new Group();
            _groups.Add(key, group);
            return group;
        }

        /// <summary>
        /// One line for the end of the run, then one line per conflict:
        /// "586 segments – 130 repetitions aligned to their first occurrence – 2
        /// groups with conflicting existing translations (listed)."
        /// </summary>
        public string Summary()
        {
            lock (_lock)
            {
                var text = new StringBuilder();
                text.Append(Count(_delivered, "segment", "segments"));
                text.Append(" – ").Append(Count(_aligned, "repetition", "repetitions"))
                    .Append(_aligned == 1 ? " aligned to its first occurrence" : " aligned to their first occurrence");

                var details = new List<string>();
                if (_differed > 0) details.Add(_differed.ToString("N0", CultureInfo.InvariantCulture) + " worded differently by the AI");
                if (_filled > 0) details.Add(_filled.ToString("N0", CultureInfo.InvariantCulture) + " in place of a failed translation");
                if (details.Count > 0) text.Append(" (").Append(string.Join(", ", details)).Append(')');

                if (_conflicts.Count > 0)
                    text.Append(" – ").Append(Count(_conflicts.Count, "group", "groups"))
                        .Append(" with conflicting existing translations (listed)");

                if (_untracked > 0)
                    text.Append(" – ").Append(Count(_untracked, "segment", "segments"))
                        .Append(" not checked: the run had already seen ")
                        .Append(_maxGroups.ToString("N0", CultureInfo.InvariantCulture)).Append(" different sources");

                text.Append('.');

                foreach (var group in _conflicts)
                {
                    text.AppendLine();
                    text.Append("Different existing translations: ")
                        .Append(group.KeptBy ?? "this run's translation")
                        .Append(" vs ").Append(string.Join(", ", group.Disagreeing));
                }

                return text.ToString();
            }
        }

        private static string Count(int n, string one, string many) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
    }
}
