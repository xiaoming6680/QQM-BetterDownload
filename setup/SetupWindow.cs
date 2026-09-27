using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BetterDownloadSetup {
    // Dark installer window in the style of BetterNCM-Installer. One primary
    // action follows the installation state, so no two buttons do the same job.
    internal static class Theme {
        internal static readonly Color Back = Color.FromArgb(28, 30, 33), Panel = Color.FromArgb(37, 40, 44), Line = Color.FromArgb(50, 54, 59);
        internal static readonly Color Text = Color.FromArgb(232, 235, 238), Muted = Color.FromArgb(140, 146, 153), Dim = Color.FromArgb(96, 101, 107);
        // The logo's gradient: QQ Music's teal-to-green, with its yellow for warnings.
        internal static readonly Color BrandStart = Color.FromArgb(20, 214, 192), Brand = Color.FromArgb(12, 196, 143), BrandEnd = Color.FromArgb(6, 186, 108);
        internal static readonly Color Ok = Color.FromArgb(61, 220, 151), Warn = Color.FromArgb(255, 205, 64), Error = Color.FromArgb(236, 120, 112);
        internal static GraphicsPath Round(RectangleF r, float radius) {
            var path = new GraphicsPath(); float d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
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
    internal sealed class FlatButton : Button {
        readonly bool primary; bool hover, down;
        internal FlatButton(string text, bool primary) {
            this.primary = primary; Text = text; Height = 34; Cursor = Cursors.Hand; Font = new Font("Microsoft YaHei UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Parent == null ? Theme.Back : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f); Color fore;
            using (var path = Theme.Round(r, 7)) {
                if (!Enabled) {
                    using (var fill = new SolidBrush(Color.FromArgb(34, 37, 40))) g.FillPath(fill, path);
                    using (var pen = new Pen(Color.FromArgb(44, 48, 52))) g.DrawPath(pen, path);
                    fore = Theme.Dim;
                } else if (primary) {
                    using (var fill = new LinearGradientBrush(r, Theme.BrandStart, Theme.BrandEnd, 20f)) g.FillPath(fill, path);
                    if (hover || down) using (var tint = new SolidBrush(down ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(34, 255, 255, 255))) g.FillPath(tint, path);
                    fore = Color.FromArgb(4, 36, 26);
                } else {
                    using (var fill = new SolidBrush(down ? Color.FromArgb(44, 48, 52) : hover ? Color.FromArgb(52, 56, 61) : Color.FromArgb(43, 46, 50))) g.FillPath(fill, path);
                    using (var pen = new Pen(hover ? Color.FromArgb(72, 78, 85) : Color.FromArgb(60, 65, 71))) g.DrawPath(pen, path);
                    fore = Theme.Text;
                }
                if (Focused && ShowFocusCues && Enabled) using (var ring = new Pen(primary ? Color.FromArgb(200, 255, 255, 255) : Theme.Brand, 1.5f)) g.DrawPath(ring, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
    internal static partial class Program {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        static Version ParseVersion(string text) { Version v; return Version.TryParse(text ?? "", out v) ? v : null; }
        static Label Value(Control parent, int top, string key) {
            parent.Controls.Add(new Label { Text = key, Left = 18, Top = top, Width = 96, Height = 22, ForeColor = Theme.Muted, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft });
            var value = new Label { Left = 118, Top = top, Width = 290, Height = 22, ForeColor = Theme.Text, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), AutoEllipsis = true };
            parent.Controls.Add(value); return value;
        }
        static void RunUi() {
            string client = FindQqMusic(), clientVersion = client == null ? null : QqMusicVersion(client), packaged = PackagedVersion();
            var form = new Form { Text = "BetterDownload 安装器", ClientSize = new Size(460, 318), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, BackColor = Theme.Back, ForeColor = Theme.Text, Font = new Font("Microsoft YaHei UI", 9.5f) };
            try { using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("brand.ico")) form.Icon = new Icon(data); } catch (Exception) { }
            form.HandleCreated += delegate { int on = 1; try { DwmSetWindowAttribute(form.Handle, 20, ref on, 4); } catch (Exception) { } };
            Bitmap logo = null; try { logo = Theme.Logo(48); } catch (Exception) { }
            var mark = new PictureBox { Left = 26, Top = 22, Width = 48, Height = 48, Image = logo, BackColor = Color.Transparent };
            var title = new Label { Text = "BetterDownload", Font = new Font("Segoe UI Semibold", 17f), ForeColor = Color.FromArgb(244, 246, 248), Left = 84, Top = 20, AutoSize = true };
            var sub = new Label { Text = "QQ 音乐版 · 自动解锁下载的 VIP 歌曲", ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 9f), Left = 87, Top = 52, AutoSize = true };
            var info = new Panel { Left = 24, Top = 90, Width = 412, Height = 96, BackColor = Theme.Back };
            info.Paint += delegate(object s, PaintEventArgs e) {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, info.Width - 1.5f, info.Height - 1.5f), 10)) {
                    using (var brush = new SolidBrush(Theme.Panel)) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(Theme.Line)) e.Graphics.DrawPath(pen, path);
                }
            };
            var vPackage = Value(info, 9, "安装包"); var vInstalled = Value(info, 37, "已安装"); var vClient = Value(info, 65, "QQ 音乐");
            var status = new Label { Left = 26, Top = 196, Width = 408, Height = 40, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 9f) };
            var track = new Panel { Left = 26, Top = 240, Width = 408, Height = 3, BackColor = Theme.Line, Visible = false };
            var fill = new Panel { Left = 0, Top = 0, Width = 0, Height = 3, BackColor = Theme.Brand }; track.Controls.Add(fill);
            var bUninstall = new FlatButton("卸载", false); var bSettings = new FlatButton("打开设置", false); var bPrimary = new FlatButton("安装", true);
            bUninstall.SetBounds(24, 260, 84, 34); bSettings.SetBounds(116, 260, 104, 34); bPrimary.SetBounds(286, 260, 150, 34);
            form.AcceptButton = bPrimary;
            var buttons = new Button[] { bPrimary, bSettings, bUninstall };

            vPackage.Text = String.IsNullOrEmpty(packaged) ? "未知" : "v" + packaged;
            bool verified = clientVersion != null && clientVersion.StartsWith("22.71", StringComparison.Ordinal);
            vClient.Text = client == null ? "未检测到" : (clientVersion ?? "未知版本") + (verified ? "  ·  已适配" : "  ·  未验证版本");
            vClient.ForeColor = client == null ? Theme.Error : verified ? Theme.Text : Theme.Warn;

            var marquee = new System.Windows.Forms.Timer { Interval = 16 }; int[] pos = { 0 };
            marquee.Tick += delegate { int seg = Math.Max(60, track.Width / 3); pos[0] = (pos[0] + 8) % (track.Width + seg); fill.Width = seg; fill.Left = pos[0] - seg; };
            bool busy = false; string note = null; Color noteColor = Theme.Muted; string action = "install";

            Action refresh = delegate {
                bool registered = Registered(); string installed = registered ? InstalledVersion() : null, pending = registered ? PendingVersion() : null;
                Version have = ParseVersion(installed), offer = ParseVersion(packaged);
                string hint;
                if (!registered) {
                    action = "install"; bPrimary.Text = "安装";
                    vInstalled.Text = "未安装"; vInstalled.ForeColor = Theme.Muted;
                    hint = client != null ? "点击“安装”即可。仅写入当前用户目录，无需管理员权限，也不修改 QQ 音乐安装目录。" : "未检测到 QQ 音乐。仍可先安装，QQ 音乐安装并打开后自动接入。";
                } else if (installed == null) {
                    action = "repair"; bPrimary.Text = "修复";
                    vInstalled.Text = "文件损坏或不完整"; vInstalled.ForeColor = Theme.Error;
                    hint = "安装文件未通过校验，点击“修复”重新写入。设置和转换记录会保留。";
                } else {
                    vInstalled.Text = "v" + installed + (pending != null ? "  →  v" + pending + " 待生效" : ""); vInstalled.ForeColor = Theme.Ok;
                    if (have != null && offer != null && offer > have && pending != packaged) { action = "update"; bPrimary.Text = "更新到 v" + packaged; hint = "发现新版本。更新会保留设置和转换记录；QQ 音乐运行中时，退出后自动切换。"; }
                    else if (have != null && offer != null && offer < have) { action = "downgrade"; bPrimary.Text = "改装 v" + packaged; hint = "已安装更新的版本，一般无需操作。"; }
                    else { action = "repair"; bPrimary.Text = "修复"; hint = pending != null ? "更新已准备好，QQ 音乐退出后自动生效。" : "已安装。打开 QQ 音乐即自动接入，设置入口在 QQ 音乐右上角。"; }
                }
                bPrimary.Enabled = !busy; bSettings.Enabled = !busy && installed != null; bUninstall.Enabled = !busy && registered;
                if (note != null) { status.Text = note; status.ForeColor = noteColor; } else { status.Text = hint; status.ForeColor = Theme.Muted; }
            };
            Action<string, Func<string>> run = delegate(string working, Func<string> job) {
                if (busy) return; busy = true; note = working; noteColor = Theme.Text;
                refresh(); track.Visible = true; pos[0] = 0; marquee.Start();
                new Thread(delegate() {
                    string message = null, error = null;
                    try { message = job(); } catch (Exception e) { error = e.Message; }
                    try {
                        if (form.IsDisposed || !form.IsHandleCreated) return;
                        form.BeginInvoke((Action)delegate {
                            marquee.Stop(); busy = false;
                            if (error == null) { fill.Left = 0; fill.Width = track.Width; note = message; noteColor = Theme.Ok; }
                            else { track.Visible = false; note = error; noteColor = Theme.Error; }
                            refresh();
                        });
                    } catch (InvalidOperationException) { }
                }) { IsBackground = true }.Start();
            };
            bPrimary.Click += delegate {
                if (action == "downgrade" && MessageBox.Show(form, "将已安装的 BetterDownload 替换为较旧的 v" + packaged + "？", "BetterDownload", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                string working = action == "update" ? "正在更新…" : action == "repair" ? "正在修复…" : "正在安装…";
                run(working, Install);
            };
            bSettings.Click += delegate {
                run("正在打开设置…", delegate {
                    bool running = ClientRunning();
                    if (!running && FindQqMusic() == null) throw new IOException("未检测到 QQ 音乐，请先安装并打开 QQ 音乐。");
                    Startup(true);
                    return running ? "已在 QQ 音乐中打开设置。" : "正在启动 QQ 音乐，接入后自动打开设置。";
                });
            };
            bUninstall.Click += delegate {
                if (MessageBox.Show(form, "卸载 BetterDownload？歌曲、设置和转换记录会保留。", "BetterDownload", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                run("正在卸载…", delegate { Uninstall(); return "已卸载。组件会在 QQ 音乐退出后彻底清理；歌曲、设置和转换记录已保留。"; });
            };
            form.FormClosing += delegate(object s, FormClosingEventArgs e) {
                if (!busy) return;
                e.Cancel = true; note = "请等待当前操作完成后再关闭。"; noteColor = Theme.Warn; status.Text = note; status.ForeColor = noteColor;
            };
            form.Controls.AddRange(new Control[] { mark, title, sub, info, status, track, bUninstall, bSettings, bPrimary });
            form.Shown += delegate { refresh(); bPrimary.Focus(); };
            refresh();
            using (form) Application.Run(form);
            marquee.Dispose(); if (logo != null) logo.Dispose();
        }
    }
}
