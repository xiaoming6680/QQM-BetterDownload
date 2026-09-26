using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace BetterDownloadSetup {
    internal sealed class Package { public string product = "", version = ""; public Dictionary<string,string> files = null; }
    internal sealed class Installation {
        public string product = Deployment.Product, current = "", previous = "", pending = "";
        public List<string> versions = new List<string>();
    }
    internal static class Deployment {
        internal const string Product = "QQM-BetterDownload/v1";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        internal static string Hash(Stream file) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
        internal static string HashFile(string file) { using (var f = File.OpenRead(file)) return Hash(f); }
        internal static void NoLinks(string path) {
            string current = Path.GetFullPath(path);
            if (current.StartsWith("\\\\") || current.Substring(2).Contains(":")) throw new IOException("安装路径必须位于本机磁盘。");
            while (!String.IsNullOrEmpty(current)) { if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装目录包含链接。"); current = Path.GetDirectoryName(current); }
        }
        internal static string Child(string root, string name) {
            if (String.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\")) throw new InvalidDataException("安装清单包含无效名称。");
            string path = Path.Combine(root, name); NoLinks(path); return path;
        }
        internal static T Read<T>(string path) { return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        internal static void Write(string path, object state) {
            NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, Json.Serialize(state), new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static Installation State(string root) {
            string path = Child(root, "installation.json"); if (!File.Exists(path)) return new Installation();
            var state = Read<Installation>(path); if (state == null || state.product != Product || state.versions == null) throw new IOException("安装记录不属于 BetterDownload，已停止修改。"); return state;
        }
        internal static Package Validate(string directory) {
            var p = Read<Package>(Child(directory, "package.json"));
            if (p == null || p.product != Product || p.files == null || !p.files.ContainsKey("BetterDownload.exe") || !p.files.ContainsKey("BetterDownloadBridge.dll") || !p.files.ContainsKey("TagLibSharp.dll")) throw new InvalidDataException("安装包缺少必要文件。");
            foreach (var item in p.files) if (!String.Equals(HashFile(Child(directory, item.Key)), item.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装文件校验失败：" + item.Key);
            return p;
        }
        internal static string Stage(string root, Stream archive, Stream manifest) {
            NoLinks(root); Directory.CreateDirectory(root); string versions = Child(root, "versions"); Directory.CreateDirectory(versions);
            string staging = Child(versions, "staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            try {
                string json; using (var reader = new StreamReader(manifest, Encoding.UTF8)) json = reader.ReadToEnd();
                var p = Json.Deserialize<Package>(json); if (p == null || p.product != Product || p.files == null || p.files.Count > 40) throw new InvalidDataException("安装包清单无效。");
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, true)) {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in zip.Entries) {
                        if (!p.files.ContainsKey(entry.FullName) || !seen.Add(entry.FullName) || entry.Length > 30 * 1024 * 1024) throw new InvalidDataException("安装包包含清单外文件。");
                        using (var input = entry.Open()) using (var output = new FileStream(Child(staging, entry.FullName), FileMode.CreateNew)) input.CopyTo(output);
                    }
                    if (seen.Count != p.files.Count) throw new InvalidDataException("安装包文件数量不符。");
                }
                File.WriteAllText(Child(staging, "package.json"), json, new UTF8Encoding(false)); Validate(staging);
                string id = p.version + "-" + HashFile(Child(staging, "package.json")).Substring(0, 12).ToLowerInvariant();
                string dest = Child(versions, id);
                if (Directory.Exists(dest)) {
                    bool valid = false;
                    try { Validate(dest); valid = true; } catch (InvalidDataException) { } catch (IOException) { } catch (ArgumentException) { }
                    if (valid) DeleteStaging(staging);
                    else {
                        // A damaged or still-mapped installation is preserved;
                        // repair activates a new, fully verified directory.
                        id += "-repair-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        dest = Child(versions, id); Directory.Move(staging, dest);
                    }
                } else Directory.Move(staging, dest);
                return id;
            } catch { DeleteStaging(staging); throw; }
        }
        static void DeleteStaging(string path) { // only our flat, randomly named extraction folder
            NoLinks(path); if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFiles(path)) { NoLinks(file); File.Delete(file); } Directory.Delete(path);
        }
        internal static string VersionPath(string root, string id) { return Child(Child(root, "versions"), id); }
        internal static string Select(string root) {
            var state = State(root);
            foreach (string id in new[] { state.current, state.previous }) {
                if (String.IsNullOrEmpty(id)) continue;
                try {
                    string path = VersionPath(root, id); Validate(path);
                    if (id != state.current) { state.current = id; state.previous = ""; Write(Child(root, "installation.json"), state); }
                    return path;
                } catch (InvalidDataException) { } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
            }
            throw new IOException("当前和上一版本均未通过校验，请运行安装包修复。");
        }
        internal static bool RemoveOwnedVersion(string root, string id) {
            string dir = VersionPath(root, id); if (!Directory.Exists(dir)) return true;
            string manifest = Child(dir, "package.json"); Package p;
            try { p = Read<Package>(manifest); } catch { return false; }
            if (p == null || p.product != Product || p.files == null) return false;
            bool done = true;
            foreach (var item in p.files) {
                string path = Child(dir, item.Key); if (!File.Exists(path)) continue;
                    try { if (HashFile(path) != item.Value.ToUpperInvariant()) continue; File.Delete(path); }
                catch (IOException) { done = false; } catch (UnauthorizedAccessException) { done = false; }
            }
            if (done && Directory.GetFiles(dir).All(f => Path.GetFileName(f) == "package.json") && Directory.GetDirectories(dir).Length == 0) { File.Delete(manifest); Directory.Delete(dir); return true; }
            return done;
        }
    }
    internal static class Program {
        static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQM-BetterDownload");
        static readonly string Launcher = Path.Combine(Root, "BetterDownload-Setup.exe");
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run", UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\QQM-BetterDownload";
        static string Quote(string value) { return "\"" + value + "\""; }
        static void Signal(string name) { try { using (var e = EventWaitHandle.OpenExisting(name)) e.Set(); } catch (WaitHandleCannotBeOpenedException) { } }
        static bool AgentAlive() { try { using (var m = Mutex.OpenExisting("Local\\QQM-BetterDownload.Agent")) { try { if (!m.WaitOne(0)) return true; m.ReleaseMutex(); } catch (AbandonedMutexException) { m.ReleaseMutex(); } return false; } } catch (WaitHandleCannotBeOpenedException) { return false; } }
        static bool WorkerAlive() { try { using (var e = EventWaitHandle.OpenExisting("Local\\QQM-BetterDownload.WorkerAlive")) return e.WaitOne(0); } catch (WaitHandleCannotBeOpenedException) { return false; } }
        static void Stop() {
            Signal("Local\\QQM-BetterDownload.AgentStop"); Signal("Local\\QQM-BetterDownload.WorkerStop");
            var timer = Stopwatch.StartNew(); while ((AgentAlive() || WorkerAlive()) && timer.ElapsedMilliseconds < 15000) Thread.Sleep(100);
            if (AgentAlive() || WorkerAlive()) throw new IOException("BetterDownload 仍在保存任务，请稍后重试。");
        }
        static Process Start(string file, string args) { return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }); }
        static void Register(string version) {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue("BetterDownload", Quote(Launcher) + " --startup");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
                key.SetValue("DisplayName", "BetterDownload"); key.SetValue("DisplayVersion", version); key.SetValue("Publisher", "XIAOMING6680"); key.SetValue("InstallLocation", Root);
                key.SetValue("UninstallString", Quote(Launcher) + " --uninstall-ask"); key.SetValue("QuietUninstallString", Quote(Launcher) + " --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 0);
            }
            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BetterDownload.lnk");
            if (!File.Exists(link)) {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = Launcher; shortcut.Arguments = "--settings"; shortcut.Description = "BetterDownload 设置"; shortcut.Save();
                File.WriteAllText(Path.Combine(Root, "shortcut-owned.txt"), link, Encoding.UTF8);
            }
        }
        static string Install() {
            using (var single = new Mutex(false, "Local\\QQM-BetterDownload.Setup")) {
                bool locked; try { locked = single.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new IOException("另一个安装或修复正在进行。");
                try {
                    var old = Deployment.State(Root); bool existing = File.Exists(Path.Combine(Root, "installation.json"));
                    var assembly = Assembly.GetExecutingAssembly(); string id;
                    using (var zip = assembly.GetManifestResourceStream("payload.zip")) using (var json = assembly.GetManifestResourceStream("payload.json")) id = Deployment.Stage(Root, zip, json);
                    // Refresh the owned launcher too: its embedded payload is
                    // the offline repair source after both active versions fail.
                    if (!File.Exists(Launcher)) File.Copy(assembly.Location, Launcher, false);
                    else if (!existing && Deployment.HashFile(Launcher) != Deployment.HashFile(assembly.Location)) throw new IOException("安装目录已有其他文件，已停止覆盖。");
                    else if (existing && !Path.GetFullPath(assembly.Location).Equals(Path.GetFullPath(Launcher),StringComparison.OrdinalIgnoreCase) && Deployment.HashFile(Launcher) != Deployment.HashFile(assembly.Location)) {
                        string replacement = Deployment.Child(Root,"launcher-"+Guid.NewGuid().ToString("N")+".tmp");
                        try { File.Copy(assembly.Location,replacement,false); File.Replace(replacement,Launcher,null); }
                        catch(IOException) { /* A currently running launcher keeps reading the version pointer. */ }
                        finally { if(File.Exists(replacement))File.Delete(replacement); }
                    }
                    if (!old.versions.Contains(id)) old.versions.Add(id);
                    bool defer = existing && old.current != id && AgentAlive() && Process.GetProcessesByName("QQMusic").Length > 0;
                    if (defer) old.pending = id;
                    else { Stop(); if (old.current != id) old.previous = old.current; old.current = id; old.pending = ""; }
                    Deployment.Write(Path.Combine(Root, "installation.json"), old);
                    Register(Deployment.Validate(Deployment.VersionPath(Root, id)).version);
                    if (!defer) Start(Path.Combine(Deployment.Select(Root), "BetterDownload.exe"), "--agent").Dispose();
                    return defer ? "更新已准备好，QQ 音乐退出后自动切换。" : "安装完成。QQ 音乐启动后自动接入，设置入口在顶部和系统托盘。";
                } finally { single.ReleaseMutex(); }
            }
        }
        static void ActivatePending() {
            var state = Deployment.State(Root); if (String.IsNullOrEmpty(state.pending)) return;
            if (Process.GetProcessesByName("QQMusic").Length != 0) return;
            Deployment.Validate(Deployment.VersionPath(Root, state.pending)); Stop();
            state.previous = state.current; state.current = state.pending; state.pending = ""; Deployment.Write(Path.Combine(Root, "installation.json"), state);
        }
        static void Startup(bool settings) {
            ActivatePending(); string app;
            try { app = Path.Combine(Deployment.Select(Root), "BetterDownload.exe"); }
            catch (IOException) { Install(); app = Path.Combine(Deployment.Select(Root), "BetterDownload.exe"); }
            Start(app, "--agent").Dispose();
            if (settings) Start(app, "").Dispose();
        }
        static void Uninstall() {
            Stop();
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (run != null && (string)run.GetValue("BetterDownload", "") == Quote(Launcher) + " --startup") run.DeleteValue("BetterDownload", false); }
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey)) { if (key != null && (string)key.GetValue("InstallLocation", "") != Root) throw new IOException("卸载记录不匹配。"); }
            Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
            string marker = Path.Combine(Root, "shortcut-owned.txt");
            if (File.Exists(marker)) {
                string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BetterDownload.lnk");
                if (File.Exists(link)) { dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic shortcut = shell.CreateShortcut(link); if ((string)shortcut.TargetPath == Launcher) File.Delete(link); }
                File.Delete(marker);
            }
            // The bridge can stay loaded until QQ exits. A temporary copy retries
            // deletion without terminating QQ or deleting songs/settings/receipts.
            string cleaner = Path.Combine(Path.GetTempPath(), "BetterDownload-Cleanup.exe");
            if (File.Exists(cleaner) && Deployment.HashFile(cleaner) != Deployment.HashFile(Assembly.GetExecutingAssembly().Location)) cleaner = Path.Combine(Path.GetTempPath(), "BetterDownload-Cleanup-" + Guid.NewGuid().ToString("N") + ".exe");
            if (!File.Exists(cleaner)) File.Copy(Assembly.GetExecutingAssembly().Location, cleaner);
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue("BetterDownloadCleanup", Quote(cleaner) + " --cleanup");
            Start(cleaner, "--cleanup").Dispose();
        }
        static void Cleanup() {
            var state = Deployment.State(Root);
            for (int attempt = 0; attempt < 60; attempt++) {
                bool done = true;
                foreach (var id in state.versions) if (!Deployment.RemoveOwnedVersion(Root, id)) done = false;
                if (done) {
                    try { if (File.Exists(Launcher)) File.Delete(Launcher); }
                    catch (IOException) { Thread.Sleep(1000); continue; }
                    File.Delete(Path.Combine(Root, "installation.json"));
                    using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (run != null && (string)run.GetValue("BetterDownloadCleanup", "") == Quote(Assembly.GetExecutingAssembly().Location) + " --cleanup") run.DeleteValue("BetterDownloadCleanup", false);
                    return;
                }
                Thread.Sleep(1000);
            }
        }
        [STAThread] public static int Main(string[] args) {
            if (args.Contains("--self-test")) {
                // Automated fault-injection tests must never display a modal
                // error or write their simulated failures into installed data.
                try { return SelfTest.Run(); }
                catch (Exception e) {
                    string failure = "FAIL\n" + e;
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup-tests.txt"), failure, Encoding.UTF8);
                    Console.Error.WriteLine(failure); return 1;
                }
            }
            Application.EnableVisualStyles();
            try {
                if (args.Contains("--cleanup")) { Cleanup(); return 0; }
                if (args.Contains("--uninstall") || args.Contains("--uninstall-ask")) {
                    if (args.Contains("--uninstall-ask") && MessageBox.Show("卸载 BetterDownload？歌曲、设置和转换记录会保留。", "BetterDownload", MessageBoxButtons.OKCancel) != DialogResult.OK) return 0;
                    Uninstall(); return 0;
                }
                if (args.Contains("--activate-pending")) { ActivatePending(); Startup(false); return 0; }
                if (args.Contains("--install")) { Console.WriteLine(Install()); return 0; }
                if (args.Contains("--startup")) { Startup(false); return 0; }
                if (args.Contains("--settings") || Assembly.GetExecutingAssembly().Location.Equals(Launcher, StringComparison.OrdinalIgnoreCase)) { Startup(true); return 0; }
                using (var form = new Form { Text = "BetterDownload 安装", ClientSize = new Size(540, 265), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.FromArgb(247,249,248) }) {
                    var title = new Label { Text = "BetterDownload", Font = new Font("Segoe UI", 25, FontStyle.Bold), Left = 28, Top = 25, AutoSize = true };
                    var text = new Label { Text = "安装一次，QQ 音乐启动后自动接入。\n顶部设置入口 · 下载完成卡片 · 保留原文件\n\n仅安装到当前用户目录，无需管理员权限。", Left = 30, Top = 84, Width = 485, Height = 100 };
                    var install = new Button { Text = "安装 / 修复", Left = 345, Top = 197, Width = 165, Height = 38, BackColor = Color.FromArgb(0,170,119), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
                    bool installed = false;
                    install.Click += delegate { if (installed) { Startup(true); form.Close(); return; } install.Enabled = false; try { text.Text = Install(); install.Text = "打开设置"; installed = true; } catch (Exception e) { text.Text = e.Message; } finally { install.Enabled = true; } };
                    form.Controls.AddRange(new Control[] { title, text, install }); Application.Run(form);
                }
                return 0;
            } catch (Exception e) {
                try { Deployment.Write(Path.Combine(Root, "setup-error.json"), new { message = e.Message, at = DateTime.UtcNow.ToString("o") }); } catch { }
                if (!args.Contains("--startup") && !args.Contains("--cleanup")) MessageBox.Show(e.Message, "BetterDownload"); return 1;
            }
        }
    }
}
