using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The one-hour prompt cache (208): what goes out and what it costs. With
    /// "long cache", a Claude request (direct or through OpenRouter) asks for
    /// an hour on the stable part - the system prompt, and the tools in a tool
    /// chat - while a chat's moving breakpoint on the last message stays at
    /// five minutes. The API refuses a request whose one-hour breakpoint comes
    /// after a five-minute one, so the order is checked in the prefix order the
    /// API reads (tools, system, messages). Every other provider gets the
    /// request it got before. Usage carries the one-hour writes separately and
    /// prices them at 2x.
    /// </summary>
    [Tests]
    internal static class PromptCacheLifetimeTests
    {
        private const string FiveMinutes = "\"cache_control\":{\"type\":\"ephemeral\"}";
        private const string OneHour = "\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"1h\"}";

        private const string Stable = "You are a professional translator.\n\n# DOCUMENT CONTENT\n1. De klep sluit.\n";
        private const string Tail = "\n# ACTIVE SEGMENT\nHet huis.\n";
        private const string System = Stable + Tail;
        private const string Tools = "[{\"name\":\"a\",\"description\":\"A\",\"input_schema\":{\"type\":\"object\",\"properties\":{}}},"
            + "{\"name\":\"b\",\"description\":\"B\",\"input_schema\":{\"type\":\"object\",\"properties\":{}}}]";

        private static List<ChatMessage> Conversation() => new List<ChatMessage>
        {
            new ChatMessage { Role = ChatRole.User, Content = "What is klep?" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "Valve." },
            new ChatMessage { Role = ChatRole.User, Content = "And segment 2?" },
        };

        private static LlmClient Claude() => new LlmClient(LlmModels.ProviderClaude, "claude-sonnet-5-5", "k");

        // ── the helper ────────────────────────────────────────────────────

        public static void TheHelper_WritesExactlyTheTwoMarkers()
        {
            Assert.Equal(FiveMinutes, LlmClient.CacheControl(false), "five minutes: Anthropic's default, no ttl");
            Assert.Equal(OneHour, LlmClient.CacheControl(true), "one hour");
            Assert.Equal("[{\"x\":1," + OneHour + "}]", LlmClient.WithCacheMarkerOnLastObject("[{\"x\":1}]", true), "on a last object");
            Assert.Equal("[{\"x\":1," + FiveMinutes + "}]", LlmClient.WithCacheMarkerOnLastObject("[{\"x\":1}]"), "default unchanged");
        }

        // ── Translate active segment: the single prompt ──────────────────

        public static void ClaudePrompt_LongCache_IsTheHourOnTheSystemPrompt()
        {
            var body = Claude().BuildClaudePromptBody("Translate: Het huis.", System, 4096, true, longCache: true);
            var expected = "{\"model\":\"claude-sonnet-5-5\",\"max_tokens\":4096,\"system\":[{\"type\":\"text\",\"text\":"
                + Json(System) + "," + OneHour + "}],\"messages\":[{\"role\":\"user\",\"content\":\"Translate: Het huis.\"}],\"stream\":true}";
            Assert.Equal(expected, body, "Alt+T to Claude, byte for byte");
            Parse(body);
            Assert.Equal(1, Markers(body), "one breakpoint");
        }

        public static void ClaudePrompt_WithoutLongCache_IsFiveMinutesAsBefore()
        {
            var body = Claude().BuildClaudePromptBody("x", System, 4096, true);
            Assert.True(body.Contains("," + FiveMinutes + "}]"), "a batch request: five minutes");
            Assert.True(!body.Contains("ttl"), "no ttl");
            var plain = Claude().BuildClaudePromptBody("x", System, 4096, false, longCache: true);
            Assert.Equal(0, Markers(plain), "long cache without caching sends nothing");
            Assert.True(plain.Contains("\"system\":" + Json(System) + ","), "the plain system string");
        }

        public static void OpenRouterClaudePrompt_LongCache_IsTheHour()
        {
            var client = new LlmClient(LlmModels.ProviderOpenRouter, "anthropic/claude-sonnet-5.5", "k");
            var body = client.BuildOpenAiPromptBody("x", System, 4096, true, longCache: true);
            Assert.True(body.Contains("{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":" + Json(System) + "," + OneHour + "}]}"),
                "OpenRouter: the system text part carries the hour");
            Assert.Equal(1, Markers(body), "one breakpoint");
            Parse(body);
        }

        // ── Chat ──────────────────────────────────────────────────────────

        public static void ClaudeChat_LongCache_SystemHour_LastMessageFiveMinutes_InThatOrder()
        {
            var body = Claude().BuildClaudeChatBody(Conversation(), System, 4096, true, Stable.Length, longCache: true);
            var req = Parse(body);
            Assert.Equal(2, Markers(body), "two breakpoints");
            var system = Arr(req["system"]);
            Assert.Equal("1h", Ttl(system[0]), "the stable system part: an hour");
            Assert.True(!Obj(system[1]).ContainsKey("cache_control"), "the changing part: unmarked");
            var last = Arr(Obj(Arr(req["messages"])[2])["content"]);
            Assert.Equal("5m", Ttl(last[last.Length - 1]), "the last message: five minutes");
            AssertLongerFirst(req, "plain chat");

            var off = Claude().BuildClaudeChatBody(Conversation(), System, 4096, true, Stable.Length);
            Assert.True(!off.Contains("ttl"), "without long cache: no ttl anywhere");
            Assert.Equal(Unmarked(Parse(off)), Unmarked(req), "the same request apart from the markers");
        }

        private const string ToolUseReply =
            "{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"a\",\"input\":{}}],\"stop_reason\":\"tool_use\","
            + "\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":3100,\"cache_read_input_tokens\":0,"
            + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":100,\"ephemeral_1h_input_tokens\":3000},\"output_tokens\":20}}";
        private const string FinalReply =
            "{\"id\":\"m2\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}],"
            + "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":60,\"cache_read_input_tokens\":3100,"
            + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":60,\"ephemeral_1h_input_tokens\":0},\"output_tokens\":4}}";

        public static void ClaudeToolChat_LongCache_ToolsAndSystemHour_MessageFiveMinutes_EveryRound()
        {
            var requests = new List<string>();
            var replies = new Queue<string>(new[] { ToolUseReply, FinalReply });
            Task<string> Post(string b, CancellationToken ct) { requests.Add(b); return Task.FromResult(replies.Dequeue()); }
            var outcome = new LlmClient.ClaudeToolOutcome();
            LlmClient.RunClaudeToolLoopAsync(Post, "claude-sonnet-5-5", 4096, System, Tools, Conversation(), true, Stable.Length,
                (n, i) => "ok", null, outcome, CancellationToken.None, longCache: true, askedForLongCache: true).GetAwaiter().GetResult();

            Assert.Equal(2, requests.Count, "two rounds");
            foreach (var body in requests)
            {
                var req = Parse(body);
                Assert.Equal(3, Markers(body), "three breakpoints, of the four allowed");
                var tools = Arr(req["tools"]);
                Assert.Equal("1h", Ttl(tools[tools.Length - 1]), "the last tool: an hour");
                Assert.Equal("1h", Ttl(Arr(req["system"])[0]), "the stable system part: an hour");
                var msgs = Arr(req["messages"]);
                var last = Arr(Obj(msgs[msgs.Length - 1])["content"]);
                Assert.Equal("5m", Ttl(last[last.Length - 1]), "the newest message: five minutes");
                AssertLongerFirst(req, "tool chat");
            }

            Assert.Equal(3100 + 60, outcome.Usage.CacheWriteTokens, "all writes of both rounds");
            Assert.Equal(3000, outcome.Usage.CacheWrite1hTokens, "of which an hour, from the breakdown");
        }

        public static void OpenRouterClaudeChat_LongCache_SystemHour_MessageFiveMinutes()
        {
            var client = new LlmClient(LlmModels.ProviderOpenRouter, "anthropic/claude-sonnet-5.5", "k");
            var body = client.BuildOpenAiChatBody(Conversation(), System, 4096, true, Stable.Length, longCache: true);
            var req = Parse(body);
            var msgs = Arr(req["messages"]);
            Assert.Equal("1h", Ttl(Arr(Obj(msgs[0])["content"])[0]), "system: an hour");
            Assert.Equal("5m", Ttl(Arr(Obj(msgs[3])["content"])[0]), "last message: five minutes");
            Assert.Equal(2, Markers(body), "two breakpoints");

            var turns = client.OpenAiToolConversation(Conversation(), System, true, Stable.Length, longCache: true);
            Assert.True(turns[0].ContentJson.Contains(OneHour), "tool chat: system an hour");
            Assert.True(turns[3].ContentJson.Contains(FiveMinutes), "tool chat: last message five minutes");
        }

        // ── every other provider ──────────────────────────────────────────

        public static void OtherProviders_NeverSeeAMarkerOrATtl()
        {
            var others = new[]
            {
                new[] { LlmModels.ProviderOpenAi, "gpt-6-luna" },
                new[] { LlmModels.ProviderDeepSeek, "deepseek-v4-pro" },
                new[] { LlmModels.ProviderGrok, "grok-4.3" },
                new[] { LlmModels.ProviderMistral, "mistral-large-latest" },
                new[] { LlmModels.ProviderOpenRouter, "openai/gpt-6-luna" },
                new[] { LlmModels.ProviderOpenRouter, "google/gemini-3.8-flash" },
                new[] { LlmModels.ProviderCustomOpenAi, "claude-sonnet-5-5" },
            };
            foreach (var p in others)
            {
                var client = new LlmClient(p[0], p[1], "k", "http://127.0.0.1:9/v1");
                var what = p[0] + "/" + p[1];
                var prompt = client.BuildOpenAiPromptBody("x", System, 4096, true, longCache: true);
                Assert.Equal(client.BuildOpenAiPromptBody("x", System, 4096, false), prompt, what + ": prompt as without caching");
                var chat = client.BuildOpenAiChatBody(Conversation(), System, 4096, true, Stable.Length, longCache: true);
                Assert.Equal(client.BuildOpenAiChatBody(Conversation(), System, 4096, false, Stable.Length), chat, what + ": chat as without caching");
                var turns = client.OpenAiToolConversation(Conversation(), System, true, Stable.Length, longCache: true);
                foreach (var body in new[] { prompt, chat }.Concat(turns.Select(t => t.ContentJson)))
                {
                    Assert.True(!body.Contains("cache_control"), what + ": no cache_control");
                    Assert.True(!body.Contains("ttl"), what + ": no ttl");
                }
            }
        }

        public static void ALocalServer_GetsNoMarker_EndToEnd_WithLongCache()
        {
            var chat = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "hello" } };
            var reply = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":10}}}";
            using (var server = new LocalServerTests.FakeJsonServer(_ => reply))
            using (var client = new LlmClient(LlmModels.ProviderCustomOpenAi, "claude-sonnet-5-5", "", server.Url + "/v1"))
            {
                client.SendPromptAsync("x", System, enablePromptCaching: true, longCache: true).GetAwaiter().GetResult();
                Assert.True(!server.LastBody.Contains("cache_control") && !server.LastBody.Contains("ttl"), "single prompt");
                Assert.Equal(0, client.LastUsage.CacheWrite1hTokens, "nothing counted as an hour");
                client.SendChatAsync(chat, System, enablePromptCaching: true, longCache: true).GetAwaiter().GetResult();
                Assert.True(!server.LastBody.Contains("cache_control"), "chat");
                client.SendChatWithToolsAsync(chat, System, "[]", (n, i) => "", enablePromptCaching: true, longCache: true).GetAwaiter().GetResult();
                Assert.True(!server.LastBody.Contains("cache_control"), "tool chat");
            }
        }

        // ── usage ─────────────────────────────────────────────────────────

        public static void ClaudeUsage_TheBreakdown_SplitsTheWrites()
        {
            var u = LlmClient.ExtractClaudeUsage("{\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":23000,\"cache_read_input_tokens\":0,"
                + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":23000},\"output_tokens\":40}}");
            Assert.Equal(23000, u.CacheWriteTokens, "all written");
            Assert.Equal(23000, u.CacheWrite1hTokens, "for an hour");

            u = LlmClient.ExtractClaudeUsage("{\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":23300,\"cache_read_input_tokens\":0,"
                + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":300,\"ephemeral_1h_input_tokens\":23000},\"output_tokens\":40}}", true);
            Assert.Equal(23300, u.CacheWriteTokens, "both lifetimes: all written");
            Assert.Equal(23000, u.CacheWrite1hTokens, "both lifetimes: the hour from the breakdown, not the request");

            u = LlmClient.ExtractClaudeUsage("{\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":0,"
                + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":300,\"ephemeral_1h_input_tokens\":0},\"output_tokens\":4}}", true);
            Assert.Equal(300, u.CacheWriteTokens, "flat zero: the breakdown's sum");
            Assert.Equal(0, u.CacheWrite1hTokens, "a breakdown saying five minutes wins over what was asked");
        }

        public static void ClaudeUsage_WithoutTheBreakdown_IsPricedByWhatWasAskedFor()
        {
            const string json = "{\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":23000,\"cache_read_input_tokens\":0,\"output_tokens\":40}}";
            Assert.Equal(23000, LlmClient.ExtractClaudeUsage(json, true).CacheWrite1hTokens, "asked for the hour: an hour");
            Assert.Equal(0, LlmClient.ExtractClaudeUsage(json, false).CacheWrite1hTokens, "asked for five minutes: five minutes");
            Assert.Equal(0, LlmClient.ExtractClaudeUsage(json).CacheWrite1hTokens, "default: five minutes");
        }

        public static void ClaudeUsage_AStreamedReply_IsNotCountedTwice()
        {
            // UsageJson is message_delta then message_start; both may carry the breakdown.
            var delta = "{\"type\":\"message_delta\",\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":0,"
                + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":23000},\"output_tokens\":40}}";
            var start = "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":0,"
                + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":23000},\"output_tokens\":1}}}";
            var u = LlmClient.ExtractClaudeUsage(delta + "\n" + start, true);
            Assert.Equal(23000, u.CacheWriteTokens, "written once");
            Assert.Equal(23000, u.CacheWrite1hTokens, "an hour, once");
            Assert.Equal(40, u.OutputTokens, "the delta's output count");
        }

        public static void OpenAiShapeUsage_ReadsCacheWriteTokens()
        {
            const string json = "{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":5,"
                + "\"prompt_tokens_details\":{\"cached_tokens\":200,\"cache_write_tokens\":700}}}";
            var u = LlmClient.ExtractOpenAiUsage(json);
            Assert.Equal(200, u.CacheReadTokens, "read");
            Assert.Equal(700, u.CacheWriteTokens, "written");
            Assert.Equal(100, u.RegularInputTokens, "the rest at the full rate");
            Assert.Equal(0, u.CacheWrite1hTokens, "OpenAI: no hour");
            Assert.Equal(700, LlmClient.ExtractOpenAiUsage(json, true).CacheWrite1hTokens, "OpenRouter to Claude, asked for the hour");

            var onlyWrite = LlmClient.ExtractOpenAiUsage("{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":5,"
                + "\"prompt_tokens_details\":{\"cache_write_tokens\":900}}}");
            Assert.Equal(900, onlyWrite.CacheWriteTokens, "a write without a read");
            Assert.Equal(100, onlyWrite.RegularInputTokens, "and the rest");

            var old = LlmClient.ExtractOpenAiUsage("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,"
                + "\"prompt_tokens_details\":{\"cached_tokens\":10}}}");
            Assert.Equal(0, old.CacheWriteTokens, "no write field: no writes");
            Assert.Equal(90, old.RegularInputTokens, "as before");
        }

        // ── cost ──────────────────────────────────────────────────────────

        public static void OneHourWrites_AreCostedAtTwiceTheInputRate()
        {
            Assert.Equal(4.00m, TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 0, 1_000_000, 0, 1_000_000),
                "Sonnet 5.5: 1h write $4/MTok (2x $2)");
            Assert.Equal(2.50m, TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 0, 1_000_000, 0),
                "Sonnet 5.5: 5m write $2.50/MTok (1.25x), as before");
            Assert.Equal(3.25m, TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 0, 1_000_000, 0, 500_000),
                "half and half");
            Assert.Equal(4.00m, TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 0, 1_000_000, 0, 5_000_000),
                "an hour count above the writes is held to them");
            Assert.Equal(16.00m, TokenEstimator.ComputeActualCost("claude-fable-5-2", 0, 0, 1_000_000, 0, 1_000_000),
                "Fable 5.2: 1h write $16/MTok");
            Assert.True(TokenEstimator.CostCeiling("claude", "claude-unlisted", 0, 0, 1_000_000, 0, 1_000_000)
                > TokenEstimator.CostCeiling("claude", "claude-unlisted", 0, 0, 1_000_000, 0), "the ceiling counts the hour too");
        }

        public static void TheWorkedExample_Sonnet55_23000Tokens()
        {
            // One Alt+T: the 23,000-token prefix written, then read.
            var fiveMinuteWrite = TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 0, 23_000, 0);
            var oneHourWrite = TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 0, 23_000, 0, 23_000);
            var read = TokenEstimator.ComputeActualCost("claude-sonnet-5-5", 0, 23_000, 0, 0);
            Assert.Equal(0.0575m, fiveMinuteWrite, "5m write 5.75 cents");
            Assert.Equal(0.092m, oneHourWrite, "1h write 9.2 cents");
            Assert.Equal(0.0023m, read, "read 0.23 cents (0.05x)");
        }

        // ── helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// The breakpoints' lifetimes in the order the API reads the prefix -
        /// tools, system, messages - must never go from five minutes to an hour.
        /// </summary>
        private static void AssertLongerFirst(Dictionary<string, object> req, string what)
        {
            var ttls = new List<string>();
            void Walk(object v)
            {
                if (v is Dictionary<string, object> d)
                {
                    if (d.ContainsKey("cache_control")) ttls.Add(Ttl(d));
                    foreach (var kv in d) if (kv.Key != "cache_control") Walk(kv.Value);
                }
                else if (v is object[] a) foreach (var x in a) Walk(x);
            }
            foreach (var key in new[] { "tools", "system", "messages" })
                if (req.ContainsKey(key)) Walk(req[key]);
            Assert.True(ttls.Count <= 4, what + ": at most four breakpoints");
            for (int i = 1; i < ttls.Count; i++)
                Assert.True(!(ttls[i - 1] == "5m" && ttls[i] == "1h"), what + ": an hour after five minutes (" + string.Join(",", ttls) + ")");
        }

        private static string Ttl(object block)
        {
            var cc = Obj(Obj(block)["cache_control"]);
            Assert.Equal("ephemeral", (string)cc["type"], "type ephemeral");
            return cc.ContainsKey("ttl") ? (string)cc["ttl"] : "5m";
        }

        private static int Markers(string json) =>
            (json.Length - json.Replace("\"cache_control\"", "").Length) / "\"cache_control\"".Length;

        private static Dictionary<string, object> Parse(string json) =>
            Obj(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json));

        private static Dictionary<string, object> Obj(object o) => (Dictionary<string, object>)o;
        private static object[] Arr(object o) => (object[])o;

        private static string Unmarked(object value) => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Strip(value));

        private static object Strip(object value)
        {
            if (value is Dictionary<string, object> d)
                return d.Where(kv => kv.Key != "cache_control").ToDictionary(kv => kv.Key, kv => Strip(kv.Value));
            if (value is object[] a) return a.Select(Strip).ToArray();
            return value;
        }

        private static string Json(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }
}
