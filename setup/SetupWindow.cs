using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using QqmBetterDownload;

namespace BetterDownloadSetup {
    // Apple-style sheet that follows the Windows light/dark app setting: the app
    // icon, a grouped list of facts, one prominent button for the recommended
    // step and quiet text links for the rest. The installer only installs,
    // updates, repairs and uninstalls; settings live inside QQ Music.
    internal static class Theme {
        internal static float Scale = 1f;
        internal static bool Dark;
        internal static Color Back, Card, Line, Text, Muted, Glyph, Accent, Link, Ok, Warn, Danger, DangerText;
        static HashSet<string> families;
        internal static float F(float value) { return value * Scale; }
        internal static int S(float value) { return (int)Math.Round(value * Scale); }
        internal static void Load() {
            bool dark = false;
            try { using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize")) dark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0; }
            catch (Exception) { }
            Apply(dark);
        }
        // Apple's system colours for each appearance, with BetterDownload green
        // as the accent; white button labels need its deeper shade.
        internal static void Apply(bool dark) {
            Dark = dark;
            Back = Rgb(dark ? 0x1C1C1E : 0xF5F5F7); Card = Rgb(dark ? 0x2C2C2E : 0xFFFFFF); Line = Rgb(dark ? 0x3A3A3C : 0xE3E3E8);
            Text = Rgb(dark ? 0xF5F5F7 : 0x1D1D1F); Muted = Rgb(dark ? 0x98989D : 0x6E6E73); Glyph = Rgb(dark ? 0xC7C7CC : 0x5A5F66);
            Accent = Rgb(0x10A87A); Link = Rgb(dark ? 0x1ECC94 : 0x07875E); Ok = Rgb(0x1ECC94); Warn = Rgb(dark ? 0xFF9F0A : 0xFF9500);
            Danger = Rgb(dark ? 0xFF453A : 0xFF3B30); DangerText = Rgb(dark ? 0xFF453A : 0xD70015);
        }
        static Color Rgb(int value) { return Color.FromArgb(value >> 16 & 255, value >> 8 & 255, value & 255); }
        internal static Color Mix(Color from, Color to, float amount) {
            return Color.FromArgb((int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
        }
        // PingFang when the user has it installed, otherwise the Windows UI
        // fonts. Sizes are logical pixels, so screen and preview renders match.
        internal static Font Face(float size, bool strong) {
            string name = strong ? "PingFang SC Medium" : "PingFang SC Regular";
            if (Has(name)) return new Font(name, F(size), GraphicsUnit.Pixel);
            return new Font("Microsoft YaHei UI", F(size), strong ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
        internal static Font Title(float size) {
            foreach (string name in new[] { "PingFang SC Semibold", "PingFang SC Medium", "Segoe UI Variable Display Semib" }) if (Has(name)) return new Font(name, F(size), GraphicsUnit.Pixel);
            return new Font("Segoe UI Semibold", F(size), GraphicsUnit.Pixel);
        }
        static bool Has(string name) {
            if (families == null) using (var fonts = new InstalledFontCollection()) families = new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            return families.Contains(name);
        }
        internal static GraphicsPath Round(RectangleF r, float radius) {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        internal static void Write(Graphics g, string text, Font font, Color color, RectangleF r, StringAlignment align, bool wrap) {
            using (var format = Format(align, wrap)) using (var brush = new SolidBrush(color)) g.DrawString(text, font, brush, r, format);
        }
        internal static SizeF Measure(Graphics g, string text, Font font, float width) {
            using (var format = Format(StringAlignment.Near, width > 0)) return g.MeasureString(text, font, new SizeF(width > 0 ? width : 10000, 10000), format);
        }
        static StringFormat Format(StringAlignment align, bool wrap) {
            var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            if (!wrap) format.FormatFlags |= StringFormatFlags.NoWrap;
            return format;
        }
        // A filled circle with a check or an exclamation point.
        internal static void Mark(Graphics g, RectangleF r, Color fill, bool check) {
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, r);
            using (var pen = new Pen(Color.White, r.Width * 0.12f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                Func<float, float, PointF> at = (x, y) => new PointF(r.X + r.Width * x, r.Y + r.Height * y);
                if (check) g.DrawLines(pen, new[] { at(0.29f, 0.52f), at(0.44f, 0.66f), at(0.72f, 0.37f) });
                else { g.DrawLine(pen, at(0.5f, 0.28f), at(0.5f, 0.56f)); g.DrawLine(pen, at(0.5f, 0.72f), at(0.5f, 0.725f)); }
            }
        }
        // The .ico holds PNG frames; read one directly to keep its alpha.
        internal static Bitmap Logo(int side) {
            using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("brand.ico"))
            using (var reader = new BinaryReader(data)) {
                reader.ReadUInt16(); reader.ReadUInt16(); int count = reader.ReadUInt16();
                uint offset = 0, length = 0; int best = Int32.MaxValue;
                for (int i = 0; i < count; i++) {
                    int width = reader.ReadByte(); if (width == 0) width = 256;
                    reader.ReadBytes(7); uint bytes = reader.ReadUInt32(), at = reader.ReadUInt32();
                    int score = width >= side ? width - side : 1000 + side - width;
                    if (score < best) { best = score; offset = at; length = bytes; }
                }
                data.Position = offset;
                using (var png = new MemoryStream(reader.ReadBytes((int)length))) using (var source = new Bitmap(png)) {
                    var result = new Bitmap(side, side);
                    using (var g = Graphics.FromImage(result)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(source, new Rectangle(0, 0, side, side)); }
                    return result;
                }
            }
        }
    }
    internal enum PillKind { Primary, Danger, Link, DangerLink, Close }
    // Capsule buttons, text links and the window's close control. Each paints
    // the sheet background itself, so no system chrome shows through.
    internal sealed class Pill : Button {
        internal PillKind Kind; bool hover, down;
        internal Pill(PillKind kind) {
            Kind = kind; Cursor = Cursors.Hand; TabStop = kind != PillKind.Close;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics); }
        internal void Render(Graphics g) {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var back = new SolidBrush(Theme.Back)) g.FillRectangle(back, 0, 0, Width, Height);
            if (Kind == PillKind.Close) {
                // A quiet cross that turns into macOS's red close button on hover.
                float d = Theme.F(18), k = Theme.F(4.2f), x = Width / 2f, y = Height / 2f;
                if (hover) using (var fill = new SolidBrush(down ? Color.FromArgb(224, 68, 62) : Color.FromArgb(255, 95, 87))) g.FillEllipse(fill, x - d / 2, y - d / 2, d, d);
                using (var pen = new Pen(hover ? Color.FromArgb(150, 90, 0, 0) : Theme.Muted, Theme.F(1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                    g.DrawLine(pen, x - k, y - k, x + k, y + k); g.DrawLine(pen, x - k, y + k, x + k, y - k);
                }
                return;
            }
            float m = Theme.F(3); var r = new RectangleF(m, m, Width - 2 * m, Height - 2 * m); Color label;
            bool filled = Kind == PillKind.Primary || Kind == PillKind.Danger, danger = Kind == PillKind.Danger || Kind == PillKind.DangerLink;
            using (var path = Theme.Round(r, r.Height / 2)) {
                if (filled) {
                    Color fill = danger ? Theme.Danger : Theme.Accent;
                    fill = !Enabled ? Theme.Mix(Theme.Back, fill, 0.45f) : down ? Theme.Mix(fill, Color.Black, 0.14f) : hover ? Theme.Mix(fill, Color.White, 0.1f) : fill;
                    using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                    label = Enabled ? Color.White : Color.FromArgb(Theme.Dark ? 140 : 230, 255, 255, 255);
                } else {
                    Color tone = danger ? Theme.DangerText : Theme.Link;
                    if (hover && Enabled) using (var brush = new SolidBrush(Theme.Mix(Theme.Back, tone, down ? 0.16f : 0.09f))) g.FillPath(brush, path);
                    label = Enabled ? tone : Theme.Mix(Theme.Back, tone, 0.4f);
                }
                if (Focused && ShowFocusCues && Enabled) {
                    var ring = RectangleF.Inflate(r, Theme.F(1.5f), Theme.F(1.5f));
                    using (var outline = Theme.Round(ring, ring.Height / 2)) using (var pen = new Pen(Color.FromArgb(120, danger ? Theme.Danger : Theme.Accent), Theme.F(2))) g.DrawPath(pen, outline);
                }
            }
            Theme.Write(g, Text, Font, label, r, StringAlignment.Center, false);
        }
    }
    // An optional step as a macOS-style checkbox: a rounded square that fills
    // red with a white check, since the one option here deletes data. Toggles
    // with a click or the space bar and paints the sheet background like Pill.
    internal sealed class Tick : Control {
        bool hover, down, value;
        internal event EventHandler Changed;
        internal bool Checked { get { return value; } set { if (this.value == value) return; this.value = value; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); } }
        internal Tick() {
            Cursor = Cursors.Hand; TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { bool click = down && ClientRectangle.Contains(e.Location); down = false; if (click) Checked = !Checked; else Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics); }
        internal float Box { get { return Theme.F(16); } }
        internal void Render(Graphics g) {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var back = new SolidBrush(Theme.Back)) g.FillRectangle(back, 0, 0, Width, Height);
            var box = new RectangleF(Theme.F(2), (Height - Box) / 2, Box, Box);
            using (var path = Theme.Round(box, Theme.F(4.5f))) {
                if (value) using (var fill = new SolidBrush(down ? Theme.Mix(Theme.Danger, Color.Black, 0.14f) : Theme.Danger)) g.FillPath(fill, path);
                else {
                    using (var fill = new SolidBrush(down ? Theme.Mix(Theme.Card, Theme.Line, 0.6f) : Theme.Card)) g.FillPath(fill, path);
                    using (var pen = new Pen(hover ? Theme.Muted : Theme.Mix(Theme.Line, Theme.Muted, 0.45f), Theme.F(1))) g.DrawPath(pen, path);
                }
                if (Focused && ShowFocusCues) {
                    var ring = RectangleF.Inflate(box, Theme.F(2), Theme.F(2));
                    using (var outline = Theme.Round(ring, Theme.F(6))) using (var pen = new Pen(Color.FromArgb(120, Theme.Danger), Theme.F(2))) g.DrawPath(pen, outline);
                }
            }
            if (value) using (var pen = new Pen(Color.White, Box * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                Func<float, float, PointF> at = (x, y) => new PointF(box.X + box.Width * x, box.Y + box.Height * y);
                g.DrawLines(pen, new[] { at(0.27f, 0.53f), at(0.43f, 0.68f), at(0.74f, 0.36f) });
            }
            float left = box.Right + Theme.F(8);
            Theme.Write(g, Text, Font, Theme.Text, new RectangleF(left, 0, Width - left, Height), StringAlignment.Near, false);
        }
    }
    internal sealed class SetupForm : Form {
        // Layout in logical pixels; Theme.Scale maps it to the screen.
        const float W = 420, H = 536, LogoSize = 68, LogoTop = 36, ListTop = 186, Row = 44, StatusTop = 372, ButtonTop = 446, LinksTop = 492;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        readonly string packaged, client, clientVersion;
        readonly Func<bool> registered;
        readonly Func<string> installed, pending;
        readonly Pill primary = new Pill(PillKind.Primary), close = new Pill(PillKind.Close);
        readonly Pill[] links = { new Pill(PillKind.Link), new Pill(PillKind.Link) };
        readonly Action[] linkActions = new Action[2];
        // Offered only while confirming an uninstall; never checked by default.
        readonly Tick purge = new Tick { Text = "同时删除插件数据（设置、转换记录和缓存）", Visible = false };
        readonly Font title = Theme.Title(26), body = Theme.Face(13, false), strong = Theme.Face(14, true), small = Theme.Face(12.5f, false);
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 16 };
        readonly Stopwatch time = Stopwatch.StartNew();
        readonly Bitmap logo;
        Action primaryAction, cancel;
        string version, next, headline = "", detail = "";
        Support keys = Support.Missing, ui = Support.Missing;
        bool isRegistered, busy, badge, detected, offerPurge;
        int tone, linkCount; // tone: 0 plain, 1 finished, 2 failed
        internal bool CloseOnCancel;
        internal float Phase = -1; // fixed progress position for previews
        internal SetupForm(string packaged, string client, string clientVersion, Func<bool> registered, Func<string> installed, Func<string> pending) {
            this.packaged = packaged; this.client = client; this.clientVersion = clientVersion; this.registered = registered; this.installed = installed; this.pending = pending;
            Text = "BetterDownload 安装器"; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual; BackColor = Theme.Back; Font = body;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(Theme.S(W), Theme.S(H));
            var area = Screen.FromPoint(Cursor.Position).WorkingArea; Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            try { using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("brand.ico")) Icon = new Icon(data); } catch (Exception) { }
            try { logo = Theme.Logo(Theme.S(LogoSize)); } catch (Exception) { }
            primary.Font = strong; foreach (var link in links) link.Font = body;
            purge.Font = body; purge.Changed += delegate { if (offerPurge) primary.Text = purge.Checked ? "卸载并删除数据" : "卸载"; };
            close.Click += delegate { Close(); };
            primary.Click += delegate { if (primaryAction != null) primaryAction(); };
            for (int i = 0; i < links.Length; i++) { int index = i; links[i].Click += delegate { if (linkActions[index] != null) linkActions[index](); }; }
            clock.Tick += delegate { Invalidate(Rectangle.Inflate(Rectangle.Round(ProgressTrack()), Theme.S(4), Theme.S(4))); };
            Controls.Add(close); Controls.Add(primary); Controls.AddRange(links); Controls.Add(purge);
            Resolve();
        }
        internal bool Purge { get { return purge.Checked; } set { purge.Checked = value; } }
        void OfferPurge(bool offer) { offerPurge = offer; purge.Visible = offer; if (!offer) purge.Checked = false; }
        // ---- States --------------------------------------------------------
        void Probe() {
            isRegistered = registered(); version = isRegistered ? installed() : null; next = isRegistered ? pending() : null;
        }
        bool ConversionReady { get { return keys == Support.Verified || keys == Support.Candidate; } }
        bool EntryReady { get { return ui == Support.Verified || ui == Support.Candidate; } }
        bool Fallback { get { return client != null && detected && !EntryReady; } }
        // Client checks hash several QQ Music modules, so the window shows first.
        internal void Detect() {
            if (client == null) { Detected(Support.Missing, Support.Missing); return; }
            new Thread(delegate() {
                string reason; Support found = Support.Unsupported, face = Support.Unsupported;
                try { found = ClientCompatibility.Keys(client, out reason); face = ClientCompatibility.Interface(client, out reason); } catch (Exception) { }
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { Detected(found, face); }); } catch (InvalidOperationException) { }
            }) { IsBackground = true }.Start();
        }
        internal void Detected(Support keys, Support ui) {
            this.keys = keys; this.ui = ui; detected = true;
            if (!busy && cancel == null && tone == 0) Resolve(); else Invalidate();
        }
        // A note when the detected client is not fully adapted.
        string Caveat() {
            if (client == null || !detected) return null;
            if (!EntryReady) return "QQ 音乐界面未适配，设置在窗口菜单（Alt+空格）中。";
            return ConversionReady ? null : "这个 QQ 音乐版本的新格式下载暂未适配，文件会保留。";
        }
        // The idle window for the current installation: the prominent button
        // is always the recommended next step.
        internal void Resolve() {
            OfferPurge(false); Probe(); busy = false; badge = false; tone = 0; headline = ""; cancel = null; AcceptButton = primary; ActiveControl = primary;
            Version have = Program.ParseVersion(version), offer = Program.ParseVersion(packaged);
            Action uninstall = delegate { Confirm("uninstall"); }, repair = delegate { Run("repair", Program.Install); };
            if (!isRegistered) {
                SetPrimary("安装", false, delegate { Run("install", Program.Install); }); ShowLinks();
                detail = client == null ? "未检测到 QQ 音乐。可先安装，打开 QQ 音乐后自动接入。" : Caveat() ?? "只写入当前用户目录，无需管理员权限，不改动 QQ 音乐。";
            } else if (version == null) {
                SetPrimary("修复", false, repair); ShowLinks(Link("卸载", true, uninstall));
                headline = "安装文件不完整"; detail = "点击“修复”重新写入，设置和转换记录会保留。";
            } else if (have != null && offer != null && offer > have && next != packaged) {
                SetPrimary("更新到 v" + packaged, false, delegate { Run("update", Program.Install); }); ShowLinks(Link("卸载", true, uninstall));
                headline = "发现新版本"; detail = "保留设置和转换记录；QQ 音乐运行时，退出后切换。";
            } else if (have != null && offer != null && offer < have) {
                SetPrimary("完成", false, Close); ShowLinks(Link("安装 v" + packaged, false, delegate { Confirm("downgrade"); }), Link("卸载", true, uninstall));
                detail = "已安装较新的 v" + version + "，无需操作。";
            } else {
                SetPrimary("完成", false, Close); ShowLinks(Link("修复", false, repair), Link("卸载", true, uninstall));
                if (next != null) { headline = "更新待生效"; detail = "v" + next + " 已准备好，QQ 音乐退出后自动切换。"; }
                else detail = Caveat() ?? "已安装。入口或自动转换异常时，可点击“修复”。";
            }
            Invalidate(true);
        }
        // Destructive or unusual steps are confirmed inline, the way an
        // Apple sheet does; Enter never confirms them by accident.
        internal void Confirm(string kind) {
            tone = 0; badge = false; AcceptButton = null;
            if (kind == "uninstall") {
                // Songs always stay; the checkbox below decides about BetterDownload's own data.
                OfferPurge(true); headline = "卸载 BetterDownload？"; detail = "";
                SetPrimary("卸载", true, delegate { bool all = purge.Checked; Run("uninstall", delegate { return Program.Uninstall(all); }); });
            } else {
                headline = "安装较旧的 v" + packaged + "？"; detail = "将替换已安装的 v" + version + "，设置和转换记录会保留。";
                SetPrimary("安装旧版", false, delegate { Run("downgrade", Program.Install); });
            }
            cancel = delegate { if (CloseOnCancel) Close(); else Resolve(); };
            ShowLinks(Link("取消", false, cancel)); ActiveControl = links[0]; Invalidate(true);
        }
        internal void Busy(string kind) {
            OfferPurge(false); busy = true; cancel = null; tone = 0; badge = false; detail = "";
            headline = kind == "update" ? "正在更新…" : kind == "repair" ? "正在修复…" : kind == "uninstall" ? "正在卸载…" : "正在安装…";
            primary.Enabled = false; foreach (var link in links) link.Enabled = false;
            clock.Start(); Invalidate(true);
        }
        internal void Finish(Outcome done) {
            OfferPurge(false); Probe(); busy = false; cancel = null; tone = 1; badge = done.Installed; headline = done.Title; detail = done.Detail; AcceptButton = primary;
            SetPrimary("完成", false, Close); ShowLinks(); ActiveControl = primary; Invalidate(true);
        }
        internal void Fail(string kind, string message) {
            Resolve(); tone = 2; detail = message;
            headline = kind == "update" ? "更新失败" : kind == "repair" ? "修复失败" : kind == "uninstall" ? "卸载失败" : "安装失败";
            Invalidate(true);
        }
        void Run(string kind, Func<Outcome> job) {
            if (busy) return; Busy(kind);
            new Thread(delegate() {
                Outcome done = null; string error = null;
                try { done = job(); } catch (Exception e) { error = e.Message; }
                try {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)delegate {
                        clock.Stop(); primary.Enabled = true; foreach (var link in links) link.Enabled = true;
                        if (error == null) Finish(done); else Fail(kind, error);
                    });
                } catch (InvalidOperationException) { }
            }) { IsBackground = true }.Start();
        }
        void SetPrimary(string text, bool danger, Action action) { primary.Text = text; primary.Kind = danger ? PillKind.Danger : PillKind.Primary; primaryAction = action; primary.Invalidate(); }
        static Tuple<string, bool, Action> Link(string text, bool danger, Action action) { return Tuple.Create(text, danger, action); }
        void ShowLinks(params Tuple<string, bool, Action>[] items) {
            linkCount = items.Length;
            for (int i = 0; i < links.Length; i++) {
                links[i].Visible = i < items.Length; if (i >= items.Length) continue;
                links[i].Text = items[i].Item1; links[i].Kind = items[i].Item2 ? PillKind.DangerLink : PillKind.Link; linkActions[i] = items[i].Item3;
            }
            Arrange();
        }
        void Arrange() {
            close.SetBounds(Theme.S(W - 46), Theme.S(10), Theme.S(34), Theme.S(34));
            primary.SetBounds(Theme.S((W - 246) / 2), Theme.S(ButtonTop - 3), Theme.S(246), Theme.S(46));
            using (var image = new Bitmap(1, 1)) using (var g = Graphics.FromImage(image)) {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                float gap = Theme.F(6), total = -gap; var widths = new float[linkCount];
                for (int i = 0; i < linkCount; i++) { widths[i] = Theme.Measure(g, links[i].Text, body, 0).Width + Theme.F(30); total += widths[i] + gap; }
                float x = (Theme.F(W) - total) / 2;
                for (int i = 0; i < linkCount; i++) { links[i].SetBounds((int)Math.Round(x), Theme.S(LinksTop), (int)Math.Round(widths[i]), Theme.S(32)); x += widths[i] + gap; }
                // Centred under the question, above the red button.
                float span = Theme.F(2) + purge.Box + Theme.F(8) + Theme.Measure(g, purge.Text, body, 0).Width + Theme.F(6);
                purge.SetBounds((int)Math.Round((Theme.F(W) - span) / 2), Theme.S(StatusTop + 38), (int)Math.Ceiling(span), Theme.S(26));
            }
        }
        // ---- Painting ------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics); }
        void Render(Graphics g) {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Theme.Back);
            // The rounded-square icon on a soft shadow, as on macOS, with a
            // check once an installation has finished.
            var icon = Box((W - LogoSize) / 2, LogoTop, LogoSize, LogoSize);
            for (int i = 1; i <= 8; i++) {
                var shade = RectangleF.Inflate(icon, Theme.F(i * 0.8f), Theme.F(i * 0.8f)); shade.Offset(0, Theme.F(3 + i * 0.8f));
                using (var path = Theme.Round(shade, icon.Width * 0.23f + Theme.F(i * 0.8f))) using (var brush = new SolidBrush(Color.FromArgb(Theme.Dark ? 18 : 5, 0, 0, 0))) g.FillPath(brush, path);
            }
            if (logo != null) g.DrawImage(logo, icon);
            if (badge) {
                var ring = new RectangleF(icon.Right - Theme.F(20), icon.Bottom - Theme.F(20), Theme.F(26), Theme.F(26));
                using (var brush = new SolidBrush(Theme.Back)) g.FillEllipse(brush, ring);
                Theme.Mark(g, RectangleF.Inflate(ring, -Theme.F(2.5f), -Theme.F(2.5f)), Theme.Ok, true);
            }
            Theme.Write(g, "BetterDownload", title, Theme.Text, Box(0, 112, W, 34), StringAlignment.Center, false);
            Theme.Write(g, "QQ 音乐版 · 自动解锁下载的 VIP 歌曲", body, Theme.Muted, Box(0, 146, W, 20), StringAlignment.Center, false);
            // Grouped facts in the manner of macOS System Settings.
            var list = Box(24, ListTop, W - 48, Row * 4);
            using (var path = Theme.Round(list, Theme.F(12))) {
                using (var brush = new SolidBrush(Theme.Card)) g.FillPath(brush, path);
                if (!Theme.Dark) using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, path);
            }
            string[] names = { "安装包", "已安装", "QQ 音乐", "设置入口" };
            for (int i = 0; i < names.Length; i++) {
                var row = Box(40, ListTop + i * Row, W - 80, Row);
                if (i > 0) using (var pen = new Pen(Theme.Line)) { var quality = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None; g.DrawLine(pen, row.Left, (int)row.Top, row.Right, (int)row.Top); g.SmoothingMode = quality; }
                Theme.Write(g, names[i], body, Theme.Text, row, StringAlignment.Near, false);
            }
            Value(g, 0, String.IsNullOrEmpty(packaged) ? "未知" : "v" + packaged, Theme.Muted, Color.Empty);
            Value(g, 1, !isRegistered ? "未安装" : version == null ? "文件不完整" : "v" + version + (next != null ? "  →  v" + next + " 待生效" : ""), isRegistered && version == null ? Theme.DangerText : Theme.Muted, Color.Empty);
            Value(g, 2, client == null ? "未检测到" : (clientVersion ?? "未知版本") + " · " + (detected ? ClientCompatibility.Label(keys, ui) : "检测中…"), Theme.Muted,
                client == null || detected && keys == Support.Missing && ui == Support.Missing ? Theme.Danger : !detected ? Theme.Line : ConversionReady && EntryReady ? Theme.Ok : Theme.Warn);
            // The glyph mirrors the entry QQ Music shows in its top-right corner.
            // Until a client's interface is adapted, settings open from its window menu.
            if (Fallback) Value(g, 3, "窗口菜单（Alt+空格）", Theme.Muted, Color.Empty);
            else {
                var entry = Box(W - 40 - 18, ListTop + Row * 3 + (Row - 18) / 2, 18, 18);
                Entry(g, entry, Theme.Glyph);
                Theme.Write(g, "QQ 音乐右上角", body, Theme.Muted, new RectangleF(Theme.F(120), Theme.F(ListTop + Row * 3), entry.Left - Theme.F(8) - Theme.F(120), Theme.F(Row)), StringAlignment.Far, false);
            }
            Status(g);
        }
        void Value(Graphics g, int index, string text, Color color, Color dot) {
            var row = Box(120, ListTop + index * Row, W - 160, Row);
            Theme.Write(g, text, body, color, row, StringAlignment.Far, false);
            if (dot.IsEmpty) return;
            float width = Theme.Measure(g, text, body, 0).Width, d = Theme.F(7);
            using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, row.Right - width - Theme.F(7) - d, row.Top + (row.Height - d) / 2, d, d);
        }
        // Drawn from src/ui/entry.svg: a download arrow with an open padlock.
        static void Entry(Graphics g, RectangleF r, Color color) {
            float k = r.Width / 24f;
            Func<float, float, PointF> at = (x, y) => new PointF(r.X + x * k, r.Y + y * k);
            using (var pen = new Pen(color, Math.Max(1f, 1.7f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var body = Theme.Round(new RectangleF(at(15.2f, 6.6f), new SizeF(6.4f * k, 5 * k)), 1.3f * k))
            using (var shackle = new GraphicsPath()) {
                g.DrawLine(pen, at(10, 4.5f), at(10, 14.5f)); g.DrawLines(pen, new[] { at(6, 10.5f), at(10, 14.5f), at(14, 10.5f) }); g.DrawLine(pen, at(5, 19.5f), at(16, 19.5f));
                g.DrawPath(pen, body);
                shackle.AddLine(at(16.7f, 6.6f), at(16.7f, 5)); shackle.AddArc(r.X + 16.7f * k, r.Y + 3.19f * k, 3.6f * k, 3.6f * k, 180, 160); g.DrawPath(pen, shackle);
            }
        }
        void Status(Graphics g) {
            if (busy) {
                Theme.Write(g, headline, strong, Theme.Text, Box(24, StatusTop + 6, W - 48, 22), StringAlignment.Center, false);
                var track = ProgressTrack();
                using (var path = Theme.Round(track, track.Height / 2)) using (var brush = new SolidBrush(Theme.Line)) g.FillPath(brush, path);
                float t = Phase >= 0 ? Phase : time.ElapsedMilliseconds % 1500 / 1500f, span = track.Width * 0.34f, x = track.Left - span + (track.Width + span) * (float)(0.5 - 0.5 * Math.Cos(t * Math.PI));
                var state = g.Save();
                using (var clip = Theme.Round(track, track.Height / 2)) { g.SetClip(clip); using (var bar = Theme.Round(new RectangleF(x, track.Top, span, track.Height), track.Height / 2)) using (var brush = new SolidBrush(Theme.Accent)) g.FillPath(brush, bar); }
                g.Restore(state);
                if (detail.Length > 0) Theme.Write(g, detail, small, Theme.Muted, Box(24, StatusTop + 44, W - 48, 20), StringAlignment.Center, false);
                return;
            }
            float width = Theme.F(W - 72), head = headline.Length == 0 ? 0 : Theme.F(22);
            float lines = detail.Length == 0 ? 0 : Math.Min(Theme.F(54), Theme.Measure(g, detail, small, width).Height), gap = head > 0 && lines > 0 ? Theme.F(4) : 0;
            float top = Theme.F(StatusTop + 32) - (head + gap + lines) / 2;
            if (offerPurge) top = Theme.F(StatusTop + 8); // the question sits above the checkbox
            if (head > 0) {
                float mark = tone == 0 ? 0 : Theme.F(22), span = Theme.Measure(g, headline, strong, 0).Width, left = (Theme.F(W) - span - mark) / 2;
                if (tone != 0) Theme.Mark(g, new RectangleF(left, top + (head - Theme.F(16)) / 2, Theme.F(16), Theme.F(16)), tone == 1 ? Theme.Ok : Theme.Danger, tone == 1);
                Theme.Write(g, headline, strong, tone == 2 ? Theme.DangerText : Theme.Text, new RectangleF(left + mark, top, span + Theme.F(4), head), StringAlignment.Near, false);
            }
            if (lines > 0) Theme.Write(g, detail, small, Theme.Muted, new RectangleF(Theme.F(36), top + head + gap, width, lines + Theme.F(2)), StringAlignment.Center, true);
        }
        RectangleF ProgressTrack() { return Box((W - 180) / 2, StatusTop + 36, 180, 4); }
        static RectangleF Box(float x, float y, float width, float height) { return new RectangleF(Theme.F(x), Theme.F(y), Theme.F(width), Theme.F(height)); }
        // The sheet with its buttons, rendered without a window.
        internal Bitmap Snapshot() {
            var image = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(image)) {
                Render(g);
                foreach (var pill in new[] { close, primary }.Concat(links.Take(linkCount))) {
                    var state = g.Save(); g.TranslateTransform(pill.Left, pill.Top); pill.Render(g); g.Restore(state);
                }
                if (offerPurge) { var state = g.Save(); g.TranslateTransform(purge.Left, purge.Top); purge.Render(g); g.Restore(state); }
            }
            return image;
        }
        // ---- Window --------------------------------------------------------
        // A framed window keeps the system shadow, rounded corners and
        // animations; its caption is removed and the sheet draws everything.
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e); UpdateFrame();
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // SWP_FRAMECHANGED | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
        }
        void UpdateFrame() {
            int dark = Theme.Dark ? 1 : 0, round = 2;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); DwmSetWindowAttribute(Handle, 33, ref round, 4); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; } // WM_NCCALCSIZE: no caption or border
            base.WndProc(ref m);
            if (m.Msg == 0x0084 && m.Result == (IntPtr)1) m.Result = (IntPtr)2; // WM_NCHITTEST: drag from the background
            if (m.Msg == 0x001A && m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet") { Theme.Load(); BackColor = Theme.Back; UpdateFrame(); Invalidate(true); }
        }
        protected override bool ProcessDialogKey(Keys keyData) {
            if (keyData == Keys.Escape) { if (cancel != null && !busy) cancel(); else Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            if (busy) { e.Cancel = true; detail = "请等待当前操作完成后再关闭。"; Invalidate(); }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { clock.Dispose(); title.Dispose(); body.Dispose(); strong.Dispose(); small.Dispose(); if (logo != null) logo.Dispose(); }
            base.Dispose(disposing);
        }
    }
    internal static partial class Program {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        internal static Version ParseVersion(string text) { Version v; return Version.TryParse(text ?? "", out v) ? v : null; }
        // Crisp at any display scale: the sheet lays itself out from the DPI.
        static void PrepareUi() {
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
            Theme.Load();
        }
        static void RunUi(bool uninstall) {
            string client = FindQqMusic();
            using (var form = new SetupForm(PackagedVersion(), client, client == null ? null : QqMusicVersion(client), Registered, InstalledVersion, PendingVersion)) {
                if (uninstall) { form.CloseOnCancel = true; form.Confirm("uninstall"); }
                form.Shown += delegate { form.Detect(); };
                Application.Run(form);
            }
        }
        // Renders every window state in both appearances at 2x, without
        // touching the installation, for design review and the README image.
        static void PreviewUi(string folder) {
            Directory.CreateDirectory(folder); Theme.Scale = 2;
            string packaged = PackagedVersion(), client = "D:\\Program Files (x86)\\Tencent\\QQMusic";
            var shots = new Dictionary<string, Bitmap>();
            try {
                foreach (bool dark in new[] { false, true }) {
                    Theme.Apply(dark);
                    foreach (string name in new[] { "install", "busy", "done", "installed", "update", "uninstall", "uninstall-purge", "removed", "error", "fallback" }) {
                        bool registered = name != "install" && name != "busy" && name != "fallback" && name != "removed"; string have = name == "update" ? "0.1.0" : packaged;
                        using (var form = new SetupForm(packaged, client, "22.71", () => registered, () => have, () => null)) {
                            form.Detected(Support.Verified, name == "fallback" ? Support.Unsupported : Support.Verified);
                            if (name == "busy") { form.Busy("install"); form.Phase = 0.5f; }
                            if (name == "done") form.Finish(new Outcome("安装完成", "打开 QQ 音乐后自动接入，点击右上角的图标即可设置。", true));
                            if (name == "uninstall" || name == "uninstall-purge") form.Confirm("uninstall");
                            if (name == "uninstall-purge") form.Purge = true;
                            if (name == "removed") form.Finish(new Outcome("已卸载", "设置、转换记录和缓存已删除，歌曲保留。", false));
                            if (name == "error") form.Fail("repair", "BetterDownload 仍在保存任务，请稍后重试。");
                            string key = (dark ? "dark-" : "light-") + name; shots[key] = form.Snapshot();
                            shots[key].Save(Path.Combine(folder, "setup-" + key + ".png"), ImageFormat.Png);
                        }
                    }
                }
                // README: the first run in light mode beside a finished install in dark mode.
                using (var sheet = Compose(shots["light-install"], shots["dark-done"])) sheet.Save(Path.Combine(folder, "setup.png"), ImageFormat.Png);
            } finally { foreach (var shot in shots.Values) shot.Dispose(); }
        }
        // Windows with rounded corners and a soft shadow on a transparent canvas.
        static Bitmap Compose(params Bitmap[] windows) {
            int pad = 56, gap = 48, width = pad * 2 + windows.Sum(w => w.Width) + gap * (windows.Length - 1), height = pad * 2 + windows.Max(w => w.Height);
            var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sheet)) {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float x = pad;
                foreach (var window in windows) {
                    var frame = new RectangleF(x, pad, window.Width, window.Height);
                    for (int i = 1; i <= 18; i++) {
                        var shade = RectangleF.Inflate(frame, i * 1.6f, i * 1.6f); shade.Offset(0, 12 + i * 0.5f);
                        using (var path = Theme.Round(shade, 16 + i * 1.6f)) using (var brush = new SolidBrush(Color.FromArgb(3, 0, 0, 0))) g.FillPath(brush, path);
                    }
                    using (var path = Theme.Round(frame, 16)) {
                        using (var brush = new TextureBrush(window)) { brush.TranslateTransform(x, pad); g.FillPath(brush, path); }
                        using (var pen = new Pen(Color.FromArgb(46, 128, 128, 128))) g.DrawPath(pen, path);
                    }
                    x += window.Width + gap;
                }
            }
            return sheet;
        }
    }
}
