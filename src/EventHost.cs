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
        // lyrics: write QQ Music's downloaded .lrc into the output; lyricsFile: copy it beside the output.
        void Configure(bool lyrics, bool lyricsFile);
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
        sealed class Retry { public DateTime Next; public string Message; public int Attempts; public string Signature, Source; public readonly string Id = Guid.NewGuid().ToString("N"); }
        readonly Dictionary<string, Retry> retry = new Dictionary<string, Retry>(StringComparer.OrdinalIgnoreCase);
        // Songs converted back to back share one card: it counts them and stays up
        // until nothing is left to convert.
        sealed class Round { public readonly string Id = Guid.NewGuid().ToString("N"); public int Converted, Failed; public readonly List<string> Folders = new List<string>(); }
        Round round;
        // Songs the last scan queued; the scan reports again once all are settled.
        HashSet<string> scanned;
        int scanConverted, scanFailed;
        string scanReason = "";
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
        public void Configure(bool lyrics, bool lyricsFile) { converter.Lyrics = lyrics; converter.LyricsFile = lyricsFile; }
        public static void Submit(string data, string source) {
            string folder = Path.Combine(data, "events", "manual"); Directory.CreateDirectory(folder); SafePath.NoLinks(folder);
            string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, SafePath.Full(source), new UnicodeEncoding(false, false)); File.Move(temp, Path.ChangeExtension(temp, ".evt"));
        }
        void Emit(string state, string message, string source, string output, int percent, string id = "", TrackInfo track = null, string warning = "") {
            if (report != null) report(new WorkStatus { State = state, Message = message, Source = source, Output = output, Percent = percent, Id = id, Track = track, Warning = warning });
        }
        static string ReadEvent(string evt) {
            try { SafePath.NoLinks(evt); return new FileInfo(evt).Length > 8192 ? "" : SafePath.Full(File.ReadAllText(evt, new UnicodeEncoding(false, false, true))); }
            catch (Exception) { return ""; }
        }
        // A song this pass will still convert: not known as converted, not
        // already failed six times. Only the card's count depends on it.
        bool Live(Retry item) { return item.Attempts < 6 && !String.IsNullOrEmpty(item.Source) && !converter.Done(item.Source); }
        WorkStatus Job(string state, string message, string source, Retry item, int percent, int pending, TrackInfo track) {
            if (state == "converting" && round == null) round = new Round();
            var status = new WorkStatus { State = state, Message = message, Source = source, Percent = percent, Id = item.Id, Track = track, Pending = pending };
            if (round != null) { status.Round = round.Id; status.Position = round.Converted + round.Failed + 1; status.Converted = round.Converted; status.Failed = round.Failed; }
            return status;
        }
        void Report(WorkStatus status) { if (report != null) report(status); }
        // A song's final result. The round ends with the last queued song.
        void Complete(string state, string message, string source, Retry item, ConversionResult result, int pending, TrackInfo track) {
            if (round == null) round = new Round();
            if (state == "success") { round.Converted++; string folder = Path.GetDirectoryName(result.Output); if (!round.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) round.Folders.Add(folder); }
            else round.Failed++;
            var status = new WorkStatus { State = state, Message = message, Source = source, Output = result == null ? "" : result.Output, Percent = state == "success" ? 100 : 0, Id = item.Id, Track = track,
                Warning = result == null ? "" : result.Warning, Lyrics = result != null && result.Lyrics, Pending = pending,
                Round = round.Id, Position = round.Converted + round.Failed, Converted = round.Converted, Failed = round.Failed, Folder = Shared(round.Folders) };
            if (pending == 0) round = null;
            Report(status); Settle(source, state == "success" ? 1 : -1, message);
        }
        // The deepest folder holding every output of the round.
        static string Shared(List<string> folders) {
            if (folders.Count == 0) return "";
            string[] first = folders[0].Split('\\'); int n = first.Length;
            foreach (string folder in folders.Skip(1)) { string[] parts = folder.Split('\\'); int i = 0; while (i < n && i < parts.Length && parts[i].Equals(first[i], StringComparison.OrdinalIgnoreCase)) i++; n = i; }
            return n < 2 ? folders[folders.Count - 1] : String.Join("\\", first.Take(n));
        }
        // outcome: 1 converted, 0 already up to date, -1 not converted.
        void Settle(string source, int outcome, string reason) {
            if (scanned == null || !scanned.Remove(SafePath.Full(source))) return;
            if (outcome < 0) { scanFailed++; if (scanReason.Length == 0) scanReason = reason; } else scanConverted++;
            if (scanned.Count > 0) return;
            int total = scanConverted + scanFailed; scanned = null;
            string brief = (scanReason.Length > 36 ? scanReason.Substring(0, 36) + "…" : scanReason).TrimEnd('。');
            Emit("scan-complete", scanFailed == 0 ? "找到的 " + total + " 首待转换歌曲已全部完成。" : "找到的 " + total + " 首待转换歌曲：" + scanConverted + " 首完成，" + scanFailed + " 首未完成（" + brief + "）。", "", "", 0);
        }
        // Converted songs are recognised by their receipts and left alone; the
        // rest are queued, and songs stuck in slow retries are tried again now.
        void Scan() {
            var roots = paths.Roots; int found = 0, done = 0;
            Emit("scanning", "正在查找已有下载", "", "", 0);
            try {
                var queued = new Dictionary<string, Retry>(StringComparer.OrdinalIgnoreCase);
                foreach (string evt in Directory.EnumerateFiles(spool, "*.evt", SearchOption.AllDirectories).Take(10000)) {
                    Retry item; if (!retry.TryGetValue(evt, out item)) retry[evt] = item = new Retry { Source = ReadEvent(evt) };
                    if (!String.IsNullOrEmpty(item.Source)) queued[item.Source] = item;
                }
                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in roots) foreach (string file in SafePath.Enumerate(root)) {
                    cancel.Token.ThrowIfCancellationRequested(); found++; string source = SafePath.Full(file);
                    if (converter.Done(source)) { done++; continue; }
                    Retry waiting;
                    if (queued.TryGetValue(source, out waiting)) { waiting.Attempts = 0; waiting.Next = DateTime.MinValue; waiting.Message = null; }
                    else Submit(Path.GetDirectoryName(spool), source);
                    batch.Add(source);
                }
                scanned = batch.Count > 0 ? batch : null; scanConverted = scanFailed = 0; scanReason = "";
                Emit("scan-complete", roots.Length == 0 ? "暂未识别到下载目录；在 QQ 音乐里正常下载一首歌后会自动识别并补处理。"
                    : found == 0 ? "下载目录中没有加密歌曲，不需要转换。"
                    : batch.Count == 0 ? "找到 " + found + " 首加密歌曲，都已转换过。"
                    : "找到 " + found + " 首加密歌曲：" + batch.Count + " 首待转换" + (done > 0 ? "，" + done + " 首已转换过" : "") + "，正在处理。", "", "", 0);
            } catch (OperationCanceledException) { throw; }
            catch (Exception e) { Emit("scan-complete", "查找失败：" + e.Message, "", "", 0); }
        }
        void Run() {
            Emit("watching", keys.Available ? "已连接 QQ 音乐，等待下载完成事件。" : keys.UnavailableReason, "", "", 0);
            while (!cancel.IsCancellationRequested) {
                try {
                    if (manualScan) { manualScan = false; Scan(); }
                    // Scan only our tiny event spool, never the user's downloads.
                    var queue = Directory.EnumerateFiles(spool, "*.evt", SearchOption.AllDirectories).Take(10000).ToList();
                    var items = new Retry[queue.Count]; var live = new bool[queue.Count]; int remaining = 0;
                    for (int i = 0; i < queue.Count; i++) {
                        SafePath.NoLinks(queue[i]);
                        if (!retry.TryGetValue(queue[i], out items[i])) retry[queue[i]] = items[i] = new Retry { Source = ReadEvent(queue[i]) };
                        if (live[i] = Live(items[i])) remaining++;
                    }
                    for (int i = 0; i < queue.Count; i++) {
                        cancel.Token.ThrowIfCancellationRequested(); string evt = queue[i]; Retry item = items[i];
                        if (live[i]) remaining--;
                        if (item.Next > DateTime.UtcNow) continue;
                        string source = "";
                        try {
                            if (new FileInfo(evt).Length > 8192) throw new InvalidDataException("下载事件过长。");
                            source = SafePath.Full(File.ReadAllText(evt, new UnicodeEncoding(false, false, true))); string root = DownloadRoot(source);
                            if (paths.Learn(source)) manualScan = true;
                            if (!File.Exists(source)) { File.Delete(evt); retry.Remove(evt); Settle(source, -1, "歌曲文件已被移动或删除。"); continue; }
                            var info = new FileInfo(source); string signature = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                            if (signature != item.Signature) { item.Signature = signature; item.Attempts = 0; item.Next = DateTime.UtcNow.AddSeconds(1); continue; }
                            // After six failures a song retries quietly; only its result reaches the card.
                            bool quiet = item.Attempts >= 6; int pending = remaining + 1; TrackInfo track = null;
                            if (live[i]) Report(Job("converting", "正在准备", source, item, 0, pending, null));
                            var result = converter.ConvertFile(root, source, keys.Read(), delegate(int p, string message) { if (!quiet) Report(Job("converting", message, source, item, p, pending, track)); }, cancel.Token, delegate(TrackInfo tags) { track = tags; if (!quiet) Report(Job("converting", "正在转换", source, item, 0, pending, track)); });
                            File.Delete(evt); retry.Remove(evt);
                            // A song already on the card that turns out converted (its output
                            // kept) still finishes its place in the round.
                            if (result.Skipped && live[i]) Complete("success", "已转换过，保留已有的输出文件。", source, item, result, remaining, track);
                            else if (result.Skipped) {
                                Report(new WorkStatus { State = "skipped", Message = "已转换过，自动跳过。", Source = source, Output = result.Output, Percent = 100, Id = item.Id, Pending = remaining });
                                Settle(source, 0, "");
                            } else Complete("success", result.Warning.Length == 0 ? "转换完成，原文件已保留。" : result.Warning, source, item, result, remaining, track);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception e) {
                            item.Attempts++; item.Next = DateTime.UtcNow.AddSeconds(item.Attempts < 6 ? 5 : 60);
                            // The sixth failure ends the quick retries: report it once as not converted.
                            if (item.Attempts == 6) { item.Message = e.Message; Complete("error", e.Message, source, item, null, remaining, null); }
                            else if (item.Message != e.Message) { item.Message = e.Message; Report(Job(item.Attempts < 6 ? "waiting" : "error", e.Message, source, item, 0, item.Attempts < 6 ? remaining + 1 : remaining, null)); }
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
