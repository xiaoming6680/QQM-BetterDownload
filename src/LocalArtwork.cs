using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace QqmBetterDownload {
    public static class LocalArtwork {
        internal static string FindDefaultCache() {
            // Known cache folder names only; no account/configuration databases.
            foreach (var drive in DriveInfo.GetDrives()) {
                try {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string path = Path.Combine(drive.RootDirectory.FullName, "QQMusicCache");
                    if (Directory.Exists(Path.Combine(path, "QQMusicPicture"))) { SafePath.NoLinks(path); return path; }
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return "";
        }
        static string Clean(string s) { return new string((s ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()); }
        internal static string FindCover(string cacheRoot, string artist, string album) {
            artist = Clean(artist); album = Clean(album);
            if (String.IsNullOrWhiteSpace(cacheRoot) || artist.Length == 0 || album.Length == 0) return "";
            string folder = Directory.Exists(Path.Combine(cacheRoot, "QQMusicPicture")) ? Path.Combine(cacheRoot, "QQMusicPicture") : cacheRoot;
            string path = Path.Combine(folder, artist + "_" + album + "_4.jpg");
            return File.Exists(path) ? path : "";
        }
        // Why the last tagging attempt fell back to the untagged audio.
        internal static string LastFailure = "";
        // Adds what the audio lacks: the exact local cover and, when given, QQ
        // Music's downloaded lyrics. digest: the validated MP4 sample digest (""
        // for FLAC / OGG), which the retagged copy must reproduce. Returns the
        // warning for the card, "" when there is none.
        public static string Enrich(string path, string format, string cacheRoot, CancellationToken cancel, string digest, string lyrics, out bool wroteLyrics) {
            string tagged = path + ".tags", mime = "taglib/" + format, warning = ""; byte[] cover = null; bool addLyrics = false;
            wroteLyrics = false;
            try {
                using (var audio = TagLib.File.Create(path, mime, TagLib.ReadStyle.None)) {
                    if (audio.Tag.Pictures.Length == 0) cover = Cover(audio.Tag, cacheRoot, out warning);
                    addLyrics = !String.IsNullOrEmpty(lyrics) && String.IsNullOrWhiteSpace(audio.Tag.Lyrics);
                }
                if (cover == null && !addLyrics) return warning;
                cancel.ThrowIfCancellationRequested(); File.Copy(path, tagged, false);
                using (var audio = TagLib.File.Create(tagged, mime, TagLib.ReadStyle.None)) {
                    if (cover != null) audio.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Description = "Cover" } };
                    if (addLyrics) audio.Tag.Lyrics = lyrics;
                    audio.Save();
                }
                using (var audio = TagLib.File.Create(tagged, mime, TagLib.ReadStyle.None)) {
                    if (cover != null && !audio.Tag.Pictures.Any(p => p.Data.Data.SequenceEqual(cover))) throw new IOException("封面写入校验失败。");
                    if (addLyrics && audio.Tag.Lyrics != lyrics) throw new IOException("歌词写入校验失败。");
                }
                using (var file = File.OpenRead(tagged)) {
                    var check = AudioFile.Validate(file, format, cancel);
                    if (check.Digest != (digest ?? "") || check.Trailer != 0) throw new IOException("写入标签后音频数据发生变化。");
                }
                cancel.ThrowIfCancellationRequested(); SafePath.Patiently(delegate { File.Replace(tagged, path, null); }, cancel);
                wroteLyrics = addLyrics; return warning;
            } catch (OperationCanceledException) { throw; }
            catch (Exception e) { LastFailure = e.GetType().Name + ": " + e.Message; return cover == null && addLyrics ? "已完成；歌词未能写入，已保留完整原音频。" : "已完成；本地封面" + (addLyrics ? "和歌词" : "") + "未能写入，已保留完整原音频。"; }
            finally { SafePath.Discard(tagged); }
        }
        static byte[] Cover(TagLib.Tag tag, string cacheRoot, out string warning) {
            warning = "";
            if (String.IsNullOrWhiteSpace(cacheRoot)) { warning = "已完成；原音频没有封面，未发现可用的本地封面缓存。"; return null; }
            string artist = Clean(tag.FirstPerformer), album = Clean(tag.Album);
            if (artist.Length == 0 || album.Length == 0) { warning = "已完成；缺少匹配本地封面所需的歌手或专辑标签。"; return null; }
            // Exact local cache convention observed in GetDownloadPicPath;
            // never guess from the user's song filename or search online.
            string picture = FindCover(cacheRoot, artist, album);
            if (picture.Length == 0) { warning = "已完成；未找到精确匹配的本地封面。"; return null; }
            SafePath.NoLinks(picture);
            if (!File.Exists(picture)) { warning = "已完成；未找到精确匹配的本地封面。"; return null; }
            byte[] cover;
            using (var file = new FileStream(picture, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (file.Length < 3 || file.Length > 8 * 1024 * 1024) { warning = "已完成；本地封面大小不受支持。"; return null; }
                cover = Binary.Read(file, (int)file.Length);
            }
            if (cover[0] != 255 || cover[1] != 216 || cover[2] != 255) { warning = "已完成；本地封面不是有效 JPEG。"; return null; }
            return cover;
        }
    }
}
