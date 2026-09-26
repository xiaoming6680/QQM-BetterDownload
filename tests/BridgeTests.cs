using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
namespace QqmBetterDownload {
    internal static class BridgeTests {
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string text);
        [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        static void Until(Func<bool> ready) { var time = Stopwatch.StartNew(); while (!ready()) { if (time.ElapsedMilliseconds > 7000) throw new Exception("Native bridge timed out."); Thread.Sleep(50); } }
        public static void Run(Action<bool,string> check, string folder) {
            Directory.CreateDirectory(Path.Combine(folder, "VipSongsDownload"));
            string source = Path.Combine(folder, "source.dat"); File.WriteAllText(source, "original synthetic bytes");
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            IntPtr library = NativeBridge.LoadLibraryEx(Path.Combine(dir, "TestBridge.dll"), IntPtr.Zero, 0x1100), hook = IntPtr.Zero;
            check(library != IntPtr.Zero, "test bridge load");
            using (var host = Process.Start(new ProcessStartInfo(Path.Combine(dir, "BridgeHost.exe"), Agent.Quote(folder)) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })) {
                IntPtr window = IntPtr.Zero; string spool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQM-BetterDownload-Test", "events", host.Id.ToString());
                try {
                    Until(() => (window = FindWindow("BetterDownload.TestHost", "BetterDownload Test Host")) != IntPtr.Zero);
                    hook = NativeBridge.Attach(library, window, true);
                    Until(() => NativeBridge.GetProp(window, "BetterDownload.Imports").ToInt32() >= 4);
                    check(NativeBridge.GetProp(window, "BetterDownload.Bridge") != IntPtr.Zero, "thread hook initialized download bridge");
                    for (int i = 1; i <= 4; i++) {
                        if (!File.Exists(source)) File.WriteAllText(source, "original synthetic bytes");
                        SendMessage(window, (uint)(0x8000 + i), IntPtr.Zero, IntPtr.Zero);
                        check(File.ReadAllText(Path.Combine(folder, "VipSongsDownload", "test" + i + ".mflac")) == "original synthetic bytes", "file operation preserves bytes " + i);
                    }
                    Until(() => Directory.Exists(spool) && Directory.GetFiles(spool, "*.evt").Length == 4);
                    var paths = Directory.GetFiles(spool, "*.evt").Select(f => File.ReadAllText(f, System.Text.Encoding.Unicode)).ToArray();
                    check(paths.Distinct().Count() == 4 && paths.All(p => p.StartsWith(folder + "\\VipSongsDownload\\")), "exact final paths delivered once");
                    SendMessage(window, 0x8005, IntPtr.Zero, IntPtr.Zero);
                    check(NativeBridge.GetProp(window, "Test.Error").ToInt32() == 2, "original GetLastError survives hook");
                    File.WriteAllText(source, "original synthetic bytes"); SendMessage(window, 0x8006, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(1200); check(Directory.GetFiles(spool, "*.evt").Length == 4, "failed and unrelated copies ignored");
                    NativeBridge.PostMessage(window, NativeBridge.DetachMessage, IntPtr.Zero, IntPtr.Zero);
                    Until(() => NativeBridge.GetProp(window, "BetterDownload.Bridge") == IntPtr.Zero);
                    NativeBridge.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; Thread.Sleep(1200);
                    File.Delete(Path.Combine(folder, "VipSongsDownload", "test1.mflac"));
                    SendMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero); Thread.Sleep(1200);
                    check(File.Exists(Path.Combine(folder, "VipSongsDownload", "test1.mflac")) && Directory.GetFiles(spool, "*.evt").Length == 4, "detach restores original operation without an event");
                } finally {
                    if (hook != IntPtr.Zero) NativeBridge.UnhookWindowsHookEx(hook);
                    if (window != IntPtr.Zero) NativeBridge.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (!host.WaitForExit(4000)) throw new Exception("Own native test host did not exit.");
                    NativeBridge.FreeLibrary(library);
                    if (Directory.Exists(spool)) { foreach (var file in Directory.GetFiles(spool)) File.Delete(file); Directory.Delete(spool); }
                }
            }
        }
    }
}
