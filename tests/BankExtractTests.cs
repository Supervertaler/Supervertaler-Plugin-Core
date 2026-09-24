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
