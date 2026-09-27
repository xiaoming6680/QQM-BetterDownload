using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QqmBetterDownload {
    // The in-QQ card reuses one off-screen view for every texture. After a
    // style switch (the settings preview), a state change or a progress step,
    // each frame must match a freshly built card in size and pixels.
    internal static class CardLayoutTests {
        static WorkStatus Status(string state, int percent) {
            return new WorkStatus { Id = "layout", Source = "示例歌曲.mflac", State = state, Percent = percent,
                Message = state == "error" ? "当前 QQ 音乐版本暂未适配。原文件已保留，请更新插件后重试。" : "正在转换",
                Output = state == "success" ? @"D:\Music\VipSongsDownload\unlock\示例歌曲.flac" : "",
                Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" } };
        }
        static byte[] Frame(Border canvas, CardView view, WorkStatus status, bool compact) {
            view.Update(status, compact, null); NativeCard.Layout(canvas, view);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth), (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
        }
        static Border Canvas(CardView view) { return new Border { Child = view, Padding = new Thickness(20, 16, 20, 22), Background = Brushes.Transparent }; }
        public static void Run(Action<bool, string> check) {
            var view = new CardView(null); var canvas = Canvas(view);
            var steps = new[] {
                Tuple.Create("converting", false, 30), Tuple.Create("converting", false, 64), Tuple.Create("success", false, 100),
                Tuple.Create("success", true, 100), Tuple.Create("success", false, 100), Tuple.Create("waiting", true, 0),
                Tuple.Create("converting", true, 80), Tuple.Create("error", true, 0), Tuple.Create("error", false, 0), Tuple.Create("converting", false, 50)
            };
            foreach (var step in steps) {
                byte[] reused = Frame(canvas, view, Status(step.Item1, step.Item3), step.Item2);
                var fresh = new CardView(null); var freshCanvas = Canvas(fresh);
                byte[] expected = Frame(freshCanvas, fresh, Status(step.Item1, step.Item3), step.Item2);
                string name = step.Item1 + "/" + (step.Item2 ? "compact" : "standard") + "/" + step.Item3;
                check(canvas.ActualHeight == freshCanvas.ActualHeight, "reused card kept the previous frame's height: " + name);
                check(reused.SequenceEqual(expected), "reused card frame differs from a fresh card: " + name);
            }
        }
    }
}
