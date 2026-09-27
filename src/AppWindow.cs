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
using Timer = System.Windows.Forms.Timer;

namespace QqmBetterDownload {
    public sealed class Settings {
        public string Root = "", Client = "", CoverCache = "";
        public bool Enabled = true;
        public string Notify = "all", CardStyle = "standard";
        public int CardStay = 4000;
    }
    // Runtime/controller. In QQ mode this is a hidden message pump; every
    // visible control belongs to QQ's native GF renderer. WebView2 remains
    // available only for standalone design previews and the fixture tests.
    public sealed class AppWindow : Form {
        readonly Settings settings;
        readonly ICardPresenter card;
        readonly NativeUiSession nativeUi;
        readonly Icon brandIcon = BrandIcon.Load();
        readonly Process parent;
        readonly IntPtr clientWindow;
        readonly bool preview;
        readonly AutomaticPaths paths;
        readonly HtmlHost web = new HtmlHost { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(247,249,248) };
        readonly Label loading = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "正在打开 BetterDownload…", ForeColor = Color.FromArgb(90,122,105) };
        readonly Timer ipcTimer = new Timer { Interval = 200 }, parentTimer = new Timer { Interval = 100 }, demoTimer = new Timer { Interval = 1400 };
        readonly EventWaitHandle settingsRequest, stopRequest, alive, sessionStop, sessionAlive;
        readonly List<object> history = new List<object>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        IDownloadMonitor engine;
        WorkStatus latest;
        string lastOutput = "", problem = "", webData = "", htmlPhase = "等待页面容器", scanNote = "";
        int generation, completed, failed, nativeRetryTicks, openSequence;
        bool runtimeStarted, closing, nativeEnding, exitRequested, initializing, htmlReady, panelVisible = true;
        EventHandler demoDone;
        internal bool HtmlReady { get { return htmlReady; } }
        internal bool CardPresented { get { return !Ending && (nativeUi != null ? nativeUi.CardVisible : ((ProgressCard)card).IsVisible); } }
        internal string HtmlError { get { return problem; } }
        bool Ending { get { return closing || nativeEnding || IsDisposed || Disposing; } }
        bool NativeMode { get { return parent != null; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message message) {
            if ((uint)message.Msg == NativeUiSession.ActionMessage && !Ending) {
                var nativeCard = card as NativeCard;
                if (message.WParam == (IntPtr)1 && nativeCard != null) nativeCard.OpenFolder();
                if (message.WParam == (IntPtr)2) { openSequence++; panelVisible = true; Publish(); }
                if (message.WParam == (IntPtr)3) { panelVisible = false; Publish(); }
                return;
            }
            // WM_DESTROY arrives before children are destroyed. Stop callbacks
            // and close our browser while the container still has its HWND.
            if (message.Msg == 0x0002 && !RecreatingHandle) {
                nativeEnding = true; htmlReady = false;
                ipcTimer.Stop(); demoTimer.Stop(); web.Shutdown();
            }
            base.WndProc(ref message);
        }
        protected override void SetVisibleCore(bool value) {
            if (NativeMode) {
                if (!runtimeStarted && value) { runtimeStarted = true; if (!IsHandleCreated) CreateHandle(); BeginInvoke((Action)StartRuntime); }
                return;
            }
            base.SetVisibleCore(value);
            if (value && web != null) InitializeHtml();
        }
        public AppWindow(bool previewMode, Process parentProcess = null, string clientPath = null, IntPtr parentWindow = default(IntPtr)) {
            preview = previewMode; parent = parentProcess;
            if (preview && parent != null) throw new ArgumentException("设计预览不接入客户端。");
            clientWindow = parent == null ? IntPtr.Zero : parentWindow == IntPtr.Zero ? ClientUi.MainWindow(parent, IntPtr.Zero) : parentWindow;
            if (parent != null && clientWindow == IntPtr.Zero) throw new InvalidOperationException("QQ 音乐窗口尚未准备好。");
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
                alive = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\QQM-BetterDownload.WorkerAlive");
                if (parent != null) {
                    sessionAlive = new EventWaitHandle(false, EventResetMode.ManualReset, WorkerSession.Name(parent, "Alive"));
                    sessionStop = new EventWaitHandle(false, EventResetMode.AutoReset, WorkerSession.Name(parent, "Stop"));
                }
            }
            if (settings.Notify != "all" && settings.Notify != "errors" && settings.Notify != "off") settings.Notify = "all";
            if (settings.CardStyle != "compact") settings.CardStyle = "standard";
            if (settings.CardStay != 2000 && settings.CardStay != 6000) settings.CardStay = 4000;
            Text = "BetterDownload"; ClientSize = new Size(850,760); MinimumSize = new Size(560,440);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(247,249,248); Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleMode = AutoScaleMode.None;
            if (parent != null) { TopLevel = NativeMode; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; MinimumSize = Size.Empty; StartPosition = FormStartPosition.Manual; }
            if (NativeMode) nativeUi = new NativeUiSession(this, clientWindow, settings.Client, HandleAction);
            card = NativeMode ? (ICardPresenter)new NativeCard(nativeUi, clientWindow, OpenOutput) : new ProgressCard(() => IntPtr.Zero, OpenOutput);
            ConfigureCard();
            web.BrowserClosed += delegate {
                htmlReady = false;
                if (Ending) return;
                if (parent != null && (parent.HasExited || !ClientUi.IsWindow(clientWindow))) { exitRequested = true; Close(); return; }
                problem = "设置页面已关闭，请重新打开 QQ 音乐。"; loading.Text = problem; loading.Show(); loading.BringToFront();
            };
            if (!NativeMode) { Controls.Add(web); Controls.Add(loading); loading.BringToFront(); }
            Icon = brandIcon;
            Shown += delegate { if (!preview && !runtimeStarted) { runtimeStarted = true; StartRuntime(); } };
            parentTimer.Tick += delegate {
                if (closing || IsDisposed || Disposing) return;
                if (nativeEnding || (parent != null && (parent.HasExited || !ClientUi.IsWindow(clientWindow)))) { exitRequested = true; Close(); }
                else if (nativeUi != null && ++nativeRetryTicks % 20 == 0 && !nativeUi.Ready) { nativeUi.Connect(); Publish(); WriteRuntimeStatus(); }
            };
            ipcTimer.Tick += delegate {
                if (Ending) return;
                if (stopRequest != null && stopRequest.WaitOne(0)) { exitRequested = true; Close(); return; }
                if (sessionStop != null && sessionStop.WaitOne(0)) { exitRequested = true; Close(); return; }
                // External requests always open; only the in-client entry toggles.
                if (settingsRequest != null && settingsRequest.WaitOne(0)) { if (NativeMode) { ClientUi.Activate(clientWindow); nativeUi.Show(); } else Restore(); }
            };
            if (!preview) ipcTimer.Start();
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!preview && !exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                Cleanup();
            };
        }
        void StartRuntime() {
            if (Ending) return;
            if (nativeUi != null) { nativeUi.Connect(); htmlReady = true; }
            if (settings.Enabled) Start(); else Publish();
            if (parent != null) parentTimer.Start();
            if (alive != null) alive.Set();
            if (sessionAlive != null) sessionAlive.Set();
            WriteRuntimeStatus();
        }
        void WriteRuntimeStatus() {
            if (!preview && parent != null) {
                try {
                    using (new ClientUi.DpiScope(clientWindow)) {
                        ClientUi.Rect host; ClientUi.GetWindowRect(clientWindow, out host);
                        StateFile.Write(Path.Combine(Program.DataFolder, "worker-status.json"), new { version = Program.Version, clientPid = parent.Id, workerPid = Process.GetCurrentProcess().Id,
                            clientWindow = clientWindow.ToInt64(), clientStyle = ClientUi.GetWindowLong(clientWindow, -20), clientScale = ClientUi.Scale(clientWindow), clientBounds = host,
                            entryVisible = nativeUi.Ready, entryError = nativeUi.Error,
                            ui = nativeUi != null ? "native-gf" : "preview", nativeReady = nativeUi != null && nativeUi.Ready,
                            nativeResult = nativeUi.LastResult, nativeSystemError = nativeUi.LastSystemError });
                    }
                } catch { }
            }
        }
        internal void RequestExit() {
            if (closing || IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { if (!closing && !IsDisposed) { exitRequested = true; Close(); } });
        }
        async void InitializeHtml() {
            if (initializing || Ending || web.IsDisposed) return; initializing = true;
            try {
                webData = preview ? Path.Combine(Path.GetTempPath(), "BetterDownload-Preview", Guid.NewGuid().ToString("N")) : Path.Combine(Program.DataFolder, "webview");
                htmlPhase = "初始化网页运行环境";
                var environment = await CoreWebView2Environment.CreateAsync(null, webData);
                if (Ending || web.IsDisposed) return;
                htmlPhase = "创建网页控件"; await web.EnsureReady(environment);
                if (Ending || web.IsDisposed || web.Core == null) return;
                var core = web.Core;
                htmlPhase = "载入内置页面";
                core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = preview;
                core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
                bool firstNavigation = true;
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    if (Ending) return;
                    // NavigateToString reports a data: URI during startup; the
                    // resulting document's source and message origin are about:blank.
                    if (firstNavigation) { firstNavigation = false; return; }
                    e.Cancel = true;
                };
                core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { if (Ending) return; if (!e.IsSuccess) { problem = "内置页面加载失败：" + e.WebErrorStatus; loading.Text = problem; } else htmlPhase = "等待页面就绪消息"; };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { if (!Ending) e.Handled = true; };
                core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
                    if (Ending || e.Source != "about:blank") return;
                    try {
                        var request = json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson); object value;
                        if (request == null || !request.TryGetValue("action", out value) || !(value is string)) return;
                        string action = (string)value; request.TryGetValue("value", out value); HandleAction(action, value as string);
                    } catch (Exception error) { problem = error.Message; Publish(); }
                };
                using (var stream = typeof(AppWindow).Assembly.GetManifestResourceStream("settings.html"))
                using (var reader = new StreamReader(stream)) core.NavigateToString(reader.ReadToEnd());
            } catch (Exception e) {
                if (Ending) return;
                problem = "设置页未能加载：" + e.Message; loading.Text = problem + "\n自动转换仍会在后台运行。";
                initializing = false;
            }
        }
        void HandleAction(string action, string value) {
            if (Ending) return;
            if (action == "ready") { htmlReady = true; loading.Hide(); Publish(); return; }
            if (!htmlReady) return;
            if (action == "close") { if (nativeUi != null) nativeUi.Hide(); else Hide(); return; }
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
            if (Ending) return;
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
            if (!htmlReady || Ending || (nativeUi == null && (web.IsDisposed || web.Core == null))) return;
            var roots = preview ? new[] { settings.Root } : paths.Roots;
            string message = !settings.Enabled ? "已关闭" : latest == null || latest.State == "watching" || latest.State == "success" || latest.State == "skipped" || latest.State == "scan-complete" ? "已启用 · 下载完成后自动转换" : latest.Message;
            string data = json.Serialize(new {
                version = Program.Version, openSequence = openSequence, visible = panelVisible, enabled = settings.Enabled, notify = settings.Notify, style = settings.CardStyle, stay = settings.CardStay,
                message = message, state = latest == null ? "" : latest.State,
                count = completed + failed == 0 ? "" : "本次完成 " + completed + " 首" + (failed > 0 ? " · " + failed + " 首待处理" : ""),
                error = problem.Length > 0 ? problem : latest != null && latest.State == "error" ? latest.Message : "",
                client = settings.Client, roots = roots, cache = settings.CoverCache, history = history,
                scanNote = scanNote
            });
            if (nativeUi != null) nativeUi.Publish(data); else web.PostJson(data);
        }
        public void PreviewCard() {
            if (Ending) return;
            demoTimer.Stop(); if (demoDone != null) demoTimer.Tick -= demoDone;
            var sample = new WorkStatus { Id = "preview-" + Guid.NewGuid().ToString("N"), Source = "示例歌曲.mflac", State = "converting", Percent = 64, Message = "正在转换", Track = new TrackInfo { Title = "夜间来信", Artist = "示例歌手", Format = "FLAC" } };
            card.Receive(sample, true);
            demoDone = delegate { demoTimer.Stop(); demoTimer.Tick -= demoDone; demoDone = null; sample.State = "success"; sample.Percent = 100; sample.Output = Path.Combine(settings.Root, "unlock", "示例歌曲.flac"); card.Receive(sample, true); };
            demoTimer.Tick += demoDone; demoTimer.Start();
        }
        void OpenOutput(string path) {
            try { SafePath.NoLinks(path); if (!Directory.Exists(path)) return; Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception e) { problem = e.Message; Publish(); }
        }
        internal void Restore() {
            if (Ending) return;
            if (nativeUi != null) { nativeUi.Show(); Publish(); return; }
            Show(); Publish();
        }
        internal Task<string> EvaluateHtml(string script) { return web.Evaluate(script); }
        internal Task CaptureHtml(string file) { return CapturePage(file); }
        async Task CapturePage(string file) { using (var output = File.Create(file)) await web.CapturePage(output); }
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
            if (closing) return; closing = true; htmlReady = false;
            ipcTimer.Stop(); parentTimer.Stop(); demoTimer.Stop();
            web.Shutdown(); Stop(); card.Dispose();
            if (nativeUi != null) nativeUi.Dispose();
            if (alive != null) alive.Reset();
            if (sessionAlive != null) sessionAlive.Reset();
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                Cleanup(); ipcTimer.Dispose(); parentTimer.Dispose(); demoTimer.Dispose(); web.Dispose(); brandIcon.Dispose();
                if (settingsRequest != null) { settingsRequest.Dispose(); stopRequest.Dispose(); alive.Dispose(); }
                if (sessionAlive != null) { sessionAlive.Dispose(); sessionStop.Dispose(); }
                if (parent != null) parent.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
