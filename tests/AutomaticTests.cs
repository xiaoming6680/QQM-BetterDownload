using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace QqmBetterDownload {
    internal static class AutomaticTests {
        internal static void Run(Action<bool,string> check, string folder) {
            string download = Path.Combine(folder, "auto-download"), vip = Path.Combine(download, "VipSongsDownload"), data = Path.Combine(folder, "auto-data");
            Directory.CreateDirectory(vip);
            string fixture = Path.Combine(folder, "legacy", "qtag.mflac");
            File.Copy(fixture, Path.Combine(vip, "existing.mflac"));
            File.Copy(fixture, Path.Combine(download, "unrelated.mflac"));
            var paths = new AutomaticPaths(data, download);
            check(paths.Roots.SequenceEqual(new[] { vip }), "detect only known VIP download folder, not mixed downloads");
            int successes = 0;
            using (var done = new AutoResetEvent(false))
            using (var monitor = new EventHost(Path.Combine(folder, "no-client"), download, data, status => { if (status.State == "success") { Interlocked.Increment(ref successes); done.Set(); } }, "", paths)) {
                check(done.WaitOne(12000) && File.Exists(Path.Combine(vip, "unlock", "existing.flac")), "startup must convert existing downloads without a scan click");
                check(!Directory.Exists(Path.Combine(download, "unlock")), "automatic startup must ignore unrelated files outside VIP downloads");
                string changed = Path.Combine(folder, "changed-location", "VipSongsDownload"), artist = Path.Combine(changed, "artist"); Directory.CreateDirectory(artist);
                File.Copy(fixture, Path.Combine(artist, "new-name.mflac"));
                File.Copy(fixture, Path.Combine(artist, "older-name.mflac"));
                string extended = @"\\?\" + Path.Combine(artist, "new-name.mflac");
                check(EventHost.DownloadRoot(extended) == changed, "client extended drive path identifies the ordinary download root");
                // Match the native bridge's raw UTF-16 event; Submit already
                // normalizes paths and would hide a reader regression.
                string rawEvent = Path.Combine(data, "events", "extended.tmp");
                File.WriteAllText(rawEvent, extended, new System.Text.UnicodeEncoding(false, false));
                File.Move(rawEvent, Path.ChangeExtension(rawEvent, ".evt"));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (successes < 3 && clock.ElapsedMilliseconds < 15000) done.WaitOne(200);
                check(successes == 3 && File.Exists(Path.Combine(changed, "unlock", "artist", "new-name.flac")) && File.Exists(Path.Combine(changed, "unlock", "artist", "older-name.flac")), "changed download directory must learn and automatically backfill without manual settings");
                check(paths.Roots.Contains(changed), "new download root must be retained");
            }
            var restored = new AutomaticPaths(data, download);
            check(restored.Roots.Contains(vip) && restored.Roots.Contains(Path.Combine(folder, "changed-location", "VipSongsDownload")), "automatic locations must survive restart");
            foreach (string unsupported in new[] { @"\\?\UNC\server\share\VipSongsDownload\song.mflac", @"\\?\GLOBALROOT\Device\HarddiskVolume1\VipSongsDownload\song.mflac", @"\\?\C:relative.mflac" }) {
                bool rejected = false; try { EventHost.DownloadRoot(unsupported); } catch (IOException) { rejected = true; }
                check(rejected, "extended non-local or relative namespace remains unsupported");
            }
            using (var scanned = new AutoResetEvent(false))
            using (var empty = new EventHost(Path.Combine(folder, "no-client"), Path.Combine(folder, "empty-downloads"), Path.Combine(folder, "empty-data"), status => { if (status.State == "scan-complete" && status.Message.Contains("暂未识别")) scanned.Set(); })) {
                check(scanned.WaitOne(5000), "startup scan with no known directories must report a visible result");
                empty.ScanExisting();
                check(scanned.WaitOne(5000), "manual rescan must report a result even with no matching downloads");
            }
        }
    }
}
