using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QqmBetterDownload {
    public interface IDownloadMonitor : IDisposable {
        void ScanExisting();
        // Empty for a verified local interface; otherwise one line for the user.
        string Notice { get; }
    }
    // Plugin mode never watches or periodically scans the music directory. The
    // loader's successful file operations supply exact paths through this spool.
    public sealed class EventHost : IDownloadMonitor {
        readonly string spool;
        readonly AutomaticPaths paths;
        readonly DownloadKeys keys;
        readonly Converter converter;
        readonly Action<WorkStatus> report;
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly FileSystemWatcher watcher;
        readonly Task task;
        volatile bool manualScan = true;
        sealed class Retry { public DateTime Next; public string Message; public int Attempts; public string Signature; public readonly string Id = Guid.NewGuid().ToString("N"); }
        readonly Dictionary<string, Retry> retry = new Dictionary<string, Retry>(StringComparer.OrdinalIgnoreCase);
        public EventHost(string client, string root, string data, Action<WorkStatus> onStatus, string coverCache = "", AutomaticPaths detected = null) {
            spool = Path.Combine(data, "events"); paths = detected ?? new AutomaticPaths(data, root); report = onStatus; keys = new DownloadKeys(client); converter = new Converter(Path.Combine(data, "receipts.json"), coverCache);
            Directory.CreateDirectory(spool); SafePath.NoLinks(spool);
            watcher = new FileSystemWatcher(spool, "*.evt") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            watcher.Created += delegate { wake.Set(); }; watcher.Renamed += delegate { wake.Set(); }; watcher.Error += delegate { wake.Set(); };
            watcher.EnableRaisingEvents = true; task = Task.Run((Action)Run);
        }
        public static string DownloadRoot(string source) {
            string full = SafePath.Full(source), marker = "\\VipSongsDownload\\";
            int at = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) throw new InvalidDataException("下载事件不在 VipSongsDownload 中，已保留待检查。");
            string root = full.Substring(0, at + marker.Length - 1);
            if (!SafePath.Candidate(root, full)) throw new InvalidDataException("下载事件路径无效。");
            return root;
        }
        public void ScanExisting() { manualScan = true; wake.Set(); }
        public string Notice { get { return keys.Notice; } }
        public static void Submit(string data, string source) {
            string folder = Path.Combine(data, "events", "manual"); Directory.CreateDirectory(folder); SafePath.NoLinks(folder);
            string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, SafePath.Full(source), new UnicodeEncoding(false, false)); File.Move(temp, Path.ChangeExtension(temp, ".evt"));
        }
        void Emit(string state, string message, string source, string output, int percent, string id = "", TrackInfo track = null, string warning = "") {
            if (report != null) report(new WorkStatus { State = state, Message = message, Source = source, Output = output, Percent = percent, Id = id, Track = track, Warning = warning });
        }
        void Run() {
            Emit("watching", keys.Available ? "已连接 QQ 音乐，等待下载完成事件。" : keys.UnavailableReason, "", "", 0);
            while (!cancel.IsCancellationRequested) {
                try {
                    if (manualScan) {
                        manualScan = false;
                        int found = 0; var roots = paths.Roots;
                        Emit("scanning", "正在查找已有下载", "", "", 0);
                        try {
                            foreach (string root in roots) foreach (string source in SafePath.Enumerate(root)) { cancel.Token.ThrowIfCancellationRequested(); Submit(Path.GetDirectoryName(spool), source); found++; }
                            Emit("scan-complete", roots.Length == 0 ? "暂未识别到下载目录；正常下载一首歌后会自动识别并补处理。" : found == 0 ? "没有需要转换的歌曲" : "已找到 " + found + " 首歌曲并加入检查队列，已转换的会自动跳过。", "", "", 0);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception e) { Emit("scan-complete", "查找失败：" + e.Message, "", "", 0); }
                    }
                    // Scan only our tiny event spool, never the user's downloads.
                    foreach (string evt in Directory.EnumerateFiles(spool, "*.evt", SearchOption.AllDirectories).Take(10000)) {
                        cancel.Token.ThrowIfCancellationRequested(); SafePath.NoLinks(evt); Retry item;
                        if (!retry.TryGetValue(evt, out item)) retry[evt] = item = new Retry();
                        if (item.Next > DateTime.UtcNow) continue;
                        string source = "";
                        try {
                            if (new FileInfo(evt).Length > 8192) throw new InvalidDataException("下载事件过长。");
                            source = SafePath.Full(File.ReadAllText(evt, new UnicodeEncoding(false, false, true))); string root = DownloadRoot(source);
                            if (paths.Learn(source)) manualScan = true;
                            if (!File.Exists(source)) { File.Delete(evt); retry.Remove(evt); continue; }
                            var info = new FileInfo(source); string signature = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                            if (signature != item.Signature) { item.Signature = signature; item.Attempts = 0; item.Next = DateTime.UtcNow.AddSeconds(1); continue; }
                            TrackInfo track = null;
                            var result = converter.ConvertFile(root, source, keys.Read(), delegate(int p, string message) { Emit("converting", message, source, "", p, item.Id, track); }, cancel.Token, delegate(TrackInfo tags) { track = tags; Emit("converting", "正在转换", source, "", 0, item.Id, track); });
                            File.Delete(evt); retry.Remove(evt);
                            Emit(result.Skipped ? "skipped" : "success", result.Skipped ? "已转换过，自动跳过。" : (result.Warning.Length == 0 ? "转换完成，原文件已保留。" : result.Warning), source, result.Output, 100, item.Id, track, result.Warning);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception e) {
                            item.Attempts++; item.Next = DateTime.UtcNow.AddSeconds(item.Attempts < 6 ? 5 : 60);
                            if (item.Message != e.Message) { item.Message = e.Message; Emit(item.Attempts < 6 ? "waiting" : "error", e.Message, source, "", 0, item.Id); }
                        }
                    }
                } catch (OperationCanceledException) { break; }
                catch (Exception e) { Emit("error", e.Message, "", "", 0); }
                wake.WaitOne(1000);
            }
        }
        public void Dispose() { cancel.Cancel(); watcher.Dispose(); wake.Set(); task.Wait(); keys.Dispose(); wake.Dispose(); cancel.Dispose(); }
    }
}
