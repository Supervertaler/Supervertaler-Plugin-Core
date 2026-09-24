using System;
using System.IO;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// <c>audience: assistant</c>: a note for the AI assistants, left out of
    /// translation, loaded for everything else. Against banks this test makes in a
    /// temporary folder.
    /// </summary>
    [Tests]
    internal static class AudienceTests
    {
        private const string Marked =
            "---\r\naudience: assistant\r\n---\r\n\r\n# Method\r\n\r\nCheck a supplied termbase actually has entries. METHOD-MARKER\r\n";

        private sealed class Banks : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "sv-audience-" + Guid.NewGuid().ToString("N"));
            public string Client => Path.Combine(Root, "acme");
            public string Shared => Path.Combine(Root, MemoryBankReader.SharedBankName);

            public Banks()
            {
                Directory.CreateDirectory(Client);
                Directory.CreateDirectory(Shared);
                File.WriteAllText(Path.Combine(Client, "brief.md"), "# Acme\r\n\r\nFormal register. BRIEF-MARKER\r\n");
                File.WriteAllText(Path.Combine(Shared, "brief.md"), "# House\r\n\r\nBritish spelling. SHARED-BRIEF-MARKER\r\n");
                File.WriteAllText(Path.Combine(Shared, "method.md"), Marked);
                File.WriteAllText(Path.Combine(Shared, "rules.md"), "# Rules\r\n\r\nClose up percent signs. RULES-MARKER\r\n");
            }

            public KbContext Load(bool forTranslation) =>
                new MemoryBankReader(Client).LoadContext("Project", null, "nl", "en", tokenBudget: 0, forTranslation: forTranslation);

            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch { }
            }
        }

        private static string Text(KbContext ctx) => MemoryBankReader.FormatForPrompt(ctx) ?? "";

        public static void Translation_LeavesOutAnAssistantNote_AndSaysSo()
        {
            using (var b = new Banks())
            {
                var ctx = b.Load(forTranslation: true);
                Assert.True(!Text(ctx).Contains("METHOD-MARKER"), "the assistant note is not sent to translation");
                Assert.True(ctx.AssistantOnlyPaths.SequenceEqual(new[] { "_shared/method.md" }),
                    "and it is recorded: " + string.Join(", ", ctx.AssistantOnlyPaths));
                Assert.True(Text(ctx).Contains("RULES-MARKER") && Text(ctx).Contains("BRIEF-MARKER")
                            && Text(ctx).Contains("SHARED-BRIEF-MARKER"), "unmarked notes are sent as before");
            }
        }

        public static void AssistantUse_StillGetsTheNote()
        {
            // The MCP tools and the chat - which is who such a note is written for.
            using (var b = new Banks())
            {
                var ctx = b.Load(forTranslation: false);
                Assert.True(Text(ctx).Contains("METHOD-MARKER"), "the assistants still get it");
                Assert.Equal(0, ctx.AssistantOnlyPaths.Count, "and nothing is recorded as left out");
            }
        }

        public static void TheMarker_WorksOnAnyNote_AndOnlyWithItsOneValue()
        {
            using (var b = new Banks())
            {
                File.WriteAllText(Path.Combine(b.Client, "style.md"), "---\r\naudience: assistant\r\n---\r\n# Style\r\n\r\nSTYLE-MARKER\r\n");
                File.WriteAllText(Path.Combine(b.Shared, "other.md"), "---\r\naudience: everyone\r\n---\r\n# Other\r\n\r\nOTHER-MARKER\r\n");

                var ctx = b.Load(forTranslation: true);
                Assert.True(!Text(ctx).Contains("STYLE-MARKER"), "a marked style.md is left out of translation too");
                Assert.True(ctx.AssistantOnlyPaths.Contains("style.md"), "and recorded under its name");
                Assert.True(Text(ctx).Contains("OTHER-MARKER"), "any other value changes nothing");
            }
        }

        public static void TheExtractReport_NamesWhatWasLeftOut()
        {
            using (var b = new Banks())
            {
                var r = BankExtract.Build(b.Load(forTranslation: true), "Some document.", "nl", "en", 32000, null);
                Assert.True(r.Report.Any(l => l.Contains("assistants only") && l.Contains("_shared/method.md")),
                    "the extract report says which note was left out, and why");
            }
        }
    }
}
