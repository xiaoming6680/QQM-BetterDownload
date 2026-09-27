using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace QqmBetterDownload {
    // QQ Music's SQ FLAC trailer and Dolby MP4 (.mmp4), with synthetic fixtures only.
    internal static class FormatTests {
        static readonly CancellationToken None = CancellationToken.None;
        static byte[] Cat(params byte[][] parts) { return parts.SelectMany(p => p).ToArray(); }
        static byte[] Be32(long v) { return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }
        static byte[] Be16(int v) { return new[] { (byte)(v >> 8), (byte)v }; }
        static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }
        static byte[] Box(string type, params byte[][] body) { byte[] b = Cat(body); return Cat(Be32(8 + b.Length), Ascii(type), b); }
        static byte[] Full(string type, int flags, params byte[][] body) { return Box(type, Cat(Be32(flags & 0xffffff), Cat(body))); }
        static byte[] Matrix() { return Cat(Be32(0x10000), Be32(0), Be32(0), Be32(0), Be32(0x10000), Be32(0), Be32(0), Be32(0), Be32(0x40000000)); }
        static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        // A syncframe with random payload and a valid whole-frame CRC.
        internal static byte[] Dolby(int bytes, int seed, bool ac3) {
            var frame = new byte[bytes]; new Random(seed).NextBytes(frame);
            frame[0] = 0x0b; frame[1] = 0x77;
            if (ac3) { frame[4] = 0x14; frame[5] = 8 << 3; } // 48 kHz, 192 kbps: 768 bytes
            else { int words = bytes / 2 - 1; frame[2] = (byte)((words >> 8) & 7); frame[3] = (byte)words; frame[4] = 0x34; frame[5] = 16 << 3 | 7; }
            ushort crc = AudioFile.Crc16(frame, 2, bytes - 4); frame[bytes - 2] = (byte)(crc >> 8); frame[bytes - 1] = (byte)crc;
            return frame;
        }
        // ftyp + moov + mdat (fast start) or ftyp + mdat + moov, one E-AC-3 track.
        internal static byte[] Mp4(bool fastStart, string codec = "ec-3", int frames = 12, int perChunk = 4, int frameBytes = 256, long offsetShift = 0) {
            byte[][] samples = Enumerable.Range(0, frames).Select(i => Dolby(frameBytes, i + 1, false)).ToArray();
            byte[] ftyp = Box("ftyp", Ascii("mp42"), Be32(0), Ascii("isommp42dby1")), data = Cat(samples);
            Func<long, byte[]> moov = delegate(long start) {
                int chunks = frames / perChunk;
                byte[] entry = Box(codec, new byte[6], Be16(1), new byte[8], Be16(2), Be16(16), new byte[4], Be32(48000L << 16), Box("dec3", new byte[] { 0x0c, 0x00, 0x20, 0x0f, 0x00 }));
                byte[] stbl = Box("stbl",
                    Full("stsd", 0, Be32(1), entry),
                    Full("stts", 0, Be32(1), Be32(frames), Be32(1536)),
                    Full("stsc", 0, Be32(1), Be32(1), Be32(perChunk), Be32(1)),
                    Full("stsz", 0, Be32(0), Be32(frames), Cat(samples.Select(s => Be32(s.Length)).ToArray())),
                    Full("stco", 0, Be32(chunks), Cat(Enumerable.Range(0, chunks).Select(c => Be32(start + offsetShift + (long)c * perChunk * frameBytes)).ToArray())));
                byte[] minf = Box("minf", Full("smhd", 0, new byte[4]), Box("dinf", Full("dref", 0, Be32(1), Full("url ", 1))), stbl);
                byte[] mdia = Box("mdia", Full("mdhd", 0, new byte[8], Be32(48000), Be32(frames * 1536), Be16(0x55c4), new byte[2]), Full("hdlr", 0, new byte[4], Ascii("soun"), new byte[12], Ascii("SoundHandler\0")), minf);
                byte[] tkhd = Full("tkhd", 7, new byte[8], Be32(1), new byte[4], Be32(frames * 1536), new byte[12], Be16(0x0100), new byte[2], Matrix(), new byte[8]);
                byte[] mvhd = Full("mvhd", 0, new byte[8], Be32(48000), Be32(frames * 1536), Be32(0x10000), Be16(0x0100), new byte[10], Matrix(), new byte[24], Be32(2));
                return Box("moov", mvhd, Box("trak", tkhd, mdia));
            };
            int moovBytes = moov(0).Length;
            return fastStart ? Cat(ftyp, moov(ftyp.Length + moovBytes + 8), Box("mdat", data)) : Cat(ftyp, Box("mdat", data), moov(ftyp.Length + 8));
        }
        static byte[] Footer(string resource) {
            byte[] footer = new byte[192], name = Encoding.Unicode.GetBytes(resource); Buffer.BlockCopy(name, 0, footer, 0x48, name.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(192), 0, footer, 176, 4); Buffer.BlockCopy(BitConverter.GetBytes(1), 0, footer, 180, 4); Buffer.BlockCopy(Ascii("musicex\0"), 0, footer, 184, 8);
            return footer;
        }
        static readonly byte[] Key = Enumerable.Range(0, 512).Select(n => (byte)(n * 29 + 7)).ToArray();
        static string Encrypt(string root, string name, byte[] plain, string resource) {
            byte[] encrypted = (byte[])plain.Clone(); using (var qmc = new Qmc(Key)) qmc.Transform(encrypted, encrypted.Length, 0);
            string path = Path.Combine(root, name); File.WriteAllBytes(path, Cat(encrypted, Footer(resource))); return path;
        }
        static string Digest(byte[] mp4) { return AudioFile.Validate(new MemoryStream(mp4), "mp4", None).Digest; }
        static byte[] Jpeg() {
            using (var bitmap = new System.Drawing.Bitmap(8, 8)) using (var stream = new MemoryStream()) {
                using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.Clear(System.Drawing.Color.SeaGreen);
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg); return stream.ToArray();
            }
        }
        internal static void Run(Action<bool, string> check, string folder) {
            string root = Path.Combine(folder, "formats", "VipSongsDownload"); Directory.CreateDirectory(root);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { { "trailer.mflac", Convert.ToBase64String(Key) }, { "junk.mflac", Convert.ToBase64String(Key) }, { "dolby.mmp4", Convert.ToBase64String(Key) }, { "tagged.mmp4", Convert.ToBase64String(Key) } };

            // SQ FLAC: QQ appends F0 00 FF 0F … 0E 55 FF F0 after the last frame.
            byte[] tone = File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tests", "fixtures", "tone.flac"));
            byte[] trailer = Cat(new byte[] { 0xf0, 0x00, 0xff, 0x0f }, Ascii("DD@HF<6"), new byte[] { 0x0e, 0x55, 0xff, 0xf0 });
            check(AudioFile.Validate(new MemoryStream(Cat(tone, trailer)), "flac", None).Trailer == trailer.Length, "client trailer after a complete FLAC is recognized");
            check(AudioFile.Validate(new MemoryStream(tone), "flac", None).Trailer == 0, "plain FLAC has no trailer");
            check(Throws<InvalidDataException>(() => AudioFile.Validate(new MemoryStream(Cat(tone, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })), "flac", None)), "unknown trailing bytes are still rejected");
            check(Throws<InvalidDataException>(() => AudioFile.Validate(new MemoryStream(Cat(tone.Take(tone.Length - 10).ToArray(), trailer)), "flac", None)), "a trailer cannot hide a truncated last frame");
            var converter = new Converter(Path.Combine(folder, "formats-receipts.json"));
            var sq = converter.ConvertFile(root, Encrypt(root, "SQ.mflac", Cat(tone, trailer), "trailer.mflac"), keys, null, None);
            check(sq.Format == "flac" && File.ReadAllBytes(sq.Output).SequenceEqual(tone), "SQ output ends with the FLAC stream, trailer removed");
            check(Throws<InvalidDataException>(() => converter.ConvertFile(root, Encrypt(root, "junk.mflac", Cat(tone, Enumerable.Repeat((byte)0x55, 15).ToArray()), "junk.mflac"), keys, null, None)), "SQ with unknown trailing data stays pending");

            // Dolby Atmos: .mmp4 decrypts to MP4 with E-AC-3 samples.
            check(AudioFile.Format(Mp4(true).Take(64).ToArray()) == "mp4", "ftyp head detected as MP4");
            Mp4Audio.Dolby(Dolby(768, 5, true), 768); Mp4Audio.Dolby(Cat(Dolby(256, 6, false), Dolby(512, 7, false)), 768);
            byte[] bad = Dolby(768, 8, true); bad[300] ^= 1;
            check(Throws<InvalidDataException>(() => Mp4Audio.Dolby(bad, bad.Length)), "damaged AC-3 frame rejected");
            byte[] fast = Mp4(true), tail = Mp4(false);
            string digest = Digest(fast);
            check(digest.Length == 64 && Digest(tail) == digest, "both MP4 layouts validate to the same sample digest");
            check(Throws<InvalidDataException>(() => Digest(fast.Take(fast.Length - 100).ToArray())), "truncated MP4 rejected");
            byte[] flipped = (byte[])fast.Clone(); flipped[flipped.Length - 1000] ^= 0x40;
            check(Throws<InvalidDataException>(() => Digest(flipped)), "damaged Dolby frame inside MP4 rejected");
            check(Throws<InvalidDataException>(() => Digest(Mp4(true, offsetShift: 100000))), "sample outside media data rejected");
            check(Throws<NotSupportedException>(() => Digest(Cat(fast, Box("moof", Full("mfhd", 0, Be32(1)))))), "fragmented MP4 kept for adaptation");
            check(Throws<NotSupportedException>(() => Digest(Mp4(true, "enca"))), "protected audio kept");
            check(Throws<InvalidDataException>(() => Digest(Cat(Box("free"), fast))), "MP4 must start with ftyp");

            var dolby = converter.ConvertFile(root, Encrypt(root, "杜比.mmp4", fast, "dolby.mmp4"), keys, null, None);
            check(dolby.Format == "mp4" && Path.GetExtension(dolby.Output) == ".mp4" && File.ReadAllBytes(dolby.Output).SequenceEqual(fast), "mmp4 converts to a byte-identical MP4");

            // Tags and a local cover: TagLib moves the samples of a fast-start file;
            // the digest proves every sample is unchanged afterwards.
            string plain = Path.Combine(folder, "formats", "tagged.mp4"); File.WriteAllBytes(plain, fast);
            using (var file = TagLib.File.Create(plain)) { file.Tag.Title = "杜比测试"; file.Tag.Performers = new[] { "标签歌手" }; file.Tag.Album = "测试专辑"; file.Save(); }
            byte[] tagged = File.ReadAllBytes(plain);
            check(tagged.Length > fast.Length && Digest(tagged) == digest, "TagLib retagging kept every MP4 sample");
            string cache = Path.Combine(folder, "formats", "cache"); Directory.CreateDirectory(Path.Combine(cache, "QQMusicPicture"));
            byte[] cover = Jpeg(); File.WriteAllBytes(Path.Combine(cache, "QQMusicPicture", "标签歌手_测试专辑_4.jpg"), cover);
            TrackInfo seen = null;
            var covered = new Converter(Path.Combine(folder, "formats-cover.json"), cache).ConvertFile(root, Encrypt(root, "封面.mmp4", tagged, "tagged.mmp4"), keys, null, None, delegate(TrackInfo info) { seen = info; });
            check(seen != null && seen.Title == "杜比测试" && seen.Format == "MP4", "card reads MP4 tags from the encrypted source");
            check(covered.Warning == "", "MP4 cover written without warning: " + covered.Warning + " / " + LocalArtwork.LastFailure);
            using (var file = TagLib.File.Create(covered.Output)) check(file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Data.Data.SequenceEqual(cover), "local cover embedded in MP4");
            check(Digest(File.ReadAllBytes(covered.Output)) == digest, "MP4 samples unchanged after the cover");
            Lyrics(check, folder, tone, fast, digest);
            Adoption(check, folder, tone);
        }
        // Plugin data deleted, outputs kept: the same audio is kept, not written again as "name (2)".
        static void Adoption(Action<bool, string> check, string folder, byte[] tone) {
            string root = Path.Combine(folder, "adopt", "VipSongsDownload"), unlock = Path.Combine(root, "unlock"); Directory.CreateDirectory(root);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { { "song.mflac", Convert.ToBase64String(Key) } };
            string source = Encrypt(root, "保留.mflac", tone, "song.mflac");
            var first = new Converter(Path.Combine(folder, "adopt-1.json")).ConvertFile(root, source, keys, null, None);
            var fresh = new Converter(Path.Combine(folder, "adopt-2.json"));
            var again = fresh.ConvertFile(root, source, keys, null, None);
            check(again.Skipped && again.Output == first.Output && Directory.GetFiles(unlock, "保留*").Length == 1 && fresh.Done(source), "the same audio already in unlock is kept after the receipts are lost");
            byte[] other = File.ReadAllBytes(first.Output); other[30] ^= 0xff; // inside the STREAMINFO audio MD5
            File.WriteAllBytes(first.Output, other);
            var third = new Converter(Path.Combine(folder, "adopt-3.json")).ConvertFile(root, source, keys, null, None);
            check(!third.Skipped && Path.GetFileName(third.Output) == "保留 (2).flac" && File.ReadAllBytes(first.Output).SequenceEqual(other), "a different file under the same name is neither taken over nor replaced");
        }
        // QQ Music saves "歌名.lrc" beside the download when its lyrics option is on.
        static void Lyrics(Action<bool, string> check, string folder, byte[] tone, byte[] mp4, string digest) {
            string root = Path.Combine(folder, "lyrics", "VipSongsDownload"), unlock = Path.Combine(root, "unlock"); Directory.CreateDirectory(root);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { { "song.mflac", Convert.ToBase64String(Key) }, { "song.mmp4", Convert.ToBase64String(Key) } };
            const string text = "[ti:示例]\n[ar:标签歌手]\n[00:01.00]第一句歌词\n[00:02.50]第二句歌词";
            File.WriteAllText(Path.Combine(root, "有歌词.lrc"), text, new UTF8Encoding(false));
            var both = new Converter(Path.Combine(folder, "lyrics-receipts.json")) { Lyrics = true, LyricsFile = true };
            string source = Encrypt(root, "有歌词.mflac", tone, "song.mflac");
            var result = both.ConvertFile(root, source, keys, null, None);
            using (var file = TagLib.File.Create(result.Output)) check(result.Lyrics && file.Tag.Lyrics == text, "downloaded lyrics written into the FLAC: " + LocalArtwork.LastFailure);
            check(File.ReadAllText(Path.Combine(unlock, "有歌词.lrc"), Encoding.UTF8) == text, ".lrc copied beside the output with its name");
            using (var output = File.OpenRead(result.Output)) AudioFile.Validate(output, "flac", None);

            // Receipts remember size and time, so a scan skips converted songs without hashing them.
            check(both.Done(source), "a converted song is known without hashing");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-3));
            check(!both.Done(source), "a source with a new write time is checked again");
            check(both.ConvertFile(root, source, keys, null, None).Skipped && both.Done(source), "unchanged content is skipped and the receipt updated");
            check(new Converter(Path.Combine(folder, "lyrics-receipts.json")).Done(source), "receipts keep size and time across restarts");
            File.Delete(result.Output);
            check(!both.Done(source), "a deleted output is converted again");

            var off = new Converter(Path.Combine(folder, "lyrics-off.json"));
            File.WriteAllText(Path.Combine(root, "关闭.lrc"), text, new UTF8Encoding(false));
            var plain = off.ConvertFile(root, Encrypt(root, "关闭.mflac", tone, "song.mflac"), keys, null, None);
            using (var file = TagLib.File.Create(plain.Output)) check(!plain.Lyrics && String.IsNullOrEmpty(file.Tag.Lyrics) && !File.Exists(Path.Combine(unlock, "关闭.lrc")), "lyrics options off leave the output as before");

            string own = Path.Combine(folder, "lyrics", "own.flac"); File.WriteAllBytes(own, tone);
            using (var file = TagLib.File.Create(own)) { file.Tag.Lyrics = "原有歌词"; file.Save(); }
            File.WriteAllText(Path.Combine(root, "自带.lrc"), text, new UTF8Encoding(false));
            var kept = both.ConvertFile(root, Encrypt(root, "自带.mflac", File.ReadAllBytes(own), "song.mflac"), keys, null, None);
            using (var file = TagLib.File.Create(kept.Output)) check(!kept.Lyrics && file.Tag.Lyrics == "原有歌词", "lyrics already in the audio are kept");

            File.WriteAllBytes(Path.Combine(root, "国标.lrc"), Encoding.GetEncoding(54936).GetBytes(text));
            var gbk = both.ConvertFile(root, Encrypt(root, "国标.mflac", tone, "song.mflac"), keys, null, None);
            using (var file = TagLib.File.Create(gbk.Output)) check(file.Tag.Lyrics == text, "GBK lyrics decoded");

            File.WriteAllText(Path.Combine(root, "杜比歌词.lrc"), text, new UTF8Encoding(false));
            var dolby = both.ConvertFile(root, Encrypt(root, "杜比歌词.mmp4", mp4, "song.mmp4"), keys, null, None);
            using (var file = TagLib.File.Create(dolby.Output)) check(dolby.Lyrics && file.Tag.Lyrics == text && Digest(File.ReadAllBytes(dolby.Output)) == digest, "lyrics written into MP4 without touching samples: " + LocalArtwork.LastFailure);
        }
    }
}
