using System;
using System.IO;
using System.Collections.Generic;

namespace QqmBetterDownload {
    // Old QTag/V1 downloads carry their own key, so an unrecognized client must
    // not disable the whole queue. New musicex jobs remain pending for adaptation.
    public sealed class DownloadKeys : IDisposable {
        readonly LocalKeys local;
        public readonly string UnavailableReason = "";
        public bool Available { get { return local != null; } }
        public string StorePath { get { return local == null ? "" : local.StorePath; } }
        public DownloadKeys(string client) {
            try { local = new LocalKeys(client); }
            catch (NotSupportedException e) { UnavailableReason = e.Message; }
            catch (IOException e) { UnavailableReason = e.Message; }
            catch (UnauthorizedAccessException e) { UnavailableReason = e.Message; }
            catch (ArgumentException e) { UnavailableReason = e.Message; }
        }
        public Dictionary<string,string> Read() { return local == null ? new Dictionary<string,string>(StringComparer.Ordinal) : local.Read(); }
        public void Dispose() { if (local != null) local.Dispose(); }
    }
}
