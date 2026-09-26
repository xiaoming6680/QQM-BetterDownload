using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace QqmBetterDownload {
    internal static class NativeBridge {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] internal static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32")] internal static extern bool FreeLibrary(IntPtr module);
        [DllImport("user32", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, IntPtr callback, IntPtr module, uint thread);
        [DllImport("user32")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32", SetLastError = true)] internal static extern bool PostMessage(IntPtr window, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("kernel32", SetLastError = true)] static extern bool IsWow64Process(IntPtr process, out bool wow64);
        internal static bool IsX86(Process p) { bool wow64; return !Environment.Is64BitOperatingSystem || (IsWow64Process(p.Handle, out wow64) && wow64); }
        internal static readonly uint AttachMessage = RegisterWindowMessage("BetterDownload.Attach.v1"), DetachMessage = RegisterWindowMessage("BetterDownload.Detach.v1");
        internal static IntPtr Attach(IntPtr module, IntPtr window, bool icon) {
            uint pid; uint thread = GetWindowThreadProcessId(window, out pid);
            if (thread == 0) throw new IOException("QQ 音乐窗口已关闭。");
            IntPtr callback = GetProcAddress(module, "BetterDownloadHook");
            if (callback == IntPtr.Zero) throw new IOException("接入组件不完整。");
            IntPtr hook = SetWindowsHookEx(3, callback, module, thread);
            if (hook == IntPtr.Zero) throw new IOException("暂未连接 QQ 音乐，系统错误 " + Marshal.GetLastWin32Error());
            if (!PostMessage(window, AttachMessage, IntPtr.Zero, icon ? (IntPtr)1 : IntPtr.Zero)) { UnhookWindowsHookEx(hook); throw new IOException("暂不能向 QQ 音乐发送接入消息。"); }
            return hook;
        }
    }
    // One small per-user agent survives client shutdown and watches for a new
    // installation path. It never writes into the client's install directory.
    internal sealed class Agent : ApplicationContext {
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\QQM-BetterDownload.AgentStop");
        readonly EventWaitHandle settings = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\QQM-BetterDownload.Settings");
        readonly IntPtr library;
        IntPtr hook, window;
        Process client, worker;
        DateTime nextWorker, lastAttach;
        int failures;
        string lastState;
        public Agent() {
            stop.Reset();
            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BetterDownloadBridge.dll"); SafePath.NoLinks(dll);
            library = NativeBridge.LoadLibraryEx(dll, IntPtr.Zero, 0x1100);
            if (library == IntPtr.Zero) throw new IOException("无法加载 BetterDownload 接入组件。");
            timer.Tick += delegate { Tick(); }; timer.Start(); Tick();
        }
        void State(string state, string message) {
            string key = state + message + window + (worker == null ? 0 : worker.Id);
            if (lastState == key) return; lastState = key;
            try { uint pid; uint thread = NativeBridge.GetWindowThreadProcessId(window, out pid); StateFile.Write(Path.Combine(Program.DataFolder, "agent-status.json"), new { version = Program.Version, state = state, message = message, at = DateTime.UtcNow.ToString("o"), clientPid = client == null ? 0 : client.Id, window = window.ToInt64(), thread = thread, workerPid = worker == null ? 0 : worker.Id }); } catch { }
        }
        void Tick() {
            if (stop.WaitOne(0)) { ExitThread(); return; }
            try {
                if (client != null && client.HasExited) Disconnect();
                if (client == null) {
                    foreach (var process in Process.GetProcessesByName("QQMusic")) {
                        bool keep = false;
                        try {
                            if (process.SessionId != Process.GetCurrentProcess().SessionId || ClientUi.MainWindow(process, IntPtr.Zero) == IntPtr.Zero) continue;
                            if (!NativeBridge.IsX86(process)) { State("incompatible", "这个架构的 QQ 音乐尚未适配；下载文件会保留。"); continue; }
                            string exe = process.MainModule.FileName;
                            if (!Path.GetFileName(exe).Equals("QQMusic.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            client = process; keep = true; window = ClientUi.MainWindow(process, IntPtr.Zero);
                            // The worker checks exact GF fingerprints before
                            // creating native controls. The bridge can still
                            // observe downloads when a new UI needs adaptation.
                            var version = FileVersionInfo.GetVersionInfo(exe);
                            hook = NativeBridge.Attach(library, window, version.FileMajorPart == 22); lastAttach = DateTime.UtcNow;
                            nextWorker = DateTime.MinValue; failures = 0; break;
                        } finally { if (!keep) process.Dispose(); }
                    }
                    if (client == null) {
                        State("waiting", "等待 QQ 音乐启动。安装和自动启动已保留。");
                        string manifest = Path.Combine(Program.DataFolder, "installation.json"), setup = Path.Combine(Program.DataFolder, "BetterDownload-Setup.exe");
                        if (File.Exists(manifest) && File.Exists(setup)) {
                            var deployment = StateFile.Read<System.Collections.Generic.Dictionary<string,object>>(manifest); object pending;
                            if (deployment.TryGetValue("pending", out pending) && pending is string && ((string)pending).Length > 0) {
                                Process.Start(new ProcessStartInfo(setup, "--activate-pending") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }).Dispose(); ExitThread();
                            }
                        }
                        return;
                    }
                }
                IntPtr currentWindow = ClientUi.MainWindow(client, window);
                if (currentWindow != window || (DateTime.UtcNow - lastAttach).TotalSeconds >= 10 && NativeBridge.GetProp(window, "BetterDownload.Bridge") == IntPtr.Zero) {
                    if (currentWindow != window) Program.Signal(WorkerSession.Name(client, "Stop"));
                    if (window != IntPtr.Zero) NativeBridge.PostMessage(window, NativeBridge.DetachMessage, IntPtr.Zero, IntPtr.Zero);
                    if (hook != IntPtr.Zero) NativeBridge.UnhookWindowsHookEx(hook);
                    hook = IntPtr.Zero; window = currentWindow; lastState = null;
                    if (window == IntPtr.Zero) { State("waiting-window", "等待 QQ 音乐主窗口打开。"); return; }
                    hook = NativeBridge.Attach(library, window, FileVersionInfo.GetVersionInfo(client.MainModule.FileName).FileMajorPart == 22); lastAttach = DateTime.UtcNow;
                }
                if (window == IntPtr.Zero) { State("waiting-window", "等待 QQ 音乐主窗口打开。"); return; }
                if (NativeBridge.GetProp(window, "BetterDownload.Bridge") == IntPtr.Zero) {
                    // Also retries after the UI thread was busy during startup.
                    NativeBridge.PostMessage(window, NativeBridge.AttachMessage, IntPtr.Zero, FileVersionInfo.GetVersionInfo(client.MainModule.FileName).FileMajorPart == 22 ? (IntPtr)1 : IntPtr.Zero);
                    State("connecting", "正在连接 QQ 音乐…");
                } else {
                    int imports = NativeBridge.GetProp(window, "BetterDownload.Imports").ToInt32();
                    bool nativeReady = NativeBridge.GetProp(window,"BetterDownload.NativeUi") != IntPtr.Zero;
                    State(imports > 0 && nativeReady ? "connected" : "partial", imports > 0 ? nativeReady ? "已连接 QQ 音乐原生界面，自动接收下载完成事件。" : "下载事件已接入；原生界面暂未就绪，可在托盘查看适配情况。" : "这个版本的下载事件接口尚未识别。");
                    if (!WorkerSession.Alive(client)) State("starting", "入口已连接，正在启动自动转换…");
                    if (NativeBridge.GetProp(window, "BetterDownload.QueueOverflow") != IntPtr.Zero) State("overflow", "下载事件队列已满，请在设置中处理已有下载。");
                }
                if (worker != null && worker.HasExited) { worker.Dispose(); worker = null; failures++; nextWorker = DateTime.UtcNow.AddSeconds(Math.Min(60, failures * 5)); }
                if (worker == null && DateTime.UtcNow >= nextWorker && !WorkerSession.Alive(client)) {
                    string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BetterDownload.exe");
                    string path = Path.GetDirectoryName(client.MainModule.FileName);
                    worker = Process.Start(new ProcessStartInfo(exe, "--plugin --parent " + client.Id + " --window " + window.ToInt64() + " --client " + Quote(path)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                }
            } catch (Exception e) { State("retrying", e.Message); if (client != null && hook == IntPtr.Zero) Disconnect(); }
        }
        internal static string Quote(string value) { if (value.IndexOf('"') >= 0) throw new ArgumentException("路径中含有无效引号。"); return "\"" + value.TrimEnd('\\') + "\""; }
        void Disconnect() {
            if (client != null) { try { Program.Signal(WorkerSession.Name(client, "Stop")); } catch (InvalidOperationException) { } }
            if (window != IntPtr.Zero) NativeBridge.PostMessage(window, NativeBridge.DetachMessage, IntPtr.Zero, IntPtr.Zero);
            if (hook != IntPtr.Zero) NativeBridge.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero; window = IntPtr.Zero;
            if (client != null) { client.Dispose(); client = null; }
            if (worker != null && worker.HasExited) { worker.Dispose(); worker = null; }
        }
        protected override void ExitThreadCore() {
            timer.Stop(); timer.Dispose(); Disconnect();
            Program.Signal("Local\\QQM-BetterDownload.WorkerStop"); State("stopped", "自动接入已停止。");
            if (worker != null) { worker.Dispose(); worker = null; }
            NativeBridge.FreeLibrary(library); stop.Dispose(); settings.Dispose(); base.ExitThreadCore();
        }
        internal static int Run() {
            using (var mutex = new Mutex(false, "Local\\QQM-BetterDownload.Agent")) {
                bool locked; try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) return 0;
                try { Application.Run(new Agent()); } finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
