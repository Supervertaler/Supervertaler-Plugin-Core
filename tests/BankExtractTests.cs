using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The per-job memory bank extract. Every bank here is built in memory; no
    /// file is read and no model is called - the article choice is a plain
    /// function standing in for the host's AI call.
    /// </summary>
    [Tests]
    internal static class BankExtractTests
    {
        private const string Table =
            "# Terminology\r\n\r\nOne row per decision.\r\n\r\n" +
            "| Source | Target | Scope | Note |\r\n" +
            "|---|---|---|---|\r\n" +
            "| schoeisel | footwear | client | |\r\n" +
            "| memory bank | geheugenbank | domain | |\r\n" +
            "| inrichting / apparaat | device | client | never \"apparatus\" |\r\n" +
            "| zool | sole | client | |\r\n" +
            "| stoomturbine | steam turbine | domain | |\r\n";

        private static string Filter(string doc, out int kept, out int total) =>
            TerminologyFilter.Filter(Table, new DocumentTerms(doc, TerminologyFilter.LongestTerm(new[] { Table })), out kept, out total);

        public static void Terminology_KeepsOnlyRowsThatOccur_AndTheHeader()
        {
            var result = Filter("Het schoeisel van de inrichting.", out var kept, out var total);
            Assert.Equal(5, total, "rows counted");
            Assert.Equal(2, kept, "rows kept");
            Assert.True(result.Contains("| schoeisel |") && result.Contains("| inrichting / apparaat |"), "the two that occur are kept");
            Assert.True(!result.Contains("stoomturbine"), "one that does not occur is dropped");
            Assert.True(result.Contains("| Source | Target |") && result.Contains("|---|"), "the header is kept");
            Assert.True(result.Contains("One row per decision."), "prose around the table is kept");
        }

        public static void Terminology_MatchesTheTargetColumn_ForATableWrittenTheOtherWay()
        {
            // The table runs nl->en; this job is en->nl, so the document is English
            // and every relevant term is in the TARGET column.
            Filter("The footwear and the steam turbine of the device.", out var kept, out _);
            Assert.Equal(3, kept, "footwear, steam turbine and device found through the target column");
        }

        public static void Terminology_FindsACompound_ButNotAShortWordInsideAnother()
        {
            Filter("Het inlegschoeisel en de zoolbeschermer.", out var kept, out _);
            // "schoeisel" (9 letters) inside "inlegschoeisel" counts; "zool" (4) inside
            // "zoolbeschermer" does not - a short word would match inside everything.
            Assert.Equal(1, kept, "only the long compound part matches");
        }

        public static void Terminology_MatchesMultiWordTermsAsWords()
        {
            Filter("Each Memory Bank is read once.", out var kept1, out _);
            Filter("The memory of the bank.", out var kept2, out _);
            Assert.Equal(1, kept1, "a multi-word term in order, any case");
            Assert.Equal(0, kept2, "the same words apart are not the term");
        }

        public static void Terminology_DropsATableWithNothingInIt_KeepsItsProse()
        {
            var result = Filter("Nothing relevant at all.", out var kept, out var total);
            Assert.Equal(0, kept, "no rows occur");
            Assert.True(total == 5 && !result.Contains("|"), "the table is gone, header and all");
            Assert.True(result.Contains("# Terminology"), "the heading stays");
        }

        public static void SmallBank_IsSentWhole_AndNothingIsAsked()
        {
            var asked = 0;
            var ctx = new KbContext { ClientProfileText = "Brief.", StyleGuideText = "Style." };
            ctx.TerminologyArticles.Add(Table);
            ctx.ExtraArticles.Add("# Method\nRules.");
            ctx.ExtraPaths.Add("method.md");

            var r = BankExtract.Build(ctx, "Unrelated document.", "nl", "en", 32000, _ => { asked++; return new List<string>(); });
            Assert.True(!r.Selected, "not selected");
            Assert.Equal(0, asked, "no article choice for a small bank");
            Assert.True(r.Context.TerminologyArticles[0].Contains("stoomturbine"), "terminology untouched");
        }

        public static void Selection_NeverRemovesBriefStyleOrDomain()
        {
            var ctx = LargeBank(out _);
            var r = BankExtract.Build(ctx, "schoeisel", "nl", "en", 9000, _ => new List<string>());
            Assert.True(r.Selected, "selected");
            Assert.True(r.Context.ClientProfileText != null && r.Context.StyleGuideText != null
                        && r.Context.DomainArticleText != null, "brief, style and domain are all still there");
            Assert.Equal(0, r.Context.ExtraArticles.Count + r.Context.SharedExtraArticles.Count, "the choice of none removed every article");
        }

        public static void ArticleChoice_IsAskedOnlyWhenStillOverBudget_AndReused()
        {
            var asked = 0;
            Func<ArticleSelectionRequest, IList<string>> choose = req => { asked++; return new List<string> { "patents.md" }; };

            // A generous budget: terminology filtering alone brings it under.
            BankExtract.Build(LargeBank(out _), "schoeisel", "nl", "en", 1000000, choose);
            Assert.Equal(0, asked, "not asked when already within budget");

            var r = BankExtract.Build(LargeBank(out _), "schoeisel", "nl", "en", 9000, choose);
            Assert.Equal(1, asked, "asked once when over budget");
            Assert.True(r.ArticleChoice != null && r.ArticleChoice.Contains("patents.md"), "the choice is returned for reuse");
            Assert.True(r.Context.ExtraPaths.SequenceEqual(new[] { "patents.md" }), "only the chosen article is left");

            BankExtract.Build(LargeBank(out _), "schoeisel", "nl", "en", 9000, choose, r.ArticleChoice);
            Assert.Equal(1, asked, "an earlier choice is reused, not asked again");
        }

        public static void ArticleChoice_Failure_KeepsEveryArticle()
        {
            var r = BankExtract.Build(LargeBank(out var articles), "schoeisel", "nl", "en", 9000,
                _ => throw new InvalidOperationException("network down"));
            Assert.True(r.Report.Any(l => l.Contains("network down")), "the failure is reported");
            Assert.True(r.ArticleChoice == null, "no choice recorded");
            Assert.True(r.Context.ExtraArticles.Count + r.Context.SharedExtraArticles.Count
                        + r.Context.TrimmedPaths.Count(p => !p.EndsWith("terminology.md", StringComparison.Ordinal)) >= articles,
                "no article removed by selection - only the usual trimming");
        }

        public static void ParseSelection_TakesOnlyKnownPaths_AndRefusesNonsense()
        {
            var candidates = new List<ArticleCandidate>
            {
                new ArticleCandidate { Path = "method.md" }, new ArticleCandidate { Path = "_shared/patents.md" }
            };
            var chosen = BankExtract.ParseSelection("Sure: [\"_shared/patents.md\", \"invented.md\"]", candidates);
            Assert.True(chosen != null && chosen.SequenceEqual(new[] { "_shared/patents.md" }), "a known path kept, an invented one ignored");
            Assert.True(BankExtract.ParseSelection("[]", candidates).Count == 0, "an empty array is an answer");
            Assert.True(BankExtract.ParseSelection("I cannot tell.", candidates) == null, "no array is no answer, not 'keep nothing'");
        }

        public static void Scale_TwelveThousandRows_FiveThousandSegments()
        {
            // Michael's real sizes: termbases to ~12,000 terms, documents of
            // thousands of segments. Asserted loosely so a slow machine never gets
            // this test muted; the point is milliseconds, not seconds.
            var table = new StringBuilder("| Source | Target | Scope | Note |\r\n|---|---|---|---|\r\n");
            for (var i = 0; i < 12000; i++)
                table.Append("| bronterm").Append(i).Append(i % 7 == 0 ? " met woorden" : "")
                     .Append(" | targetterm").Append(i).Append(" | client | |\r\n");

            var doc = new StringBuilder();
            var rnd = new Random(7);
            for (var s = 0; s < 5000; s++)
            {
                for (var w = 0; w < 15; w++) doc.Append("woord").Append(rnd.Next(20000)).Append(' ');
                doc.Append("bronterm").Append(rnd.Next(24000)).Append(". ");
            }

            var ctx = new KbContext { ClientProfileText = "Brief." };
            ctx.TerminologyArticles.Add(table.ToString());
            ctx.TerminologyPaths.Add("terminology.md");

            var clock = Stopwatch.StartNew();
            var r = BankExtract.Build(ctx, doc.ToString(), "nl", "en", 32000, null);
            clock.Stop();

            Console.WriteLine("      12,000 rows x 5,000 segments: " + clock.ElapsedMilliseconds + " ms, "
                              + r.TokensBefore + " -> " + r.TokensAfter + " tokens");
            foreach (var line in r.Report) Console.WriteLine("      " + line);
            Assert.True(r.Selected && r.TokensAfter < r.TokensBefore, "the table was filtered");
            Assert.True(r.Context.TerminologyArticles.Count == 1 && r.TokensAfter > 20000 && r.TokensAfter <= 32000,
                "relevant terminology still over the budget is cut to fit, not dropped whole: " + r.TokensAfter + " tokens");
            Assert.True(clock.ElapsedMilliseconds < 3000, "well under a few seconds: " + clock.ElapsedMilliseconds + " ms");
        }

        public static void Shrink_CutsTheLeastUsedRowsFirst()
        {
            var table = "| Source | Target |\r\n|---|---|\r\n| often | vaak |\r\n| once | eens |\r\n| twice | tweemaal |\r\n";
            var doc = new DocumentTerms("often often often twice twice once", 1);
            var shrunk = TerminologyFilter.Shrink(table, doc, table.Length - 10, out var cut);
            Assert.Equal(1, cut, "one row cut");
            Assert.True(!shrunk.Contains("| once |") && shrunk.Contains("| often |") && shrunk.Contains("| twice |"),
                "the row used least is the one cut");
        }

        public static void RelevantTerminology_OutranksSharedArticles_WhenNothingCanBeChosen()
        {
            // The core owner's probe. No article choice (offline); 300 client rows,
            // ALL in the document; two _shared prose articles of ~4k tokens each;
            // a 9,000-token budget. The priority order drops a _shared article long
            // before client terminology, and dropping one fits everything - so all
            // 300 rows must survive. Before the fix, 207 were cut and both
            // articles kept.
            var rows = new StringBuilder("| Source | Target | Scope | Note |\r\n|---|---|---|---|\r\n");
            var doc = new StringBuilder();
            for (var i = 0; i < 300; i++)
            {
                rows.Append("| relevantterm").Append(i).Append(" | relevantdoel").Append(i).Append(" | client | ordinary note |\r\n");
                doc.Append("relevantterm").Append(i).Append(' ');
            }

            var ctx = new KbContext { ClientProfileText = "Brief." };
            ctx.TerminologyArticles.Add(rows.ToString());
            ctx.TerminologyPaths.Add("terminology.md");
            ctx.SharedExtraArticles.Add("# One\n" + new string('x', 16000));
            ctx.SharedExtraPaths.Add("_shared/one.md");
            ctx.SharedExtraArticles.Add("# Two\n" + new string('y', 16000));
            ctx.SharedExtraPaths.Add("_shared/two.md");

            var r = BankExtract.Build(ctx, doc.ToString(), "nl", "en", 9000, _ => null);

            var kept = r.Context.TerminologyArticles.Count == 0 ? 0
                : r.Context.TerminologyArticles[0].Split('\n').Count(l => l.StartsWith("| relevantterm", StringComparison.Ordinal));
            Assert.Equal(300, kept, "every relevant client row kept");
            Assert.Equal(1, r.Context.TrimmedPaths.Count, "one _shared article trimmed: " + string.Join(", ", r.Context.TrimmedPaths));
            Assert.True(r.Context.TrimmedPaths[0].StartsWith("_shared/", StringComparison.Ordinal), "and it is a _shared one");
        }

        public static void RelevantTerminology_IsCutByRows_WhenNothingElseIsLeftToDrop()
        {
            // Shrink still earns its place: prose already gone, the relevant
            // terminology alone over budget. Rows are cut, not the table.
            var rows = new StringBuilder("| Source | Target |\r\n|---|---|\r\n");
            var doc = new StringBuilder();
            for (var i = 0; i < 2000; i++)
            {
                rows.Append("| relevantterm").Append(i).Append(" | relevantdoel").Append(i).Append(" |\r\n");
                doc.Append("relevantterm").Append(i).Append(' ');
            }
            var ctx = new KbContext { ClientProfileText = "Brief." };
            ctx.TerminologyArticles.Add(rows.ToString());
            ctx.TerminologyPaths.Add("terminology.md");

            var r = BankExtract.Build(ctx, doc.ToString(), "nl", "en", 9000, null);
            Assert.True(r.Context.TerminologyArticles.Count == 1, "the table is still there");
            Assert.True(r.TokensAfter <= 9000 && r.TokensAfter > 8000, "cut to fit the budget: " + r.TokensAfter);
            Assert.True(r.Report.Any(l => l.Contains("terminology rows cut")), "and the report says rows were cut");
        }

        public static void Fragments_StayModest_OnDutchLikeWords()
        {
            // The core owner measured the old every-fragment set at 73 MB on a
            // Dutch-like document: 12,000 distinct words, lengths 4-30 skewed short,
            // median about 12. Prefixes and suffixes only should be a small fraction.
            var rnd = new Random(11);
            var words = new List<string>();
            var letters = "abcdefghijklmnopqrstuvwxyz";
            while (words.Count < 12000)
            {
                var len = Math.Min(30, 4 + (int)(-Math.Log(1 - rnd.NextDouble()) * 11));
                var sb = new StringBuilder();
                for (var i = 0; i < len; i++) sb.Append(letters[rnd.Next(letters.Length)]);
                words.Add(sb.ToString());
            }
            var text = new StringBuilder();
            for (var s = 0; s < 5000; s++)
                for (var w = 0; w < 15; w++) text.Append(words[rnd.Next(words.Count)]).Append(' ');

            var median = words.Select(w => w.Length).OrderBy(n => n).ElementAt(words.Count / 2);

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var start = GC.GetTotalMemory(true);
            var doc = new DocumentTerms(text.ToString(), 1);
            var afterIndex = GC.GetTotalMemory(true);
            doc.Contains("zzzzzzzzq");   // a missed single word: builds the fragment set
            var afterFragments = GC.GetTotalMemory(true);
            GC.KeepAlive(doc);

            var indexMb = (afterIndex - start) / 1048576.0;
            var fragmentsMb = (afterFragments - afterIndex) / 1048576.0;
            Console.WriteLine("      median word length " + median + ": run index " + indexMb.ToString("F1")
                              + " MB, fragment set " + fragmentsMb.ToString("F1") + " MB");
            Assert.True(fragmentsMb < 20, "the fragment set is modest: " + fragmentsMb.ToString("F1") + " MB");
        }

        /// <summary>A bank over the threshold: a big terminology table, five articles, brief, style, domain.</summary>
        private static KbContext LargeBank(out int articleCount)
        {
            var ctx = new KbContext
            {
                ClientProfileText = "Brief.",
                StyleGuideText = "Style.",
                DomainArticleText = "Domain."
            };

            var rows = new StringBuilder("| Source | Target | Scope | Note |\r\n|---|---|---|---|\r\n| schoeisel | footwear | client | |\r\n");
            for (var i = 0; i < 2000; i++) rows.Append("| onbekendwoord").Append(i).Append(" | unknownword").Append(i).Append(" | client | |\r\n");
            ctx.TerminologyArticles.Add(rows.ToString());
            ctx.TerminologyPaths.Add("terminology.md");

            foreach (var name in new[] { "patents.md", "marketing.md", "legal.md" })
            {
                ctx.ExtraArticles.Add("# " + name + "\n" + new string('x', 8000));
                ctx.ExtraPaths.Add(name);
            }
            foreach (var name in new[] { "_shared/method.md", "_shared/checks.md" })
            {
                ctx.SharedExtraArticles.Add("# " + name + "\n" + new string('y', 8000));
                ctx.SharedExtraPaths.Add(name);
            }

            articleCount = 5;
            return ctx;
        }
    }
}
