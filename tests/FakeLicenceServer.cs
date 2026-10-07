using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// A stand-in for the licence server on a local port: answers each POST
    /// with whatever the test says, and records what was asked. Raw sockets
    /// rather than HttpListener, which needs a URL reservation to run unelevated.
    /// </summary>
    internal sealed class FakeLicenceServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, IDictionary<string, string>, string> _reply;
        private readonly List<string> _requests = new List<string>();

        public string Url { get; }

        /// <param name="reply">Given the endpoint ("activate", "validate", "deactivate") and the form, returns the JSON body.</param>
        public FakeLicenceServer(Func<string, IDictionary<string, string>, string> reply)
        {
            _reply = reply;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/v1/licenses";
            new Thread(Serve) { IsBackground = true }.Start();
        }

        /// <summary>Each request as "endpoint instance_id", in order.</summary>
        public string[] Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
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
                        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var path = lines[0].Split(' ')[1];
                        int length = 0;
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                length = int.Parse(line.Substring("Content-Length:".Length).Trim());
                            // .NET Framework's HttpClient waits for this before sending a body.
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

                        var form = ParseForm(Encoding.UTF8.GetString(body));
                        var endpoint = path.Substring(path.LastIndexOf('/') + 1);
                        lock (_requests)
                            _requests.Add(endpoint + " " + (form.TryGetValue("instance_id", out var id) ? id : ""));

                        var json = Encoding.UTF8.GetBytes(_reply(endpoint, form));
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

        private static Dictionary<string, string> ParseForm(string body)
        {
            var form = new Dictionary<string, string>();
            foreach (var pair in body.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                form[Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '))] =
                    Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return form;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }
}
