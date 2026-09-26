using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QqmBetterDownload {
    // Reuse the original card design. GF owns the texture, clipping, placement,
    // hover and animation in QQ; this presenter never creates a visible HWND.
    internal sealed class NativeCard : ICardPresenter {
        readonly NativeUiSession ui;
        readonly IntPtr window;
        readonly Action<string> openFolder;
        readonly CardView view = new CardView(null);
        readonly Border canvas;
        readonly CardSession session = new CardSession();
        readonly Queue<string> frames = new Queue<string>();
        WorkStatus latest;
        TrackInfo track;
        byte[] artwork;
        ImageSource image;
        string mode = "all";
        bool compact, disposed;
        int stay = 4000;
        uint sequence;
        internal NativeCard(NativeUiSession surface, IntPtr parent, Action<string> open) {
            ui = surface; window = parent; openFolder = open;
            canvas = new Border { Child = view, Padding = new Thickness(20,16,20,22), Background = Brushes.Transparent };
        }
        public void Configure(string notify, bool small, int duration) {
            bool changed = compact != small || stay != duration;
            mode = notify; compact = small; stay = duration;
            if (mode == "off" || (mode == "errors" && latest != null && latest.State != "error")) Dismiss();
            else if (latest != null && ui.CardVisible) Render(changed);
        }
        public void Receive(WorkStatus status, bool force = false) {
            if (disposed) return;
            bool appear = session.Apply(status, mode, force);
            if (!session.Visible) { Dismiss(); return; }
            if (status.State != "converting" && status.State != "waiting" && status.State != "success" && status.State != "error") return;
            if (latest == null || (status.Id.Length > 0 ? latest.Id != status.Id : latest.Source != status.Source)) track = null;
            if (status.Track != null) track = status.Track;
            status.Track = track; latest = status;
            byte[] bytes = track == null ? null : track.Artwork;
            if (!Object.ReferenceEquals(bytes, artwork)) { artwork = bytes; image = CardView.LoadArt(bytes); }
            if (appear || ui.CardVisible) Render(appear);
        }
        void Render(bool appear) {
            if (!ui.Ready) return;
            view.Update(latest, compact, image);
            canvas.Measure(new Size(CardView.CardWidth + 40,500));
            canvas.Arrange(new Rect(0,0,CardView.CardWidth + 40,canvas.DesiredSize.Height));canvas.UpdateLayout();view.FreezeProgress();
            double scale = ClientUi.Scale(window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth * scale),(int)Math.Ceiling(canvas.ActualHeight * scale),96 * scale,96 * scale,PixelFormats.Pbgra32);
            bitmap.Render(canvas);var png = new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            string file = Path.Combine(ui.AssetFolder,"card-" + (++sequence) + ".png");
            using (var output = File.Create(file)) png.Save(output);
            var folder = view.FolderBounds;
            int x = folder.IsEmpty ? 0 : (int)folder.X + 20, y = folder.IsEmpty ? 0 : (int)folder.Y + 16;
            int w = folder.IsEmpty ? 0 : (int)folder.Width, h = folder.IsEmpty ? 0 : (int)folder.Height;
            ui.ShowCard(String.Format(CultureInfo.InvariantCulture,"{0},{1},{2},{3},{4},{5},{6},{7}\ncard-{8}.png",(int)Math.Ceiling(canvas.ActualWidth),(int)Math.Ceiling(canvas.ActualHeight),x,y,w,h,stay,appear ? 1 : 0,sequence));
            frames.Enqueue(file);
            while (frames.Count > 8) { string old = frames.Dequeue(); try { File.Delete(old); } catch (IOException) { } }
        }
        internal void OpenFolder() { if (latest != null && !String.IsNullOrEmpty(latest.Output) && openFolder != null) openFolder(Path.GetDirectoryName(latest.Output)); }
        public void Dismiss() { session.Hide(); ui.DismissCard(); }
        public void Dispose() { if (disposed) return; Dismiss(); disposed = true; }
    }
}
