using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace QqmBetterDownload {
    public sealed class MusicEx {
        public long AudioLength;
        public string Resource = "", EmbeddedKey = "";
    }
    public static class AudioFile {
        static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".mflac", ".mflac0", ".mflach", ".mgg", ".mgg0", ".mgg1", ".mggl", ".mmp4"
        };
        public static bool IsEncrypted(string path) { return Extensions.Contains(Path.GetExtension(path)); }
        public static MusicEx Footer(Stream file) {
            if (file.Length < 20) throw new InvalidDataException("文件尚未完成。");
            file.Position = file.Length - 16; byte[] tail = Binary.Read(file, 16);
            if (Encoding.ASCII.GetString(tail, 8, 8) != "musicex\0") return LegacyFooter(file, tail);
            if (Binary.U32(tail, 4) != 1) throw new NotSupportedException("此 musicex 文件尾版本尚未支持。");
            uint size = Binary.U32(tail, 0);
            if (size < 192 || size > 4096 || size >= file.Length) throw new InvalidDataException("文件尾长度无效。");
            file.Position = file.Length - size; byte[] footer = Binary.Read(file, (int)size);
            string resource = new UnicodeEncoding(false, false, true).GetString(footer, 0x48, 68).Split('\0')[0];
            if (!IsEncrypted(resource) || resource != Path.GetFileName(resource) || resource.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("文件尾中的歌曲资源标识无效。");
            return new MusicEx { AudioLength = file.Length - size, Resource = resource };
        }
        static MusicEx LegacyFooter(Stream file, byte[] tail) {
            bool qtag = Encoding.ASCII.GetString(tail, 12, 4) == "QTag";
            uint size = qtag ? ((uint)tail[8] << 24 | (uint)tail[9] << 16 | (uint)tail[10] << 8 | tail[11]) : Binary.U32(tail, 12);
            int trailer = qtag ? 8 : 4;
            if (size < 16 || size > (qtag ? 8192 : 1024) || size >= file.Length - trailer) throw new InvalidDataException("未找到完整的 musicex、QTag 或旧版文件内密钥。");
            file.Position = file.Length - trailer - size;
            string metadata = new UTF8Encoding(false, true).GetString(Binary.Read(file, (int)size));
            string key = metadata;
            if (qtag) {
                string[] fields = metadata.Split(','); long songId;
                if (fields.Length != 3 || !Int64.TryParse(fields[1], out songId) || songId < 0) throw new InvalidDataException("QTag 元数据无效。");
                key = fields[0];
            }
            if (key.Length < 16 || key.Length > 6000 || key.Any(c => !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '+' && c != '/' && c != '=')) throw new InvalidDataException("文件内密钥格式无效。");
            return new MusicEx { AudioLength = file.Length - trailer - size, EmbeddedKey = key };
        }
        public static string Format(byte[] head) {
            if (head.Length >= 4 && Encoding.ASCII.GetString(head, 0, 4) == "fLaC") return "flac";
            if (head.Length >= 4 && Encoding.ASCII.GetString(head, 0, 4) == "OggS") return "ogg";
            if (head.Length >= 8 && Encoding.ASCII.GetString(head, 4, 4) == "ftyp") throw new NotSupportedException("已识别 M4A；当前原型暂未实现其完整性校验，因此保留源文件并跳过。");
            throw new InvalidDataException("解密后的音频头无效，密钥可能不匹配。");
        }
        public static void Validate(Stream file, string format, CancellationToken cancel) {
            file.Position = 0;
            if (format == "flac") ValidateFlac(file, cancel);
            else if (format == "ogg") ValidateOgg(file, cancel);
            else throw new NotSupportedException("尚未支持此音频格式。");
        }
        static readonly ushort[] FlacTable = FlacCrcTable();
        static ushort[] FlacCrcTable() {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++) { int c = i << 8; for (int j = 0; j < 8; j++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1; table[i] = (ushort)c; }
            return table;
        }
        static byte Crc8(byte[] bytes, int count) {
            int c = 0;
            for (int i = 0; i < count; i++) { c ^= bytes[i]; for (int j = 0; j < 8; j++) c = (c & 128) != 0 ? (c << 1) ^ 7 : c << 1; c &= 255; }
            return (byte)c;
        }
        sealed class Frame { public long Number; public int Samples; public bool Variable; }
        static Frame ReadFrame(Stream f) {
            long start = f.Position;
            try {
                byte[] h = Binary.Read(f, (int)Math.Min(24, f.Length - start));
                if (h.Length < 6 || h[0] != 255 || (h[1] & 254) != 248 || (h[3] & 1) != 0 || (h[3] >> 4) > 10 || (h[2] >> 4) == 0 || (h[2] & 15) == 15) return null;
                int p = 4, first = h[p++], extra = 0; long number;
                if (first < 128) number = first;
                else {
                    int mask = 128;
                    while ((first & mask) != 0 && mask > 0) { extra++; mask >>= 1; }
                    if (extra < 2 || extra > 7) return null;
                    number = first & (mask - 1);
                    for (int n = 1; n < extra; n++) { if (p >= h.Length || (h[p] & 192) != 128) return null; number = (number << 6) | (uint)(h[p++] & 63); }
                }
                int block = h[2] >> 4, samples;
                if (block == 1) samples = 192;
                else if (block <= 5) samples = 576 << (block - 2);
                else if (block == 6) { if (p >= h.Length) return null; samples = h[p++] + 1; }
                else if (block == 7) { if (p + 1 >= h.Length) return null; samples = (h[p] << 8 | h[p + 1]) + 1; p += 2; }
                else samples = 256 << (block - 8);
                int rate = h[2] & 15; p += rate == 12 ? 1 : (rate == 13 || rate == 14 ? 2 : 0);
                if (p >= h.Length || Crc8(h, p + 1) != 0) return null;
                return new Frame { Number = number, Samples = samples, Variable = (h[1] & 1) != 0 };
            } finally { f.Position = start; }
        }
        // Validate every frame CRC16 and the STREAMINFO sample count. This catches
        // unfinished/preallocated downloads even if they already have a footer.
        static void ValidateFlac(Stream f, CancellationToken cancel) {
            if (Encoding.ASCII.GetString(Binary.Read(f, 4)) != "fLaC") throw new InvalidDataException("FLAC 头部无效。");
            bool last = false, first = true; long totalSamples = 0; int blocks = 0;
            while (!last) {
                cancel.ThrowIfCancellationRequested(); byte[] h = Binary.Read(f, 4);
                int type = h[0] & 127, size = h[1] << 16 | h[2] << 8 | h[3]; last = (h[0] & 128) != 0;
                if (++blocks > 10000 || type == 127 || size > f.Length - f.Position || (first && (type != 0 || size != 34))) throw new InvalidDataException("FLAC 元数据不完整。");
                if (first) { byte[] info = Binary.Read(f, 34); totalSamples = ((long)info[13] & 15) << 32 | (long)info[14] << 24 | (long)info[15] << 16 | (long)info[16] << 8 | info[17]; first = false; }
                else { if (type == 0) throw new InvalidDataException("重复的 FLAC STREAMINFO。"); f.Position += size; }
            }
            if (totalSamples == 0) throw new InvalidDataException("FLAC 未提供样本总数，无法确认下载完整。");
            Frame current = ReadFrame(f);
            if (current == null || current.Number != 0) throw new InvalidDataException("FLAC 首帧无效。");
            long samples = 0, frames = 0, frameBytes = 0, end = f.Length, position = f.Position; ushort crc = 0;
            while (position < end) {
                int value = f.ReadByte(); if (value < 0) throw new EndOfStreamException(); position++;
                crc = (ushort)((crc << 8) ^ FlacTable[((crc >> 8) ^ value) & 255]); frameBytes++;
                if ((position & 0x3ffff) == 0) cancel.ThrowIfCancellationRequested();
                if (crc != 0 || frameBytes < 8) continue;
                if (position == end) { samples += current.Samples; frames++; break; }
                // A zero CRC alone isn't a frame boundary: require a valid next
                // header and its expected frame/sample number as well.
                int a = f.ReadByte(), b = f.ReadByte(); f.Position = position;
                if (a != 255 || (b & 254) != 248) continue;
                Frame next = ReadFrame(f);
                if (next == null || next.Variable != current.Variable || next.Number != (next.Variable ? samples + current.Samples : frames + 1)) continue;
                samples += current.Samples; frames++; current = next; frameBytes = 0;
            }
            if (crc != 0 || frames == 0 || samples != totalSamples) throw new InvalidDataException("FLAC 帧校验或样本总数不符，文件可能未完成或已损坏。");
        }
        sealed class OggStream { public uint Sequence; public bool Ended; public bool Continuation; }
        static void ValidateOgg(Stream f, CancellationToken cancel) {
            var streams = new Dictionary<uint, OggStream>(); int pages = 0;
            while (f.Position < f.Length) {
                cancel.ThrowIfCancellationRequested(); byte[] h = Binary.Read(f, 27);
                if (Encoding.ASCII.GetString(h, 0, 4) != "OggS" || h[4] != 0 || (h[5] & ~7) != 0) throw new InvalidDataException("Ogg 页面头无效。");
                byte[] lacing = Binary.Read(f, h[26]); int size = lacing.Sum(b => (int)b);
                byte[] body = Binary.Read(f, size); uint serial = Binary.U32(h, 14), sequence = Binary.U32(h, 18), expected = Binary.U32(h, 22);
                bool bos = (h[5] & 2) != 0, eos = (h[5] & 4) != 0, continuation = (h[5] & 1) != 0;
                OggStream stream;
                if (!streams.TryGetValue(serial, out stream)) {
                    if (!bos || continuation || sequence != 0 || streams.Count > 64) throw new InvalidDataException("Ogg 缺少起始页面。");
                    stream = new OggStream(); streams.Add(serial, stream);
                } else if (stream.Ended || bos || sequence != unchecked(stream.Sequence + 1) || continuation != stream.Continuation) throw new InvalidDataException("Ogg 页面顺序不完整。");
                Array.Clear(h, 22, 4); uint crc = 0;
                foreach (byte b in h.Concat(lacing).Concat(body)) { crc ^= (uint)b << 24; for (int i = 0; i < 8; i++) crc = (crc & 0x80000000) == 0 ? crc << 1 : (crc << 1) ^ 0x04c11db7; }
                if (crc != expected) throw new InvalidDataException("Ogg 页面校验失败。");
                stream.Sequence = sequence; stream.Ended = eos; stream.Continuation = lacing.Length != 0 && lacing[lacing.Length - 1] == 255; pages++;
                if (eos && stream.Continuation) throw new InvalidDataException("Ogg 末尾音频包不完整。");
            }
            if (pages == 0 || streams.Values.Any(s => !s.Ended)) throw new InvalidDataException("Ogg 缺少结束页面。");
        }
    }
}
