using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QqmBetterDownload {
    public sealed class WorkStatus {
        public string State, Message, Source = "", Output = "";
        public string Id = "", Warning = "";
        [System.Web.Script.Serialization.ScriptIgnore] public TrackInfo Track;
        public int Percent, Pending;
    }
    public sealed class Engine : IDownloadMonitor {
        sealed class PendingFile { public string Signature = ""; public DateTime Next; public int Attempts; public bool Dormant; public string LastMessage = ""; public readonly string Id = Guid.NewGuid().ToString("N"); }
        readonly string root;
        readonly DownloadKeys localKeys;
        readonly Converter converter;
        readonly Action<WorkStatus> report;
        readonly object gate = new object();
        readonly Dictionary<string, PendingFile> pending = new Dictionary<string, PendingFile>(StringComparer.OrdinalIgnoreCase);
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly FileSystemWatcher files, keys;
        readonly Task worker;
        bool scan;
        public Engine(string downloadRoot, string client, string receiptPath, Action<WorkStatus> onStatus, string coverCache = "") {
            root = SafePath.Full(downloadRoot); SafePath.NoLinks(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("请选择已存在的 QQ 音乐下载文件夹。");
            localKeys = new DownloadKeys(client); converter = new Converter(receiptPath, coverCache); report = onStatus;
            try {
                files = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite, InternalBufferSize = 32768 };
                files.Created += OnFile; files.Changed += OnFile; files.Renamed += OnRename; files.Error += delegate { ScanExisting(); };
                if (localKeys.Available && Directory.Exists(Path.GetDirectoryName(localKeys.StorePath))) {
                    keys = new FileSystemWatcher(Path.GetDirectoryName(localKeys.StorePath), Path.GetFileName(localKeys.StorePath) + "*") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
                    keys.Created += OnKeys; keys.Changed += OnKeys; keys.Renamed += OnKeys; keys.Error += delegate { Reactivate(); };
                    keys.EnableRaisingEvents = true;
                }
                files.EnableRaisingEvents = true; worker = Task.Run((Action)Run);
            } catch { if (files != null) files.Dispose(); if (keys != null) keys.Dispose(); localKeys.Dispose(); throw; }
        }
        void OnFile(object sender, FileSystemEventArgs args) { Enqueue(args.FullPath); }
        void OnRename(object sender, RenamedEventArgs args) { Enqueue(args.FullPath); }
        void OnKeys(object sender, FileSystemEventArgs args) { Reactivate(); }
        void Reactivate() { lock (gate) { foreach (var item in pending.Values) { item.Dormant = false; item.Attempts = 0; item.Next = DateTime.MinValue; } } wake.Set(); }
        public void Enqueue(string path) {
            if (!SafePath.Candidate(root, path) || cancel.IsCancellationRequested) return;
            lock (gate) pending[SafePath.Full(path)] = new PendingFile();
            wake.Set();
        }
        public void ScanExisting() { lock (gate) scan = true; wake.Set(); }
        void Emit(string state, string message, string source, string output, int percent, string id = "", TrackInfo track = null, string warning = "") {
            int count; lock (gate) count = pending.Count;
            if (report != null) report(new WorkStatus { State = state, Message = message, Source = source, Output = output, Percent = percent, Pending = count, Id = id, Track = track, Warning = warning });
        }
        void Remove(string path, PendingFile item) { lock (gate) { PendingFile now; if (pending.TryGetValue(path, out now) && Object.ReferenceEquals(item, now)) pending.Remove(path); } }
        void Run() {
            Emit("watching", localKeys.Available ? "已启用，等待新下载。" : "当前客户端本地接口尚未适配；可处理文件内含密钥的旧格式，新版下载会等待适配。", "", "", 0);
            while (!cancel.IsCancellationRequested) {
                try {
                    bool doScan; lock (gate) { doScan = scan; scan = false; }
                    if (doScan) {
                        Emit("scanning", "正在查找已有的加密下载…", "", "", 0);
                        foreach (string file in SafePath.Enumerate(root)) { cancel.Token.ThrowIfCancellationRequested(); Enqueue(file); }
                        Emit("watching", "查找完成，等待文件完成写入。", "", "", 0);
                    }
                    KeyValuePair<string, PendingFile>[] batch;
                    lock (gate) batch = pending.Where(p => !p.Value.Dormant && p.Value.Next <= DateTime.UtcNow).ToArray();
                    foreach (var pair in batch) {
                        cancel.Token.ThrowIfCancellationRequested(); string path = pair.Key; PendingFile item = pair.Value;
                        try {
                            var info = new FileInfo(path); if (!info.Exists) { Remove(path, item); continue; }
                            string signature = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                            if (item.Signature != signature) { item.Signature = signature; item.Attempts = 0; item.Next = DateTime.UtcNow.AddSeconds(2); continue; }
                            TrackInfo track = null;
                            var result = converter.ConvertFile(root, path, localKeys.Read(), delegate(int percent, string message) { Emit("converting", message, path, "", percent, item.Id, track); }, cancel.Token, delegate(TrackInfo tags) { track = tags; Emit("converting", "正在转换", path, "", 0, item.Id, track); });
                            Remove(path, item);
                            Emit(result.Skipped ? "skipped" : "success", result.Skipped ? "已转换过，自动跳过。" : (result.Warning.Length > 0 ? result.Warning : "转换完成，原文件已保留。"), path, result.Output, 100, item.Id, track, result.Warning);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception e) {
                            if (!(e is InvalidDataException) && !(e is IOException) && !(e is UnauthorizedAccessException) && !(e is KeyNotFoundException) && !(e is NotSupportedException) && !(e is FormatException) && !(e is System.Security.Cryptography.CryptographicException)) throw;
                            item.Attempts++; item.Next = DateTime.UtcNow.AddSeconds(5);
                            item.Dormant = e is NotSupportedException || item.Attempts >= 6;
                            string message = e.Message;
                            if (message != item.LastMessage) { item.LastMessage = message; Emit(item.Dormant ? "error" : "waiting", message, path, "", 0, item.Id); }
                        }
                    }
                } catch (OperationCanceledException) { break; }
                catch (Exception e) { Emit("error", "处理暂停：" + e.Message, "", "", 0); }
                wake.WaitOne(500);
            }
        }
        public void Dispose() {
            cancel.Cancel(); files.Dispose(); if (keys != null) keys.Dispose(); wake.Set();
            try { worker.Wait(); } catch (AggregateException) { }
            localKeys.Dispose(); cancel.Dispose(); wake.Dispose();
        }
    }
}
