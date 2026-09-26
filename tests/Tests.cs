using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace QqmBetterDownload {
    public static class Tests {
        static int passed;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        static WorkStatus Status(string id, string state, int percent = 0) { return new WorkStatus { Id = id, State = state, Percent = percent, Source = "示例.mflac", Message = "状态说明" }; }
        static void Notifications() {
            var card = new CardSession();
            Check(!card.Apply(Status("a", "watching"), "all", false), "idle should not pop");
            Check(card.Apply(Status("a", "converting", 10), "all", false), "first progress should pop");
            Check(!card.Apply(Status("a", "converting", 20), "all", false), "progress should not restart stay timer");
            card.Hide();
            Check(!card.Apply(Status("a", "converting", 64), "all", false) && !card.Visible, "dismissed progress must stay hidden");
            Check(card.Apply(Status("a", "success", 100), "all", false), "completion may notify once");
            card.Hide();
            Check(!card.Apply(Status("a", "success", 100), "all", false) && !card.Visible, "completion duplicates must stay hidden");
            Check(!card.Apply(Status("b", "converting"), "errors", false), "errors-only suppresses progress");
            Check(!card.Apply(Status("b", "waiting"), "errors", false), "transient retries must not alert in errors-only mode");
            Check(card.Apply(Status("b", "error"), "errors", false), "failure must appear");
            card.Hide();
            Check(card.Apply(Status("b", "success"), "all", false), "successful retry after an error must notify");
            Check(!card.Apply(Status("c", "error"), "off", false) && !card.Visible, "off must suppress errors");
            Check(card.Apply(Status("preview", "converting"), "off", true), "explicit preview ignores notify setting");
            Check(!card.Apply(Status("preview", "skipped"), "all", false) && !card.Visible, "skipped conversion must hide its card");
            Check(card.Apply(Status("d", "converting"), "all", false), "new job after skip appears");
        }
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var child = VisualTreeHelper.GetChild(parent, i); var match = child as T; if (match != null) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        static void Visuals(string folder) {
            foreach (bool compact in new[] { false, true }) foreach (string phase in new[] { "converting", "waiting", "success", "error" }) {
                int opened = 0;
                var status = Status("v", phase, 64); status.Output = Path.Combine(folder, "song.flac"); status.Warning = phase == "success" ? "已完成；未找到精确匹配的本地封面。" : "";
                status.Track = new TrackInfo { Title = "歌名与文件名完全不同的长标题测试", Artist = "标签歌手", Format = "FLAC" };
                var view = new CardView(delegate { opened++; }); view.Update(status, compact, CardPreview.SampleCover());
                view.Measure(new Size(CardView.CardWidth, 500)); view.Arrange(new Rect(0, 0, CardView.CardWidth, view.DesiredSize.Height)); view.UpdateLayout(); view.FreezeProgress();
                Check(view.ActualHeight > 40 && view.ActualHeight < 260, "card height out of bounds: " + compact + "/" + phase);
                Check(Descendants<TextBlock>(view).Any(t => t.Text == status.Track.Title), "card must prefer audio title");
                if (phase == "success") {
                    var button = Descendants<Button>(view).Single(); Check(button.Visibility == Visibility.Visible, "completion folder action missing");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(opened == 1, "folder callback not wired");
                    Check(Descendants<TextBlock>(view).Any(t => t.Text.Contains("无本地封面")), "missing cover warning must remain visible in both styles");
                }
            }
            CardPreview.Save(Path.Combine(folder, "cards.png"), 1);
            Check(new FileInfo(Path.Combine(folder, "cards.png")).Length > 10000, "native preview was not rendered");
            Check(CardView.LoadArt(new byte[] { 0, 1, 2, 3 }) == null, "bad cover should safely fall back");
            IntPtr foreground = GetForegroundWindow();
            using (var card = new ProgressCard(() => IntPtr.Zero, path => { })) {
                card.Configure("all", false, 2000); card.Receive(Status("native", "converting", 64)); Forms.Application.DoEvents();
                Check(card.IsVisible, "native notification not visible");
                Check(GetForegroundWindow() == foreground, "notification stole foreground focus");
                card.Dismiss(); Check(!card.IsVisible, "notification did not hide");
                card.Receive(Status("native", "converting", 80)); Check(!card.IsVisible, "native progress resurrected dismissed card");
                card.Receive(Status("native", "success", 100)); Check(card.IsVisible, "native completion did not appear");
                card.Configure("off", true, 4000); Check(!card.IsVisible, "notification setting did not hide card");
                card.Configure("all", false, 2000); card.Receive(Status("timer", "converting", 64));
                card.Left = -10000; // own test window only; keep the real pointer outside
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                Pump(2200); Check(card.IsVisible, "hover must pause automatic dismissal");
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                Pump(2300); Check(!card.IsVisible, "notification did not dismiss after leaving hover");
            }
        }
        static void Pump(int ms) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); } }
        static void Metadata(string folder) {
            string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tests", "fixtures", "tone.flac");
            string tagged = Path.Combine(folder, "tagged.flac"); File.Copy(fixture, tagged);
            using (var file = TagLib.File.Create(tagged)) { file.Tag.Title = "真正的歌名"; file.Tag.Performers = new[] { "标签歌手" }; file.Tag.Album = "测试专辑"; file.Save(); }
            byte[] plain = File.ReadAllBytes(tagged), key = Enumerable.Range(0, 257).Select(n => (byte)(n * 17 + 3)).ToArray(), encrypted = (byte[])plain.Clone();
            using (var cipher = new Qmc(key)) {
                cipher.Transform(encrypted, encrypted.Length, 0);
                using (var source = new MemoryStream(encrypted)) {
                    source.Position = 25;
                    var info = TrackInfo.ReadEncrypted(source, cipher, encrypted.Length, "flac", "");
                    Check(info.Title == "真正的歌名" && info.Artist == "标签歌手", "encrypted metadata callback failed");
                    Check(source.Position == 25, "metadata reader moved converter's stream position");
                    var stream = new TrackInfo.TagStream(source, cipher, encrypted.Length);
                    byte[] slice = new byte[43]; stream.Position = 5120 - 7; int got = stream.Read(slice, 3, 31);
                    Check(got == 31 && slice.Skip(3).Take(31).SequenceEqual(plain.Skip(5113).Take(31)), "random access with destination offset failed");
                    stream.Position = stream.Length - 2; Check(stream.Read(slice, 0, 40) == 2 && stream.Read(slice, 0, 1) == 0, "read crossed audio footer boundary");
                }
            }
            string root = Path.Combine(folder, "VipSongsDownload"), sourcePath = Path.Combine(root, "不要拆分-这个文件名.mflac"); Directory.CreateDirectory(root);
            byte[] footer = new byte[192]; byte[] resource = System.Text.Encoding.Unicode.GetBytes("fixture.mflac"); Buffer.BlockCopy(resource, 0, footer, 0x48, resource.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(192), 0, footer, 176, 4); Buffer.BlockCopy(BitConverter.GetBytes(1), 0, footer, 180, 4); Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("musicex\0"), 0, footer, 184, 8);
            File.WriteAllBytes(sourcePath, encrypted.Concat(footer).ToArray());
            var keys = new Dictionary<string, string> { { "fixture.mflac", Convert.ToBase64String(key) } };
            var converter = new Converter(Path.Combine(folder, "receipts.json")); int identified = 0;
            var result = converter.ConvertFile(root, sourcePath, keys, null, CancellationToken.None, delegate(TrackInfo info) { identified++; Check(info.Title == "真正的歌名", "conversion card used filename instead of tags"); });
            Check(identified == 1 && File.ReadAllBytes(result.Output).SequenceEqual(plain), "metadata presentation changed audio bytes");
            Check(File.ReadAllBytes(sourcePath).SequenceEqual(encrypted.Concat(footer)), "source was modified");
            var repeat = converter.ConvertFile(root, sourcePath, keys, null, CancellationToken.None, delegate { identified++; });
            Check(repeat.Skipped && identified == 1, "duplicate caused another metadata notification");
            Check(CardView.LoadArt(null) == null, "no artwork should use fallback");
        }
        static void RealMetadata(string sourcePath, string outputPath, string client, string cache) {
            using (var keys = new LocalKeys(client)) using (var source = File.OpenRead(sourcePath)) {
                var footer = AudioFile.Footer(source);
                using (var cipher = new Qmc(keys.Read()[footer.Resource])) {
                    var info = TrackInfo.ReadEncrypted(source, cipher, footer.AudioLength, "flac", cache);
                    var saved = TrackInfo.Read(outputPath, "flac");
                    Check(info.Title.Length > 0 && info.Title == saved.Title && info.Artist == saved.Artist && info.Album == saved.Album, "real metadata mismatch");
                    Check(info.Artwork != null && saved.Artwork != null && info.Artwork.SequenceEqual(saved.Artwork), "real local cover mismatch");
                    Check(CardView.LoadArt(info.Artwork) != null, "real artwork not displayable");
                }
            }
            Console.WriteLine("PASS real track: encrypted tags match completed audio; local cover matches embedded cover and decodes. No track data saved.");
        }
        [STAThread] public static int Main(string[] args) {
            try {
                Forms.Application.EnableVisualStyles(); Forms.Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length == 5 && args[0] == "--real-metadata") { RealMetadata(args[1], args[2], args[3], args[4]); return 0; }
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                if (args.Length == 1 && args[0] == "--shutdown") { ShutdownTests.Run(Check, folder); Console.WriteLine("PASS " + passed + " shutdown assertions."); return 0; }
                if (args.Length == 1 && args[0] == "--in-app") { BridgeTests.Run(Check, folder); ShutdownTests.Run(Check, folder); Console.WriteLine("PASS " + passed + " bridge and in-app assertions."); return 0; }
                Notifications(); Metadata(folder); LegacyTests.Run(Check, folder); AutomaticTests.Run(Check, folder); Visuals(folder); BridgeTests.Run(Check, folder); ShutdownTests.Run(Check, folder);
                Console.WriteLine("PASS " + passed + " assertions; synthetic fixtures only."); return 0;
            } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    }
}
