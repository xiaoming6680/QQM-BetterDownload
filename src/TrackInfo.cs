using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace QqmBetterDownload {
    // Presentation data comes from audio tags, never from splitting the user's
    // filename. A filename is only a display fallback when no title is present.
    public sealed class TrackInfo {
        public string Title = "", Artist = "", Album = "", Format = "";
        [ScriptIgnore] public byte[] Artwork;

        public static TrackInfo Read(string path, string format) {
            try {
                using (var file = TagLib.File.Create(path, "taglib/" + format, TagLib.ReadStyle.None))
                    return FromTag(file.Tag, format, "");
            } catch { return new TrackInfo { Format = format.ToUpperInvariant() }; }
        }
        internal static TrackInfo ReadEncrypted(Stream source, Qmc cipher, long length, string format, string cacheRoot) {
            long original = source.Position;
            try {
                using (var file = TagLib.File.Create(new AudioTags(source, cipher, length, format), TagLib.ReadStyle.None))
                    return FromTag(file.Tag, format, cacheRoot);
            } catch { return new TrackInfo { Format = format.ToUpperInvariant() }; }
            finally { source.Position = original; }
        }
        static TrackInfo FromTag(TagLib.Tag tag, string format, string cacheRoot) {
            var info = new TrackInfo { Title = Clean(tag.Title), Artist = Clean(tag.FirstPerformer), Album = Clean(tag.Album), Format = format.ToUpperInvariant() };
            var picture = tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures.FirstOrDefault();
            if (picture != null && picture.Data.Count <= 8 * 1024 * 1024) info.Artwork = picture.Data.Data;
            if (info.Artwork == null && !String.IsNullOrEmpty(cacheRoot)) {
                try {
                    string path = LocalArtwork.FindCover(cacheRoot, tag.FirstPerformer, tag.Album);
                    if (path.Length > 0) {
                        SafePath.NoLinks(path);
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                            if (file.Length > 0 && file.Length <= 8 * 1024 * 1024) info.Artwork = Binary.Read(file, (int)file.Length);
                    }
                } catch { /* Artwork is optional and never blocks conversion. */ }
            }
            return info;
        }
        static string Clean(string value) {
            if (String.IsNullOrEmpty(value)) return "";
            string text = new string(value.Where(c => !Char.IsControl(c)).Take(512).ToArray()).Trim();
            return text;
        }

        sealed class AudioTags : TagLib.File.IFileAbstraction {
            readonly Stream stream;
            public AudioTags(Stream source, Qmc cipher, long length, string format) { Name = "audio." + format; stream = new TagStream(source, cipher, length); }
            public string Name { get; private set; }
            public Stream ReadStream { get { stream.Position = 0; return stream; } }
            public Stream WriteStream { get { throw new NotSupportedException(); } }
            public void CloseStream(Stream value) { /* source belongs to Converter */ }
        }
        // TagLib may seek, but it cannot read past the audio into the key footer,
        // write to the source, or consume unbounded data just to draw a card.
        internal sealed class TagStream : Stream {
            readonly Stream source;
            readonly Qmc cipher;
            readonly long length;
            long position, read;
            public TagStream(Stream source, Qmc cipher, long length) { this.source = source; this.cipher = cipher; this.length = length; }
            public override int Read(byte[] buffer, int offset, int count) {
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentException();
                int wanted = (int)Math.Min(count, Math.Max(0, length - position));
                if (wanted == 0) return 0;
                if (read + wanted > 16 * 1024 * 1024) throw new InvalidDataException("标签读取量超过上限。");
                byte[] bytes = new byte[wanted]; source.Position = position;
                int n = source.Read(bytes, 0, wanted); cipher.Transform(bytes, n, position);
                Buffer.BlockCopy(bytes, 0, buffer, offset, n); position += n; read += n; return n;
            }
            public override long Seek(long offset, SeekOrigin origin) {
                long next = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : length) + offset);
                if (next < 0 || next > length) throw new IOException("标签读取位置越界。");
                return position = next;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
