using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Reading a batch reply back into segments. A translation can contain its
    /// own numbered lines - a contents list, the steps of a procedure - and none
    /// of them may be taken for the start of another segment. On 2026-09-24 a
    /// contents list's "4.1 ..." lines were cut off segment 60 and appended to
    /// segment 4, with the "4." stripped.
    /// </summary>
    [Tests]
    internal static class TranslationPromptTests
    {
        private static Dictionary<int, string> Parse(string reply, params (int n, string source)[] sent)
        {
            var segments = sent.Select(s => new BatchSegmentInput { Number = s.n, SourceText = s.source }).ToList();
            return TranslationPrompt.ParseBatchResponse(reply.Replace("\r", ""), segments)
                .ToDictionary(p => p.Number, p => p.Translation.Replace("\r", ""));
        }

        private static Dictionary<int, string> ParseWithoutSources(string reply) =>
            TranslationPrompt.ParseBatchResponse(reply.Replace("\r", ""), 0)
                .ToDictionary(p => p.Number, p => p.Translation.Replace("\r", ""));

        private const string TocSource = "4 Kaizen\n4.1 Introduction to Kaizen\n4.2 The five S\n4.3 Waste";
        private const string TocReply =
            "59. Voorwoord\n" +
            "60. 4 Kaizen\n4.1 Inleiding tot Kaizen\n4.2 De vijf S'en\n4.3 Verspilling\n" +
            "61. Doelen\n" +
            "62. Samenvatting";

        public static void SubNumberedLines_StayInTheirSegment()
        {
            var r = Parse(TocReply, (59, "Foreword"), (60, TocSource), (61, "Goals"), (62, "Summary"));

            Assert.Equal(4, r.Count, "segments");
            Assert.Equal("4 Kaizen\n4.1 Inleiding tot Kaizen\n4.2 De vijf S'en\n4.3 Verspilling", r[60], "segment 60 whole");
            Assert.Equal("Doelen", r[61], "segment 61 untouched");
            Assert.True(!r.ContainsKey(4), "nothing invented for segment 4");
        }

        public static void SubNumberedLines_StayInTheirSegment_EvenWithoutTheSources()
        {
            var r = ParseWithoutSources(TocReply);
            Assert.Equal(4, r.Count, "segments");
            Assert.True(r[60].EndsWith("4.3 Verspilling"), "segment 60 whole: " + r[60]);
            Assert.True(!r.ContainsKey(4), "nothing invented for segment 4");
        }

        public static void ANumberedListInALaterSegment_StaysInIt()
        {
            var r = Parse("6. Begin\n7. Stappen:\n1. Openen\n2. Sluiten\n8. Klaar",
                (6, "Start"), (7, "Steps:\n1. Open\n2. Close"), (8, "Done"));

            Assert.Equal("Stappen:\n1. Openen\n2. Sluiten", r[7], "segment 7 whole");
            Assert.Equal("Begin", r[6], "segment 6 untouched");
            Assert.Equal("Klaar", r[8], "segment 8 untouched");
            Assert.Equal(3, r.Count, "segments");
        }

        /// <summary>
        /// The hard case: segment 1's own list has a "2." line, and the next
        /// segment is 2. The source says segment 1 has that line, so the first
        /// "2." belongs to it and the second opens segment 2.
        /// </summary>
        public static void ANumberedListInTheFirstSegment_StaysInItWhenTheSourcesAreKnown()
        {
            var r = Parse("1. Stappen:\n1. Openen\n2. Sluiten\n2. Klaar.",
                (1, "Steps:\n1. Open\n2. Close"), (2, "Done."));

            Assert.Equal("Stappen:\n1. Openen\n2. Sluiten", r[1], "segment 1 whole");
            Assert.Equal("Klaar.", r[2], "segment 2");
        }

        public static void AnOrdinaryReply_ParsesAsBefore()
        {
            var r = Parse("Here are the translations:\n1. Een\n2. Twee\nregel twee\n3.Drie",
                (1, "One"), (2, "Two\nline two"), (3, "Three"));

            Assert.Equal(3, r.Count, "segments, with the preamble ignored");
            Assert.Equal("Een", r[1], "1");
            Assert.Equal("Twee\nregel twee", r[2], "a multi-line segment");
            Assert.Equal("Drie", r[3], "no space after the full stop is still a marker");
        }

        public static void AnswersOutOfOrder_AreStillPlacedWhenTheSourcesAreKnown()
        {
            var r = Parse("2. Twee\n1. Een", (1, "One"), (2, "Two"));
            Assert.Equal("Een", r[1], "1");
            Assert.Equal("Twee", r[2], "2");
        }
    }
}
