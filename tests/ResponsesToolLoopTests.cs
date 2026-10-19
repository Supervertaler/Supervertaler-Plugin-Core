using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The tool-using chat on OpenAI's Responses API, for GPT-6.1 Sol, which
    /// takes tools nowhere else (18/19.20.201). Run on replies shaped as the API
    /// reference gives them (checked 2026-10-19): what goes out, what is read,
    /// and what goes back with the tool results.
    /// </summary>
    [Tests]
    internal static class ResponsesToolLoopTests
    {
        // As Trados's TradosTools.GetToolDefinitionsJson gives them for OpenAI.
        private const string ChatTools =
            "[{\"type\":\"function\",\"function\":{\"name\":\"lookup_term\",\"description\":\"Look a term up.\","
            + "\"parameters\":{\"type\":\"object\",\"properties\":{\"term\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"term\"]}}},"
            + "{\"type\":\"function\",\"function\":{\"name\":\"get_segment\",\"description\":\"Read a segment.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}]";

        private const string Reasoning =
            "{\"id\":\"rs_1\",\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"gAAAA-opaque==\"}";
        private const string CallLookup =
            "{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{\\\"term\\\":\\\"klep\\\"}\",\"call_id\":\"call_1\",\"name\":\"lookup_term\"}";
        private const string CallSegment =
            "{\"id\":\"fc_2\",\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{}\",\"call_id\":\"call_2\",\"name\":\"get_segment\"}";

        private static string Reply(string status, string outputItems, int input, int cached, int output, string extra = "")
        {
            return "{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"" + status + "\",\"error\":null," + extra
                + "\"output\":[" + outputItems + "],"
                + "\"usage\":{\"input_tokens\":" + input + ",\"input_tokens_details\":{\"cached_tokens\":" + cached + "},"
                + "\"output_tokens\":" + output + ",\"output_tokens_details\":{\"reasoning_tokens\":" + (output / 2) + "},"
                + "\"total_tokens\":" + (input + output) + "}}";
        }

        private static string Message(string text) =>
            "{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\",\"annotations\":[]}]}";

        private static List<ChatMessage> Conversation() => new List<ChatMessage>
        {
            new ChatMessage { Role = ChatRole.User, Content = "What is klep?" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "Let me check." },
            new ChatMessage { Role = ChatRole.User, Content = "And segment 3?" },
        };

        /// <summary>Plays recorded replies in order and keeps every request body.</summary>
        private sealed class Server
        {
            private readonly Queue<string> _replies;
            public readonly List<string> Requests = new List<string>();
            public Server(params string[] replies) { _replies = new Queue<string>(replies); }
            public Task<string> Post(string body, CancellationToken ct)
            {
                Requests.Add(body);
                if (_replies.Count == 0) throw new InvalidOperationException("no more replies");
                return Task.FromResult(_replies.Dequeue());
            }
        }

        private static LlmClient.ResponsesOutcome Run(Server server, LlmClient.ToolExecutor executor,
            List<string> statuses = null, bool temperature = false)
        {
            return LlmClient.RunResponsesToolLoopAsync(server.Post, "gpt-6.1-sol", "You are a translator's assistant.",
                Conversation(), ChatTools, 16384, temperature, executor, s => statuses?.Add(s), CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        public static void Tools_TakeTheResponsesShape_NotStrict()
        {
            var tools = LlmClient.ToResponsesTools(ChatTools);
            Assert.True(tools.StartsWith("[{\"type\":\"function\",\"strict\":false,\"name\":\"lookup_term\""), "flattened, strict false, name kept: " + tools);
            Assert.True(tools.Contains("\"required\":[\"term\"]"), "the schema is passed on as it is");
            Assert.True(!tools.Contains("\"function\":{"), "no Chat Completions wrapper left");
            Assert.Equal(2, tools.Split(new[] { "\"type\":\"function\",\"strict\":false" }, StringSplitOptions.None).Length - 1, "both tools");

            var strict = LlmClient.ToResponsesTools("[{\"type\":\"function\",\"function\":{\"name\":\"a\",\"strict\":true,\"parameters\":{}}}]");
            Assert.Equal("[{\"type\":\"function\",\"name\":\"a\",\"strict\":true,\"parameters\":{}}]", strict, "an explicit strict is left as it is");
            var flat = "[{\"type\":\"function\",\"name\":\"b\",\"parameters\":{}}]";
            Assert.Equal(flat, LlmClient.ToResponsesTools(flat), "a tool already in the Responses shape is passed on");
            Assert.Equal("[]", LlmClient.ToResponsesTools(null), "no tools");
        }

        public static void TopLevelValue_ReadsOnlyTheObjectsOwnKeys()
        {
            var item = "{\"content\":[{\"type\":\"output_text\",\"text\":\"a \\\"quoted\\\" word\"}],\"type\":\"message\",\"n\":42,\"ok\":true,\"none\":null}";
            Assert.Equal("message", LlmClient.TopLevelString(item, "type"), "the item's own type, not the nested part's (which comes first)");
            Assert.Equal("42", LlmClient.TopLevelValue(item, "n"), "a number");
            Assert.Equal("true", LlmClient.TopLevelValue(item, "ok"), "a literal");
            Assert.True(LlmClient.TopLevelString(item, "none") == null && LlmClient.TopLevelValue(item, "none") == "null", "null");
            Assert.True(LlmClient.TopLevelValue(item, "text") == null, "a key only nested deeper is not found");
            Assert.True(LlmClient.TopLevelValue(item, "typ") == null && LlmClient.TopLevelValue(item, "types") == null, "no prefix or longer-name matches");
            var part = LlmClient.TopLevelValue(item, "content");
            Assert.True(part.StartsWith("[") && part.EndsWith("]"), "an array, whole");
            Assert.Equal("a \"quoted\" word", LlmClient.TopLevelString(part.Substring(1, part.Length - 2), "text"), "escapes undone");
        }

        public static void ToolCall_ResultGoesBack_WithTheReasoning_AndTheAnswerIsRead()
        {
            var server = new Server(
                Reply("completed", Reasoning + "," + CallLookup, 1000, 0, 120),
                Reply("completed", Message("Klep is valve."), 1300, 1000, 40));
            var calls = new List<string>();
            var statuses = new List<string>();
            var outcome = Run(server, (name, args) => { calls.Add(name + " " + args); return "klep -> \"valve\""; }, statuses);

            Assert.Equal("Klep is valve.", outcome.Text, "the answer");
            Assert.Equal("completed", outcome.FinishReason, "finished");
            Assert.True(calls.SequenceEqual(new[] { "lookup_term {\"term\":\"klep\"}" }), "the tool ran once, with its arguments unescaped: " + string.Join(" | ", calls));
            Assert.True(statuses.SequenceEqual(new[] { "lookup_term" }), "the chat was told which tool");
            Assert.Equal(2, server.Requests.Count, "two requests");

            var first = server.Requests[0];
            Assert.Equal("gpt-6.1-sol", LlmClient.TopLevelString(first, "model"), "the model");
            Assert.Equal("You are a translator's assistant.", LlmClient.TopLevelString(first, "instructions"), "the system prompt as instructions");
            Assert.Equal("false", LlmClient.TopLevelValue(first, "store"), "store false: OpenAI keeps no copy");
            Assert.Equal("[\"reasoning.encrypted_content\"]", LlmClient.TopLevelValue(first, "include"), "the reasoning comes back encrypted, to be sent back");
            Assert.Equal("16384", LlmClient.TopLevelValue(first, "max_output_tokens"), "the output ceiling");
            Assert.True(LlmClient.TopLevelValue(first, "temperature") == null, "no temperature for a reasoning model");
            Assert.True(LlmClient.TopLevelValue(first, "reasoning") == null, "no reasoning setting: the model's own default");
            Assert.True(first.Contains("{\"role\":\"user\",\"content\":\"What is klep?\"}")
                        && first.Contains("{\"role\":\"assistant\",\"content\":\"Let me check.\"}"), "the conversation as input items");
            Assert.True(!first.Contains("function_call_output"), "nothing to send back yet");

            var second = server.Requests[1];
            int iReasoning = second.IndexOf(Reasoning, StringComparison.Ordinal);
            int iCall = second.IndexOf(CallLookup, StringComparison.Ordinal);
            int iResult = second.IndexOf("{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"klep -> \\\"valve\\\"\"}", StringComparison.Ordinal);
            Assert.True(iReasoning > 0 && iCall > iReasoning && iResult > iCall,
                "reasoning, then the call, both verbatim, then its result by call_id (" + iReasoning + " < " + iCall + " < " + iResult + ")");
            Assert.True(second.IndexOf("What is klep?", StringComparison.Ordinal) > 0, "the whole conversation again: nothing is stored");

            Assert.True(outcome.Usage != null && outcome.Usage.RegularInputTokens == 1300 && outcome.Usage.CacheReadTokens == 1000
                        && outcome.Usage.OutputTokens == 160,
                "usage summed over both rounds, cached tokens apart: " + (outcome.Usage == null ? "none"
                    : outcome.Usage.RegularInputTokens + "/" + outcome.Usage.CacheReadTokens + "/" + outcome.Usage.OutputTokens));
        }

        public static void TwoCallsInOneRound_EachGetsItsOwnResult()
        {
            var server = new Server(
                Reply("completed", Reasoning + "," + CallLookup + "," + CallSegment, 900, 0, 80),
                Reply("completed", Message("Both done."), 1200, 0, 20));
            var outcome = Run(server, (name, args) => name == "lookup_term" ? "valve" : "segment 3 text");
            var second = server.Requests[1];
            int r1 = second.IndexOf("\"call_id\":\"call_1\",\"output\":\"valve\"", StringComparison.Ordinal);
            int r2 = second.IndexOf("\"call_id\":\"call_2\",\"output\":\"segment 3 text\"", StringComparison.Ordinal);
            Assert.True(r1 > 0 && r2 > r1, "one result per call, in order");
            Assert.Equal("Both done.", outcome.Text, "then the answer");
        }

        public static void CutShort_SaysWhy()
        {
            var server = new Server(Reply("incomplete", Message("Klep is"), 1000, 0, 16384,
                "\"incomplete_details\":{\"reason\":\"max_output_tokens\"},"));
            var outcome = Run(server, (n, a) => "");
            Assert.Equal("Klep is", outcome.Text, "what there is");
            Assert.Equal("max_output_tokens", outcome.FinishReason, "and why it stopped");
        }

        public static void Failed_Throws_WithOpenAisMessage()
        {
            var server = new Server("{\"id\":\"resp_1\",\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"The server had an error.\"},\"output\":[]}");
            try { Run(server, (n, a) => ""); Assert.True(false, "a failed response must throw"); }
            catch (InvalidOperationException ex)
            {
                Assert.True(ex.Message.Contains("The server had an error."), "OpenAI's own message: " + ex.Message);
            }
        }

        public static void Refusal_IsReadWhenThereIsNoText()
        {
            var refusal = "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"I can't help with that.\"}]}";
            var outcome = Run(new Server(Reply("completed", refusal, 10, 0, 5)), (n, a) => "");
            Assert.Equal("I can't help with that.", outcome.Text, "the refusal's text");
        }

        public static void Images_GoAsInputImages()
        {
            var input = LlmClient.ResponsesInput(new List<ChatMessage>
            {
                new ChatMessage { Role = ChatRole.User, Content = "What does this show?",
                    Images = new List<ImageAttachment> { new ImageAttachment { MimeType = "image/png", Data = new byte[] { 1, 2, 3 } } } }
            });
            Assert.Equal(1, input.Count, "one item");
            Assert.True(input[0].Contains("{\"type\":\"input_text\",\"text\":\"What does this show?\"}")
                        && input[0].Contains("{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,AQID\"}"),
                "text and image parts: " + input[0]);
        }

        public static void ARunawayLoop_StopsAfterFiveRounds()
        {
            var replies = Enumerable.Repeat(Reply("completed", CallLookup, 10, 0, 5), 6).ToArray();
            var server = new Server(replies);
            int ran = 0;
            try { Run(server, (n, a) => { ran++; return "x"; }); Assert.True(false, "must stop"); }
            catch (InvalidOperationException ex)
            {
                Assert.True(ex.Message.Contains("maximum rounds") && server.Requests.Count == 5 && ran == 5,
                    "five rounds, then it stops: " + server.Requests.Count + " requests, " + ran + " tool runs");
            }
        }

        public static void Temperature_OnlyWhenTheModelTakesIt()
        {
            var server = new Server(Reply("completed", Message("ok"), 10, 0, 5));
            Run(server, (n, a) => "", temperature: true);
            Assert.Equal("0.3", LlmClient.TopLevelValue(server.Requests[0], "temperature"), "sent when asked for");
        }
    }
}
