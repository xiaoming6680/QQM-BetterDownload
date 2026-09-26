// Native rendition of NCM-Better-Download/plugin/progress-card.js (GPL-3.0).
// Layout and interaction match the existing card; QQ Music uses a green accent.
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace QqmBetterDownload {
    public interface ICardPresenter : IDisposable {
        void Configure(string notificationMode, bool small, int stayMs);
        void Receive(WorkStatus status, bool force = false);
        void Dismiss();
    }
    public sealed class CardSession {
        string identity = "";
        bool dismissed, successShown, errorShown;
        public bool Visible { get; private set; }
        public bool Apply(WorkStatus status, string mode, bool force) {
            if (status == null) return false;
            string id = String.IsNullOrEmpty(status.Id) ? status.Source : status.Id;
            if (status.State == "skipped") { if (id == identity) Hide(); return false; }
            if (status.State != "converting" && status.State != "waiting" && status.State != "success" && status.State != "error") return false;
            if (!force && (mode == "off" || (mode == "errors" && status.State != "error"))) { Hide(); return false; }
            if (String.IsNullOrEmpty(id)) id = "system:" + status.Message;
            bool changed = id != identity;
            if (changed) { identity = id; dismissed = false; successShown = errorShown = false; }
            bool terminal = status.State == "success" || status.State == "error";
            // Completion can appear once after a long conversion was dismissed.
            // Subsequent progress/metadata updates never restart the hide timer.
            bool terminalShown = status.State == "success" ? successShown : errorShown;
            bool show = (changed && !dismissed) || (terminal && !terminalShown);
            if (status.State == "success") successShown = true;
            if (status.State == "error") errorShown = true;
            if (show) Visible = true;
            return show;
        }
        public void Hide() { Visible = false; dismissed = true; }
    }

    public sealed class CardView : Grid {
        public const double CardWidth = 300;
        static readonly Color Mint = Color.FromRgb(51, 218, 165);
        readonly Border frame, glow;
        readonly Action open;
        TextBlock title, detail, state, brand, percent, summary, format, notice;
        Image art;
        Border formatBadge, fill;
        Grid line, bottom;
        Button folder;
        FrameworkElement root;
        string layout = "";
        WorkStatus current;
        ImageSource currentArt;
        bool compact;
        public CardView(Action onOpen) {
            open = onOpen; Width = CardWidth; HorizontalAlignment = HorizontalAlignment.Left;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            frame = new Border {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
                Background = Gradient("#F01A2425", "#F011191C", 110),
                BorderBrush = Gradient("#658CE6C0", "#182F5146", 125),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 26, ShadowDepth = 8, Opacity = .34 }
            };
            glow = new Border { CornerRadius = new CornerRadius(14), IsHitTestVisible = false };
            Children.Add(frame); Children.Add(glow);
            AutomationProperties.SetName(this, "QQ 音乐转换通知");
            SizeChanged += delegate { ClipLine(); };
            SetGlow(Mint);
        }
        static Color ColorOf(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static Brush BrushOf(string hex) { var brush = new SolidColorBrush(ColorOf(hex)); brush.Freeze(); return brush; }
        static LinearGradientBrush Gradient(string a, string b, double angle) { var brush = new LinearGradientBrush(ColorOf(a), ColorOf(b), angle); brush.Freeze(); return brush; }
        static TextBlock Text(double size, string color, bool bold) {
            return new TextBlock { FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = size, Foreground = BrushOf(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        }
        static void Cell(Grid grid, UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        public void Update(WorkStatus activity, bool isCompact, ImageSource image) {
            current = activity; compact = isCompact;
            bool done = activity.State == "success", error = activity.State == "error", waiting = activity.State == "waiting";
            string key = compact + ":" + done + ":" + error;
            if (layout != key) { layout = key; Build(done, error); }
            TrackInfo info = activity.Track;
            string name = info == null ? "" : info.Title;
            if (String.IsNullOrEmpty(name)) name = String.IsNullOrEmpty(activity.Source) ? "BetterDownload" : Path.GetFileNameWithoutExtension(activity.Source);
            title.Text = name; title.ToolTip = name;
            string label = done ? "已完成" : error ? "未完成" : waiting ? "等待中" : "转换中";
            state.Text = "●  " + label;
            state.Foreground = BrushOf(error ? "#FFB7A8" : waiting ? "#E6D5A1" : "#9DEACA");
            string ext = info == null ? "" : info.Format;
            if (String.IsNullOrEmpty(ext) && done && !String.IsNullOrEmpty(activity.Output)) ext = Path.GetExtension(activity.Output).TrimStart('.').ToUpperInvariant();
            format.Text = ext; formatBadge.Visibility = String.IsNullOrEmpty(ext) || error ? Visibility.Collapsed : Visibility.Visible;
            string artist = info == null ? "" : info.Artist;
            string lineText = error || waiting ? activity.Message : done ? (artist.Length > 0 ? artist + " · 原音质已保留" : "原音质已保留 · 音乐已就绪")
                : activity.Percent >= 90 ? activity.Message : activity.Pending > 1 ? "还剩 " + (activity.Pending - 1) + " 首 · 正在转换" : artist.Length > 0 ? artist + " · 正在整理音频" : "正在整理音频与封面";
            detail.Text = lineText; detail.ToolTip = lineText;
            notice.Text = done && !String.IsNullOrEmpty(activity.Warning) ? ShortWarning(activity.Warning) : "";
            notice.ToolTip = activity.Warning; notice.Visibility = notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            summary.Text = "原文件已保留";
            folder.Visibility = done && !String.IsNullOrEmpty(activity.Output) ? Visibility.Visible : Visibility.Collapsed;
            int value = Math.Max(0, Math.Min(99, activity.Percent));
            percent.Text = value + "%"; percent.Visibility = value == 0 || waiting ? Visibility.Collapsed : Visibility.Visible;
            if (!Object.ReferenceEquals(currentArt, image)) { currentArt = image; art.Source = image ?? Mark(); SetGlow(image == null ? Mint : Average(image)); }
            if (art.Source == null) art.Source = Mark();
            AutomationProperties.SetName(this, name + "，" + label + "，" + lineText);
            SetProgress(false);
        }
        static string ShortWarning(string warning) {
            if (warning.Contains("没有封面") || warning.Contains("未找到精确匹配")) return "无本地封面 · 音频已保存";
            if (warning.Contains("封面")) return "封面未写入 · 音频已保存";
            return warning;
        }
        void Build(bool done, bool error) {
            if (root != null) Children.Remove(root);
            var body = new Grid { Margin = compact ? new Thickness(13, 10, 13, 10) : new Thickness(15, 14, 15, 13) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 30 : 44) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cover = new Border { Width = compact ? 30 : 44, Height = compact ? 30 : 44, CornerRadius = new CornerRadius(compact ? 8 : 10), VerticalAlignment = VerticalAlignment.Top, Background = Gradient("#B3EFA0", "#0AAA8A", 130) };
            art = new Image { Stretch = Stretch.UniformToFill, Source = currentArt ?? Mark() };
            art.Clip = new RectangleGeometry(new Rect(0, 0, cover.Width, cover.Height), compact ? 8 : 10, compact ? 8 : 10); cover.Child = art;
            Cell(top, cover, 0, 0);
            title = Text(compact ? 12.5 : 13.5, "#F5FAF7", true);
            state = Text(10.5, "#9DEACA", true);
            brand = Text(9.5, "#B8CBC4", true); brand.Text = "BetterDownload";
            detail = Text(11, "#C7D8D1", false); detail.LineHeight = 16;
            format = Text(9, "#CDF8DD", true);
            formatBadge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Background = BrushOf("#3037D99F"), BorderBrush = BrushOf("#4267E4B4"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), Child = format, VerticalAlignment = VerticalAlignment.Center };
            var description = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 0) }; description.Children.Add(formatBadge); description.Children.Add(detail);
            folder = FolderButton(compact); folder.Click += delegate { if (open != null) open(); };
            if (compact) {
                title.Margin = new Thickness(10, 0, 8, 0); Cell(top, title, 0, 1);
                state.Margin = new Thickness(0, 0, 2, 0); Cell(top, state, 0, 2);
                folder.Margin = new Thickness(7, 0, 0, 0); Cell(top, folder, 0, 3);
                if (error) {
                    description.Children.Remove(detail);
                    top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    detail.Margin = new Thickness(10, 5, 0, 0); detail.TextWrapping = TextWrapping.Wrap; detail.MaxHeight = 66;
                    Cell(top, detail, 1, 1); Grid.SetColumnSpan(detail, 3);
                }
            } else {
                var copy = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumnSpan(copy, 3); Cell(top, copy, 0, 1);
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 3) }; DockPanel.SetDock(state, Dock.Right); head.Children.Add(state); head.Children.Add(brand);
                copy.Children.Add(head); copy.Children.Add(title); copy.Children.Add(description);
                if (error) { detail.TextWrapping = TextWrapping.Wrap; detail.MaxHeight = 66; }
            }
            Cell(body, top, 0, 0);
            bottom = new Grid { Margin = new Thickness(0, 12, 0, 0), MinHeight = 23 };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line = new Grid { Height = compact ? 2 : 3, Background = BrushOf("#26FFFFFF"), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
            fill = new Border { Height = compact ? 2 : 3, Width = 0, CornerRadius = new CornerRadius(2), Background = Gradient("#B2F18C", "#35D8B6", 0), HorizontalAlignment = HorizontalAlignment.Left };
            line.Children.Add(fill); line.SizeChanged += delegate { SetProgress(true); ClipLine(); };
            percent = Text(10.5, "#CBE1D5", true); percent.MinWidth = 32; percent.TextAlignment = TextAlignment.Right; percent.Margin = new Thickness(10, 0, 0, 0);
            summary = Text(10.5, "#B9CEC4", false);
            if (compact) {
                if (!done && !error) { line.VerticalAlignment = VerticalAlignment.Bottom; line.Margin = new Thickness(-12, 0, -12, -9); Cell(body, line, 0, 0); line.IsHitTestVisible = false; }
            } else {
                if (done) { Cell(bottom, summary, 0, 0); Cell(bottom, folder, 0, 1); }
                else { Cell(bottom, line, 0, 0); Cell(bottom, percent, 0, 1); }
                bottom.Visibility = error ? Visibility.Collapsed : Visibility.Visible; Cell(body, bottom, 1, 0);
            }
            notice = Text(10.5, "#EAD7A8", false); notice.Margin = new Thickness(0, 8, 0, 0); notice.TextWrapping = TextWrapping.Wrap; notice.MaxHeight = 54; notice.LineHeight = 16;
            Cell(body, notice, 2, 0); root = body; Children.Add(body);
        }
        void SetProgress(bool instant) {
            if (fill == null || current == null) return;
            double total = Math.Max(0, line.ActualWidth), value = Math.Max(0, Math.Min(99, current.Percent));
            bool waiting = value == 0 || current.State == "waiting";
            double width = total * (waiting ? .32 : value / 100);
            if (instant || !SystemParameters.ClientAreaAnimation) { fill.BeginAnimation(WidthProperty, null); fill.Width = width; }
            else fill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            if (waiting && SystemParameters.ClientAreaAnimation) {
                var shift = fill.RenderTransform as TranslateTransform;
                if (shift == null) { shift = new TranslateTransform(); fill.RenderTransform = shift; }
                if (!shift.HasAnimatedProperties) shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-width, total, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
            } else fill.RenderTransform = Transform.Identity;
        }
        public void FreezeProgress() {
            if (fill == null || current == null) return;
            fill.BeginAnimation(WidthProperty, null); fill.Width = Math.Max(0, line.ActualWidth) * Math.Max(0, Math.Min(100, current.Percent)) / 100;
            fill.RenderTransform = Transform.Identity;
            ClipLine();
        }
        public Rect FolderBounds {
            get { return folder == null || folder.Visibility != Visibility.Visible ? Rect.Empty : new Rect(folder.TranslatePoint(new Point(), this), folder.RenderSize); }
        }
        void ClipLine() {
            if (line == null || line.ActualWidth <= 0 || ActualHeight <= 0) return;
            if (compact && line.Parent != null) {
                Point offset = line.TranslatePoint(new Point(0, 0), this);
                line.Clip = new RectangleGeometry(new Rect(-offset.X, -offset.Y, ActualWidth, ActualHeight), 14, 14);
            } else line.Clip = new RectangleGeometry(new Rect(0, 0, line.ActualWidth, line.ActualHeight), 2, 2);
        }
        void SetGlow(Color color) {
            var brush = new RadialGradientBrush { Center = new Point(.05, .05), GradientOrigin = new Point(.05, .05), RadiusX = 1.1, RadiusY = 1.8 };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(72, color.R, color.G, color.B), 0)); brush.GradientStops.Add(new GradientStop(Colors.Transparent, .85)); brush.Freeze(); glow.Background = brush;
        }
        static Color Average(ImageSource source) {
            try {
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, 6, 6));
                var bitmap = new RenderTargetBitmap(6, 6, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                byte[] bytes = new byte[144]; bitmap.CopyPixels(bytes, 24, 0); int r = 0, g = 0, b = 0;
                for (int i = 0; i < bytes.Length; i += 4) { b += bytes[i]; g += bytes[i + 1]; r += bytes[i + 2]; }
                return Color.FromRgb((byte)(r / 36), (byte)(g / 36), (byte)(b / 36));
            } catch { return Mint; }
        }
        static Button FolderButton(bool compact) {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,4.6 Q2,3 3.6,3 L6.1,3 7.6,4.6 12.4,4.6 Q14,4.6 14,6.2 L14,11.4 Q14,13 12.4,13 L3.6,13 Q2,13 2,11.4 Z"), Stroke = BrushOf("#E5F5EA"), StrokeThickness = 1.4, Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
            if (!compact) { var label = Text(10.5, "#E5F5EA", true); label.Text = "打开文件夹"; label.Margin = new Thickness(5, 0, 0, 0); content.Children.Add(label); }
            var button = new Button { Content = content, Padding = compact ? new Thickness(6, 5, 6, 5) : new Thickness(8, 5, 9, 5), Background = BrushOf("#18FFFFFF"), BorderBrush = BrushOf("#25FFFFFF"), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "打开输出文件夹" };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty)); border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf("#30FFFFFF"))); template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Control.BorderBrushProperty, BrushOf("#92F2C9"))); template.Triggers.Add(focus); button.Template = template;
            AutomationProperties.SetName(button, "打开输出文件夹"); return button;
        }
        public static ImageSource Mark() {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open()) {
                dc.DrawRectangle(Gradient("#BBEE9C", "#00AB8D", 135), null, new Rect(0, 0, 44, 44));
                var pen = new Pen(Brushes.White, 2.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, Geometry.Parse("M18,12 L18,29 M11,22 L18,29 25,22 M11,34 L28,34"));
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(27, 16, 9, 7), 1.7, 1.7);
                dc.DrawGeometry(null, new Pen(Brushes.White, 1.7), Geometry.Parse("M29,16 L29,13 C29,9 35,9 35,12"));
            }
            drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
        }
        public static ImageSource LoadArt(byte[] bytes) {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) return null;
            try {
                using (var stream = new MemoryStream(bytes, false)) {
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 128; bitmap.StreamSource = stream; bitmap.EndInit();
                    if (bitmap.PixelHeight > 4096) return null; bitmap.Freeze(); return bitmap;
                }
            } catch { return null; }
        }
    }

    // Separate window only for the explicitly requested developer preview.
    public sealed class ProgressCard : Window, ICardPresenter {
        readonly CardView view;
        readonly CardSession session = new CardSession();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Func<IntPtr> anchor;
        readonly Action<string> openFolder;
        WorkStatus latest;
        TrackInfo lastTrack;
        byte[] artBytes;
        ImageSource art;
        string mode = "all";
        bool compact, disposed, interacting;
        int stay = 4000;
        DateTime deadline;
        public ProgressCard(Func<IntPtr> anchorWindow, Action<string> onOpen) {
            anchor = anchorWindow; openFolder = onOpen;
            AllowsTransparency = true; WindowStyle = WindowStyle.None; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false;
            SizeToContent = SizeToContent.Height; Width = CardView.CardWidth + 40; FontFamily = new FontFamily("Microsoft YaHei UI");
            Title = "BetterDownload · 转换进度";
            view = new CardView(Open); view.Margin = new Thickness(20, 16, 20, 22); Content = view;
            SourceInitialized += delegate {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80); // tool window: no taskbar/Alt-Tab entry
            };
            MouseEnter += delegate { interacting = true; };
            MouseLeave += delegate { interacting = false; Arm(); };
            GotKeyboardFocus += delegate { interacting = true; };
            LostKeyboardFocus += delegate { interacting = IsMouseOver; Arm(); };
            Deactivated += delegate { interacting = false; Arm(); };
            PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) { Dismiss(); e.Handled = true; } };
            MouseRightButtonUp += delegate { Dismiss(); };
            SizeChanged += delegate { if (IsVisible) Position(); };
            timer.Tick += delegate {
                if (IsVisible && !interacting && !IsMouseOver && !IsKeyboardFocusWithin && DateTime.UtcNow >= deadline) Dismiss();
            };
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        }
        public void Configure(string notificationMode, bool small, int stayMs) {
            mode = notificationMode == "off" || notificationMode == "errors" ? notificationMode : "all";
            compact = small; stay = stayMs == 2000 || stayMs == 6000 ? stayMs : 4000;
            if (latest != null) view.Update(latest, compact, art);
            if (mode == "off" || (mode == "errors" && latest != null && latest.State != "error")) Dismiss();
            else if (IsVisible) Arm();
        }
        public void Receive(WorkStatus status, bool force = false) {
            if (disposed) return;
            bool show = session.Apply(status, mode, force);
            if (!session.Visible) { Hide(); timer.Stop(); return; }
            if (status.State != "converting" && status.State != "waiting" && status.State != "success" && status.State != "error") return;
            bool changed = latest == null || (status.Id.Length > 0 ? latest.Id != status.Id : latest.Source != status.Source);
            if (changed) lastTrack = null;
            if (status.Track != null) lastTrack = status.Track;
            status.Track = lastTrack;
            latest = status;
            byte[] bytes = lastTrack == null ? null : lastTrack.Artwork;
            if (!Object.ReferenceEquals(bytes, artBytes)) { artBytes = bytes; art = CardView.LoadArt(bytes); }
            view.Update(status, compact, art);
            if (show) {
                interacting = false; Opacity = 1; Show(); UpdateLayout(); Position(); Arm(); timer.Start();
                if (SystemParameters.ClientAreaAnimation) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                if (SystemParameters.ClientAreaAnimation) {
                    var shift = new TranslateTransform(); view.RenderTransform = shift;
                    shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
            }
        }
        void Arm() { deadline = DateTime.UtcNow.AddMilliseconds(stay); }
        public void Dismiss() { session.Hide(); timer.Stop(); Hide(); }
        void Open() {
            if (latest == null || String.IsNullOrEmpty(latest.Output)) return;
            try { if (openFolder != null) openFolder(Path.GetDirectoryName(latest.Output)); }
            finally { interacting = false; System.Windows.Input.Keyboard.ClearFocus(); Arm(); }
        }
        void Position() {
            IntPtr parent = anchor == null ? IntPtr.Zero : anchor();
            var work = parent == IntPtr.Zero ? Forms.Screen.PrimaryScreen.WorkingArea : Forms.Screen.FromHandle(parent).WorkingArea;
            NativeRect client = new NativeRect(); bool inClient = parent != IntPtr.Zero && IsWindowVisible(parent) && !IsIconic(parent) && GetWindowRect(parent, out client);
            var source = PresentationSource.FromVisual(this); double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
            int right = inClient ? Math.Min(client.Right, work.Right) : work.Right;
            int bottom = inClient ? Math.Min(client.Bottom - (int)(80 * scale), work.Bottom) : work.Bottom;
            int x = Math.Max(work.Left, Math.Min(work.Right - width, right - width - (int)(2 * scale)));
            int y = Math.Max(work.Top, Math.Min(work.Bottom - height, bottom - height - (int)(2 * scale)));
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // no size, no z-order, no activation
        }
        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); Close(); }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
