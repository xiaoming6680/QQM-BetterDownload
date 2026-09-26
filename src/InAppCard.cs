using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QqmBetterDownload {
    // A real child of QQ's client area. WPF draws the shared card into an alpha
    // surface; Win32 clips, moves and hides that surface with the QQ window.
    internal sealed class InAppCard : Forms.NativeWindow, ICardPresenter {
        readonly IntPtr parent;
        readonly Action<string> openFolder;
        readonly CardView view = new CardView(null);
        readonly Border canvas;
        readonly CardSession session = new CardSession();
        readonly Forms.Timer timer = new Forms.Timer { Interval = 16 };
        readonly Stopwatch animation = new Stopwatch();
        WorkStatus latest;
        TrackInfo track;
        byte[] artwork;
        ImageSource image;
        string mode = "all";
        bool compact, hovered, showing, leaving, disposed;
        bool raise;
        int stay = 4000, width, height;
        double scale, animationStart, animationEnd, offset;
        DateTime deadline;
        IntPtr dc, bitmap, previousBitmap;
        internal string LastError { get; private set; }
        internal bool IsPresented { get { return showing && ClientUi.IsWindowVisible(Handle); } }

        internal InAppCard(IntPtr client, Action<string> onOpen) {
            if (!ClientUi.IsWindow(client)) throw new ArgumentException("QQ 音乐窗口已关闭。");
            parent = client; openFolder = onOpen;
            canvas = new Border { Child = view, Padding = new Thickness(20, 16, 20, 22), Background = Brushes.Transparent };
            using (new ClientUi.DpiScope(parent)) {
                CreateHandle(new Forms.CreateParams { Caption = "BetterDownload · 转换进度", Parent = parent,
                    Style = ClientUi.Child, ExStyle = ClientUi.Layered | ClientUi.NoActivate, Width = 1, Height = 1 });
                ClientUi.RequireChild(Handle, parent);
            }
            timer.Tick += delegate { Tick(); };
        }
        public void Configure(string notificationMode, bool small, int stayMs) {
            mode = notificationMode == "off" || notificationMode == "errors" ? notificationMode : "all";
            compact = small; stay = stayMs == 2000 || stayMs == 6000 ? stayMs : 4000;
            if (mode == "off" || (mode == "errors" && latest != null && latest.State != "error")) { Dismiss(); return; }
            if (latest != null) { Render(); if (showing) { Arm(); Present(); } }
        }
        public void Receive(WorkStatus status, bool force = false) {
            if (disposed || !ClientUi.IsWindow(parent)) return;
            bool appear = session.Apply(status, mode, force);
            if (!session.Visible) { Dismiss(); return; }
            if (status.State != "converting" && status.State != "waiting" && status.State != "success" && status.State != "error") return;
            if (latest == null || (status.Id.Length > 0 ? latest.Id != status.Id : latest.Source != status.Source)) track = null;
            if (status.Track != null) track = status.Track;
            status.Track = track; latest = status;
            var bytes = track == null ? null : track.Artwork;
            if (!Object.ReferenceEquals(bytes, artwork)) { artwork = bytes; image = CardView.LoadArt(bytes); }
            Render();
            if (appear) {
                hovered = false; Arm();
                double from = showing ? offset : 22;
                leaving = false; showing = true; raise = true; Animate(from, 0); timer.Start();
            }
            if (showing) Present();
        }
        void Render() {
            if (latest == null) return;
            using (new ClientUi.DpiScope(parent)) {
                scale = ClientUi.Scale(parent);
                view.Update(latest, compact, image);
                canvas.Measure(new Size(CardView.CardWidth + 40, 500));
                canvas.Arrange(new Rect(0, 0, CardView.CardWidth + 40, canvas.DesiredSize.Height));
                canvas.UpdateLayout(); view.FreezeProgress();
                width = (int)Math.Ceiling(canvas.ActualWidth * scale); height = (int)Math.Ceiling(canvas.ActualHeight * scale);
                var frame = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); frame.Render(canvas);
                var pixels = new byte[width * height * 4]; frame.CopyPixels(pixels, width * 4, 0);
                ReleaseSurface();
                dc = CreateCompatibleDC(IntPtr.Zero);
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
                IntPtr buffer; bitmap = CreateDIBSection(dc, ref info, 0, out buffer, IntPtr.Zero, 0);
                if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || buffer == IntPtr.Zero) { ReleaseSurface(); throw new Win32Exception("无法绘制进度卡片。"); }
                previousBitmap = SelectObject(dc, bitmap); Marshal.Copy(pixels, 0, buffer, pixels.Length);
            }
        }
        void Animate(double from, double to) { animationStart = from; animationEnd = to; offset = from; animation.Restart(); }
        void Arm() { deadline = DateTime.UtcNow.AddMilliseconds(stay); }
        public void Dismiss() {
            session.Hide(); hovered = false;
            if (!showing || leaving) return;
            leaving = true; Animate(offset, CardView.CardWidth + 64); timer.Start();
        }
        void Tick() {
            if (disposed) return;
            if (!ClientUi.IsWindow(parent)) { HideNow(); return; }
            if (!leaving && !hovered && DateTime.UtcNow >= deadline) Dismiss();
            double t = SystemParameters.ClientAreaAnimation ? Math.Min(1, animation.Elapsed.TotalMilliseconds / 420) : 1;
            double ease = 1 - Math.Pow(1 - t, 3); offset = animationStart + (animationEnd - animationStart) * ease;
            if (leaving && t >= 1) { HideNow(); return; }
            if (Math.Abs(scale - ClientUi.Scale(parent)) > .01) Render();
            Present();
        }
        void HideNow() { showing = leaving = false; timer.Stop(); if (Handle != IntPtr.Zero) ClientUi.ShowWindow(Handle, 0); }
        void Present() {
            if (!showing || dc == IntPtr.Zero || Handle == IntPtr.Zero) return;
            using (new ClientUi.DpiScope(parent)) {
                ClientUi.Rect client;
                if (!ClientUi.GetClientRect(parent, out client) || client.Width < 500 * scale || client.Height < 350 * scale || !ClientUi.IsWindowVisible(parent) || ClientUi.IsIconic(parent)) {
                    ClientUi.ShowWindow(Handle, 0); return;
                }
                int x = client.Width - width - (int)(2 * scale) + (int)(offset * scale), y = client.Height - height - (int)(96 * scale);
                var dest = new ClientUi.Point(x, Math.Max(0, y)); ClientUi.ClientToScreen(parent, ref dest);
                var size = new ClientUi.Point(width, height); var origin = new ClientUi.Point();
                double alpha = leaving ? Math.Max(0, 1 - animation.Elapsed.TotalMilliseconds / 320) : Math.Min(1, animation.Elapsed.TotalMilliseconds / 320);
                if (!SystemParameters.ClientAreaAnimation) alpha = leaving ? 0 : 1;
                var blend = new Blend { Operation = 0, ConstantAlpha = (byte)(255 * alpha), AlphaFormat = 1 };
                if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref dest, ref size, dc, ref origin, 0, ref blend, 2)) {
                    LastError = "卡片绘制失败（" + Marshal.GetLastWin32Error() + "）"; HideNow(); return;
                }
                ClientUi.SetWindowPos(Handle, IntPtr.Zero, x, Math.Max(0, y), width, height, raise ? 0x0050u : 0x0054u);
                raise = false; // Do not keep reordering foreign siblings on animation ticks.
            }
        }
        protected override void WndProc(ref Forms.Message message) {
            if (message.Msg == 0x21) { message.Result = (IntPtr)3; return; } // MA_NOACTIVATE
            if (message.Msg == 0x200 && !leaving) { // hover pauses dismissal
                if (!hovered) { hovered = true; var tracking = new Track { Size = (uint)Marshal.SizeOf(typeof(Track)), Flags = 2, Window = Handle }; TrackMouseEvent(ref tracking); }
            }
            if (message.Msg == 0x2a3) { hovered = false; Arm(); }
            if (message.Msg == 0x205) { Dismiss(); return; }
            if (message.Msg == 0x202 && !leaving && latest != null && !String.IsNullOrEmpty(latest.Output)) {
                long packed = message.LParam.ToInt64();
                var point = new Point((short)(packed & 65535) / scale - 20, (short)((packed >> 16) & 65535) / scale - 16);
                if (view.FolderBounds.Contains(point) && openFolder != null) { try { openFolder(Path.GetDirectoryName(latest.Output)); } finally { hovered = false; Arm(); } }
            }
            base.WndProc(ref message);
        }
        void ReleaseSurface() {
            if (dc != IntPtr.Zero && previousBitmap != IntPtr.Zero) SelectObject(dc, previousBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            bitmap = dc = previousBitmap = IntPtr.Zero;
        }
        public void Dispose() { if (disposed) return; disposed = true; HideNow(); timer.Dispose(); ReleaseSurface(); DestroyHandle(); }
        [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPixels, YPixels; public uint Colors, Important; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] struct Track { public uint Size, Flags; public IntPtr Window; public uint HoverTime; }
        [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32")] static extern bool TrackMouseEvent(ref Track track);
        [DllImport("user32", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destDc, ref ClientUi.Point dest, ref ClientUi.Point size, IntPtr srcDc, ref ClientUi.Point src, uint key, ref Blend blend, uint flags);
    }
}
