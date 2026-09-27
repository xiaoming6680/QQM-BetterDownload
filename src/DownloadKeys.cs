using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;

namespace QqmBetterDownload {
    // Old QTag/V1 downloads carry their own key, so an unrecognized client must
    // not disable the whole queue. New musicex jobs remain pending for adaptation.
    public sealed class DownloadKeys : IDisposable {
        readonly string client, storeFolder;
        readonly object gate = new object();
        LocalKeys local;
        bool permanent;
        DateTime retry;
        TimeSpan delay = TimeSpan.FromMinutes(1);
        public string UnavailableReason { get; private set; }
        public bool Available { get { lock (gate) return local != null; } }
        public bool Verified { get { lock (gate) return local != null && local.Verified; } }
        public string StorePath { get { lock (gate) return local == null ? "" : local.StorePath; } }
        // One line for the settings page; empty for a verified interface.
        public string Notice {
            get {
                lock (gate) {
                    if (local == null) return UnavailableReason;
                    return local.Verified ? "" : "此版本 QQ 音乐的本地接口尚未实测，已通过签名和接口检查自动启用，每首歌转换后都会校验。";
                }
            }
        }
        public DownloadKeys(string client, string storeFolder = null) { this.client = client; this.storeFolder = storeFolder; UnavailableReason = ""; Open(); }
        void Open() {
            try { local = new LocalKeys(client, storeFolder); UnavailableReason = ""; }
            catch (Exception e) {
                if (!(e is NotSupportedException) && !(e is IOException) && !(e is UnauthorizedAccessException) && !(e is ArgumentException) && !(e is InvalidDataException) && !(e is Win32Exception) && !(e is InvalidOperationException)) throw;
                UnavailableReason = e.Message;
                // An unsupported interface stays off until the next client start;
                // transient failures (busy files, a slow child) back off and retry.
                permanent = e is NotSupportedException || e is InvalidDataException;
                retry = DateTime.UtcNow + delay; delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(30).Ticks));
            }
        }
        public Dictionary<string,string> Read() {
            LocalKeys current;
            lock (gate) { if (local == null && !permanent && DateTime.UtcNow >= retry) Open(); current = local; }
            return current == null ? new Dictionary<string,string>(StringComparer.Ordinal) : current.Read();
        }
        public void Dispose() { lock (gate) if (local != null) local.Dispose(); }
    }
}
