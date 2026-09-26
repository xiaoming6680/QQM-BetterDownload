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
        public static string Enrich(string path, string format, string cacheRoot, CancellationToken cancel) {
            string tagged = path + ".tags";
            try {
                string mime = "taglib/" + format; byte[] cover;
                using (var audio = TagLib.File.Create(path, mime, TagLib.ReadStyle.None)) {
                    if (audio.Tag.Pictures.Length > 0) return "";
                    if (String.IsNullOrWhiteSpace(cacheRoot)) return "已完成；原音频没有封面，未发现可用的本地封面缓存。";
                    string artist = Clean(audio.Tag.FirstPerformer), album = Clean(audio.Tag.Album);
                    if (artist.Length == 0 || album.Length == 0) return "已完成；缺少匹配本地封面所需的歌手或专辑标签。";
                    // Exact local cache convention observed in GetDownloadPicPath;
                    // never guess from the user's song filename or search online.
                    string picture = FindCover(cacheRoot, artist, album);
                    if (picture.Length == 0) return "已完成；未找到精确匹配的本地封面。";
                    SafePath.NoLinks(picture);
                    if (!File.Exists(picture)) return "已完成；未找到精确匹配的本地封面。";
                    using (var file = new FileStream(picture, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        if (file.Length < 3 || file.Length > 8 * 1024 * 1024) return "已完成；本地封面大小不受支持。";
                        cover = Binary.Read(file, (int)file.Length);
                    }
                    if (cover[0] != 255 || cover[1] != 216 || cover[2] != 255) return "已完成；本地封面不是有效 JPEG。";
                }
                cancel.ThrowIfCancellationRequested(); File.Copy(path, tagged, false);
                using (var audio = TagLib.File.Create(tagged, mime, TagLib.ReadStyle.None)) {
                    audio.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Description = "Cover" } };
                    audio.Save();
                }
                using (var audio = TagLib.File.Create(tagged, mime, TagLib.ReadStyle.None)) {
                    if (!audio.Tag.Pictures.Any(p => p.Data.Data.SequenceEqual(cover))) throw new IOException("封面写入校验失败。");
                }
                using (var file = File.OpenRead(tagged)) AudioFile.Validate(file, format, cancel);
                cancel.ThrowIfCancellationRequested(); File.Replace(tagged, path, null); return "";
            } catch (OperationCanceledException) { throw; }
            catch (Exception) { return "已完成；本地封面未能写入，已保留完整原音频。"; }
            finally { if (File.Exists(tagged)) File.Delete(tagged); }
        }
    }
}
