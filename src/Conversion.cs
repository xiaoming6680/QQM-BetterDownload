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
        // The source's size and write time when converted (0 in older receipts).
        public long SourceSize, SourceTime;
    }
    public sealed class ConversionResult {
        public string Source, Output, Format;
        public bool Skipped, Lyrics;
        public string Warning = "";
    }
    public static class SafePath {
        // Antivirus scanners and indexers briefly open a file we have just written,
        // which makes replacing, moving or deleting it fail. Wait them out, up to
        // about six seconds; anything else fails at once.
        public static void Patiently(Action action, CancellationToken cancel, Func<bool> hopeless = null) {
            for (int delay = 100; ; delay *= 2) {
                try { action(); return; }
                catch (IOException) { if (delay > 3200 || (hopeless != null && hopeless())) throw; }
                catch (UnauthorizedAccessException) { if (delay > 3200) throw; }
                if (cancel.WaitHandle.WaitOne(delay)) cancel.ThrowIfCancellationRequested();
            }
        }
        // Cleanup of our own temporary file never fails a conversion.
        public static void Discard(string path) {
            try { if (File.Exists(path)) Patiently(delegate { File.Delete(path); }, CancellationToken.None); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public static string Full(string path) {
            // QQ Music can report a normal local download as \\?\X:\... .
            // .NET Framework's legacy path handling rejects that prefix.
            // Accept only absolute drive paths, never device or UNC namespaces.
            if (path != null && path.StartsWith(@"\\?\", StringComparison.Ordinal)) {
                if (path.Length < 7 || !((path[4] >= 'A' && path[4] <= 'Z') || (path[4] >= 'a' && path[4] <= 'z')) || path[5] != ':' || path[6] != '\\')
                    throw new IOException("当前仅支持本机磁盘上的普通文件路径。");
                path = path.Substring(4);
            }
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
                SafePath.Patiently(delegate { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }, CancellationToken.None);
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
        // Set from the settings page; each song reads them once.
        public volatile bool Lyrics, LyricsFile;
        public static string Hash(Stream stream) {
            stream.Position = 0;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        // Converted before, output still there, source unchanged by size and time:
        // a scan need not queue it. Converting still decides by content hash.
        public bool Done(string path) {
            try {
                path = SafePath.Full(path); Receipt receipt; var info = new FileInfo(path);
                if (!receipts.TryGetValue(path, out receipt) || receipt == null || !info.Exists || (receipt.Profile ?? "") != cacheRoot || String.IsNullOrEmpty(receipt.Output) || !File.Exists(receipt.Output)) return false;
                return receipt.SourceSize == 0 || (receipt.SourceSize == info.Length && receipt.SourceTime == info.LastWriteTimeUtc.Ticks);
            } catch (Exception e) {
                if (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) return false;
                throw;
            }
        }
        // QQ Music saves lyrics beside the download when "同时下载歌词" is on.
        static string ReadLyrics(string path) {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 1024 * 1024) return null;
            SafePath.NoLinks(path); byte[] bytes = File.ReadAllBytes(path); string text;
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else {
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { text = Encoding.GetEncoding(54936).GetString(bytes); }
            }
            text = text.TrimStart('﻿').Trim();
            return text.Length == 0 ? null : text;
        }
        public ConversionResult ConvertFile(string root, string path, IDictionary<string, string> keys, Action<int, string> progress, CancellationToken cancel, Action<TrackInfo> identified = null) {
            root = SafePath.Full(root); path = SafePath.Full(path);
            if (!SafePath.Candidate(root, path)) throw new InvalidDataException("源文件必须是下载目录中的受支持加密文件。");
            SafePath.NoLinks(path); SafePath.NoLinks(root);
            string temp = null; bool lyrics = Lyrics, lyricsFile = LyricsFile;
            try {
                // Deny writes and deletes throughout extraction and validation.
                // Playback readers remain allowed. A downloader holding a write
                // handle makes this open fail, and the queue retries later.
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 262144)) {
                    cancel.ThrowIfCancellationRequested(); MusicEx footer = AudioFile.Footer(source);
                    string ekey = footer.EmbeddedKey;
                    if (String.IsNullOrEmpty(ekey) && !keys.TryGetValue(footer.Resource, out ekey)) throw new KeyNotFoundException("本机暂未取得这首下载的密钥；等待 QQ 音乐写入或客户端适配更新。");
                    long sourceTime = File.GetLastWriteTimeUtc(path).Ticks;
                    string sourceHash = Hash(source); Receipt receipt;
                    if (receipts.TryGetValue(path, out receipt) && receipt != null && receipt.SourceHash == sourceHash && (receipt.Profile ?? "") == cacheRoot && !String.IsNullOrEmpty(receipt.Output) && SafePath.Inside(Path.Combine(root, "unlock"), receipt.Output) && File.Exists(receipt.Output)) {
                        SafePath.NoLinks(receipt.Output);
                        using (var prior = new FileStream(receipt.Output, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                            if (Hash(prior) == receipt.OutputHash) {
                                // Older receipts lack size and time; add them so scans can skip it cheaply.
                                if (receipt.SourceSize != source.Length || receipt.SourceTime != sourceTime) {
                                    receipt.SourceSize = source.Length; receipt.SourceTime = sourceTime;
                                    try { StateFile.Write(state, receipts); } catch (Exception e) { if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw; }
                                }
                                return new ConversionResult { Source = path, Output = receipt.Output, Format = Path.GetExtension(receipt.Output).TrimStart('.'), Skipped = true };
                            }
                        }
                    }
                    string lrc = Path.ChangeExtension(path, ".lrc"), text = null;
                    if (lyrics || lyricsFile) { try { text = ReadLyrics(lrc); } catch (Exception e) { if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw; } }
                    using (var cipher = new Qmc(ekey)) {
                        source.Position = 0; byte[] head = Binary.Read(source, (int)Math.Min(64, footer.AudioLength)); cipher.Transform(head, head.Length, 0);
                        string format = AudioFile.Format(head);
                        if (identified != null) identified(TrackInfo.ReadEncrypted(source, cipher, footer.AudioLength, format, cacheRoot));
                        string relative = path.Substring(root.TrimEnd('\\').Length + 1);
                        string target = Path.ChangeExtension(Path.Combine(root, "unlock", relative), format);
                        SafePath.NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)); SafePath.NoLinks(target);
                        // No receipt (plugin data deleted) but this audio already in unlock:
                        // keep that file instead of writing "name (2)".
                        if (receipt == null && File.Exists(target) && Adopt(target, head, format, cancel)) {
                            receipt = new Receipt { SourceHash = sourceHash, Profile = cacheRoot, SourceSize = source.Length, SourceTime = sourceTime, Output = target };
                            using (var prior = File.OpenRead(target)) receipt.OutputHash = Hash(prior);
                            receipts[path] = receipt;
                            try { StateFile.Write(state, receipts); } catch (Exception e) { if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw; }
                            return new ConversionResult { Source = path, Output = target, Format = format, Skipped = true };
                        }
                        temp = Path.Combine(Path.GetDirectoryName(target), ".qqm-" + Guid.NewGuid().ToString("N") + ".part");
                        source.Position = 0; AudioCheck check;
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
                            check = AudioFile.Validate(output, format, cancel);
                            // The client's trailer is not audio; the output ends with the stream.
                            if (check.Trailer > 0) output.SetLength(output.Length - check.Trailer);
                            receipt = new Receipt { SourceHash = sourceHash, Profile = cacheRoot, SourceSize = source.Length, SourceTime = sourceTime };
                            output.Flush(true);
                        }
                        if (progress != null) progress(95, text != null && lyrics ? "正在写入封面与歌词" : "正在检查本地封面");
                        bool wroteLyrics;
                        string warning = LocalArtwork.Enrich(temp, format, cacheRoot, cancel, check.Digest, lyrics ? text : null, out wroteLyrics);
                        using (var finished = File.OpenRead(temp)) receipt.OutputHash = Hash(finished);
                        cancel.ThrowIfCancellationRequested(); SafePath.NoLinks(target);
                        string published = Publish(temp, target, cancel); temp = null; receipt.Output = published; receipts[path] = receipt;
                        try { StateFile.Write(state, receipts); }
                        catch (Exception e) { if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw; warning = "音频已保存，但转换记录暂未写入。"; }
                        // The .lrc keeps the output's name, so players that only read files find it.
                        if (lyricsFile && text != null) {
                            string copy = Path.ChangeExtension(published, ".lrc");
                            try { SafePath.NoLinks(copy); if (!File.Exists(copy)) File.Copy(lrc, copy, false); }
                            catch (Exception e) { if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw; if (warning.Length == 0) warning = "已完成；歌词文件未能保存。"; }
                        }
                        if (progress != null) progress(100, "已完成");
                        return new ConversionResult { Source = path, Output = published, Format = format, Warning = warning, Lyrics = wroteLyrics };
                    }
                }
            } finally { if (temp != null) SafePath.Discard(temp); }
        }
        // The existing output holds the same audio: FLAC by its STREAMINFO (sample
        // count and audio MD5), OGG by its unchanged first page (stream serial and
        // CRC). It must also pass the full check. MP4 is always converted again.
        static bool Adopt(string output, byte[] head, string format, CancellationToken cancel) {
            int from, count;
            if (format == "flac" && head.Length >= 42) { from = 8; count = 34; }
            else if (format == "ogg" && head.Length > 27 && 27 + head[26] <= head.Length) {
                count = 27 + head[26]; for (int i = 0; i < head[26]; i++) count += head[27 + i];
                if (count > head.Length) return false; from = 0;
            } else return false;
            try {
                SafePath.NoLinks(output);
                using (var file = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (file.Length < from + count) return false;
                    byte[] existing = Binary.Read(file, from + count);
                    if (format == "flac" && (Encoding.ASCII.GetString(existing, 0, 4) != "fLaC" || (existing[4] & 127) != 0)) return false;
                    for (int i = from; i < from + count; i++) if (existing[i] != head[i]) return false;
                    return AudioFile.Validate(file, format, cancel).Trailer == 0;
                }
            } catch (Exception e) {
                if (e is OperationCanceledException) throw;
                return false;
            }
        }
        static string Publish(string temp, string target, CancellationToken cancel) {
            for (int n = 1; n < 10000; n++) {
                string candidate = n == 1 ? target : Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + " (" + n + ")" + Path.GetExtension(target));
                if (File.Exists(candidate)) continue;
                SafePath.NoLinks(candidate);
                // A name taken meanwhile moves on to the next; a scanner holding
                // the new file is waited out.
                try { SafePath.Patiently(delegate { File.Move(temp, candidate); }, cancel, () => File.Exists(candidate)); return candidate; }
                catch (IOException) { if (!File.Exists(candidate)) throw; }
            }
            throw new IOException("同名输出文件过多。");
        }
    }
}
