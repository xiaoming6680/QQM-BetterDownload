using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace QqmBetterDownload {
    internal static class LocalPageTests {
        static string Request(Uri uri, string method, string path, string body = null, string host = null, string origin = null, string type = "application/json") {
            using (var client = new TcpClient()) {
                client.Connect(uri.Host, uri.Port); client.ReceiveTimeout = 5000;
                using (var stream = client.GetStream()) {
                    string header = method + " " + path + " HTTP/1.1\r\nHost: " + (host ?? uri.Authority) + "\r\n";
                    if (origin != null) header += "Origin: " + origin + "\r\n";
                    if (body != null) header += "Content-Type: " + type + "\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(header + "\r\n" + (body ?? ""));stream.Write(bytes,0,bytes.Length);
                    var reply = new StringBuilder();int size = 0;
                    while (!reply.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)) { int next = stream.ReadByte();if(next<0)throw new IOException("Incomplete HTTP response");reply.Append((char)next); }
                    foreach(string line in reply.ToString().Split(new[]{"\r\n"},StringSplitOptions.None)) if(line.StartsWith("Content-Length: ",StringComparison.OrdinalIgnoreCase))size=Int32.Parse(line.Substring(16));
                    byte[] content = new byte[size];int read = 0;
                    while(read<size){int n=stream.Read(content,read,size-read);if(n==0)throw new IOException("Truncated HTTP body");read+=n;}
                    return reply.Append(Encoding.UTF8.GetString(content)).ToString();
                }
            }
        }
        internal static void Run(Action<bool,string> check) {
            int calls = 0; string last = "";
            using (var server = new LocalPageServer("<!doctype html><title>Test</title>", delegate(string action,string value) { last = action + ":" + value; Interlocked.Increment(ref calls); })) {
                var uri = new Uri(server.Url); string prefix = uri.AbsolutePath.Substring(0, uri.AbsolutePath.LastIndexOf('/') + 1);
                server.Publish("{\"version\":\"test\",\"message\":\"已就绪\"}");
                string page = Request(uri,"GET",uri.AbsolutePath);
                check(page.Contains("200 OK") && page.Contains("<title>Test</title>") && page.Contains("connect-src 'self'"),"local page and restricted CSP must be served");
                string first = Request(uri,"GET",prefix+"state");
                check(first.Contains("已就绪"),"state must round-trip UTF-8");
                int at = first.IndexOf("X-State-Revision: ", StringComparison.Ordinal), end = at < 0 ? -1 : first.IndexOf("\r\n", at, StringComparison.Ordinal);
                string revision = at < 0 || end < 0 ? "" : first.Substring(at + 18, end - at - 18);
                check(revision.Length > 0,"state must carry its revision");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var waiting = System.Threading.Tasks.Task.Run(() => Request(uri,"GET",prefix+"state?after="+revision));
                check(!waiting.Wait(300),"long poll must hold while the state is unchanged");
                server.Publish("{\"version\":\"test\",\"message\":\"已打开\"}");
                check(waiting.Wait(2000) && waiting.Result.Contains("已打开") && watch.ElapsedMilliseconds < 2500,"long poll must answer as soon as the state changes");
                check(Request(uri,"GET",prefix+"state?after=0").Contains("已打开"),"stale revision must answer immediately");
                check(Request(uri,"GET",prefix+"state?after=-1").Contains("400 Bad Request"),"malformed revision must be rejected");
                check(Request(uri,"POST",prefix+"action","{\"action\":\"style\",\"value\":\"compact\"}",null,"http://"+uri.Authority).Contains("202 Accepted") && calls==1 && last=="style:compact","page action must reach the controller");
                check(Request(uri,"GET","/state").Contains("403 Forbidden"),"missing capability must be rejected");
                check(Request(uri,"GET",prefix+"state",null,"untrusted.example").Contains("403 Forbidden"),"DNS rebinding Host must be rejected");
                check(Request(uri,"POST",prefix+"action","{\"action\":\"scan\"}",null,"https://untrusted.example").Contains("403 Forbidden"),"cross-origin action must be rejected");
                check(Request(uri,"POST",prefix+"action","{\"action\":\"scan\"}",null,null,"text/plain").Contains("400 Bad Request"),"simple cross-site form body must be rejected");
                check(Request(uri,"POST",prefix+"action","{\"action\":\"exec\",\"value\":\"ignored\"}").Contains("400 Bad Request"),"unknown commands must be rejected");
                check(Request(uri,"POST",prefix+"action","{\"action\":\"style\",\"value\":{}}").Contains("400 Bad Request"),"non-string values must be rejected");
                check(Request(uri,"POST",prefix+"action","{").Contains("400 Bad Request"),"malformed JSON must be rejected");
                check(Request(uri,"POST",prefix+"action",new string('x',4097)).Contains("400 Bad Request"),"oversized commands must be rejected");
                check(Request(uri,"GET",prefix+"../settings.json").Contains("404 Not Found"),"server must expose no local file route");
                check(calls==1,"rejected requests must never execute actions");
                check(!NativeUiCompatibility.Supported(Path.GetTempPath()),"unknown GF binaries must disable the private ABI");
            }
        }
    }
}
