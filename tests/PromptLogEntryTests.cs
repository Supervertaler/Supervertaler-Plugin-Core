using System;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Every kind of AI call has a name on its Reports card. A feature added to
    /// the enum without one showed as "Unknown": the AutoTagger in 20.14x, and
    /// the memory bank selection until 20.198.
    /// </summary>
    [Tests]
    internal static class PromptLogEntryTests
    {
        public static void EveryFeature_HasALabel()
        {
            foreach (PromptLogFeature f in Enum.GetValues(typeof(PromptLogFeature)))
                Assert.True(new PromptLogEntry { Feature = f }.FeatureLabel != "Unknown", f + " has a label");
        }

        public static void TheMemoryBankSelection_IsNamedAsSuperMemory()
        {
            var e = new PromptLogEntry { Feature = PromptLogFeature.SuperMemory, PromptName = "Memory bank selection" };
            Assert.Equal("SuperMemory · Memory bank selection", e.FeatureLabel, "card header");
        }
    }
}
