using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// What a model on the translator's own machine is sent and how its answer is
    /// judged: a context window sized to the prompt (Ollama cuts silently
    /// without one), a size that only grows once it has worked, the model kept
    /// loaded, and an answer refused when the server read too little of the
    /// prompt. Measurements behind the numbers: LlmClient, "Servers on the
    /// translator's own machine".
    /// </summary>
    [Tests]
    internal static class LocalServerTests
    {
        // ── sizing ─────────────────────────────────────────────────────────

        public static void TheContext_HoldsTwiceTheEstimatedPrompt_PlusTheAnswer()
        {
            Assert.Equal(8192, LlmClient.OllamaContextFor(300, 1024), "a short chat question");
            Assert.Equal(8192, LlmClient.OllamaContextFor(0, 8192), "exactly a step");
            Assert.Equal(16384, LlmClient.OllamaContextFor(1, 8192), "one token over a step");
            // Measured 17 Oct: a glossary of numbers and codes estimated at 6,114
            // tokens was 12,219 to the model. Its request must still fit.
            Assert.True(LlmClient.OllamaContextFor(6114, 1024) >= 12219 + 1024, "the densest prompt measured fits");
            // A memory-bank batch: ~16,000 estimated, Batch Translate's 16,384 for the answer.
            Assert.Equal(65536, LlmClient.OllamaContextFor(16000, 16384), "a batch with the memory bank");
            Assert.Equal(131072, LlmClient.OllamaContextFor(500000, 8192), "above the largest step, the largest step");
        }

        public static void TheContextSent_NeverShrinks_AndGrowsOnlyAfterSuccess()
        {
            var endpoint = "http://test-" + Guid.NewGuid().ToString("N");
            Assert.Equal(16384, LlmClient.OllamaContextToSend(endpoint, "m", 16384), "nothing used yet: what is needed");

            LlmClient.RememberOllamaContext(endpoint, "m", 32768);
            Assert.Equal(32768, LlmClient.OllamaContextToSend(endpoint, "m", 8192), "a short prompt keeps the size in use");
            Assert.Equal(65536, LlmClient.OllamaContextToSend(endpoint, "m", 65536), "a longer one asks for more");
            // ... but until that request has worked, the size in use stays.
            Assert.Equal(32768, LlmClient.OllamaContextToSend(endpoint, "m", 8192), "a size that has not worked is not kept");

            LlmClient.RememberOllamaContext(endpoint, "m", 16384);
            Assert.Equal(32768, LlmClient.OllamaContextToSend(endpoint, "m", 8192), "a smaller success does not shrink it");
            Assert.Equal(8192, LlmClient.OllamaContextToSend(endpoint, "other", 8192), "each model has its own");
        }

        public static void ReadTooLittle_IsLessThanHalf_OfALongEnoughPrompt()
        {
            // aya-expanse:8b, 13 Oct: a 16,446-token prompt, 4,099 read.
            Assert.True(LlmClient.ReadTooLittle(16446, 4099), "a quarter read");
            Assert.True(!LlmClient.ReadTooLittle(16446, 16600), "all of it read");
            Assert.True(!LlmClient.ReadTooLittle(16446, 8223), "exactly half is not judged short");
            Assert.True(!LlmClient.ReadTooLittle(1000, 10), "too short a prompt to judge");
            Assert.True(!LlmClient.ReadTooLittle(16446, 0), "nothing reported is no evidence");
        }

        // ── the request itself ─────────────────────────────────────────────

        public static void AnOllamaRequest_SendsContextKeepAliveAndNoThinking()
        {
            using (var server = new FakeJsonServer(_ => OllamaReply("Hallo", promptTokens: 40)))
            using (var client = new LlmClient(LlmModels.ProviderOllama, "test-model", "", server.Url))
            {
                var reply = client.SendPromptAsync("Translate: hello", "Be brief.").GetAwaiter().GetResult();
                Assert.Equal("Hallo", reply, "the answer");

                var body = server.LastBody;
                Assert.True(body.Contains("\"think\":false"), "thinking off: " + body);
                Assert.True(body.Contains("\"keep_alive\":\"" + LlmClient.OllamaKeepAlive + "\""), "keep_alive: " + body);
                Assert.True(body.Contains("\"num_predict\":8192"), "num_predict as before: " + body);
                // Room for the whole answer allowance, not just the prompt: a
                // context that cannot hold the answer cuts the translation off.
                Assert.True(body.Contains("\"num_ctx\":16384"), "num_ctx holds prompt and answer: " + body);
            }
        }

        public static void ASmallAnswerAllowance_GetsTheSmallestStep()
        {
            using (var server = new FakeJsonServer(_ => OllamaReply("ja", promptTokens: 20)))
            using (var client = new LlmClient(LlmModels.ProviderOllama, "small-step-model", "", server.Url))
            {
                client.SendPromptAsync("Yes or no?", null, maxTokens: 1024).GetAwaiter().GetResult();
                Assert.True(server.LastBody.Contains("\"num_ctx\":8192"), "num_ctx: " + server.LastBody);
            }
        }

        public static void AnOllamaAnswer_MadeFromPartOfThePrompt_IsRefused_AndItsSizeNotKept()
        {
            var system = new string('x', 40000); // ~10,000 tokens by the estimate
            using (var server = new FakeJsonServer(_ => OllamaReply("Fluent but uninstructed", promptTokens: 2051)))
            using (var client = new LlmClient(LlmModels.ProviderOllama, "short-window", "", server.Url))
            {
                var refused = Throws<PromptTooLargeException>(() =>
                    client.SendPromptAsync("Translate: hello", system).GetAwaiter().GetResult());
                Assert.True(refused.Message.Contains("2,051") || refused.Message.Contains("2.051"),
                    "the message says how much was read: " + refused.Message);
                Assert.True(server.LastBody.Contains("\"num_ctx\":32768"), "it asked for room: " + server.LastBody);

                // The failed size is not what the next, short request asks for.
                client.SendPromptAsync("Translate: hi", null, maxTokens: 1024).GetAwaiter().GetResult();
                Assert.True(server.LastBody.Contains("\"num_ctx\":8192"), "back to what is needed: " + server.LastBody);
            }
        }

        public static void ACustomServerAnswer_MadeFromPartOfThePrompt_IsRefused()
        {
            var system = new string('x', 40000);
            using (var server = new FakeJsonServer(_ =>
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":2048,\"completion_tokens\":1}}"))
            using (var client = new LlmClient(LlmModels.ProviderCustomOpenAi, "local-model", "", server.Url + "/v1"))
            {
                Throws<PromptTooLargeException>(() =>
                    client.SendPromptAsync("Translate: hello", system).GetAwaiter().GetResult());
                Throws<PromptTooLargeException>(() =>
                    client.SendChatAsync(new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = "hello" } }, system)
                        .GetAwaiter().GetResult());
            }
        }

        public static void TheTranslatorsOwnCancel_StaysACancel_NotATimeout()
        {
            using (var server = new FakeJsonServer(_ => OllamaReply("x", promptTokens: 1)))
            using (var client = new LlmClient(LlmModels.ProviderOllama, "test-model", "", server.Url))
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var ex = Throws<Exception>(() =>
                    client.SendPromptAsync("hello", null, cancellationToken: cancelled.Token).GetAwaiter().GetResult());
                Assert.True(ex is OperationCanceledException, "a cancellation, got " + ex.GetType().Name);
            }
        }

        // ── helpers ────────────────────────────────────────────────────────

        private static string OllamaReply(string content, int promptTokens) =>
            "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}," +
            "\"done_reason\":\"stop\",\"done\":true,\"prompt_eval_count\":" + promptTokens + ",\"eval_count\":3}";

        private static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new AssertFailed("expected " + typeof(T).Name + ", got " + ex.GetType().Name + ": " + ex.Message); }
            throw new AssertFailed("expected " + typeof(T).Name + ", nothing was thrown");
        }

        /// <summary>
        /// A server on a local port that answers every request with the JSON the
        /// test gives and keeps the last request body. Raw sockets, as in
        /// <see cref="FakeLicenceServer"/>: HttpListener needs a URL reservation.
        /// </summary>
        private sealed class FakeJsonServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Func<string, string> _reply;
            private string _lastBody;

            public string Url { get; }

            public string LastBody
            {
                get { lock (this) return _lastBody; }
            }

            public FakeJsonServer(Func<string, string> reply)
            {
                _reply = reply;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
                new Thread(Serve) { IsBackground = true }.Start();
            }

            private void Serve()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { return; }

                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            var head = ReadHead(stream);
                            int length = 0;
                            foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    length = int.Parse(line.Substring("Content-Length:".Length).Trim());
                                if (line.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
                                    Write(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                            }

                            var body = new byte[length];
                            for (int read = 0; read < length;)
                            {
                                int n = stream.Read(body, read, length - read);
                                if (n == 0) break;
                                read += n;
                            }
                            var text = Encoding.UTF8.GetString(body);
                            lock (this) _lastBody = text;

                            var json = Encoding.UTF8.GetBytes(_reply(text));
                            Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                                json.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(json, 0, json.Length);
                        }
                    }
                    catch (IOException) { }
                }
            }

            private static string ReadHead(Stream stream)
            {
                var sb = new StringBuilder();
                while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int b = stream.ReadByte();
                    if (b < 0) break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            private static void Write(Stream stream, string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
