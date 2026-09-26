using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Timer = System.Windows.Forms.Timer;

namespace QqmBetterDownload {
    public sealed class Settings {
        public string Root = "", Client = "", CoverCache = "";
        public bool Enabled = true;
        public string Notify = "all", CardStyle = "standard";
        public int CardStay = 4000;
    }
    // WinForms is only the child HWND container. The entire settings page is HTML.
    public sealed class AppWindow : Form {
        readonly Settings settings;
        readonly ICardPresenter card;
        readonly Process parent;
        readonly IntPtr clientWindow;
        readonly bool preview;
        readonly AutomaticPaths paths;
        readonly ClientLayout clientLayout;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(247,249,248) };
        readonly Label loading = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "正在打开 BetterDownload…", ForeColor = Color.FromArgb(90,122,105) };
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Timer ipcTimer = new Timer { Interval = 200 }, parentTimer = new Timer { Interval = 100 }, demoTimer = new Timer { Interval = 1400 };
        readonly EventWaitHandle settingsRequest, stopRequest, alive;
        readonly List<object> history = new List<object>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        IDownloadMonitor engine;
        WorkStatus latest;
        string lastOutput = "", problem = "", webData = "", htmlPhase = "等待页面容器", scanNote = "";
        int generation, completed, failed;
        bool runtimeStarted, closing, exitRequested, initializing, htmlReady;
        EventHandler demoDone;
        internal bool HtmlReady { get { return htmlReady; } }
        internal bool CardPresented { get { return card is InAppCard && ((InAppCard)card).IsPresented; } }
        internal string HtmlError { get { return problem; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var value = base.CreateParams;
                if (clientWindow != IntPtr.Zero) {
                    value.Parent = clientWindow;
                    value.Style = (value.Style & ~unchecked((int)0x80c40000)) | ClientUi.Child;
                    value.ExStyle &= ~0x00040008;
                }
                return value;
            }
        }
        protected override void CreateHandle() {
            if (clientWindow == IntPtr.Zero) { base.CreateHandle(); return; }
            using (new ClientUi.DpiScope(clientWindow)) { base.CreateHandle(); ClientUi.RequireChild(Handle, clientWindow); }
        }
        protected override void SetVisibleCore(bool value) {
            if (parent != null && !preview && !runtimeStarted && value) {
                runtimeStarted = true; if (!IsHandleCreated) CreateHandle();
                BeginInvoke((Action)StartRuntime); return;
            }
            base.SetVisibleCore(value);
            if (value && web != null) InitializeHtml();
        }
        public AppWindow(bool previewMode, Process parentProcess = null, string clientPath = null) {
            preview = previewMode; parent = parentProcess;
            clientWindow = parent == null ? IntPtr.Zero : parent.MainWindowHandle;
            if (parent != null && clientWindow == IntPtr.Zero) throw new InvalidOperationException("QQ 音乐窗口尚未准备好。");
            if (parent != null) clientLayout = new ClientLayout(clientWindow);
            try { settings = preview ? new Settings() : StateFile.Read<Settings>(Path.Combine(Program.DataFolder, "settings.json")); }
            catch { settings = new Settings(); }
            if (settings == null) settings = new Settings();
            if (preview) {
                settings.Root = @"D:\Music\VipSongsDownload"; settings.Client = @"D:\Apps\QQMusic"; settings.CoverCache = @"D:\QQMusicCache";
            } else {
                if (String.IsNullOrEmpty(settings.Root)) settings.Root = Program.DefaultRoot();
                settings.Client = String.IsNullOrEmpty(clientPath) ? LocalKeys.FindClient() : clientPath;
                settings.CoverCache = LocalArtwork.FindDefaultCache();
                paths = new AutomaticPaths(Program.DataFolder, Program.DefaultRoot(), settings.Root);
                settingsRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\QQM-BetterDownload.Settings");
                stopRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\QQM-BetterDownload.WorkerStop");
                alive = new EventWaitHandle(true, EventResetMode.ManualReset, "Local\\QQM-BetterDownload.WorkerAlive");
            }
            if (settings.Notify != "all" && settings.Notify != "errors" && settings.Notify != "off") settings.Notify = "all";
            if (settings.CardStyle != "compact") settings.CardStyle = "standard";
            if (settings.CardStay != 2000 && settings.CardStay != 6000) settings.CardStay = 4000;
            Text = "BetterDownload"; ClientSize = new Size(850,760); MinimumSize = new Size(560,440);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(247,249,248); Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleMode = AutoScaleMode.None;
            if (parent != null) { TopLevel = false; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; MinimumSize = Size.Empty; StartPosition = FormStartPosition.Manual; }
            card = parent == null ? (ICardPresenter)new ProgressCard(() => IntPtr.Zero, OpenOutput) : new InAppCard(clientWindow, OpenOutput);
            ConfigureCard();
            Controls.Add(web); Controls.Add(loading); loading.BringToFront();
            tray.Text = "BetterDownload"; tray.Icon = SystemIcons.Application;
            var menu = new ContextMenuStrip(); menu.Items.Add("在 QQ 音乐中打开设置", null, delegate { Restore(); });
            menu.Items.Add("退出 BetterDownload", null, delegate { Program.Signal("Local\\QQM-BetterDownload.AgentStop"); exitRequested = true; Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Restore(); };
            Shown += delegate { if (!preview && !runtimeStarted) { runtimeStarted = true; StartRuntime(); } };
            parentTimer.Tick += delegate {
                if (parent != null && (parent.HasExited || !ClientUi.IsWindow(clientWindow))) { exitRequested = true; Close(); }
                else { if (clientLayout != null) clientLayout.Refresh(); if (Visible) PositionInClient(); }
            };
            ipcTimer.Tick += delegate {
                if (stopRequest != null && stopRequest.WaitOne(0)) { exitRequested = true; Close(); return; }
                if (settingsRequest != null && settingsRequest.WaitOne(0)) { if (Visible) Hide(); else Restore(); }
            };
            if (!preview) ipcTimer.Start();
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!preview && !exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                Cleanup();
            };
        }
        void StartRuntime() { if (settings.Enabled) Start(); else Publish(); if (parent != null) { tray.Visible = true; parentTimer.Start(); } }
        async void InitializeHtml() {
            if (initializing || closing || IsDisposed) return; initializing = true;
            try {
                webData = preview ? Path.Combine(Path.GetTempPath(), "BetterDownload-Preview", Guid.NewGuid().ToString("N")) : Path.Combine(Program.DataFolder, "webview");
                htmlPhase = "初始化网页运行环境";
                var environment = await CoreWebView2Environment.CreateAsync(null, webData);
                if (closing || IsDisposed) return;
                htmlPhase = "创建网页控件"; await web.EnsureCoreWebView2Async(environment);
                if (closing || IsDisposed) return;
                var core = web.CoreWebView2;
                htmlPhase = "载入内置页面";
                core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = preview;
                core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
                bool firstNavigation = true;
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    // NavigateToString reports a data: URI during startup; the
                    // resulting document's source and message origin are about:blank.
                    if (firstNavigation) { firstNavigation = false; return; }
                    e.Cancel = true;
                };
                core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { if (!e.IsSuccess) { problem = "内置页面加载失败：" + e.WebErrorStatus; loading.Text = problem; } else htmlPhase = "等待页面就绪消息"; };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
                    if (e.Source != "about:blank") return;
                    try {
                        var request = json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson); object value;
                        if (request == null || !request.TryGetValue("action", out value) || !(value is string)) return;
                        string action = (string)value; request.TryGetValue("value", out value); HandleAction(action, value as string);
                    } catch (Exception error) { problem = error.Message; Publish(); }
                };
                using (var stream = typeof(AppWindow).Assembly.GetManifestResourceStream("settings.html"))
                using (var reader = new StreamReader(stream)) core.NavigateToString(reader.ReadToEnd());
            } catch (Exception e) {
                problem = "设置页未能加载：" + e.Message; loading.Text = problem + "\n自动转换仍会在后台运行。";
                initializing = false;
            }
        }
        void HandleAction(string action, string value) {
            if (action == "ready") { htmlReady = true; loading.Hide(); Publish(); return; }
            if (!htmlReady) return;
            if (action == "close") { Hide(); return; }
            if (action == "preview") { PreviewCard(); return; }
            if (action == "project" || action == "issues") { Process.Start(new ProcessStartInfo("https://github.com/xiaoming6680/QQM-BetterDownload" + (action == "issues" ? "/issues" : "")) { UseShellExecute = true }); return; }
            if (action == "open") { if (lastOutput.Length > 0) OpenOutput(Path.GetDirectoryName(lastOutput)); return; }
            if (action == "scan") {
                if (engine != null) { UpdateStatus(new WorkStatus { State = "scanning", Message = "正在查找已有下载" }); engine.ScanExisting(); }
                else if (preview) UpdateStatus(new WorkStatus { State = "scan-complete", Message = "预览模式：未处理歌曲。" });
                else UpdateStatus(new WorkStatus { State = "scan-complete", Message = "查找失败：" + (problem.Length > 0 ? problem : "自动转换尚未启动，请重新启用后重试。") });
                return;
            }
            if (action == "toggle") { settings.Enabled = !settings.Enabled; if (!preview) { if (settings.Enabled) Start(); else Stop(); } }
            else if (action == "notify" && (value == "all" || value == "errors" || value == "off")) settings.Notify = value;
            else if (action == "style" && (value == "standard" || value == "compact")) settings.CardStyle = value;
            else if (action == "stay" && (value == "2000" || value == "4000" || value == "6000")) settings.CardStay = Int32.Parse(value);
            else return;
            ConfigureCard(); Save(); Publish();
        }
        void ConfigureCard() { card.Configure(settings.Notify, settings.CardStyle == "compact", settings.CardStay); }
        void Save() { if (preview) return; try { StateFile.Write(Path.Combine(Program.DataFolder, "settings.json"), settings); } catch (Exception e) { problem = "设置暂未保存：" + e.Message; } }
        void Start() {
            try {
                Stop(); problem = ""; int stamp = generation;
                engine = new EventHost(settings.Client, settings.Root, Program.DataFolder, status => Receive(status, stamp), settings.CoverCache, paths);
                Save(); Publish();
            } catch (Exception e) { problem = e.Message; Publish(); card.Receive(new WorkStatus { Id = "startup-" + generation, State = "error", Message = e.Message }); }
        }
        void Stop() { generation++; card.Dismiss(); if (engine != null) { engine.Dispose(); engine = null; } }
        void Receive(WorkStatus status, int stamp) {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!closing && stamp == generation) UpdateStatus(status); }); } catch (InvalidOperationException) { }
        }
        void UpdateStatus(WorkStatus status) {
            latest = status;
            if (status.State == "scanning" || status.State == "scan-complete") scanNote = status.Message;
            if (!preview) card.Receive(status);
            if (status.Output.Length > 0) lastOutput = status.Output;
            if (status.State == "success") completed++;
            if (status.State == "error") failed++;
            if (status.State == "success" || status.State == "error" || status.State == "waiting") {
                history.Insert(0, new { title = status.Track == null || String.IsNullOrEmpty(status.Track.Title) ? Path.GetFileName(status.Source) : status.Track.Title,
                    state = status.State == "success" ? "已完成" : status.State == "waiting" ? "等待中" : "未完成" });
                if (history.Count > 30) history.RemoveAt(history.Count - 1);
            }
            Publish();
        }
        void Publish() {
            if (!htmlReady || closing || web.CoreWebView2 == null) return;
            var roots = preview ? new[] { settings.Root } : paths.Roots;
            string message = !settings.Enabled ? "已关闭" : latest == null || latest.State == "watching" || latest.State == "success" || latest.State == "skipped" || latest.State == "scan-complete" ? "已启用 · 下载完成后自动转换" : latest.Message;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(new {
                version = Program.Version, enabled = settings.Enabled, notify = settings.Notify, style = settings.CardStyle, stay = settings.CardStay,
                message = message, state = latest == null ? "" : latest.State,
                count = completed + failed == 0 ? "" : "本次完成 " + completed + " 首" + (failed > 0 ? " · " + failed + " 首待处理" : ""),
                error = problem.Length > 0 ? problem : latest != null && latest.State == "error" ? latest.Message : "",
                client = settings.Client, roots = roots, cache = settings.CoverCache, history = history,
                scanNote = scanNote
            }));
        }
        public void PreviewCard() {
            demoTimer.Stop(); if (demoDone != null) demoTimer.Tick -= demoDone;
            var sample = new WorkStatus { Id = "preview-" + Guid.NewGuid().ToString("N"), Source = "示例歌曲.mflac", State = "converting", Percent = 64, Message = "正在转换", Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" } };
            card.Receive(sample, true);
            demoDone = delegate { demoTimer.Stop(); demoTimer.Tick -= demoDone; demoDone = null; sample.State = "success"; sample.Percent = 100; sample.Output = Path.Combine(settings.Root, "unlock", "示例歌曲.flac"); sample.Warning = "预览示例 · 不会生成文件"; card.Receive(sample, true); };
            demoTimer.Tick += demoDone; demoTimer.Start();
        }
        void OpenOutput(string path) {
            try { SafePath.NoLinks(path); if (!Directory.Exists(path)) return; Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception e) { problem = e.Message; Publish(); }
        }
        internal void PositionInClient() {
            if (parent == null || !IsHandleCreated) return;
            using (new ClientUi.DpiScope(clientWindow)) {
                ClientUi.Rect client; if (!ClientUi.GetClientRect(clientWindow, out client)) return;
                var bounds = clientLayout.Bounds(client); double scale = ClientUi.Scale(clientWindow);
                if (bounds.Width < 460 * scale || bounds.Height < 180 * scale) { Hide(); return; }
                if (Bounds != bounds) Bounds = bounds;
            }
        }
        internal void Restore() {
            if (parent != null) { PositionInClient(); Show(); PositionInClient(); BringToFront(); parentTimer.Start(); Publish(); return; }
            Show(); Publish();
        }
        internal Task<string> EvaluateHtml(string script) { return web.CoreWebView2.ExecuteScriptAsync(script); }
        internal Task CaptureHtml(string file) { return CapturePage(file); }
        async Task CapturePage(string file) { using (var output = File.Create(file)) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output); }
        internal void PreparePreview() {
            if (!preview) throw new InvalidOperationException("仅预览可使用示例数据。");
            UpdateStatus(new WorkStatus { State = "success", Message = "转换完成", Source = "示例歌曲.mflac", Output = @"D:\Music\VipSongsDownload\unlock\示例歌曲.flac", Percent = 100 });
        }
        public void SavePreview(string path) {
            ClientSize = new Size(1182, 1260); ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-10000,-10000); Show();
            var watch = Stopwatch.StartNew();
            while (!htmlReady && watch.ElapsedMilliseconds < 20000) { Application.DoEvents(); Thread.Sleep(10); }
            if (!htmlReady) throw new IOException(problem.Length > 0 ? problem : "设置页面加载超时：" + htmlPhase);
            PreparePreview(); watch.Restart(); while (watch.ElapsedMilliseconds < 900) { Application.DoEvents(); Thread.Sleep(10); }
            var capture = CapturePage(path); while (!capture.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); } capture.GetAwaiter().GetResult(); Close();
        }
        void Cleanup() {
            if (closing) return; closing = true;
            ipcTimer.Stop(); parentTimer.Stop(); demoTimer.Stop(); Stop(); card.Dispose(); tray.Visible = false;
            if (alive != null) alive.Reset();
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                Cleanup(); ipcTimer.Dispose(); parentTimer.Dispose(); demoTimer.Dispose(); tray.Dispose(); web.Dispose();
                if (settingsRequest != null) { settingsRequest.Dispose(); stopRequest.Dispose(); alive.Dispose(); }
                if (parent != null) parent.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
