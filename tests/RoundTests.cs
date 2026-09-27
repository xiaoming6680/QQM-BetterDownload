using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QqmBetterDownload {
    // Songs converted back to back share one card, and a scan reports what it
    // found and then what came of it. Synthetic fixtures only.
    internal static class RoundTests {
        static WorkStatus Song(string id, string state, int pending, string round = "r1") {
            return new WorkStatus { Id = id, State = state, Pending = pending, Round = round, Source = id + ".mflac", Message = "说明" };
        }
        static IEnumerable<TextBlock> Texts(DependencyObject parent) {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                var child = VisualTreeHelper.GetChild(parent, i); var text = child as TextBlock; if (text != null) yield return text;
                foreach (var nested in Texts(child)) yield return nested;
            }
        }
        internal static void Run(Action<bool, string> check, string folder) {
            var card = new CardSession();
            check(card.Apply(Song("a", "converting", 3), "all", false), "a round opens its card once");
            check(!card.Apply(Song("a", "success", 2), "all", false), "a song finishing mid-round does not end the card");
            check(!card.Apply(Song("b", "converting", 2), "all", false), "the next song of the round does not pop the card again");
            card.Hide();
            check(!card.Apply(Song("c", "converting", 1), "all", false) && !card.Visible, "a dismissed round stays hidden for its later songs");
            check(card.Apply(Song("c", "success", 0), "all", false), "the round's result appears once");
            check(!card.Apply(Song("c", "success", 0), "all", false), "the result does not repeat");
            check(card.Apply(Song("d", "converting", 1, "r2"), "all", false), "a new round opens a new card");
            check(card.Apply(Song("e", "error", 1, "r2"), "errors", false) && card.Apply(Song("f", "error", 0, "r2"), "errors", false), "errors-only mode still shows every failure");

            var view = new CardView(null);
            var converting = Song("g", "converting", 2); converting.Position = 2; converting.Percent = 40;
            view.Update(converting, false, null);
            check(Texts(view).Any(t => t.Text == "第 2 首 · 还剩 1 首"), "a round's card counts its songs");
            var done = Song("g", "success", 0); done.Position = 3; done.Converted = 2; done.Failed = 1; done.Lyrics = true; done.Percent = 100; done.Output = Path.Combine(folder, "unlock", "g.flac");
            view.Update(done, false, null);
            check(Texts(view).Any(t => t.Text == "本轮 2 首 · 1 首未完成") && Texts(view).Any(t => t.Text == "原音质已保留 · 歌词已写入"), "the round's result sums it up and mentions lyrics");

            string download = Path.Combine(folder, "round-download"), vip = Path.Combine(download, "VipSongsDownload"), data = Path.Combine(folder, "round-data");
            Directory.CreateDirectory(vip);
            string fixture = Path.Combine(folder, "legacy", "qtag.mflac");
            foreach (string name in new[] { "一.mflac", "二.mflac", "三.mflac" }) File.Copy(fixture, Path.Combine(vip, name));
            var seen = new List<WorkStatus>(); var gate = new object();
            using (var finished = new AutoResetEvent(false))
            using (var host = new EventHost(Path.Combine(folder, "no-client"), download, data, delegate(WorkStatus status) {
                lock (gate) seen.Add(status);
                if (status.State == "scan-complete" && (status.Message.Contains("已全部完成") || status.Message.Contains("都已转换过"))) finished.Set();
            }, "", new AutomaticPaths(data, download))) {
                check(finished.WaitOne(20000), "the scan reports again once its songs are done");
                WorkStatus[] all; lock (gate) all = seen.ToArray();
                check(all.Any(s => s.State == "scan-complete" && s.Message == "找到 3 首加密歌曲：3 首待转换，正在处理。"), "the scan first says how many songs it queued");
                check(all.Any(s => s.State == "scan-complete" && s.Message == "找到的 3 首待转换歌曲已全部完成。"), "then what came of them");
                var songs = all.Where(s => s.State == "converting" || s.State == "success").ToArray();
                check(songs.Length > 0 && songs.All(s => s.Round == songs[0].Round && s.Round.Length > 0), "songs converted back to back share one round");
                check(all.Where(s => s.State == "converting" && s.Message == "正在准备").Select(s => s.Pending).SequenceEqual(new[] { 3, 2, 1 }), "each song starts with the count of songs left");
                var results = all.Where(s => s.State == "success").ToArray();
                check(results.Select(s => s.Pending).SequenceEqual(new[] { 2, 1, 0 }) && results.Select(s => s.Position).SequenceEqual(new[] { 1, 2, 3 }), "each result counts the songs still queued");
                check(results[2].Converted == 3 && results[2].Folder == Path.Combine(vip, "unlock"), "the last result holds the round's total and folder");
                host.ScanExisting();
                check(finished.WaitOne(10000), "a rescan answers");
                lock (gate) all = seen.ToArray();
                check(all.Last(s => s.State == "scan-complete").Message == "找到 3 首加密歌曲，都已转换过。" && all.Count(s => s.State == "success") == 3, "a rescan recognises converted songs without converting them again");
            }
        }
    }
}
