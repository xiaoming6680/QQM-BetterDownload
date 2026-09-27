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
        // The canvas is reused for every frame. A style switch or new wording
        // changes the height only in the queued layout pass, so settle that
        // first; one pass would size the texture from the previous frame and
        // stretch or clip the card. Then apply the frozen progress width.
        internal static void Layout(Border canvas, CardView view) {
            double width = CardView.CardWidth + 40;
            canvas.Measure(new Size(width,500));canvas.UpdateLayout();
            canvas.Arrange(new Rect(0,0,width,canvas.DesiredSize.Height));canvas.UpdateLayout();
            view.FreezeProgress();canvas.UpdateLayout();
        }
        void Render(bool appear) {
            if (!ui.Ready) return;
            view.Update(latest, compact, image);
            Layout(canvas, view);
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
    // A QQ Music build whose UI is not adapted still shows progress: the
    // standalone card anchors to the client window instead of drawing inside it.
    internal sealed class AdaptiveCard : ICardPresenter {
        readonly NativeUiSession ui;
        readonly Func<ProgressCard> create;
        ProgressCard window;
        string mode = "all";
        bool compact;
        int stay = 4000;
        internal NativeCard Native { get; private set; }
        internal bool WindowVisible { get { return window != null && window.IsVisible; } }
        internal AdaptiveCard(NativeUiSession session, NativeCard inside, Func<ProgressCard> outside) { ui = session; Native = inside; create = outside; }
        public void Configure(string notify, bool small, int duration) {
            mode = notify; compact = small; stay = duration;
            Native.Configure(notify, small, duration); if (window != null) window.Configure(notify, small, duration);
        }
        public void Receive(WorkStatus status, bool force = false) {
            if (!ui.Unavailable) { if (window != null) window.Dismiss(); Native.Receive(status, force); return; }
            if (window == null) { window = create(); window.Configure(mode, compact, stay); }
            window.Receive(status, force);
        }
        public void Dismiss() { Native.Dismiss(); if (window != null) window.Dismiss(); }
        public void Dispose() { Native.Dispose(); if (window != null) window.Dispose(); }
    }
}
