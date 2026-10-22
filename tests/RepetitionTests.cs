using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The match key and the per-run groups behind "consistent repetitions in
    /// Batch Translate" (docs/design/consistent-repetitions.md). About half the
    /// repeated sentences in recent runs came back worded differently, because
    /// each occurrence is translated on its own; a run now gives every later
    /// repetition its group's translation. The acceptance cases of the design
    /// that are logic rather than a CAT tool are here; the numbers in the test
    /// names are the design's.
    /// </summary>
    [Tests]
    internal static class RepetitionTests
    {
        private static RepetitionToken T(string text) => RepetitionToken.Text(text);
        private static RepetitionToken O(string kind) => RepetitionToken.Open(kind);
        private static RepetitionToken C(string kind) => RepetitionToken.Close(kind);
        private static RepetitionToken S(string kind) => RepetitionToken.Standalone(kind);
        private static string Key(params RepetitionToken[] tokens) => RepetitionKey.Of(tokens);

        // ---- the key ---------------------------------------------------------

        public static void Case3_SameTextAndTagKinds_ShareAKey_WhateverTheTagIds()
        {
            // The host never passes ids: "<t1>Figuur 7</t1>" and "<t3>Figuur 7</t3>"
            // both reach the key as an open b, text, a close b.
            Assert.Equal(Key(O("b"), T("Figuur 7"), C("b"), T(" toont een schema.")),
                         Key(O("b"), T("Figuur 7"), C("b"), T(" toont een schema.")), "same sentence, tags renumbered");
        }

        public static void Case4_NonBreakingSpace_IsNotAnOrdinarySpace()
        {
            Assert.True(Key(T("5\u00A0%")) != Key(T("5 %")), "an NBSP and a space give different keys");
            Assert.True(Key(T("5\u202F%")) != Key(T("5\u00A0%")), "a narrow NBSP is not an NBSP");
        }

        public static void LineBreaks_SoftReturns_Tabs_AreKeptApart()
        {
            Assert.True(Key(T("a\u2028b")) != Key(T("a b")), "a soft return is not a space (Trados sends it as one; the key must not)");
            Assert.True(Key(T("a\nb")) != Key(T("a b")), "a line break is not a space");
            Assert.True(Key(T("a\tb")) != Key(T("a b")), "a tab is not a space");
            Assert.True(Key(T("a\u2028b")) != Key(T("a\nb")), "a soft return is not a line break");
        }

        public static void UnicodeSubscript_IsNotATaggedSubscript()
        {
            Assert.True(Key(T("CH\u2084")) != Key(T("CH"), O("sub"), T("4"), C("sub")), "CH\u2084 is not CH<sub>4</sub>");
        }

        public static void Case_Leading_AndTrailingSpace_Count()
        {
            Assert.True(Key(T("Figuur 7")) != Key(T("figuur 7")), "case counts");
            Assert.True(Key(T("Figuur 7 ")) != Key(T("Figuur 7")), "a trailing space counts");
        }

        public static void TagKind_Role_Order_AndNesting_Count()
        {
            Assert.True(Key(O("b"), T("x"), C("b")) != Key(O("i"), T("x"), C("i")), "bold is not italic around the same words");
            Assert.True(Key(S("ph"), T("x")) != Key(O("ph"), C("ph"), T("x")), "a placeholder is not an empty tag pair");
            Assert.True(Key(O("x"), T("a"), C("x")) != Key(S("x"), T("a"), C("x")), "an opening tag is not a placeholder in the same place");
            Assert.True(Key(O("x"), T("a"), C("x")) != Key(O("x"), T("a"), S("x")), "nor a closing one");
            Assert.True(Key(O("b"), O("i"), T("x"), C("i"), C("b")) != Key(O("i"), O("b"), T("x"), C("b"), C("i")), "the nesting order counts");
            Assert.True(Key(O("b"), T("x"), C("b"), T("y")) != Key(O("b"), T("x"), T("y"), C("b")), "where a tag closes counts");
            Assert.True(Key(S("x"), T("a")) != Key(T("a"), S("x")), "where a placeholder stands counts");
        }

        public static void HowAHostSplitsText_DecidesNothing()
        {
            Assert.Equal(Key(T("abc")), Key(T("ab"), T("c")), "adjacent text joins");
            Assert.Equal(Key(T("abc")), Key(T(""), T("abc"), T("")), "empty text is nothing");
            Assert.Equal(Key(T("abc")), Key(T("abc"), null), "a null token is nothing");
        }

        public static void NoTextCanImitateATagOrAnEdge()
        {
            // Joined without lengths, each pair below would be the same string.
            Assert.True(Key(T("a\u001FSb")) != Key(T("a"), S("b")), "a separator inside text is still text");
            Assert.True(Key(T("a\u001FS1:b")) != Key(T("a"), S("b")), "even when it imitates a whole token");
            Assert.True(Key(T("T1:a")) != Key(T("a")), "a role-and-length prefix inside text is still text");
            Assert.True(Key(O("b\u001FT1:x")) != Key(O("b"), T("x")), "nor inside a tag's kind");
        }

        public static void NothingHasAnEmptyKey_TagsAloneDoNot()
        {
            Assert.Equal("", Key(), "no tokens");
            Assert.Equal("", RepetitionKey.Of(null), "no list");
            Assert.True(Key(S("x")).Length > 0, "a segment of tags alone has a key; whether to group it is the host's call");
        }

        // ---- the run ---------------------------------------------------------

        public static void Case1_AnExactRepeat_GetsTheFirstOccurrencesTranslation()
        {
            var run = new RepetitionRun();
            var k = Key(T("Het verbruik daalt bijvoorbeeld met minstens 5%."));

            var first = run.Settle(k, "Consumption falls, for example, by at least 5%.");
            var second = run.Settle(k, "Consumption drops, for example, by at least 5%.");

            Assert.Equal(RepetitionSource.Own, first.Source, "the first occurrence keeps its own");
            Assert.Equal(RepetitionSource.Group, second.Source, "the repeat gets the group's");
            Assert.Equal("Consumption falls, for example, by at least 5%.", second.Text, "and it is the first one's wording");
            Assert.True(second.Differed && !second.OwnFailed, "the AI had worded it differently, and did not fail");
            Assert.Equal(2, run.Delivered, "two delivered");
            Assert.Equal(1, run.Aligned, "one aligned");
            Assert.Equal(1, run.Differed, "one differed");
        }

        public static void Case2_ARepeatInAnotherFile_IsTheSameGroup()
        {
            // Files are labels, not part of the key: grouping runs across every
            // file in scope.
            var run = new RepetitionRun();
            var k = Key(T("Een standaard met drie poten."));
            run.Settle(k, "A stand with three legs.");
            var other = run.Settle(k, "A three-legged stand.");
            Assert.Equal("A stand with three legs.", other.Text, "File 2's repeat takes File 1's translation");
        }

        public static void Case5_AKeptTmMatch_IsWhatTheEmptyOccurrencesGet()
        {
            var run = new RepetitionRun();
            var k = Key(T("Medewerkers in loondienst."));

            run.Keep(k, "Salaried employees.", "File 1, segment 3");
            var empty = run.Settle(k, "Employees on the payroll.");

            Assert.Equal(RepetitionSource.Group, empty.Source, "the AI's version is set aside");
            Assert.Equal("Salaried employees.", empty.Text, "for the kept target");
        }

        public static void Case6_TwoKeptTargetsDisagree_ListedAndLeftAlone()
        {
            var run = new RepetitionRun();
            var k = Key(T("Niet van toepassing."));

            run.Keep(k, "Not applicable.", "File 1, segment 4");
            run.Keep(k, "N/A.", "File 1, segment 9");
            run.Keep(k, "Not applicable.", "File 1, segment 12");

            var conflicts = run.Conflicts;
            Assert.Equal(1, conflicts.Count, "one group conflicts");
            Assert.Equal("File 1, segment 4", conflicts[0].FirstLabel, "against the segment the group took its translation from");
            Assert.Equal("File 1, segment 9", string.Join("|", conflicts[0].OtherLabels), "only the one that differs is listed");

            // Nothing is changed: a kept segment is never delivered anything, and
            // the group keeps the first kept translation for the run's own writes.
            Assert.Equal(0, run.Delivered, "keeping writes nothing");
            Assert.Equal("Not applicable.", run.Settle(k, "x").Text, "the first kept translation stands");
        }

        public static void Case7_ALockedEmptySegment_FixesNothing()
        {
            // A locked segment is kept and never written. Locked and empty, it has
            // no translation to give: the first write fixes the group instead.
            var run = new RepetitionRun();
            var k = Key(T("Zorgverzekeringswet."));
            run.Keep(k, null, "File 1, segment 2 (locked)");
            run.Keep(k, "   ", "File 1, segment 5 (blank)");
            Assert.Equal("Health Insurance Act.", run.Settle(k, "Health Insurance Act.").Text, "the first write is the group's");
            Assert.Equal(0, run.Conflicts.Count, "an empty target conflicts with nothing");
        }

        public static void AFailedFirstOccurrence_TheNextSuccessFixesTheGroup_AndFillsIt()
        {
            var run = new RepetitionRun();
            var k = Key(T("De lamp omvat een behuizing."));

            var failed = run.Settle(k, null);
            Assert.Equal(RepetitionSource.None, failed.Source, "nothing to give yet");
            Assert.True(failed.Text == null, "and no text");

            Assert.Equal(RepetitionSource.Own, run.Settle(k, "The lamp comprises a housing.").Source, "the next success is the group's");

            var retried = run.Settle(k, null);
            Assert.Equal("The lamp comprises a housing.", retried.Text, "a failure retried later is filled from the group");
            Assert.True(retried.OwnFailed, "and says so");
            Assert.Equal(2, run.Delivered, "a failure with nothing to give is not delivered");
            Assert.Equal(1, run.Filled, "one filled");
        }

        public static void ADeclinedWrite_DoesNotFixItsGroup()
        {
            // Trados: decide, write, commit only when Studio took the write.
            var run = new RepetitionRun();
            var k = Key(T("De lamp omvat een lichtbron."));

            var declined = run.Decide(k, "The lamp comprises a light source.");
            Assert.Equal(RepetitionSource.Own, declined.Source, "the first decides its own");
            // ... Studio declined it: no Commit.

            var next = run.Decide(k, "The lamp has a light source.");
            Assert.Equal(RepetitionSource.Own, next.Source, "so the next is still the first");
            run.Commit(k, next);

            Assert.Equal("The lamp has a light source.", run.Decide(k, "anything").Text, "the written one fixed the group");
            Assert.Equal(1, run.Delivered, "only the written one counts");
        }

        public static void ASourceWithNoKey_IsNeverGrouped()
        {
            var run = new RepetitionRun();
            Assert.Equal("a", run.Settle("", "a").Text, "own");
            Assert.Equal("b", run.Settle("", "b").Text, "own again: no key, no group");
            Assert.Equal("c", run.Settle(null, "c").Text, "nor for a null key");
            Assert.Equal(0, run.Aligned, "nothing aligned");
            run.Keep("", "kept", "label");
            Assert.Equal(0, run.Conflicts.Count, "keeping without a key is ignored");
        }

        public static void PastTheCap_NewSourcesAreDeliveredAsTheyAre_AndCounted()
        {
            var run = new RepetitionRun(2);
            run.Settle(Key(T("one")), "1");
            run.Settle(Key(T("two")), "2");

            Assert.Equal("3a", run.Settle(Key(T("three")), "3a").Text, "a third source is not tracked");
            Assert.Equal("3b", run.Settle(Key(T("three")), "3b").Text, "so its repeat is not aligned");
            Assert.Equal("1", run.Settle(Key(T("one")), "1b").Text, "sources already tracked still align");
            Assert.Equal(2, run.Untracked, "two deliveries not checked");
            Assert.True(run.Summary().Contains("2 segments not checked"), "and the summary says so: " + run.Summary());
        }

        public static void TheSummary_SaysWhatHappened()
        {
            var run = new RepetitionRun();
            var a = Key(T("a"));
            var b = Key(T("b"));
            var c = Key(T("c"));

            run.Keep(c, "C1", "File 1, segment 7");
            run.Keep(c, "C2", "File 2, segment 3");
            run.Settle(a, "A");
            run.Settle(a, "A");        // aligned, same wording
            run.Settle(a, "A other");  // aligned, worded differently
            run.Settle(b, "B");
            run.Settle(b, null);       // aligned, filled

            Assert.Equal(
                "5 segments – 3 repetitions aligned to their first occurrence (1 worded differently by the AI, 1 in place of a failed translation)"
                + " – 1 group with conflicting existing translations (listed)."
                + Environment.NewLine + "Different existing translations: File 1, segment 7 vs File 2, segment 3",
                run.Summary(), "the summary");

            var one = new RepetitionRun();
            one.Settle(a, "A");
            one.Settle(a, "A");
            Assert.Equal("2 segments – 1 repetition aligned to its first occurrence.", one.Summary(), "singular");

            Assert.Equal("0 segments – 0 repetitions aligned to their first occurrence.", new RepetitionRun().Summary(), "an empty run");
        }

        public static void ManyThreadsAtOnce_EveryOccurrenceGetsOneTranslation()
        {
            // memoQ answers a Pre-translate from parallel sessions: two occurrences
            // can come back at the same moment. Exactly one may become the group's.
            const int threads = 16, keys = 300;
            var run = new RepetitionRun();
            var seen = new ConcurrentDictionary<int, ConcurrentBag<string>>();
            var errors = new ConcurrentBag<Exception>();
            var start = new Barrier(threads);

            // An exception on a worker thread would end the whole test process;
            // caught here, it fails this test instead.
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (int k = 0; k < keys; k++)
                    {
                        var delivered = run.Settle(RepetitionKey.Of(new[] { T("source " + k) }), "thread " + t);
                        seen.GetOrAdd(k, _ => new ConcurrentBag<string>()).Add(delivered.Text);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            })).ToList();

            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());

            Assert.True(errors.IsEmpty, "no thread failed: " + string.Join("; ", errors.Select(e => e.GetType().Name + ": " + e.Message)));
            Assert.Equal(keys, seen.Count, "every source seen");
            Assert.True(seen.Values.All(bag => bag.Distinct().Count() == 1 && bag.Count == threads),
                "every occurrence of a source got the same translation");
            Assert.Equal(threads * keys, run.Delivered, "every occurrence delivered");
            Assert.Equal((threads - 1) * keys, run.Aligned, "all but the first of each aligned");
        }
    }
}
