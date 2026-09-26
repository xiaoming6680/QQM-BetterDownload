using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace QqmBetterDownload {
    public sealed class Receipt {
        public string SourceHash = "", OutputHash = "", Output = "", Profile = "";
    }
    public sealed class ConversionResult {
        public string Source, Output, Format;
        public bool Skipped;
        public string Warning = "";
    }
    public static class SafePath {
        public static string Full(string path) {
            string full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }
        public static bool Inside(string root, string path) {
            return Full(path).StartsWith(Full(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        public static void NoLinks(string path) {
            string current = Full(path);
            if (current.StartsWith("\\\\") || current.Substring(2).Contains(":")) throw new IOException("当前仅支持本机磁盘上的普通文件路径。");
            while (!String.IsNullOrEmpty(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("路径包含文件或目录链接，已跳过。");
                current = Path.GetDirectoryName(current);
            }
        }
        public static bool Candidate(string root, string source) {
            try { return AudioFile.IsEncrypted(source) && Inside(root, source) && !Inside(Path.Combine(root, "unlock"), source); }
            catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; }
        }
        public static IEnumerable<string> Enumerate(string root) {
            NoLinks(root);
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count != 0) {
                string dir = stack.Pop();
                string[] files, dirs;
                try { NoLinks(dir); files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
                catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                foreach (string file in files) if (Candidate(root, file)) yield return file;
                foreach (string child in dirs) {
                    if (Full(child).Equals(Full(Path.Combine(root, "unlock")), StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) stack.Push(child);
                }
            }
        }
    }
    public static class StateFile {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public static T Read<T>(string path) where T : new() {
            if (!File.Exists(path)) return new T();
            return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }
        public static void Write(string path, object value) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public sealed class Converter {
        readonly Dictionary<string, Receipt> receipts;
        readonly string state;
        readonly string cacheRoot;
        public Converter(string statePath, string coverCache = "") {
            state = statePath; cacheRoot = coverCache ?? "";
            // Preserve a damaged receipt file for inspection; never trust it to
            // authorize overwriting any output. Outputs are always create-new.
            try { receipts = new Dictionary<string, Receipt>(StateFile.Read<Dictionary<string, Receipt>>(state), StringComparer.OrdinalIgnoreCase); }
            catch { receipts = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase); }
        }
        public static string Hash(Stream stream) {
            stream.Position = 0;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        public ConversionResult ConvertFile(string root, string path, IDictionary<string, string> keys, Action<int, string> progress, CancellationToken cancel, Action<TrackInfo> identified = null) {
            root = SafePath.Full(root); path = SafePath.Full(path);
            if (!SafePath.Candidate(root, path)) throw new InvalidDataException("源文件必须是下载目录中的受支持加密文件。");
            SafePath.NoLinks(path); SafePath.NoLinks(root);
            string temp = null;
            try {
                // Deny writes and deletes throughout extraction and validation.
                // Playback readers remain allowed. A downloader holding a write
                // handle makes this open fail, and the queue retries later.
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 262144)) {
                    cancel.ThrowIfCancellationRequested(); MusicEx footer = AudioFile.Footer(source);
                    string ekey = footer.EmbeddedKey;
                    if (String.IsNullOrEmpty(ekey) && !keys.TryGetValue(footer.Resource, out ekey)) throw new KeyNotFoundException("本机暂未取得这首下载的密钥；等待 QQ 音乐写入或客户端适配更新。");
                    string sourceHash = Hash(source); Receipt receipt;
                    if (receipts.TryGetValue(path, out receipt) && receipt != null && receipt.SourceHash == sourceHash && (receipt.Profile ?? "") == cacheRoot && !String.IsNullOrEmpty(receipt.Output) && SafePath.Inside(Path.Combine(root, "unlock"), receipt.Output) && File.Exists(receipt.Output)) {
                        SafePath.NoLinks(receipt.Output);
                        using (var prior = new FileStream(receipt.Output, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                            if (Hash(prior) == receipt.OutputHash) return new ConversionResult { Source = path, Output = receipt.Output, Format = Path.GetExtension(receipt.Output).TrimStart('.'), Skipped = true };
                        }
                    }
                    using (var cipher = new Qmc(ekey)) {
                        source.Position = 0; byte[] head = Binary.Read(source, (int)Math.Min(64, footer.AudioLength)); cipher.Transform(head, head.Length, 0);
                        string format = AudioFile.Format(head);
                        if (identified != null) identified(TrackInfo.ReadEncrypted(source, cipher, footer.AudioLength, format, cacheRoot));
                        string relative = path.Substring(root.TrimEnd('\\').Length + 1);
                        string target = Path.ChangeExtension(Path.Combine(root, "unlock", relative), format);
                        SafePath.NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)); SafePath.NoLinks(target);
                        temp = Path.Combine(Path.GetDirectoryName(target), ".qqm-" + Guid.NewGuid().ToString("N") + ".part");
                        source.Position = 0;
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 262144)) {
                            byte[] buffer = new byte[262144]; long done = 0; int last = -1;
                            while (done < footer.AudioLength) {
                                cancel.ThrowIfCancellationRequested(); int wanted = (int)Math.Min(buffer.Length, footer.AudioLength - done), n = source.Read(buffer, 0, wanted);
                                if (n == 0) throw new EndOfStreamException("音频数据不完整。");
                                cipher.Transform(buffer, n, done); output.Write(buffer, 0, n); done += n;
                                int percent = (int)(done * 85 / footer.AudioLength);
                                if (percent != last) { last = percent; if (progress != null) progress(percent, "正在转换 " + format.ToUpperInvariant()); }
                            }
                            output.Flush(); cancel.ThrowIfCancellationRequested();
                            if (progress != null) progress(90, "正在校验完整音频");
                            AudioFile.Validate(output, format, cancel);
                            receipt = new Receipt { SourceHash = sourceHash, Profile = cacheRoot };
                            output.Flush(true);
                        }
                        if (progress != null) progress(95, "正在检查本地封面");
                        string warning = LocalArtwork.Enrich(temp, format, cacheRoot, cancel);
                        using (var finished = File.OpenRead(temp)) receipt.OutputHash = Hash(finished);
                        cancel.ThrowIfCancellationRequested(); SafePath.NoLinks(target);
                        string published = Publish(temp, target); temp = null; receipt.Output = published; receipts[path] = receipt;
                        try { StateFile.Write(state, receipts); }
                        catch (Exception e) { if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw; warning = "音频已保存，但转换记录暂未写入。"; }
                        if (progress != null) progress(100, "已完成");
                        return new ConversionResult { Source = path, Output = published, Format = format, Warning = warning };
                    }
                }
            } finally { if (temp != null && File.Exists(temp)) File.Delete(temp); }
        }
        static string Publish(string temp, string target) {
            for (int n = 1; n < 10000; n++) {
                string candidate = n == 1 ? target : Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + " (" + n + ")" + Path.GetExtension(target));
                if (File.Exists(candidate)) continue;
                SafePath.NoLinks(candidate);
                try { File.Move(temp, candidate); return candidate; }
                catch (IOException) { if (!File.Exists(candidate)) throw; }
            }
            throw new IOException("同名输出文件过多。");
        }
    }
}
