using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace QqmBetterDownload {
    // Only our native browser's settings page talks to this short-lived server.
    // Bind loopback, use a fresh capability URL, reject other Host/Origin values,
    // and expose no filesystem, proxy, script execution, or credential APIs.
    internal sealed class LocalPageServer : IDisposable {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly Action<string,string> action;
        readonly Thread thread;
        readonly string token, page, authority;
        volatile bool closed;
        string state = "{}";
        int clients;
        internal string Url { get; private set; }
        internal LocalPageServer(string html, Action<string,string> onAction) {
            page = html; action = onAction;
            var bytes = new byte[24]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            token = "/" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() + "/";
            listener.Start(8); authority = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = "http://" + authority + token + "settings";
            thread = new Thread(Accept) { IsBackground = true, Name = "BetterDownload local page" }; thread.Start();
        }
        internal void Publish(string json) { Interlocked.Exchange(ref state, json); }
        void Accept() {
            while (!closed) {
                try {
                    var client = listener.AcceptTcpClient();
                    if (Interlocked.Increment(ref clients) > 8) { Interlocked.Decrement(ref clients); client.Close(); continue; }
                    ThreadPool.QueueUserWorkItem(delegate { try { Serve(client); } finally { client.Close(); Interlocked.Decrement(ref clients); } });
                } catch (SocketException) { if (!closed) Thread.Sleep(100); }
                catch (ObjectDisposedException) { break; }
            }
        }
        void Serve(TcpClient client) {
            try {
                client.ReceiveTimeout = client.SendTimeout = 2000;
                using (var stream = client.GetStream()) {
                    var header = new StringBuilder();
                    while (header.Length < 8192) {
                        int b = stream.ReadByte(); if (b < 0 || b == 0 || b > 127) return;
                        header.Append((char)b); if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    if (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
                    string[] lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None), request = lines[0].Split(' ');
                    var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length && lines[i].Length > 0; i++) {
                        int at = lines[i].IndexOf(':'); if (at <= 0 || headers.ContainsKey(lines[i].Substring(0, at))) return;
                        headers.Add(lines[i].Substring(0, at), lines[i].Substring(at + 1).Trim());
                    }
                    // Consume a bounded body before rejecting a request. Closing
                    // a TCP socket with unread data resets it and can discard the
                    // rejection response before the browser receives feedback.
                    string declared; int bodySize = 0;
                    if (headers.TryGetValue("Content-Length", out declared) && (!Int32.TryParse(declared, out bodySize) || bodySize < 0 || bodySize > 8192)) { Reply(stream,400,"text/plain","Bad request");return; }
                    var body = new byte[bodySize];int received = 0;
                    while(received<bodySize){int n=stream.Read(body,received,bodySize-received);if(n==0)return;received+=n;}
                    string host, origin;
                    if (request.Length != 3 || request[2] != "HTTP/1.1" || !headers.TryGetValue("Host", out host) || host != authority ||
                        !request[1].StartsWith(token, StringComparison.Ordinal) || headers.ContainsKey("Transfer-Encoding") ||
                        (headers.TryGetValue("Origin", out origin) && origin != "http://" + authority)) { Reply(stream, 403, "text/plain", "Forbidden"); return; }
                    string endpoint = request[1].Substring(token.Length);
                    if (request[0] == "GET" && endpoint == "settings") Reply(stream, 200, "text/html; charset=utf-8", page);
                    else if (request[0] == "GET" && endpoint == "state") Reply(stream, 200, "application/json; charset=utf-8", Interlocked.CompareExchange(ref state, null, null));
                    else if (request[0] == "POST" && endpoint == "action") {
                        string length, type; int size;
                        if (!headers.TryGetValue("Content-Type", out type) || type != "application/json" ||
                            !headers.TryGetValue("Content-Length", out length) || !Int32.TryParse(length, out size) || size < 2 || size > 4096) { Reply(stream, 400, "text/plain", "Bad request"); return; }
                        Dictionary<string,object> data;
                        try { data = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(new UTF8Encoding(false, true).GetString(body)); }
                        catch (ArgumentException) { Reply(stream, 400, "text/plain", "Bad request"); return; }
                        object command, value;
                        if (data == null || !data.TryGetValue("action", out command) || !(command is string)) { Reply(stream, 400, "text/plain", "Bad request"); return; }
                        data.TryGetValue("value", out value);
                        if (Array.IndexOf(new[] { "ready", "close", "preview", "project", "issues", "open", "scan", "toggle", "notify", "style", "stay" }, (string)command) < 0 || (value != null && !(value is string))) { Reply(stream, 400, "text/plain", "Bad request"); return; }
                        if (!closed) action((string)command, value as string);
                        Reply(stream, 202, "application/json", "{}");
                    } else Reply(stream, 404, "text/plain", "Not found");
                }
            } catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        static void Reply(Stream stream, int code, string type, string text) {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string status = code == 200 ? "OK" : code == 202 ? "Accepted" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Bad Request";
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n\r\n");
            stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
        }
        public void Dispose() { if (closed) return; closed = true; listener.Stop(); thread.Join(2200); }
    }
}
