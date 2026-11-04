using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Prompt caching for the AI Assistant chat (18/19.20.205): what goes out.
    /// A Claude chat, direct or through OpenRouter, with or without tools,
    /// carries cache breakpoints on the system prompt's stable part (and the
    /// tool definitions) and on the last message; the system text is the text
    /// the caller gave; every other provider gets the request it got before.
    /// Each request is parsed as JSON, so a marker in the wrong place fails here
    /// rather than as a 400 from the API.
    /// </summary>
    [Tests]
    internal static class ChatCachingTests
    {
        private const string Stable =
            "You are a professional translation assistant.\n\n# DOCUMENT CONTENT\n1. De klep sluit.\n2. Het \"huis\" – en dash.\n";
        private const string Tail =
            "\n# ACTIVE SEGMENT\n- Position: Segment 2 of 2\n\n## Current Source Segment\nHet huis.\n\n# GUIDELINES\n- Be brief\n";
        private const string System = Stable + Tail;

        // As TradosTools.GetToolDefinitionsJson gives them for Claude: pretty-printed.
        private const string ClaudeTools = @"[
  {
    ""name"": ""studio_list_projects"",
    ""description"": ""Lists projects {with braces} and [brackets]."",
    ""input_schema"": { ""type"": ""object"", ""properties"": {} }
  },
  {
    ""name"": ""studio_get_project"",
    ""description"": ""One project."",
    ""input_schema"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" } }, ""required"": [""name""] }
  }
]";

        private static List<ChatMessage> Conversation() => new List<ChatMessage>
        {
            new ChatMessage { Role = ChatRole.User, Content = "What is klep?" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "Valve." },
            new ChatMessage { Role = ChatRole.User, Content = "And segment 2?" },
        };

        private static LlmClient Claude() => new LlmClient(LlmModels.ProviderClaude, "claude-sonnet-5-5", "test-key");

        // ── direct Claude, plain chat ─────────────────────────────────────

        public static void ClaudeChat_MarksTheStableSystemPart_AndTheLastMessage()
        {
            var body = Claude().BuildClaudeChatBody(Conversation(), System, 4096, true, Stable.Length);
            var req = Parse(body);

            Assert.Equal(2, Markers(body), "two breakpoints: system and last message");
            var system = Array(req["system"]);
            Assert.Equal(2, system.Length, "the system prompt in two blocks");
            Assert.Equal(Stable, (string)Obj(system[0])["text"], "first block: the stable part");
            Assert.True(Obj(system[0]).ContainsKey("cache_control"), "the stable part is marked");
            Assert.Equal(Tail, (string)Obj(system[1])["text"], "second block: the part that changes");
            Assert.True(!Obj(system[1]).ContainsKey("cache_control"), "the changing part is not marked");
            Assert.Equal(System, SystemText(req), "the system text is unchanged, character for character");

            var messages = Array(req["messages"]);
            Assert.Equal(3, messages.Length, "all three messages");
            var last = Array(Obj(messages[2])["content"]);
            Assert.True(Obj(last[last.Length - 1]).ContainsKey("cache_control"), "the last message's last block is marked");
            Assert.Equal("And segment 2?", (string)Obj(last[0])["text"], "its text unchanged");
            Assert.True(Obj(req).ContainsKey("stream"), "still streamed");
        }

        public static void ClaudeChat_WithoutCaching_IsTheRequestItAlwaysWas()
        {
            var body = Claude().BuildClaudeChatBody(Conversation(), System, 4096, false, Stable.Length);
            var expected = "{\"model\":\"claude-sonnet-5-5\",\"max_tokens\":4096,\"system\":" + Json(System)
                + ",\"messages\":[{\"role\":\"user\",\"content\":\"What is klep?\"},"
                + "{\"role\":\"assistant\",\"content\":\"Valve.\"},"
                + "{\"role\":\"user\",\"content\":\"And segment 2?\"}],\"stream\":true}";
            Assert.Equal(expected, body, "byte for byte the pre-205 body");
        }

        public static void ClaudeChat_TurnTwo_BeginsWithTurnOnesBytes()
        {
            // What makes turn 2 a cache read: everything turn 1 marked must come
            // back unchanged, the marker apart. The last message of turn 1 is not
            // the last any more, and must not change shape for that.
            var turn1 = new List<ChatMessage> { Conversation()[0] };
            var turn2 = Conversation();
            var a = Parse(Claude().BuildClaudeChatBody(turn1, System, 4096, true, Stable.Length));
            var b = Parse(Claude().BuildClaudeChatBody(turn2, System, 4096, true, Stable.Length));

            Assert.Equal(Unmarked(a["system"]), Unmarked(b["system"]), "the same system blocks");
            Assert.Equal(Unmarked(Array(a["messages"])[0]), Unmarked(Array(b["messages"])[0]),
                "turn 1's message, as turn 2 sends it again");
        }

        public static void ClaudeChat_TheWholeSystemPrompt_IsStable_WhenNoLengthIsGiven()
        {
            var req = Parse(Claude().BuildClaudeChatBody(Conversation(), System, 4096, true, null));
            var system = Array(req["system"]);
            Assert.Equal(1, system.Length, "one block");
            Assert.True(Obj(system[0]).ContainsKey("cache_control"), "marked");
            Assert.Equal(System, SystemText(req), "unchanged");
        }

        public static void ClaudeChat_AStableLengthOutOfRange_NeverBreaksTheRequest()
        {
            foreach (var n in new int?[] { -5, 0, System.Length, System.Length + 10, Stable.Length + 0 })
            {
                var body = Claude().BuildClaudeChatBody(Conversation(), System, 4096, true, n);
                var req = Parse(body);
                Assert.Equal(System, SystemText(req), "unchanged for length " + n);
                foreach (var block in Array(req["system"]))
                    Assert.True(((string)Obj(block)["text"]).Trim().Length > 0, "no empty block for length " + n);
                Assert.True(Markers(body) <= 4, "at most four breakpoints for length " + n);
            }

            // Nothing stable: no system breakpoint, the message one still there.
            var none = Claude().BuildClaudeChatBody(Conversation(), System, 4096, true, 0);
            Assert.Equal(1, Markers(none), "length 0: only the message is marked");

            // A tail that is only whitespace is not split off: an empty block is a 400.
            var padded = Stable + "\n\n  ";
            var req2 = Parse(Claude().BuildClaudeChatBody(Conversation(), padded, 4096, true, Stable.Length));
            Assert.Equal(1, Array(req2["system"]).Length, "a whitespace tail stays with the stable part");
            Assert.Equal(padded, SystemText(req2), "and nothing is lost");
        }

        public static void ClaudeChat_ImagesInTheLastMessage_TheMarkerGoesOnItsLastBlock()
        {
            var img = new ImageAttachment { Data = new byte[] { 1, 2, 3 }, MimeType = "image/png" };
            var withText = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "What is this?", Images = new List<ImageAttachment> { img } } };
            var req = Parse(Claude().BuildClaudeChatBody(withText, System, 4096, true, Stable.Length));
            var blocks = Array(Obj(Array(req["messages"])[0])["content"]);
            Assert.Equal("image", (string)Obj(blocks[0])["type"], "image first, as always");
            Assert.True(Obj(blocks[1]).ContainsKey("cache_control"), "the text after it carries the marker");

            // Image only: no empty text block (it could not carry a marker); the image does.
            var imageOnly = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "", Images = new List<ImageAttachment> { img } } };
            var body = Claude().BuildClaudeChatBody(imageOnly, System, 4096, true, Stable.Length);
            blocks = Array(Obj(Array(Parse(body)["messages"])[0])["content"]);
            Assert.Equal(1, blocks.Length, "just the image");
            Assert.True(Obj(blocks[0]).ContainsKey("cache_control"), "the image is marked");
        }

        // ── direct Claude, tool chat ──────────────────────────────────────

        private const string ToolUseReply =
            "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\",\"content\":["
            + "{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"c2ln\"},"
            + "{\"type\":\"text\",\"text\":\"Let me look.\"},"
            + "{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"studio_list_projects\",\"input\":{}}],"
            + "\"stop_reason\":\"tool_use\",\"stop_sequence\":null,"
            + "\"usage\":{\"input_tokens\":12,\"cache_creation_input_tokens\":3000,\"cache_read_input_tokens\":0,"
            + "\"cache_creation\":{\"ephemeral_5m_input_tokens\":3000,\"ephemeral_1h_input_tokens\":0},\"output_tokens\":40}}";

        private const string FinalReply =
            "{\"id\":\"msg_2\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"You have 2 projects.\"}],"
            + "\"stop_reason\":\"end_turn\",\"stop_sequence\":null,"
            + "\"usage\":{\"input_tokens\":30,\"cache_creation_input_tokens\":80,\"cache_read_input_tokens\":3000,\"output_tokens\":20}}";

        /// <summary>Plays recorded replies in order and keeps every request body.</summary>
        private sealed class Server
        {
            private readonly Queue<Func<string>> _replies;
            public readonly List<string> Requests = new List<string>();
            public Server(params Func<string>[] replies) { _replies = new Queue<Func<string>>(replies); }
            public Task<string> Post(string body, CancellationToken ct)
            {
                Requests.Add(body);
                if (_replies.Count == 0) throw new InvalidOperationException("no more replies");
                return Task.FromResult(_replies.Dequeue()());
            }
        }

        private static LlmClient.ClaudeToolOutcome RunTools(Server server, bool cache, LlmClient.ClaudeToolOutcome outcome = null)
        {
            outcome = outcome ?? new LlmClient.ClaudeToolOutcome();
            LlmClient.RunClaudeToolLoopAsync(server.Post, "claude-sonnet-5-5", 4096, System, ClaudeTools, Conversation(),
                cache, Stable.Length, (name, input) => "[{\"name\":\"PROJ-001\"}]", null, outcome, CancellationToken.None)
                .GetAwaiter().GetResult();
            return outcome;
        }

        public static void ClaudeTools_MarkTools_System_AndTheNewestMessage_EachRound()
        {
            var server = new Server(() => ToolUseReply, () => FinalReply);
            var outcome = RunTools(server, cache: true);

            Assert.Equal("You have 2 projects.", outcome.Text, "the answer");
            Assert.Equal(2, server.Requests.Count, "two rounds");

            foreach (var body in server.Requests)
            {
                var req = Parse(body);
                Assert.Equal(3, Markers(body), "three breakpoints a round, of the four allowed");
                var tools = Array(req["tools"]);
                Assert.True(Obj(tools[tools.Length - 1]).ContainsKey("cache_control"), "the last tool is marked");
                Assert.True(!Obj(tools[0]).ContainsKey("cache_control"), "only the last");
                Assert.Equal(System, SystemText(req), "the system text unchanged");
                Assert.True(Obj(Array(req["system"])[0]).ContainsKey("cache_control"), "the stable part marked");
            }

            var round1 = Array(Parse(server.Requests[0])["messages"]);
            var r1Last = Array(Obj(round1[2])["content"]);
            Assert.True(Obj(r1Last[r1Last.Length - 1]).ContainsKey("cache_control"), "round 1: the user's question marked");

            var round2 = Array(Parse(server.Requests[1])["messages"]);
            Assert.Equal(5, round2.Length, "round 2: plus the tool call and its result");
            var question = Array(Obj(round2[2])["content"]);
            Assert.True(!Obj(question[question.Length - 1]).ContainsKey("cache_control"), "round 2: the question no longer marked");
            var results = Array(Obj(round2[4])["content"]);
            Assert.Equal("tool_result", (string)Obj(results[0])["type"], "the tool result goes back");
            Assert.True(Obj(results[results.Length - 1]).ContainsKey("cache_control"), "round 2: the newest tool result marked");
            var replayed = Array(Obj(round2[3])["content"]);
            Assert.Equal("c2ln", (string)Obj(replayed[0])["signature"], "the thinking block goes back as it came");

            // Round 2 begins with round 1's bytes, the markers apart: a cache read.
            for (int i = 0; i < 3; i++)
                Assert.Equal(Unmarked(round1[i]), Unmarked(round2[i]), "message " + i + " the same in both rounds");
        }

        public static void ClaudeTools_UsageIsSummedOverTheRounds_CacheIncluded()
        {
            var outcome = RunTools(new Server(() => ToolUseReply, () => FinalReply), cache: true);
            Assert.Equal(12 + 30, outcome.Usage.RegularInputTokens, "uncached input of both rounds");
            Assert.Equal(3000 + 80, outcome.Usage.CacheWriteTokens, "cache writes of both rounds");
            Assert.Equal(3000, outcome.Usage.CacheReadTokens, "round 2 read what round 1 wrote");
            Assert.Equal(40 + 20, outcome.Usage.OutputTokens, "output of both rounds");
            Assert.Equal("end_turn", outcome.FinishReason, "the last round's stop reason");
        }

        public static void ClaudeTools_ARoundThatFails_KeepsWhatTheEarlierRoundsWereBilled()
        {
            var outcome = new LlmClient.ClaudeToolOutcome();
            var server = new Server(() => ToolUseReply, () => throw new HttpRequestException("Claude API error (529)"));
            try
            {
                RunTools(server, cache: true, outcome: outcome);
                throw new AssertFailed("the failure was swallowed");
            }
            catch (HttpRequestException) { }

            Assert.True(outcome.Text == null, "no answer");
            Assert.Equal(3000, outcome.Usage.CacheWriteTokens, "round 1 was billed and is kept");
            Assert.Equal(40, outcome.Usage.OutputTokens, "round 1's output");

            // A cancel mid-loop: the same.
            outcome = new LlmClient.ClaudeToolOutcome();
            server = new Server(() => ToolUseReply, () => throw new OperationCanceledException());
            try { RunTools(server, cache: true, outcome: outcome); throw new AssertFailed("the cancel was swallowed"); }
            catch (OperationCanceledException) { }
            Assert.Equal(12, outcome.Usage.RegularInputTokens, "cancelled in round 2: round 1 kept");
        }

        public static void ClaudeTools_WithoutCaching_SendNoMarker_AndAPlainSystemString()
        {
            var server = new Server(() => ToolUseReply, () => FinalReply);
            var outcome = RunTools(server, cache: false);
            Assert.Equal("You have 2 projects.", outcome.Text, "the answer");
            foreach (var body in server.Requests)
            {
                Assert.Equal(0, Markers(body), "no breakpoint");
                Assert.True(body.Contains("\"system\":" + Json(System) + ","), "the system prompt as the string it was");
                Assert.True(body.Contains(",\"tools\":" + ClaudeTools + ","), "the tools exactly as given");
            }
            Assert.Equal(42, outcome.Usage.RegularInputTokens, "usage is recorded without caching too");
        }

        // ── Claude through OpenRouter ─────────────────────────────────────

        public static void OpenRouterClaude_MarksTheSystemAndTheLastMessage()
        {
            var client = new LlmClient(LlmModels.ProviderOpenRouter, "anthropic/claude-sonnet-5.5", "k");
            var body = client.BuildOpenAiChatBody(Conversation(), System, 4096, true, Stable.Length);
            var req = Parse(body);
            Assert.Equal(2, Markers(body), "system and last message");

            var messages = Array(req["messages"]);
            var system = Array(Obj(messages[0])["content"]);
            Assert.Equal("system", (string)Obj(messages[0])["role"], "system first");
            Assert.True(Obj(system[0]).ContainsKey("cache_control"), "the stable part marked");
            Assert.Equal(System, string.Concat(system.Select(b => (string)Obj(b)["text"])), "system text unchanged");
            var last = Array(Obj(messages[3])["content"]);
            Assert.True(Obj(last[0]).ContainsKey("cache_control"), "the last message's text part marked");

            var turns = client.OpenAiToolConversation(Conversation(), System, true, Stable.Length);
            Assert.True(turns[0].ContentJson.Contains("cache_control"), "tool chat: system marked");
            Assert.True(turns[3].ContentJson.Contains("cache_control"), "tool chat: last message marked");
            Assert.True(!turns[1].ContentJson.Contains("cache_control"), "tool chat: earlier messages not");
        }

        // ── every other provider ──────────────────────────────────────────

        public static void OtherProviders_GetNoMarker_AndTheRequestTheyGotBefore()
        {
            var others = new[]
            {
                new[] { LlmModels.ProviderOpenAi, "gpt-6-luna" },
                new[] { LlmModels.ProviderDeepSeek, "deepseek-chat" },
                new[] { LlmModels.ProviderGrok, "grok-4" },
                new[] { LlmModels.ProviderMistral, "mistral-large-latest" },
                new[] { LlmModels.ProviderOpenRouter, "openai/gpt-6-luna" },
                new[] { LlmModels.ProviderOpenRouter, "deepseek/deepseek-chat" },
                // A custom endpoint serving a Claude model is still someone else's
                // server: it might refuse a field it does not know.
                new[] { LlmModels.ProviderCustomOpenAi, "claude-sonnet-5-5" },
            };
            foreach (var p in others)
            {
                var client = new LlmClient(p[0], p[1], "k", "http://127.0.0.1:9/v1");
                var cached = client.BuildOpenAiChatBody(Conversation(), System, 4096, true, Stable.Length);
                var plain = client.BuildOpenAiChatBody(Conversation(), System, 4096, false, Stable.Length);
                Assert.Equal(0, Markers(cached), p[0] + "/" + p[1] + ": no marker");
                Assert.Equal(plain, cached, p[0] + "/" + p[1] + ": the same request as without caching");
                Parse(cached);

                var a = client.OpenAiToolConversation(Conversation(), System, true, Stable.Length);
                var b = client.OpenAiToolConversation(Conversation(), System, false, Stable.Length);
                Assert.Equal(string.Join("|", b.Select(t => t.ContentJson)), string.Join("|", a.Select(t => t.ContentJson)),
                    p[0] + "/" + p[1] + ": tool chat unchanged");
            }
            // Gemini and Ollama are not handed the setting at all: CallGeminiChatAsync,
            // CallOllamaChatAsync and the Gemini tool loop take no caching argument.
        }

        public static void ALocalServer_GetsNoMarker_EndToEnd()
        {
            var chat = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "hello" } };

            using (var server = new LocalServerTests.FakeJsonServer(_ => OpenAiReply("hi")))
            using (var client = new LlmClient(LlmModels.ProviderCustomOpenAi, "claude-sonnet-5-5", "", server.Url + "/v1"))
            {
                client.SendChatAsync(chat, System, enablePromptCaching: true, stableSystemPromptLength: Stable.Length).GetAwaiter().GetResult();
                Assert.Equal(0, Markers(server.LastBody), "custom endpoint, plain chat");
                Assert.True(server.LastBody.Contains("\"content\":" + Json(System)), "its system prompt as a plain string");

                client.SendChatWithToolsAsync(chat, System, "[]", (n, i) => "", enablePromptCaching: true,
                    stableSystemPromptLength: Stable.Length).GetAwaiter().GetResult();
                Assert.Equal(0, Markers(server.LastBody), "custom endpoint, tool chat");
            }

            using (var server = new LocalServerTests.FakeJsonServer(_ =>
                "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done_reason\":\"stop\",\"done\":true,"
                + "\"prompt_eval_count\":40,\"eval_count\":3}"))
            using (var client = new LlmClient(LlmModels.ProviderOllama, "test-model", "", server.Url))
            {
                client.SendChatAsync(chat, System, enablePromptCaching: true).GetAwaiter().GetResult();
                Assert.Equal(0, Markers(server.LastBody), "Ollama, plain chat");
                client.SendChatWithToolsAsync(chat, System, "[]", (n, i) => "", enablePromptCaching: true).GetAwaiter().GetResult();
                Assert.Equal(0, Markers(server.LastBody), "Ollama, tool chat (falls back to plain)");
            }
        }

        // ── usage on the other chat paths, and no stale state ─────────────

        public static void OpenAiShapedChat_RecordsUsage_PlainAndWithTools()
        {
            var chat = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "hello" } };
            using (var server = new LocalServerTests.FakeJsonServer(_ => OpenAiReply("hi")))
            using (var client = new LlmClient(LlmModels.ProviderCustomOpenAi, "local", "", server.Url + "/v1"))
            {
                client.SendChatAsync(chat, System).GetAwaiter().GetResult();
                Assert.True(client.LastUsage != null, "plain chat: usage recorded");
                Assert.Equal(90, client.LastUsage.RegularInputTokens, "prompt tokens less the cached ones");
                Assert.Equal(10, client.LastUsage.CacheReadTokens, "cached tokens");
                Assert.Equal("stop", client.LastFinishReason, "finish reason recorded");

                client.SendChatWithToolsAsync(chat, System, "[]", (n, i) => "").GetAwaiter().GetResult();
                Assert.Equal(10, client.LastUsage.CacheReadTokens, "tool chat: usage recorded");
            }
        }

        public static void AFailedChat_IsNotLoggedWithThePreviousCallsUsage()
        {
            var chat = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "hello" } };
            var server = new LocalServerTests.FakeJsonServer(_ => OpenAiReply("hi"));
            using (var client = new LlmClient(LlmModels.ProviderCustomOpenAi, "local", "", server.Url + "/v1"))
            {
                client.SendChatAsync(chat, System).GetAwaiter().GetResult();
                Assert.True(client.LastUsage != null, "the first call recorded usage");

                server.Dispose();   // the next request finds nobody listening
                try { client.SendChatAsync(chat, System).GetAwaiter().GetResult(); throw new AssertFailed("no failure"); }
                catch (HttpRequestException) { }
                Assert.True(client.LastUsage == null, "plain chat: the failed call has no usage of its own");
                Assert.True(client.LastFinishReason == null, "nor a stop reason");

                try { client.SendChatWithToolsAsync(chat, System, "[]", (n, i) => "").GetAwaiter().GetResult(); throw new AssertFailed("no failure"); }
                catch (HttpRequestException) { }
                Assert.True(client.LastUsage == null, "tool chat: the same");
            }
        }

        // ── the marker itself ─────────────────────────────────────────────

        public static void TheMarker_GoesOnTheLastObject_OrNowhere()
        {
            var tools = LlmClient.WithCacheMarkerOnLastObject(ClaudeTools);
            var parsed = Array(new JavaScriptSerializer().DeserializeObject(tools));
            Assert.True(Obj(parsed[1]).ContainsKey("cache_control"), "pretty-printed tools: the last one marked");
            Assert.Equal(1, Markers(tools), "only once");

            Assert.Equal("[{\"type\":\"text\",\"text\":\"a } ] b\",\"cache_control\":{\"type\":\"ephemeral\"}}]",
                LlmClient.WithCacheMarkerOnLastObject("[{\"type\":\"text\",\"text\":\"a } ] b\"}]"),
                "braces inside a string do not mislead it");

            foreach (var unchanged in new[] { null, "", "[]", "[{}]", "\"text\"", "[{\"a\":1},\"str\"]",
                "[{\"type\":\"text\",\"text\":\"\"}]", "[{\"type\":\"text\",\"text\":\"x\",\"cache_control\":{\"type\":\"ephemeral\"}}]" })
                Assert.Equal(unchanged, LlmClient.WithCacheMarkerOnLastObject(unchanged), "left alone: " + (unchanged ?? "null"));
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static string OpenAiReply(string content) =>
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":10}}}";

        private static int Markers(string json) =>
            json == null ? 0 : (json.Length - json.Replace("\"cache_control\"", "").Length) / "\"cache_control\"".Length;

        private static Dictionary<string, object> Parse(string json)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return Obj(js.DeserializeObject(json));
        }

        private static Dictionary<string, object> Obj(object o) => (Dictionary<string, object>)o;
        private static object[] Array(object o) => (object[])o;

        private static string SystemText(Dictionary<string, object> req) =>
            req["system"] is string s ? s : string.Concat(Array(req["system"]).Select(b => (string)Obj(b)["text"]));

        /// <summary>The value as JSON with every cache_control removed, for comparing prefixes.</summary>
        private static string Unmarked(object value) => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Strip(value));

        private static object Strip(object value)
        {
            if (value is Dictionary<string, object> d)
                return d.Where(kv => kv.Key != "cache_control").ToDictionary(kv => kv.Key, kv => Strip(kv.Value));
            if (value is object[] a) return a.Select(Strip).ToArray();
            return value;
        }

        /// <summary>A JSON string literal as LlmClient writes one, for the texts used here
        /// (quotes, backslashes and newlines escaped, an en dash left as it is).</summary>
        private static string Json(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }
}
