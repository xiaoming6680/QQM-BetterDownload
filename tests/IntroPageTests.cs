using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace QqmBetterDownload {
    // The first-run notice on the loopback page that QQ's own browser loads; the
    // fallback window hosts the same page. It covers the settings until the
    // agreement reaches the controller, and status updates never disturb it.
    internal static class IntroPageTests {
        static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
        static void Until(Func<bool> ready, int ms, Func<string> step) { var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.ElapsedMilliseconds > ms) throw new Exception("Intro page timed out: " + step()); Pump(20); } }
        static string Script(FallbackWindow window, string script) { var work = window.Evaluate(script); Until(() => work.IsCompleted, 10000, () => "script"); return work.GetAwaiter().GetResult(); }
        internal static void Run(Action<bool,string> check, string folder) {
            string html;
            using (var input = typeof(Program).Assembly.GetManifestResourceStream("settings.html")) using (var reader = new StreamReader(input)) html = reader.ReadToEnd();
            int ready = 0, agreed = 0, declined = 0; var sent = new System.Collections.Generic.List<string>();
            const string state = "{\"version\":\"test\",\"visible\":true,\"openSequence\":1,\"enabled\":true,\"notify\":\"all\",\"style\":\"standard\",\"stay\":4000,\"state\":\"watching\",\"history\":[],\"intro\":true}";
            using (var server = new LocalPageServer(html, delegate(string name, string value) { lock (sent) sent.Add(name); if (name == "ready") Interlocked.Increment(ref ready); if (name == "intro-accept") Interlocked.Increment(ref agreed); if (name == "uninstall") Interlocked.Increment(ref declined); }))
            using (var icon = BrandIcon.Load())
            using (var window = new FallbackWindow(server.Url, Path.Combine(folder, "intro-webview"), icon, 1)) {
                window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-10000, -10000); window.ShowInTaskbar = false;
                server.Publish(state); window.Present();
                Until(() => (Volatile.Read(ref ready) > 0 && window.PageLoaded) || window.Problem.Length > 0, 20000, () => "page load " + window.Problem);
                Until(() => Script(window, "document.body.getAttribute('data-view')") == "\"intro\"", 10000, () => "notice");
                check(Script(window, "document.body.className==='native' && document.querySelector('.nbd-panel').offsetParent===null && document.querySelector('[data-intro-agree]').offsetParent!==null") == "true", "the loopback page opens on the notice, not the settings");
                Script(window, "window.scrollTo(0,300);1");
                server.Publish(state.Replace("watching", "converting")); Pump(500);
                check(Script(window, "document.body.getAttribute('data-view')==='intro' && window.scrollY>0") == "true", "status updates keep the notice where the reader is");
                // Declining asks the installer to confirm an uninstall; if it cannot
                // open, the reason shows on the notice, where the reader still is.
                Script(window, "document.querySelector('[data-intro-decline]').click();1");
                Until(() => Volatile.Read(ref declined) == 1, 5000, () => "decline");
                server.Publish(state.Replace("\"history\":[]", "\"history\":[],\"error\":\"找不到卸载程序\""));
                Until(() => Script(window, "document.querySelector('[data-intro-error]').textContent") == "\"找不到卸载程序\"", 5000, () => "decline error");
                check(Script(window, "document.body.getAttribute('data-view')") == "\"intro\"" && Volatile.Read(ref agreed) == 0, "declining keeps the notice and records no agreement");
                Script(window, "document.querySelector('[data-intro-agree]').click();1");
                Until(() => Volatile.Read(ref agreed) == 1, 5000, () => "agreement");
                Until(() => Script(window, "document.body.getAttribute('data-view')") == "\"settings\"", 5000, () => "settings");
                check(Script(window, "window.scrollY===0 && document.querySelector('.nbd-panel').offsetParent!==null") == "true", "agreeing opens the settings at the top");
                // The controller has not published yet: an update still marked
                // intro must not bring the agreed notice back.
                server.Publish(state.Replace("watching", "scanning")); Pump(500);
                check(Script(window, "document.body.getAttribute('data-view')") == "\"settings\"", "a state published before the agreement cannot reopen the notice");
                check(Volatile.Read(ref agreed) == 1, "the agreement is sent once");
                // Every settings control must be an action this server accepts; a
                // rejected one only shows "连接暂时中断" inside QQ Music.
                server.Publish(state.Replace("\"intro\":true", "\"intro\":false,\"lyrics\":true,\"lyricsFile\":false")); Pump(500);
                lock (sent) sent.Clear();
                // One at a time, as a person clicks: the server keeps at most eight connections.
                foreach (string[] control in new[] { new[] { "[data-toggle]", "toggle" }, new[] { "[data-scan]", "scan" }, new[] { "[data-preview]", "preview" }, new[] { "[data-switch=lyrics]", "lyrics" }, new[] { "[data-switch=lyrics-file]", "lyrics-file" },
                        new[] { "[data-notify] [data-value=errors]", "notify" }, new[] { "[data-card-style] [data-value=compact]", "style" }, new[] { "[data-card-stay] [data-value=\"6000\"]", "stay" } }) {
                    Script(window, "document.querySelector('" + control[0] + "').click();1");
                    Until(() => { lock (sent) return sent.Contains(control[1]); }, 5000, () => "control " + control[1]);
                }
                check(Script(window, "document.querySelector('[data-detail]').textContent") == "\"\"", "every settings control is accepted by the loopback server");
                window.Shutdown(); Pump(100);
            }
        }
    }
}
