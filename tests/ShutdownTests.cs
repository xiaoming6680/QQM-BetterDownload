using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace QqmBetterDownload {
    internal static class ShutdownTests {
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
        static void Until(Func<bool> ready) { var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.ElapsedMilliseconds > 20000) throw new Exception("Shutdown integration timed out."); Pump(20); } }
        internal static void Run(Action<bool, string> check, string folder) {
            var errors = new List<Exception>();
            ThreadExceptionEventHandler onError = (sender, e) => errors.Add(e.Exception);
            Application.ThreadException += onError;
            try {
                foreach (string phase in new[] { "initializing", "loaded", "controller-closed" }) {
                    using (var host = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BridgeHost.exe"), Agent.Quote(folder)) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })) {
                        IntPtr window = IntPtr.Zero;
                        Until(() => (window = FindWindow("BetterDownload.TestHost", "BetterDownload Test Host")) != IntPtr.Zero);
                        PostMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero);
                        Until(() => { host.Refresh(); return host.MainWindowHandle != IntPtr.Zero; });
                        try {
                            using (new ClientUi.DpiScope(window))
                            using (var parent = Process.GetProcessById(host.Id))
                            using (var page = new AppWindow(true, parent, @"D:\Apps\QQMusic")) {
                                page.Restore();
                                if (phase != "initializing") {
                                    Until(() => page.HtmlReady || page.HtmlError.Length > 0);
                                    check(page.HtmlReady, "shutdown fixture must load HTML");
                                    page.PreviewCard();
                                    page.BeginInvoke((Action)page.PreparePreview);
                                    if (phase == "controller-closed") {
                                        // Fault injection into our own controller:
                                        // native browser closes before its HWND owner.
                                        var controllerField = typeof(HtmlHost).GetField("controller", BindingFlags.Instance | BindingFlags.NonPublic);
                                        check(controllerField != null, "owned controller must be available for shutdown fault injection");
                                        ((CoreWebView2Controller)controllerField.GetValue(page.Controls.OfType<HtmlHost>().Single())).Close();
                                        page.PreparePreview();
                                    }
                                }
                                PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                                Until(() => host.HasExited);
                                Pump(1200);
                                check(errors.Count == 0, "client exit must not access disposed WebView2: " + (errors.Count == 0 ? "" : errors[0].ToString()));
                                check(page.IsDisposed, "client exit must dispose its embedded settings, phase=" + phase);
                                page.PreparePreview(); page.PreviewCard();
                                check(!page.CardPresented, "late updates must not reopen disposed UI, phase=" + phase);
                            }
                        } finally {
                            if (!host.HasExited) { PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); host.WaitForExit(4000); }
                        }
                    }
                }
            } finally { Application.ThreadException -= onError; }
        }
    }
}
