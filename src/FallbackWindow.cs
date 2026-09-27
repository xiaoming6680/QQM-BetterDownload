using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace QqmBetterDownload {
    // Only for QQ Music builds whose own UI cannot host the settings page: the
    // same loopback page in a plain window. Closing it hides it; the worker
    // disposes it when QQ Music exits.
    internal sealed class FallbackWindow : Form {
        readonly HtmlHost web = new HtmlHost { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
        readonly Label loading = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "正在打开 BetterDownload 设置…", ForeColor = Color.FromArgb(90, 122, 105), BackColor = Color.White };
        readonly string page, data;
        bool started, closing;
        internal bool PageLoaded { get; private set; }
        internal string Problem { get; private set; }
        internal FallbackWindow(string url, string webData, Icon icon, double scale) {
            page = url; data = webData; Problem = "";
            Text = "BetterDownload 设置"; Icon = icon; BackColor = Color.White; Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size((int)(760 * scale), (int)(760 * scale)); MinimumSize = new Size((int)(480 * scale), (int)(420 * scale));
            Controls.Add(web); Controls.Add(loading); loading.BringToFront();
            web.BrowserClosed += delegate {
                if (closing || IsDisposed) return;
                PageLoaded = false; Problem = "设置页已关闭，请重新打开。"; loading.Text = Problem; loading.Show(); loading.BringToFront();
            };
        }
        // Create the top-level window now, so it takes the caller's DPI context.
        internal void Realize() { if (!IsHandleCreated) CreateHandle(); }
        internal void Present() {
            if (closing || IsDisposed) return;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate(); Start();
        }
        async void Start() {
            if (started || closing) return; started = true;
            try {
                var environment = await CoreWebView2Environment.CreateAsync(null, data);
                if (closing || IsDisposed) return;
                await web.EnsureReady(environment);
                var core = web.Core; if (closing || core == null) return;
                core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
                // Only our capability URL may load; the page's links are actions.
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri != page) e.Cancel = true; };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) {
                    if (closing || IsDisposed) return;
                    if (e.IsSuccess) { PageLoaded = true; loading.Hide(); }
                    else { Problem = "设置页加载失败：" + e.WebErrorStatus; loading.Text = Problem; }
                };
                core.Navigate(page);
            } catch (Exception error) {
                if (closing || IsDisposed) return;
                started = false; Problem = "设置页需要 Microsoft Edge WebView2 运行时：" + error.Message; loading.Text = Problem;
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            closing = true; web.Shutdown(); base.OnFormClosing(e);
        }
        internal void Shutdown() { if (closing) return; closing = true; web.Shutdown(); Close(); }
        internal System.Threading.Tasks.Task<string> Evaluate(string script) { return web.Evaluate(script); }
        protected override void Dispose(bool disposing) {
            if (disposing) { closing = true; web.Dispose(); loading.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
