using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace QqmBetterDownload {
    internal static class LifecycleTests {
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        static void Until(Func<bool> ready) { var watch = Stopwatch.StartNew(); while (!ready()) { if (watch.ElapsedMilliseconds > 15000) throw new Exception("Client lifecycle timed out."); System.Windows.Forms.Application.DoEvents(); Thread.Sleep(20); } }
        internal static void Run(Action<bool, string> check, string folder) {
            using (var logo = BrandIcon.Raster(32)) {
                int opaque = 0; for (int y = 0; y < logo.Height; y++) for (int x = 0; x < logo.Width; x++) if (logo.GetPixel(x, y).A > 128) opaque++;
                logo.Save(Path.Combine(folder, "brand-logo.png"), System.Drawing.Imaging.ImageFormat.Png);
                check(opaque > 32 * 32 / 2 && logo.GetPixel(0, 0).A == 0, "tray/entry logo must keep its visible pixels and transparent corners; opaque=" + opaque + "; corner alpha=" + logo.GetPixel(0,0).A);
            }
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            using (var host = Process.Start(new ProcessStartInfo(Path.Combine(dir, "BridgeHost.exe"), Agent.Quote(folder) + " --lifecycle") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })) {
                IntPtr window = IntPtr.Zero, hook = IntPtr.Zero;
                IntPtr library = NativeBridge.LoadLibraryEx(Path.Combine(dir, "TestBridge.dll"), IntPtr.Zero, 0x1100);
                try {
                    Until(() => { host.Refresh(); return host.MainWindowHandle != IntPtr.Zero && (window = FindWindow("BetterDownload.TestHost", "BetterDownload Test Host")) != IntPtr.Zero; });
                    IntPtr cached = host.MainWindowHandle;
                    check(cached != window && ClientUi.MainWindow(host, IntPtr.Zero) == IntPtr.Zero, "startup helper must not be mistaken for the music window");
                    SendMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero);
                    check(host.MainWindowHandle == cached && ClientUi.MainWindow(host, IntPtr.Zero) == window, "client discovery must bypass Process.MainWindowHandle's cached auxiliary window");
                    hook = NativeBridge.Attach(library, ClientUi.MainWindow(host, IntPtr.Zero), true);
                    Until(() => NativeBridge.GetProp(window, "BetterDownload.Bridge") != IntPtr.Zero);
                    check(NativeBridge.GetProp(cached, "BetterDownload.Bridge") == IntPtr.Zero, "only the actual client window receives the entry");
                    SendMessage(window, 0x8009, IntPtr.Zero, IntPtr.Zero);
                    check(ClientUi.MainWindow(host, window) == window, "minimizing to tray must retain the known client window");
                    SendMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(window, 0x800b, IntPtr.Zero, IntPtr.Zero);
                    check(ClientUi.IsIconic(window) && ClientUi.MainWindow(host, window) == window, "ordinary minimization must retain the client even with an empty client rectangle");
                    SendMessage(window, 0x8007, IntPtr.Zero, IntPtr.Zero);
                    IntPtr old = window;
                    int requested = 0, forced = 0;
                    using (var lifetime = new WorkerLifetime(() => !host.HasExited && ClientUi.IsClientWindow(old, host.Id), () => Interlocked.Increment(ref requested), () => Interlocked.Increment(ref forced), 250)) {
                        window = SendMessage(old, 0x800a, IntPtr.Zero, IntPtr.Zero);
                        check(window != IntPtr.Zero && window != old && ClientUi.MainWindow(host, old) == window, "recreated main window must replace the stale HWND");
                        Until(() => Volatile.Read(ref forced) == 1);
                        check(requested == 1 && forced == 1, "a blocked UI must not leave a worker alive after its parent window disappears");
                    }
                    NativeBridge.UnhookWindowsHookEx(hook); hook = NativeBridge.Attach(library, window, true);
                    Until(() => NativeBridge.GetProp(window, "BetterDownload.Bridge") != IntPtr.Zero);
                    using (var self = Process.GetCurrentProcess())
                    using (var stale = new EventWaitHandle(true, EventResetMode.ManualReset, WorkerSession.Name(self, "Alive"))) {
                        check(!WorkerSession.Alive(host), "another client session's worker must not suppress startup");
                        using (var current = new EventWaitHandle(true, EventResetMode.ManualReset, WorkerSession.Name(host, "Alive")))
                            check(WorkerSession.Alive(host), "ready worker must be detected in the matching session");
                        check(!WorkerSession.Alive(host), "exited worker must release session readiness");
                    }
                    requested = forced = 0;
                    using (var lifetime = new WorkerLifetime(() => false, () => Interlocked.Increment(ref requested), () => Interlocked.Increment(ref forced), 1000)) {
                        Until(() => Volatile.Read(ref requested) == 1);
                    }
                    check(requested == 1 && forced == 0, "normal cleanup must cancel the termination fallback");
                } finally {
                    if (hook != IntPtr.Zero) { NativeBridge.PostMessage(window, NativeBridge.DetachMessage, IntPtr.Zero, IntPtr.Zero); NativeBridge.UnhookWindowsHookEx(hook); }
                    if (window != IntPtr.Zero) NativeBridge.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (!host.WaitForExit(4000)) throw new Exception("Own lifecycle test host did not exit.");
                    if (library != IntPtr.Zero) NativeBridge.FreeLibrary(library);
                }
            }
        }
    }
}
