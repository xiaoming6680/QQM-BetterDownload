using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace QqmBetterDownload {
    internal static class InAppTests {
        [DllImport("user32")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        [DllImport("user32")] static extern IntPtr GetForegroundWindow();
        static void Pump(int ms) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
        static void Until(Func<bool> ready) { var watch = Stopwatch.StartNew(); while (!ready()) { if (watch.ElapsedMilliseconds > 20000) throw new Exception("HTML integration timed out."); Pump(20); } }
        static string Script(AppWindow page, string script) { var work = page.EvaluateHtml(script); Until(() => work.IsCompleted); return work.GetAwaiter().GetResult(); }
        static bool ChildInside(IntPtr child, IntPtr parent) {
            ClientUi.Rect a, b; ClientUi.GetWindowRect(child, out a); ClientUi.GetClientRect(parent, out b);
            var origin = new ClientUi.Point(); ClientUi.ClientToScreen(parent, ref origin);
            return a.Width > 50 && a.Height > 20 && a.Left >= origin.X && a.Top >= origin.Y && a.Right <= origin.X + b.Width && a.Bottom <= origin.Y + b.Height;
        }
        internal static void Run(Action<bool, string> check, Process host, IntPtr window, string folder) {
            IntPtr foreground = GetForegroundWindow();
            SendMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero);
            check(NativeBridge.GetProp(window, "BetterDownload.IconError") == IntPtr.Zero, "native entry must render with per-pixel transparency");
            using (var card = new InAppCard(window, path => { })) {
                card.Configure("all", false, 2000);
                var progress = new WorkStatus { Id = "child-card", State = "converting", Percent = 64, Source = "示例歌曲.mflac", Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" } };
                card.Receive(progress); Pump(550);
                check(card.LastError == null && card.IsPresented, "layered child card rendered: " + card.LastError);
                check(ClientUi.GetParent(card.Handle) == window && (ClientUi.GetWindowLong(card.Handle, -16) & ClientUi.Child) != 0, "card must be a real foreign client child");
                check((ClientUi.GetWindowLong(card.Handle, -20) & 8) == 0 && ChildInside(card.Handle, window), "card is clipped to client and never globally topmost");
                check(GetForegroundWindow() == foreground, "embedded notification stole foreground");
                SendMessage(card.Handle, 0x200, IntPtr.Zero, IntPtr.Zero); Pump(2200);
                check(card.IsPresented, "embedded hover must keep card open");
                SendMessage(card.Handle, 0x2a3, IntPtr.Zero, IntPtr.Zero); Pump(2500);
                check(!card.IsPresented, "embedded card must slide out after hover leaves");
                card.Receive(progress); Pump(50); check(!card.IsPresented, "progress must not resurrect a dismissed embedded card");
                var complete = new WorkStatus { Id = "child-card", State = "success", Percent = 100, Source = progress.Source, Output = Path.Combine(folder, "song.flac") };
                card.Receive(complete); Pump(500); check(card.IsPresented, "completion should slide in once");
                SendMessage(window, 0x8009, IntPtr.Zero, IntPtr.Zero); Pump(100);
                check(!card.IsPresented, "hiding client must hide child card");
                SendMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero); Pump(200);
                check(card.IsPresented, "restoring client restores an active child card");
                SendMessage(window, 0x8008, (IntPtr)1250, (IntPtr)800); Pump(200);
                check(ChildInside(card.Handle, window), "resized client must retain card bounds");
                card.Dismiss(); Pump(900); check(!card.IsPresented, "explicit dismissal must finish slide out");
                card.Receive(complete); Pump(100); check(!card.IsPresented, "duplicate completion must stay dismissed");
            }
            // Preview mode prevents the integration test from reading user state
            // or creating a conversion monitor. All HWNDs belong to our test host.
            using (new ClientUi.DpiScope(window))
            using (var parent = Process.GetProcessById(host.Id)) using (var page = new AppWindow(true, parent, @"D:\Apps\QQMusic")) {
                page.Restore(); Until(() => page.HtmlReady || page.HtmlError.Length > 0);
                check(page.HtmlReady, "embedded HTML must initialize: " + page.HtmlError);
                page.PreparePreview();
                Pump(900);
                check(ClientUi.GetParent(page.Handle) == window && (ClientUi.GetWindowLong(page.Handle, -16) & ClientUi.Child) != 0, "settings must be embedded in the client");
                check(!page.TopLevel && !page.ShowInTaskbar && (ClientUi.GetWindowLong(page.Handle, -20) & 8) == 0, "settings must not become another application window");
                check(ChildInside(page.Handle, window), "settings must fit in client content area");
                check(page.Controls[0].Width <= page.ClientSize.Width && page.Controls[0].Height <= page.ClientSize.Height, "settings controls must respect parent DPI without growing beyond the panel");
                check(Script(page, "document.body.dataset.ready === 'true' && document.querySelectorAll('input').length === 0") == "true", "HTML renders without manual path inputs");
                check(Script(page, "document.documentElement.scrollWidth <= innerWidth") == "true", "HTML must not overflow horizontally");
                Script(page, "document.querySelector('[data-toggle]').click()"); Pump(200);
                check(Script(page, "document.querySelector('[data-toggle]').getAttribute('aria-checked') === 'false' && document.querySelector('[data-scan]').disabled") == "true", "HTML toggle must update the native settings and disabled state");
                Script(page, "document.querySelector('[data-card-style] [data-value=compact]').click()"); Pump(200);
                check(Script(page, "document.querySelector('[data-card-style] [data-value=compact]').getAttribute('aria-checked') === 'true'") == "true", "HTML segmented choice must round-trip through the backend");
                Script(page, "document.querySelector('[data-toggle]').click(); document.querySelector('[data-card-style] [data-value=standard]').click()"); Pump(200);
                Script(page, "document.querySelector('[data-scan]').click()"); Pump(250);
                check(Script(page, "document.querySelector('[data-scan-note]').textContent.includes('预览模式')") == "true", "scan action must show its result in the existing scan row");
                Script(page, "document.querySelector('[data-preview]').click()"); Pump(650);
                check(page.CardPresented && ClientUi.IsWindowVisible(page.Handle), "HTML preview must show the embedded card while settings remain open");
                var capture = page.CaptureHtml(Path.Combine(folder, "embedded-settings.png")); Until(() => capture.IsCompleted); capture.GetAwaiter().GetResult();
                check(new FileInfo(Path.Combine(folder, "embedded-settings.png")).Length > 15000, "real HTML capture must render settings content");
                Script(page, "document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape'}))"); Pump(100);
                check(!ClientUi.IsWindowVisible(page.Handle), "Escape must return to the client");
                page.Hide(); Pump(50); check(!ClientUi.IsWindowVisible(page.Handle), "returning to QQ must hide settings only");
                page.Restore(); Pump(100); check(ClientUi.IsWindowVisible(page.Handle), "settings must reopen inside the same client");
                // The user can change foreground during this long integration
                // run. The child/taskbar checks above establish window ownership
                // without assuming the desktop remains idle throughout the test.
                SendMessage(window, 0x8008, (IntPtr)1000, (IntPtr)650); Pump(200);
                check(ChildInside(page.Handle, window), "settings must resize inside the client");
                check(Script(page, "document.documentElement.scrollWidth <= innerWidth") == "true", "responsive HTML must fit the narrower client");
                page.Close();
            }
        }
    }
}
