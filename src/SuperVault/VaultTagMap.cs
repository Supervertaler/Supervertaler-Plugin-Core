using System.Collections.Generic;

namespace Supervertaler.Core.SuperVault
{
    public enum VaultTagOutcome
    {
        /// <summary>Neither the document segment nor the stored unit has tags.</summary>
        NoTags,
        /// <summary>The stored source has the same tags as the document segment, and every target tag maps onto one of them.</summary>
        Mapped,
        /// <summary>The tags can't be placed with confidence. The unit is offered as text, without tags.</summary>
        Unmapped,
    }

    /// <summary>
    /// Places a stored unit's tags onto the document segment's tags, so that inserting a SuperVault match brings the
    /// formatting along, as a match from a CAT tool's own TM would.
    ///
    /// The rule is deliberately strict. The stored source's tags must have the same shape as the document's: the
    /// same number, the same opening/closing/standalone order. Then the i-th stored source tag is the i-th document
    /// tag, and each target tag follows its source counterpart (paired by id, or by order of appearance). Anything
    /// else - a different number of tags, a target tag with no source counterpart, tags on one side only - is
    /// <see cref="VaultTagOutcome.Unmapped"/>: the unit is offered as plain text and the caller applies the CAT
    /// tool's formatting penalty, rather than guessing where formatting goes.
    /// </summary>
    public sealed class VaultTagMap
    {
        public VaultTagOutcome Outcome;
        /// <summary>For each stored source piece: the index of the document piece its tag maps to, or -1 (text, or unmapped).</summary>
        public int[] Source;
        /// <summary>For each stored target piece: the index of the document piece its tag maps to, or -1.</summary>
        public int[] Target;
        /// <summary>Why the tags weren't mapped, for diagnostics and tests.</summary>
        public string Reason;

        public static VaultTagMap Build(IList<VaultPiece> document, IList<VaultPiece> source, IList<VaultPiece> target)
        {
            var map = new VaultTagMap { Source = Fill(source.Count), Target = Fill(target.Count) };
            var docTags = TagIndexes(document);
            var srcTags = TagIndexes(source);
            var trgTags = TagIndexes(target);

            if (docTags.Count == 0 && srcTags.Count == 0 && trgTags.Count == 0) { map.Outcome = VaultTagOutcome.NoTags; return map; }
            if (docTags.Count != srcTags.Count) return Fail(map, $"the document has {docTags.Count} tag(s), the stored source {srcTags.Count}");
            for (int i = 0; i < docTags.Count; i++)
                if (document[docTags[i]].Kind != source[srcTags[i]].Kind)
                    return Fail(map, $"tag {i + 1} is {document[docTags[i]].Kind} in the document but {source[srcTags[i]].Kind} in the stored source");

            var byKey = new Dictionary<string, int>();
            for (int i = 0; i < srcTags.Count; i++)
            {
                var key = source[srcTags[i]].Key ?? source[srcTags[i]].Raw;
                if (byKey.ContainsKey(key)) return Fail(map, "the stored source repeats tag " + key);
                byKey[key] = docTags[i];
                map.Source[srcTags[i]] = docTags[i];
            }
            var used = new HashSet<int>();
            foreach (var t in trgTags)
            {
                var key = target[t].Key ?? target[t].Raw;
                if (!byKey.TryGetValue(key, out int doc)) return Fail(map, "target tag " + target[t].Raw + " has no counterpart in the stored source");
                if (document[doc].Kind != target[t].Kind) return Fail(map, "target tag " + target[t].Raw + " opens/closes differently from its source counterpart");
                if (!used.Add(doc)) return Fail(map, "target tag " + target[t].Raw + " appears twice");
                map.Target[t] = doc;
            }
            map.Outcome = VaultTagOutcome.Mapped;
            return map;
        }

        static VaultTagMap Fail(VaultTagMap map, string reason)
        {
            map.Outcome = VaultTagOutcome.Unmapped;
            map.Reason = reason;
            for (int i = 0; i < map.Source.Length; i++) map.Source[i] = -1;
            for (int i = 0; i < map.Target.Length; i++) map.Target[i] = -1;
            return map;
        }

        static int[] Fill(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = -1; return a; }

        static List<int> TagIndexes(IList<VaultPiece> pieces)
        {
            var list = new List<int>();
            for (int i = 0; i < pieces.Count; i++) if (pieces[i].IsTag) list.Add(i);
            return list;
        }
    }
}
