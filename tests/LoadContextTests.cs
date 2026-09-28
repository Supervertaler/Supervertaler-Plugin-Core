using System;
using System.IO;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// LoadContext loads the bank whole: a domain or a query only labels the
    /// result. Trados's chat and AutoPrompt stopped detecting the document's
    /// domain on that basis (item 10 after 197) - it cost a walk of every
    /// segment per chat message and changed nothing. If LoadContext ever starts
    /// selecting by domain, this fails, and those callers need one again.
    /// </summary>
    [Tests]
    internal static class LoadContextTests
    {
        public static void DomainAndQuery_OnlyLabelTheResult()
        {
            var root = Path.Combine(Path.GetTempPath(), "sv-loadcontext-" + Guid.NewGuid().ToString("N"));
            var bank = Path.Combine(root, "acme");
            try
            {
                Directory.CreateDirectory(bank);
                File.WriteAllText(Path.Combine(bank, "brief.md"), "---\r\ndomain: legal\r\n---\r\n# Acme\r\n\r\nFormal register.\r\n");
                File.WriteAllText(Path.Combine(bank, "terminology.md"), "| nl | en |\r\n|---|---|\r\n| hellingmeter | inclinometer |\r\n");
                File.WriteAllText(Path.Combine(bank, "notes.md"), "---\r\ndomain: medical\r\n---\r\n# Sterilisation\r\n\r\nSay chamber, not room.\r\n");

                var reader = new MemoryBankReader(bank);
                var plain = reader.LoadContext("Project", null, "nl", "en", tokenBudget: 0);
                var labelled = reader.LoadContext("Project", "medical", "nl", "en", tokenBudget: 0, queryText: "inclinometer");

                Assert.Equal(MemoryBankReader.FormatForPrompt(plain), MemoryBankReader.FormatForPrompt(labelled),
                    "the same block with or without a domain and a query");
                Assert.Equal("medical", labelled.DomainName, "the domain is kept as a label, which the MCP tool echoes");
                Assert.True(plain.DomainName == null, "and nothing is invented when none is given");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
