using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QqmBetterDownload {
    public static class CardPreview {
        static SolidColorBrush Brush(string value) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        static void Put(Canvas canvas, UIElement child, double left, double top) { Canvas.SetLeft(child, left); Canvas.SetTop(child, top); canvas.Children.Add(child); }
        static TextBlock Label(string text, double size, string color, bool bold) {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        }
        public static ImageSource SampleCover() {
            var group = new DrawingGroup();
            using (var dc = group.Open()) {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(54, 139, 149), Color.FromRgb(15, 36, 61), 130), null, new Rect(0, 0, 44, 44));
                dc.DrawEllipse(Brush("#CFDFAD"), null, new Point(30, 13), 7, 7);
                dc.DrawGeometry(Brush("#122D37"), null, Geometry.Parse("M0,28 L11,21 26,36 36,24 44,30 44,44 0,44 Z"));
                dc.DrawLine(new Pen(Brush("#83C2AC"), .5), new Point(0, 37), new Point(44, 37));
                dc.DrawLine(new Pen(Brush("#69998A"), .5), new Point(0, 40), new Point(44, 40));
            }
            group.Freeze(); var image = new DrawingImage(group); image.Freeze(); return image;
        }
        public static void Save(string path, double scale = 2) {
            const int width = 1080, height = 566;
            var canvas = new Canvas { Width = width, Height = height, Background = new LinearGradientBrush(Color.FromRgb(244, 248, 246), Color.FromRgb(233, 240, 236), 90) };
            Put(canvas, new Border { Width = 28, Height = 3, Background = Brush("#1ECC94"), CornerRadius = new CornerRadius(2) }, 48, 41);
            Put(canvas, Label("BetterDownload", 28, "#1F2E29", true), 48, 55);
            Put(canvas, Label("熟悉的卡片，换上 QQ 音乐绿。", 13, "#5E6D66", false), 50, 101);
            Put(canvas, Label("标准", 12, "#47554F", true), 48, 146);
            Put(canvas, Label("简洁", 12, "#47554F", true), 48, 356);
            var states = new[] {
                new WorkStatus { Id = "preview-1", State = "converting", Source = "示例.mflac", Percent = 64, Pending = 4, Message = "正在转换", Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" } },
                new WorkStatus { Id = "preview-2", State = "success", Source = "示例.mflac", Output = @"C:\Music\unlock\示例.flac", Percent = 100, Message = "转换完成", Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" } },
                new WorkStatus { Id = "preview-3", State = "error", Source = "一首文件名很长的歌曲.mflac", Message = "当前 QQ 音乐版本暂未适配。原文件已保留，请更新插件后重试。" }
            };
            for (int row = 0; row < 2; row++) for (int col = 0; col < 3; col++) {
                var card = new CardView(null); card.Update(states[col], row == 1, col == 2 ? null : SampleCover());
                Put(canvas, card, 48 + col * 342, row == 0 ? 176 : 386);
            }
            Put(canvas, Label("封面取色  /  悬停保持  /  2 · 4 · 6 秒收起", 12, "#5E6D66", false), 48, 519);
            Put(canvas, Label("样式预览 · 示例数据", 11, "#8A9A93", false), 877, 520);
            canvas.Measure(new Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout();
            foreach (UIElement child in canvas.Children) { var card = child as CardView; if (card != null) card.FreezeProgress(); }
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var file = File.Create(path)) png.Save(file);
        }
        // Render the real card controls with fictional metadata, off screen.
        public static void SaveReadmeCards(string folder) {
            Directory.CreateDirectory(folder);
            string[] files = { "card-converting.png", "card-complete.png", "card-compact.png", "card-error.png" };
            for (int i = 0; i < files.Length; i++) {
                bool compact = i == 2;
                var activity = new WorkStatus {
                    Id = "readme-" + i, State = i == 1 ? "success" : i == 3 ? "error" : "converting",
                    Source = "示例歌曲.mflac", Output = i == 1 ? @"D:\Music\VipSongsDownload\unlock\夜间来信.flac" : "",
                    Percent = i == 1 ? 100 : 64, Message = i == 3 ? "当前客户端接口尚未适配。下载任务已保留，请等待适配更新。" : "正在转换",
                    Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" }
                };
                var card = new CardView(null); card.Update(activity, compact, i == 3 ? null : SampleCover());
                const int width = 364, height = 236;
                var canvas = new Canvas { Width = width, Height = height, Background = new LinearGradientBrush(Color.FromRgb(243, 247, 245), Color.FromRgb(232, 239, 235), 90) };
                card.Measure(new Size(CardView.CardWidth, height)); Put(canvas, card, (width - CardView.CardWidth) / 2, (height - card.DesiredSize.Height) / 2);
                canvas.Measure(new Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout(); card.FreezeProgress();
                var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(canvas);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(folder, files[i]))) png.Save(file);
            }
        }
    }
}
