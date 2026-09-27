using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace QqmBetterDownload {
    // Completeness check for decrypted MP4 audio (QQ Music's .mmp4, used for
    // Dolby Atmos). Every box must fit the file and every sample of every track
    // must lie inside the media data; Dolby frames are also checked against their
    // own CRC. MP4 samples otherwise carry no checksum, so the result is a digest
    // of the sample data: rewriting the tags must leave it unchanged.
    internal static class Mp4Audio {
        const int MoovLimit = 64 * 1024 * 1024, SampleLimit = 16 * 1024 * 1024;
        struct Box { public string Type; public long Body, End; }
        sealed class Track {
            public string Handler = "", Codec = "";
            public long[] Chunks;
            public uint[] FirstChunk, PerChunk;
            public long Samples;
            public uint FixedSize;
            public uint[] Sizes;
            public long Size(long sample) { return FixedSize != 0 ? FixedSize : Sizes[sample]; }
        }
        static InvalidDataException Broken(string detail) { return new InvalidDataException("MP4 " + detail + "，文件可能未完成或已损坏。"); }
        static uint U32(byte[] b, long p) { return (uint)b[p] << 24 | (uint)b[p + 1] << 16 | (uint)b[p + 2] << 8 | b[p + 3]; }
        static ulong U64(byte[] b, long p) { return (ulong)U32(b, p) << 32 | U32(b, p + 4); }
        static string Name(byte[] b, long p) { return Encoding.GetEncoding(28591).GetString(b, (int)p, 4); }
        public static string Validate(Stream f, CancellationToken cancel) {
            long length = f.Length, pos = 0; byte[] moov = null; bool fragments = false;
            var media = new List<long[]>();
            while (pos < length) {
                cancel.ThrowIfCancellationRequested();
                if (length - pos < 8) throw Broken("文件末尾不完整");
                f.Position = pos; byte[] h = Binary.Read(f, (int)Math.Min(16, length - pos));
                long size = U32(h, 0), header = 8;
                if (size == 1) {
                    if (h.Length < 16 || U64(h, 8) > (ulong)(length - pos)) throw Broken("盒长度越界");
                    size = (long)U64(h, 8); header = 16;
                } else if (size == 0) size = length - pos;
                if (size < header || size > length - pos) throw Broken("盒长度越界");
                if (h.Skip(4).Take(4).Any(c => c < 0x20 || c > 0x7e)) throw Broken("盒类型无效");
                string type = Name(h, 4);
                if (pos == 0 && type != "ftyp") throw Broken("缺少文件类型头");
                if (type == "moov") {
                    if (moov != null || size - header > MoovLimit) throw Broken("音频索引无效");
                    f.Position = pos + header; moov = Binary.Read(f, (int)(size - header));
                } else if (type == "mdat") media.Add(new[] { pos + header, pos + size });
                else if (type == "moof") fragments = true;
                pos += size;
            }
            if (moov == null || media.Count == 0) throw Broken("缺少音频索引或音频数据");
            var top = Children(moov, 0, moov.Length);
            if (fragments || top.Any(b => b.Type == "mvex")) throw new NotSupportedException("暂不支持分片 MP4，已保留源文件。");
            var tracks = top.Where(b => b.Type == "trak").Select(b => Parse(moov, b)).ToList();
            if (!tracks.Any(t => t.Handler == "soun" && t.Samples > 0)) throw Broken("没有可用的音轨");
            using (var sha = SHA256.Create()) {
                byte[] buffer = new byte[65536];
                foreach (var track in tracks) Walk(f, track, media, sha, ref buffer, cancel);
                sha.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        static List<Box> Children(byte[] b, long start, long end) {
            var list = new List<Box>(); long p = start;
            while (p < end) {
                if (end - p < 8) {
                    // A container may end with a 32-bit zero terminator.
                    for (long i = p; i < end; i++) if (b[i] != 0) throw Broken("音频索引不完整");
                    break;
                }
                long size = U32(b, p), header = 8;
                if (size == 1) {
                    if (end - p < 16 || U64(b, p + 8) > (ulong)(end - p)) throw Broken("音频索引越界");
                    size = (long)U64(b, p + 8); header = 16;
                } else if (size == 0) size = end - p;
                if (size < header || size > end - p) throw Broken("音频索引越界");
                list.Add(new Box { Type = Name(b, p + 4), Body = p + header, End = p + size });
                p += size;
            }
            return list;
        }
        static Box Child(byte[] b, Box parent, string type) {
            foreach (var box in Children(b, parent.Body, parent.End)) if (box.Type == type) return box;
            throw Broken("音轨缺少 " + type);
        }
        static bool Has(byte[] b, Box parent, string type) { return Children(b, parent.Body, parent.End).Any(x => x.Type == type); }
        static void Need(Box box, long bytes) { if (bytes < 0 || box.End - box.Body < bytes) throw Broken(box.Type + " 表不完整"); }
        static Track Parse(byte[] b, Box trak) {
            var t = new Track();
            Box mdia = Child(b, trak, "mdia"), minf = Child(b, mdia, "minf"), stbl = Child(b, minf, "stbl"), hdlr = Child(b, mdia, "hdlr");
            Need(hdlr, 12); t.Handler = Name(b, hdlr.Body + 8);
            Box stsd = Child(b, stbl, "stsd"); Need(stsd, 8);
            var entries = Children(b, stsd.Body + 8, stsd.End);
            if (U32(b, stsd.Body + 4) == 0 || entries.Count == 0) throw Broken("音轨缺少编码描述");
            t.Codec = entries[0].Type;
            if (t.Handler == "soun" && t.Codec.StartsWith("enc", StringComparison.Ordinal)) throw new NotSupportedException("音频带有额外的版权保护，无法转换，已保留源文件。");
            Box stts = Child(b, stbl, "stts"); Need(stts, 8);
            long timed = 0, n = U32(b, stts.Body + 4); Need(stts, 8 + n * 8);
            for (long i = 0; i < n; i++) timed += U32(b, stts.Body + 8 + i * 8);
            if (Has(b, stbl, "stsz")) {
                Box stsz = Child(b, stbl, "stsz"); Need(stsz, 12);
                t.FixedSize = U32(b, stsz.Body + 4); t.Samples = U32(b, stsz.Body + 8);
                if (t.FixedSize == 0) {
                    Need(stsz, 12 + t.Samples * 4); t.Sizes = new uint[t.Samples];
                    for (long i = 0; i < t.Samples; i++) t.Sizes[i] = U32(b, stsz.Body + 12 + i * 4);
                }
            } else {
                Box stz2 = Child(b, stbl, "stz2"); Need(stz2, 12);
                int field = b[stz2.Body + 7]; t.Samples = U32(b, stz2.Body + 8);
                if (field != 4 && field != 8 && field != 16) throw Broken("stz2 表无效");
                Need(stz2, 12 + (t.Samples * field + 7) / 8); t.Sizes = new uint[t.Samples];
                for (long i = 0; i < t.Samples; i++) {
                    long p = stz2.Body + 12 + i * field / 8;
                    t.Sizes[i] = field == 16 ? (uint)(b[p] << 8 | b[p + 1]) : field == 8 ? b[p] : (uint)((i & 1) == 0 ? b[p] >> 4 : b[p] & 15);
                }
            }
            if (timed != t.Samples) throw Broken("样本数与时长表不符");
            if (t.Sizes != null && t.Sizes.Any(s => s > SampleLimit)) throw Broken("样本长度无效");
            Box stsc = Child(b, stbl, "stsc"); Need(stsc, 8);
            n = U32(b, stsc.Body + 4); Need(stsc, 8 + n * 12);
            t.FirstChunk = new uint[n]; t.PerChunk = new uint[n];
            for (long i = 0; i < n; i++) {
                t.FirstChunk[i] = U32(b, stsc.Body + 8 + i * 12); t.PerChunk[i] = U32(b, stsc.Body + 12 + i * 12);
                uint description = U32(b, stsc.Body + 16 + i * 12);
                if (t.FirstChunk[i] < 1 || (i == 0 && t.FirstChunk[i] != 1) || (i > 0 && t.FirstChunk[i] <= t.FirstChunk[i - 1]) || t.PerChunk[i] == 0 || description < 1 || description > entries.Count) throw Broken("stsc 表无效");
            }
            bool large = !Has(b, stbl, "stco");
            Box stco = Child(b, stbl, large ? "co64" : "stco"); Need(stco, 8);
            n = U32(b, stco.Body + 4); Need(stco, 8 + n * (large ? 8 : 4)); t.Chunks = new long[n];
            for (long i = 0; i < n; i++) {
                ulong offset = large ? U64(b, stco.Body + 8 + i * 8) : U32(b, stco.Body + 8 + i * 4);
                if (offset > long.MaxValue) throw Broken("块偏移无效");
                t.Chunks[i] = (long)offset;
            }
            if (t.Samples > 0 && (n == 0 || t.FirstChunk.Length == 0)) throw Broken("音轨缺少块索引");
            return t;
        }
        static void Walk(Stream f, Track t, List<long[]> media, HashAlgorithm sha, ref byte[] buffer, CancellationToken cancel) {
            long sample = 0; int entry = 0;
            for (long chunk = 0; chunk < t.Chunks.Length; chunk++) {
                while (entry + 1 < t.FirstChunk.Length && t.FirstChunk[entry + 1] <= chunk + 1) entry++;
                long offset = t.Chunks[chunk];
                for (uint i = 0; i < t.PerChunk[entry]; i++, sample++) {
                    if (sample >= t.Samples) throw Broken("块索引与样本数不符");
                    int size = (int)t.Size(sample);
                    if (size > SampleLimit || !media.Any(m => offset >= m[0] && offset + size <= m[1])) throw Broken("音频数据不完整");
                    if (size > buffer.Length) buffer = new byte[size];
                    if (f.Position != offset) f.Position = offset;
                    for (int done = 0; done < size; ) { int got = f.Read(buffer, done, size - done); if (got == 0) throw Broken("音频数据不完整"); done += got; }
                    if (t.Handler == "soun" && (t.Codec == "ec-3" || t.Codec == "ac-3")) Dolby(buffer, size);
                    sha.TransformBlock(buffer, 0, size, null, 0);
                    offset += size;
                }
                if ((chunk & 63) == 0) cancel.ThrowIfCancellationRequested();
            }
            if (sample != t.Samples) throw Broken("块索引与样本数不符");
        }
        static readonly int[] Ac3Rates = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640 };
        // One sample holds whole AC-3 / E-AC-3 syncframes. The CRC over each frame
        // after its sync word is zero, as decoders verify it.
        internal static void Dolby(byte[] b, int count) {
            for (int p = 0; p < count; ) {
                if (count - p < 6 || b[p] != 0x0b || b[p + 1] != 0x77) throw Broken("杜比音频帧不完整");
                int bsid = b[p + 5] >> 3, size;
                if (bsid <= 10) {
                    int fscod = b[p + 4] >> 6, code = b[p + 4] & 63;
                    if (fscod == 3 || code >= 38) throw Broken("杜比音频帧头无效");
                    int rate = Ac3Rates[code >> 1];
                    size = 2 * (fscod == 0 ? rate * 2 : fscod == 2 ? rate * 3 : rate * 320 / 147 + (code & 1));
                } else if (bsid <= 16) size = 2 * (((b[p + 2] & 7) << 8 | b[p + 3]) + 1);
                else throw Broken("杜比音频帧头无效");
                if (size > count - p) throw Broken("杜比音频帧不完整");
                if (AudioFile.Crc16(b, p + 2, size - 2) != 0) throw Broken("杜比音频帧校验失败");
                p += size;
            }
        }
    }
}
