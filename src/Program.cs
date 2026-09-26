using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Diagnostics;

namespace QqmBetterDownload {
    public static class Program {
        public const string Version = "0.1.1";
        public static string DataFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQM-BetterDownload"); } }
        public static string DefaultRoot() {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders")) {
                if (key != null) downloads = Environment.ExpandEnvironmentVariables((string)key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}", downloads));
            }
            string vip = Path.Combine(downloads, "VipSongsDownload"); return Directory.Exists(vip) ? vip : downloads;
        }
        static string Option(string[] args, string name, string fallback) {
            int i = Array.IndexOf(args, name); if (i < 0) return fallback;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException(name + " 缺少参数。");
            return args[i + 1];
        }
        static void Print(object value) { Console.WriteLine(new JavaScriptSerializer().Serialize(value)); }
        internal static bool Signal(string name) { try { using (var e = EventWaitHandle.OpenExisting(name)) { e.Set(); return true; } } catch (WaitHandleCannotBeOpenedException) { return false; } }
        static void OpenSettings() {
            string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BetterDownload.exe");
            Process.Start(new ProcessStartInfo(app, "--agent") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }).Dispose();
            for (int i = 0; i < 30; i++) { if (Signal("Local\\QQM-BetterDownload.Settings")) break; Thread.Sleep(100); }
            var clients = Process.GetProcessesByName("QQMusic"); bool running = clients.Length > 0; foreach (var p in clients) p.Dispose();
            if (!running) {
                string client = Path.Combine(LocalKeys.FindClient(), "QQMusic.exe");
                if (!File.Exists(client)) throw new IOException("请先安装并打开 QQ 音乐，BetterDownload 会自动接入。");
                Process.Start(new ProcessStartInfo(client) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(client) }).Dispose();
            }
        }
        [STAThread] public static int Main(string[] args) {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
            try {
                if (args.Contains("--agent")) return Agent.Run();
                if (args.Contains("--stop-agent")) { Signal("Local\\QQM-BetterDownload.AgentStop"); Signal("Local\\QQM-BetterDownload.WorkerStop"); return 0; }
                if (args.Length == 0 || args.Contains("--settings")) { OpenSettings(); return 0; }
                if (args.Contains("--help")) { Console.WriteLine("BetterDownload " + Version + "\n--probe | --convert FILE | --scan | --watch\n--root DIR --client QQMUSIC_DIR --data STATE_DIR --cover-cache DIR\n--card-demo | --card-preview PNG | --ui-smoke PNG\n--settings: open settings inside QQ Music."); return 0; }
                if (args.Contains("--card-preview")) { CardPreview.Save(Option(args, "--card-preview", "")); return 0; }
                if (args.Contains("--readme-cards")) { CardPreview.SaveReadmeCards(Option(args, "--readme-cards", "")); return 0; }
                if (args.Contains("--ui-smoke") || args.Contains("--plugin") || args.Contains("--card-demo")) {
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (var single = new Mutex(false, "Local\\QQM-BetterDownload-" + Environment.UserName + (args.Contains("--plugin") ? "" : "-Preview"))) {
                        bool locked; try { locked = single.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                        if (!locked) { if (!args.Contains("--plugin")) Signal("Local\\QQM-BetterDownload.Settings"); return 0; }
                        try {
                            Process parent = null; string clientPath = null;
                            if (args.Contains("--plugin")) {
                                parent = Process.GetProcessById(Int32.Parse(Option(args, "--parent", "0"))); clientPath = Option(args, "--client", "");
                                if (!parent.ProcessName.Equals("QQMusic", StringComparison.OrdinalIgnoreCase) || !SafePath.Full(Path.GetDirectoryName(parent.MainModule.FileName)).Equals(SafePath.Full(clientPath), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("插件启动来源无效。");
                            }
                            using (var dpi = parent == null ? null : new ClientUi.DpiScope(parent.MainWindowHandle))
                            using (var form = new AppWindow(args.Contains("--ui-smoke") || args.Contains("--card-demo"), parent, clientPath)) {
                                if (args.Contains("--ui-smoke")) form.SavePreview(Option(args, "--ui-smoke", ""));
                                else { if (args.Contains("--card-demo")) form.Shown += delegate { form.PreviewCard(); }; Application.Run(form); }
                            }
                        } finally { single.ReleaseMutex(); }
                    }
                    return 0;
                }
                string root = Option(args, "--root", DefaultRoot()), client = Option(args, "--client", LocalKeys.FindClient()), data = Option(args, "--data", DataFolder);
                using (var single = new Mutex(false, "Local\\QQM-BetterDownload-" + Environment.UserName)) {
                    if (!single.WaitOne(0)) throw new IOException("程序正在运行，请先关闭桌面程序或其他命令行任务。");
                    try {
                        using (var keys = new DownloadKeys(client)) {
                            if (args.Contains("--probe")) { Print(new { version = Version, clientFound = File.Exists(Path.Combine(client, "QQMusic.exe")), localInterfaceReady = keys.Available, storeFound = File.Exists(keys.StorePath), localKeyCount = keys.Read().Count, downloadRoot = root }); return keys.Available ? 0 : 2; }
                            if (args.Contains("--watch")) {
                                using (var stop = new ManualResetEvent(false)) using (var engine = new Engine(root, client, Path.Combine(data, "receipts.json"), Print)) {
                                    ConsoleCancelEventHandler handler = delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; stop.Set(); };
                                    Console.CancelKeyPress += handler;
                                    try { if (args.Contains("--scan")) engine.ScanExisting(); stop.WaitOne(); } finally { Console.CancelKeyPress -= handler; }
                                }
                                return 0;
                            }
                            string file = Option(args, "--convert", "");
                            if (file.Length == 0 && !args.Contains("--scan")) throw new ArgumentException("未知参数。使用 --help 查看命令。");
                            var converter = new Converter(Path.Combine(data, "receipts.json"), Option(args, "--cover-cache", "")); var snapshot = keys.Read(); int failures = 0;
                            foreach (string source in file.Length > 0 ? new[] { file } : SafePath.Enumerate(root)) {
                                try { Print(converter.ConvertFile(root, source, snapshot, null, CancellationToken.None)); }
                                catch (Exception e) { failures++; Print(new { state = "error", source = source, message = e.Message }); }
                            }
                            return failures == 0 ? 0 : 1;
                        }
                    } finally { single.ReleaseMutex(); }
                }
            } catch (Exception e) {
                if (args.Contains("--plugin") || args.Contains("--agent")) try { StateFile.Write(Path.Combine(DataFolder, "worker-error.json"), new { message = e.Message, at = DateTime.UtcNow.ToString("o") }); } catch { }
                Print(new { state = "error", message = e.Message }); return 1;
            }
        }
    }
}
