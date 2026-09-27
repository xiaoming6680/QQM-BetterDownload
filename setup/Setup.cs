using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using QqmBetterDownload;

namespace BetterDownloadSetup {
    internal sealed class Package { public string product = "", version = ""; public Dictionary<string,string> files = null; }
    internal sealed class Installation {
        public string product = Deployment.Product, current = "", previous = "", pending = "";
        public List<string> versions = new List<string>();
    }
    // A finished operation, as the installer window reports it.
    internal sealed class Outcome {
        internal readonly string Title, Detail; internal readonly bool Installed;
        internal Outcome(string title, string detail, bool installed) { Title = title; Detail = detail; Installed = installed; }
        public override string ToString() { return Title + "。" + Detail; }
    }
    internal static class Deployment {
        internal const string Product = "QQM-BetterDownload/v1";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
        internal static void RequireSharedLocation(string directory) {
            NoLinks(directory); Directory.CreateDirectory(directory);
            // An installer launched by a packaged desktop app can inherit its
            // AppData redirection. Hash checks still pass there, but QQ Music
            // and Explorer cannot see the apparent installation path.
            // Directory handles can retain the logical path of a merged MSIX
            // view. Inspect an actual newly written file instead.
            string probe = Path.Combine(directory, "location-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose)) RequireFileLocation(probe, file);
        }
        static void RequireFileLocation(string path, FileStream file) {
            var actual = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(file.SafeFileHandle, actual, (uint)actual.Capacity, 0);
            if (length == 0 || length >= actual.Capacity) throw new IOException("无法确认安装文件的实际位置。");
            RequireSameLocation(path, actual.ToString());
        }
        internal static void RequireSameLocation(string expected, string actual) {
            if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual.Substring(4);
            if (!Path.GetFullPath(expected).TrimEnd('\\').Equals(Path.GetFullPath(actual).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("安装目录被当前启动环境重定向，QQ 音乐无法访问。请在资源管理器中直接打开安装包，再点击“安装 / 修复”。");
        }
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
            foreach (var item in p.files) {
                string path = Child(directory, item.Key);
                using (var file = File.OpenRead(path)) {
                    RequireFileLocation(path, file);
                    if (!String.Equals(Hash(file), item.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装文件校验失败：" + item.Key);
                }
            }
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
        // What BetterDownload itself writes next to the installation: settings,
        // conversion receipts, status files, the event spool, QQ-side textures
        // and the fallback window's browser data. Songs live elsewhere and stay.
        internal static readonly string[] DataFiles = { "settings.json", "receipts.json", "detected-paths.json", "agent-status.json", "worker-status.json", "worker-error.json", "setup-error.json", "shortcut-owned.txt" };
        internal static readonly string[] DataFolders = { "events", "native-ui", "webview" };
        // A link at one of our names is the user's own arrangement: it is left
        // in place and never followed, and does not stop the rest.
        static bool Linked(string path) { return (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        // Only these names inside a link-free root; true once all are gone or left as links.
        internal static bool RemoveData(string root) {
            NoLinks(root); bool done = true;
            foreach (string name in DataFiles.Concat(DataFolders)) {
                string path = Path.Combine(root, name); bool folder = DataFolders.Contains(name);
                if ((folder ? !Directory.Exists(path) : !File.Exists(path)) || Linked(path)) continue;
                // Directory.Delete removes nested links themselves, not their targets.
                try { if (folder) Directory.Delete(path, true); else File.Delete(path); }
                catch (IOException) { done = false; } catch (UnauthorizedAccessException) { done = false; }
            }
            return done;
        }
        // After a full removal: the emptied versions folder, then the root,
        // each only if nothing else (such as a user's own file) is left in it.
        internal static void RemoveEmpty(string root) {
            NoLinks(root);
            foreach (string path in new[] { Path.Combine(root, "versions"), root }) {
                if (Linked(path)) continue;
                try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        // Up to 0.1.3 the installer added a Start-menu shortcut that opened
        // settings through itself. Remove exactly that one: our launcher, --settings.
        internal static bool RemoveSettingsShortcut(string link, string launcher) {
            if (!File.Exists(link)) return false;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic shortcut = shell.CreateShortcut(link);
            if (!String.Equals((string)shortcut.TargetPath, launcher, StringComparison.OrdinalIgnoreCase) || ((string)shortcut.Arguments).Trim() != "--settings") return false;
            File.Delete(link); return true;
        }
    }
    internal static partial class Program {
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
            if (AgentAlive() && !WorkerAlive()) EndStuckAgents();
            if (AgentAlive() || WorkerAlive()) throw new IOException("BetterDownload 仍在保存任务，请稍后重试。");
        }
        // An agent that ignores the stop signal is stuck. It saves nothing (the
        // worker does), so only our own agent processes are ended, never workers.
        static void EndStuckAgents() {
            string versions = Path.Combine(Root, "versions") + "\\";
            try {
                using (var query = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'BetterDownload.exe'"))
                foreach (ManagementObject item in query.Get()) using (item) {
                    string path = item["ExecutablePath"] as string, command = item["CommandLine"] as string;
                    if (path == null || command == null || !path.StartsWith(versions, StringComparison.OrdinalIgnoreCase) || !command.TrimEnd().EndsWith(" --agent", StringComparison.Ordinal)) continue;
                    try { using (var agent = Process.GetProcessById(Convert.ToInt32(item["ProcessId"]))) { agent.Kill(); agent.WaitForExit(5000); } }
                    catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                }
            } catch (ManagementException) { } catch (COMException) { }
        }
        static Process Start(string file, string args) { return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }); }
        static string PackagedVersion() {
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.json")) using (var r = new StreamReader(s)) { var p = new JavaScriptSerializer().Deserialize<Package>(r.ReadToEnd()); return p == null ? "" : p.version; } }
            catch { return ""; }
        }
        static string InstalledVersion() {
            try { var state = Deployment.State(Root); if (String.IsNullOrEmpty(state.current)) return null; return Deployment.Validate(Deployment.VersionPath(Root, state.current)).version; }
            catch { return null; }
        }
        static bool ClientRunning() { var clients = Process.GetProcessesByName("QQMusic"); foreach (var p in clients) p.Dispose(); return clients.Length > 0; }
        // Only a QQ Music that has loaded one of our bridges pins the current
        // version. One reopened after the old agent quit has none yet, so the
        // switch can go ahead and the new agent connects it. A client whose
        // modules cannot be read counts as holding one.
        static bool BridgeInUse() {
            string versions = Path.Combine(Root, "versions") + "\\";
            foreach (var client in Process.GetProcessesByName("QQMusic")) using (client) {
                try { foreach (ProcessModule module in client.Modules) using (module) if (module.FileName.StartsWith(versions, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception) { return true; }
            }
            return false;
        }
        static string FindQqMusic() {
            foreach (var p in Process.GetProcessesByName("QQMusic")) using (p) { try { return Path.GetDirectoryName(p.MainModule.FileName); } catch (Exception) { } }
            // QQ Music can live on any drive; its installer records the location.
            foreach (string[] entry in new[] { new[] { "SOFTWARE\\Tencent\\QQMusic", "Install" }, new[] { "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\QQMusic", "InstallLocation" } }) {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser }) {
                    try {
                        using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32))
                        using (var k = b.OpenSubKey(entry[0])) {
                            string install = k == null ? null : k.GetValue(entry[1]) as string;
                            if (!String.IsNullOrEmpty(install) && File.Exists(Path.Combine(install, "QQMusic.exe"))) return install.TrimEnd('\\');
                        }
                    } catch (Exception) { }
                }
            }
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }) {
                if (String.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, "Tencent", "QQMusic");
                if (File.Exists(Path.Combine(path, "QQMusic.exe"))) return path;
            }
            return null;
        }
        static string QqMusicVersion(string path) { try { return FileVersionInfo.GetVersionInfo(Path.Combine(path, "QQMusic.exe")).FileVersion; } catch (Exception) { return null; } }
        static bool Registered() { try { using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey)) return key != null; } catch (Exception) { return false; } }
        static string PendingVersion() {
            try { var state = Deployment.State(Root); return String.IsNullOrEmpty(state.pending) ? null : Deployment.Read<Package>(Deployment.Child(Deployment.VersionPath(Root, state.pending), "package.json")).version; }
            catch { return null; }
        }
        static bool FrameworkReady() {
            try {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var k = b.OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full"))
                    return k != null && Convert.ToInt32(k.GetValue("Release", 0)) >= 394802; // 4.6.2
            } catch (Exception) { return true; }
        }
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool DeleteFile(string path);
        // A downloaded installer carries the browser's Mark of the Web, and
        // File.Copy keeps it. Drop it from our own copies so the logon entry
        // does not stop at a security prompt.
        static void Unblock(string file) { DeleteFile(file + ":Zone.Identifier"); }
        static string CleanupArguments(bool purge) { return purge ? "--cleanup --purge" : "--cleanup"; }
        static void Register(string version) {
            // Reinstalling cancels a pending cleanup, including its data removal.
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) { string pending = (run == null ? null : run.GetValue("BetterDownloadCleanup", "") as string) ?? ""; if (pending.EndsWith(" " + CleanupArguments(false)) || pending.EndsWith(" " + CleanupArguments(true))) run.DeleteValue("BetterDownloadCleanup", false); }
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue("BetterDownload", Quote(Launcher) + " --startup");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
                key.SetValue("DisplayName", "BetterDownload"); key.SetValue("DisplayVersion", version); key.SetValue("Publisher", "XIAOMING6680"); key.SetValue("InstallLocation", Root); key.SetValue("DisplayIcon", Launcher + ",0");
                key.SetValue("UninstallString", Quote(Launcher) + " --uninstall-ask"); key.SetValue("QuietUninstallString", Quote(Launcher) + " --uninstall");
                // “修改” in Windows' installed-apps list reopens this installer window.
                key.SetValue("ModifyPath", Quote(Launcher)); key.SetValue("NoModify", 0); key.SetValue("NoRepair", 1);
            }
            RemoveLegacyShortcut();
        }
        // Settings live only inside QQ Music; the installer adds no shortcut.
        static void RemoveLegacyShortcut() {
            try {
                Deployment.RemoveSettingsShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BetterDownload.lnk"), Launcher);
                string marker = Path.Combine(Root, "shortcut-owned.txt"); if (File.Exists(marker)) File.Delete(marker);
            } catch (Exception) { /* A leftover shortcut only reopens this window. */ }
        }
        // Settings open from QQ Music's top-right entry, or from its window menu
        // while the client's interface is not adapted yet.
        static bool EntryInClient() {
            string client = FindQqMusic(), reason; if (client == null) return true;
            try { var ui = ClientCompatibility.Interface(client, out reason); return ui == Support.Verified || ui == Support.Candidate; }
            catch (Exception) { return true; }
        }
        // What the user needs next: where settings are after a first install,
        // and when a prepared version takes over.
        static Outcome Result(bool fresh, string before, string after, bool deferred) {
            if (fresh) return new Outcome("安装完成", (ClientRunning() ? "正在接入 QQ 音乐，" : "打开 QQ 音乐后自动接入，") + (EntryInClient() ? "点击右上角的图标即可设置。" : "设置在窗口菜单（Alt+空格）中。"), true);
            if (before != null && before != after) {
                if (deferred) return new Outcome("v" + after + " 已准备好", "QQ 音乐退出后自动切换，设置和转换记录会保留。", true);
                Version from = ParseVersion(before), to = ParseVersion(after);
                return new Outcome((from != null && to != null && to < from ? "已切换到 v" : "已更新到 v") + after, "设置和转换记录已保留。", true);
            }
            if (deferred) return new Outcome("修复已准备好", "QQ 音乐退出后自动生效。", true);
            return new Outcome("修复完成", ClientRunning() ? "正在重新接入 QQ 音乐。" : "打开 QQ 音乐后自动接入。", true);
        }
        internal static Outcome Install() {
            using (var single = new Mutex(false, "Local\\QQM-BetterDownload.Setup")) {
                bool locked; try { locked = single.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new IOException("另一个安装或修复正在进行。");
                try {
                    if (!FrameworkReady()) throw new IOException("需要 .NET Framework 4.6.2 或更高版本。Windows 10 1607 及以上已自带，请先通过 Windows 更新安装。");
                    Deployment.RequireSharedLocation(Root);
                    var old = Deployment.State(Root); bool existing = File.Exists(Path.Combine(Root, "installation.json"));
                    bool fresh = !Registered(); string before = fresh ? null : InstalledVersion();
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
                    Unblock(Launcher);
                    if (!old.versions.Contains(id)) old.versions.Add(id);
                    bool defer = existing && old.current != id && AgentAlive() && Process.GetProcessesByName("QQMusic").Length > 0;
                    if (defer) old.pending = id;
                    else { Stop(); if (old.current != id) old.previous = old.current; old.current = id; old.pending = ""; }
                    Deployment.Write(Path.Combine(Root, "installation.json"), old);
                    string version = Deployment.Validate(Deployment.VersionPath(Root, id)).version;
                    Register(version);
                    if (!defer) Start(Path.Combine(Deployment.Select(Root), "BetterDownload.exe"), "--agent").Dispose();
                    return Result(fresh, before, version, defer);
                } finally { single.ReleaseMutex(); }
            }
        }
        // wait: the agent asks for the switch as soon as QQ Music's window is
        // gone, but the process can take several more seconds to exit. Giving up
        // then would restart the old agent instead of switching.
        static void ActivatePending(bool wait) {
            var state = Deployment.State(Root); if (String.IsNullOrEmpty(state.pending)) return;
            for (int i = 0; wait && i < 40 && BridgeInUse(); i++) Thread.Sleep(500);
            if (BridgeInUse()) return;
            Deployment.Validate(Deployment.VersionPath(Root, state.pending)); Stop();
            state.previous = state.current; state.current = state.pending; state.pending = ""; Deployment.Write(Path.Combine(Root, "installation.json"), state);
        }
        static void Startup() {
            Deployment.RequireSharedLocation(Root);
            ActivatePending(false); string app;
            try { app = Path.Combine(Deployment.Select(Root), "BetterDownload.exe"); }
            catch (IOException) { Install(); app = Path.Combine(Deployment.Select(Root), "BetterDownload.exe"); }
            Start(app, "--agent").Dispose();
        }
        internal static Outcome Uninstall() { return Uninstall(false); }
        // purge: the user also chose to delete BetterDownload's own data
        // (settings, receipts, caches). Songs are never touched.
        internal static Outcome Uninstall(bool purge) {
            Stop();
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (run != null && (string)run.GetValue("BetterDownload", "") == Quote(Launcher) + " --startup") run.DeleteValue("BetterDownload", false); }
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey)) { if (key != null && (string)key.GetValue("InstallLocation", "") != Root) throw new IOException("卸载记录不匹配。"); }
            Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
            RemoveLegacyShortcut();
            // Whatever is still in use (QQ-side textures, browser data) goes with the cleanup below.
            bool removed = false;
            if (purge) try { removed = Deployment.RemoveData(Root); } catch (IOException) { }
            // The bridge can stay loaded until QQ exits. A temporary copy retries
            // deletion without terminating QQ, and keeps songs/settings/receipts
            // unless the user chose to delete the data too.
            string cleaner = Path.Combine(Path.GetTempPath(), "BetterDownload-Cleanup.exe");
            if (File.Exists(cleaner) && Deployment.HashFile(cleaner) != Deployment.HashFile(Assembly.GetExecutingAssembly().Location)) cleaner = Path.Combine(Path.GetTempPath(), "BetterDownload-Cleanup-" + Guid.NewGuid().ToString("N") + ".exe");
            if (!File.Exists(cleaner)) File.Copy(Assembly.GetExecutingAssembly().Location, cleaner);
            Unblock(cleaner);
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue("BetterDownloadCleanup", Quote(cleaner) + " " + CleanupArguments(purge));
            Start(cleaner, CleanupArguments(purge)).Dispose();
            bool client = ClientRunning();
            if (!purge) return new Outcome("已卸载", "歌曲、设置和转换记录均已保留。" + (client ? "剩余组件会在 QQ 音乐退出后清理。" : ""), false);
            if (removed) return new Outcome("已卸载", "设置、转换记录和缓存已删除，歌曲保留。" + (client ? "剩余组件会在 QQ 音乐退出后清理。" : ""), false);
            return new Outcome("已卸载", "歌曲保留；设置、转换记录和缓存" + (client ? "会在 QQ 音乐退出后删除。" : "稍后自动删除。"), false);
        }
        static void DropCleanupEntry() {
            string self = Quote(Assembly.GetExecutingAssembly().Location) + " ";
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
                string pending = run == null ? null : run.GetValue("BetterDownloadCleanup", "") as string;
                if (pending == self + CleanupArguments(false) || pending == self + CleanupArguments(true)) run.DeleteValue("BetterDownloadCleanup", false);
            }
        }
        static void Cleanup(bool purge) {
            // Share the installer's lock, and stand down when the user has
            // reinstalled in the meantime: removing files by version id would
            // otherwise delete the fresh installation of the same version.
            using (var single = new Mutex(false, "Local\\QQM-BetterDownload.Setup")) {
                for (int attempt = 0; attempt < 60; attempt++, Thread.Sleep(1000)) {
                    bool locked; try { locked = single.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) continue;
                    try {
                        if (Registered()) { DropCleanupEntry(); return; }
                        var state = Deployment.State(Root); bool done = true;
                        foreach (var id in state.versions) if (!Deployment.RemoveOwnedVersion(Root, id)) done = false;
                        if (!done) continue;
                        try { if (File.Exists(Launcher)) File.Delete(Launcher); }
                        catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                        File.Delete(Path.Combine(Root, "installation.json"));
                        if (purge && !Deployment.RemoveData(Root)) continue;
                        if (purge) Deployment.RemoveEmpty(Root);
                        DropCleanupEntry(); return;
                    } finally { single.ReleaseMutex(); }
                }
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
            PrepareUi();
            try {
                if (args.Contains("--ui-preview")) { int at = Array.IndexOf(args, "--ui-preview"); PreviewUi(at + 1 < args.Length ? args[at + 1] : "setup-preview"); return 0; }
                // A working directory inside the installation would keep it from being removed.
                if (args.Contains("--cleanup")) { Directory.SetCurrentDirectory(Path.GetTempPath()); Cleanup(args.Contains("--purge")); return 0; }
                if (args.Contains("--uninstall")) { Uninstall(args.Contains("--purge")); return 0; }
                // Windows' “卸载” asks in the installer window itself.
                if (args.Contains("--uninstall-ask")) { RunUi(true); return 0; }
                if (args.Contains("--activate-pending")) { ActivatePending(true); Startup(); return 0; }
                if (args.Contains("--install")) { Console.WriteLine(Install()); return 0; }
                if (args.Contains("--startup")) { Startup(); return 0; }
                // The downloaded installer, the installed launcher and Windows'
                // “修改” all open the same window. It never opens settings.
                RunUi(false);
                return 0;
            } catch (Exception e) {
                try { Deployment.Write(Path.Combine(Root, "setup-error.json"), new { message = e.Message, at = DateTime.UtcNow.ToString("o") }); } catch { }
                if (!args.Contains("--startup") && !args.Contains("--cleanup")) MessageBox.Show(e.Message, "BetterDownload"); return 1;
            }
        }
    }
}
