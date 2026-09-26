using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace QqmBetterDownload {
    // Learn locations from successful client downloads and our own receipts.
    // No QQ account settings or machine-wide recursive search is needed.
    public sealed class AutomaticPaths {
        readonly string state;
        readonly List<string> roots = new List<string>();
        readonly object gate = new object();
        public AutomaticPaths(string data, string defaultRoot, string previousRoot = "") {
            state = Path.Combine(data, "detected-paths.json");
            try { foreach (var root in StateFile.Read<List<string>>(state).Take(64)) Add(root, false); } catch (IOException) { } catch (ArgumentException) { }
            Discover(defaultRoot); if (!String.IsNullOrEmpty(previousRoot)) Discover(previousRoot);
            try { foreach (string source in StateFile.Read<Dictionary<string, Receipt>>(Path.Combine(data, "receipts.json")).Keys) Learn(source); } catch (IOException) { } catch (ArgumentException) { }
        }
        public string[] Roots { get { lock (gate) return roots.Where(Directory.Exists).ToArray(); } }
        void Discover(string folder) {
            if (String.IsNullOrWhiteSpace(folder)) return;
            try { Add(folder, false); Add(Path.Combine(folder, "VipSongsDownload"), false); } catch (ArgumentException) { }
        }
        bool Add(string folder, bool persist) {
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || !Path.GetFileName(SafePath.Full(folder)).Equals("VipSongsDownload", StringComparison.OrdinalIgnoreCase)) return false;
            try { SafePath.NoLinks(folder); } catch (IOException) { return false; }
            folder = SafePath.Full(folder);
            lock (gate) {
                if (roots.Contains(folder, StringComparer.OrdinalIgnoreCase)) return false;
                roots.Insert(0, folder); if (roots.Count > 64) roots.RemoveAt(roots.Count - 1);
                if (persist) StateFile.Write(state, roots);
                return true;
            }
        }
        public bool Learn(string source) { try { return Add(EventHost.DownloadRoot(source), true); } catch (InvalidDataException) { return false; } catch (ArgumentException) { return false; } }
    }
}
