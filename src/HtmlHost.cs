using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace QqmBetterDownload {
    // Own the controller explicitly. The SDK's WinForms wrapper reads Profile
    // while disposing, which fails if QQ already destroyed the native browser.
    internal sealed class HtmlHost : Control {
        CoreWebView2Controller controller;
        CoreWebView2 core;
        Task initialization;
        bool ending;
        internal event EventHandler BrowserClosed;
        internal CoreWebView2 Core { get { return ending || IsDisposed ? null : core; } }
        internal Color DefaultBackgroundColor { get { return BackColor; } set { BackColor = value; } }
        internal HtmlHost() { TabStop = true; SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true); }
        protected override CreateParams CreateParams {
            get { var value = base.CreateParams; if (BackColor.A == 0) value.ExStyle |= 0x00200000; return value; }
        }
        internal Task EnsureReady(CoreWebView2Environment environment) {
            if (initialization == null) initialization = Initialize(environment);
            return initialization;
        }
        async Task Initialize(CoreWebView2Environment environment) {
            if (ending || IsDisposed) return;
            IntPtr parent = Handle;
            var created = await environment.CreateCoreWebView2ControllerAsync(parent);
            if (ending || IsDisposed || !IsHandleCreated || Handle != parent) { CloseController(created); return; }
            controller = created;
            try {
                core = created.CoreWebView2;
                created.DefaultBackgroundColor = BackColor;
                created.MoveFocusRequested += MoveFocus;
                core.ProcessFailed += ProcessFailed;
                UpdateController();
            } catch { ReleaseController(); throw; }
        }
        void MoveFocus(object sender, CoreWebView2MoveFocusRequestedEventArgs e) {
            if (ending || IsDisposed) return;
            e.Handled = true;
            if (Parent != null) Parent.SelectNextControl(this, e.Reason != CoreWebView2MoveFocusReason.Previous, true, true, true);
        }
        void ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e) {
            if (ending || IsDisposed) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) LostController();
        }
        internal static bool IsClosed(Exception error) {
            for (Exception current = error; current != null; current = current.InnerException) {
                if (current is ObjectDisposedException) return true;
                // Native controller already closed / COM server disconnected.
                if (current is COMException && (current.HResult == unchecked((int)0x8007139f) || current.HResult == unchecked((int)0x80010108))) return true;
            }
            return false;
        }
        void WithController(Action<CoreWebView2Controller> action) {
            if (ending || IsDisposed || controller == null) return;
            try { action(controller); } catch (Exception error) { if (!IsClosed(error)) throw; LostController(); }
        }
        void LostController() {
            if (ending) return;
            ReleaseController();
            var callback = BrowserClosed; if (callback != null) callback(this, EventArgs.Empty);
        }
        internal bool PostJson(string json) {
            if (Core == null) return false;
            try { core.PostWebMessageAsJson(json); return true; }
            catch (Exception error) { if (!IsClosed(error)) throw; LostController(); return false; }
        }
        internal Task<string> Evaluate(string script) {
            if (Core == null) throw new ObjectDisposedException("HTML settings");
            return core.ExecuteScriptAsync(script);
        }
        internal Task CapturePage(Stream output) {
            if (Core == null) throw new ObjectDisposedException("HTML settings");
            return core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        }
        internal void UpdateController() {
            WithController(current => {
                current.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                current.IsVisible = Visible && ClientSize.Width > 0 && ClientSize.Height > 0;
                current.NotifyParentWindowPositionChanged();
            });
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateController(); }
        protected override void OnPaintBackground(PaintEventArgs e) { if (BackColor.A != 0) base.OnPaintBackground(e); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateController(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); WithController(current => current.MoveFocus(CoreWebView2MoveFocusReason.Programmatic)); }
        protected override void OnHandleDestroyed(EventArgs e) { ReleaseController(); base.OnHandleDestroyed(e); }
        static void CloseController(CoreWebView2Controller value) {
            // Close is the only API allowed here; never query Profile or other
            // members while tearing down a controller that may already be gone.
            try { value.Close(); } catch (Exception error) { if (!IsClosed(error)) throw; }
        }
        void ReleaseController() {
            ending = true;
            var previous = controller; controller = null; core = null;
            if (previous != null) CloseController(previous);
        }
        internal void Shutdown() { ReleaseController(); }
        protected override void Dispose(bool disposing) {
            try { if (disposing) ReleaseController(); }
            finally { base.Dispose(disposing); }
        }
    }
}
