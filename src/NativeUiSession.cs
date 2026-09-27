using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QqmBetterDownload {
    internal sealed class NativeUiSession : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Packet { internal UIntPtr Kind; internal int Bytes; internal IntPtr Text; }
        [DllImport("user32", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr sender, ref Packet packet, uint flags, uint timeout, out IntPtr result);
        internal static readonly uint ActionMessage = NativeBridge.RegisterWindowMessage("BetterDownload.NativeAction.v1");
        readonly Form owner;
        readonly IntPtr parent;
        readonly LocalPageServer server;
        readonly bool compatible;
        bool disposed;
        // Verified: GF, Common and the wrapper match tested builds. Candidate:
        // tested GF and Common with a rebuilt, Tencent-signed wrapper, which the
        // bridge checks at run time. Anything else keeps QQ's UI untouched.
        internal Support Support { get; private set; }
        internal string SupportReason { get; private set; }
        internal bool Compatible { get { return compatible; } }
        // Not adapted, or still not connected well after the client started.
        internal bool Unavailable { get { return !compatible || (!Ready && DateTime.UtcNow - created > TimeSpan.FromSeconds(30)); } }
        readonly DateTime created = DateTime.UtcNow;
        internal string PageUrl { get { return server.Url; } }
        internal string AssetFolder { get; private set; }
        internal string Error { get; private set; }
        internal int LastResult { get; private set; }
        internal int LastSystemError { get; private set; }
        internal bool Ready { get { return !disposed && owner.IsHandleCreated && NativeBridge.GetProp(parent, "BetterDownload.NativeUi") != IntPtr.Zero && NativeBridge.GetProp(parent, "BetterDownload.NativeOwner") == owner.Handle; } }
        internal bool SettingsVisible { get { return Ready && NativeBridge.GetProp(parent, "BetterDownload.NativeSettings") != IntPtr.Zero; } }
        internal bool CardVisible { get { return Ready && NativeBridge.GetProp(parent, "BetterDownload.NativeCard") != IntPtr.Zero; } }
        internal NativeUiSession(Form form, IntPtr window, string client, Action<string,string> action) {
            owner = form; parent = window;
            string reason; Support = ClientCompatibility.Interface(client, out reason); SupportReason = reason;
            compatible = Support == Support.Verified || Support == Support.Candidate;
            AssetFolder = Path.Combine(Program.DataFolder, "native-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AssetFolder); SafePath.NoLinks(AssetFolder);
            string icon;
            using (var input = typeof(Program).Assembly.GetManifestResourceStream("entry.svg"))
            using (var reader = new StreamReader(input)) icon = reader.ReadToEnd();
            // Match QQ's native top-bar buttons: a monochrome glyph that turns the
            // client's theme green on hover/press, with no background swatch.
            File.WriteAllText(Path.Combine(AssetFolder, "entry.svg"), icon);
            File.WriteAllText(Path.Combine(AssetFolder, "entry-hover.svg"), icon.Replace("#5A5F66", "#1ECC94"));
            File.WriteAllText(Path.Combine(AssetFolder, "entry-pressed.svg"), icon.Replace("#5A5F66", "#13BE86"));
            string html;
            using (var input = typeof(Program).Assembly.GetManifestResourceStream("settings.html"))
            using (var reader = new StreamReader(input)) html = reader.ReadToEnd();
            server = new LocalPageServer(html, delegate(string name, string value) {
                if (disposed || owner.IsDisposed || !owner.IsHandleCreated) return;
                try { owner.BeginInvoke((Action)delegate { if (!disposed && !owner.IsDisposed) action(name, value); }); } catch (InvalidOperationException) { }
            });
            Error = compatible ? "" : SupportReason;
        }
        internal bool Connect() {
            if (disposed || !compatible) return false;
            if (Ready) return true;
            // The last line tells the bridge whether the wrapper is a tested build.
            bool ok = Send(0, server.Url + "\n" + AssetFolder + "\n" + (Support == Support.Verified ? "1" : "0")) > 0;
            Error = ok ? "" : "正在等待 QQ 音乐的原生界面准备就绪。"; return ok;
        }
        internal void Publish(string state) { if (!disposed) server.Publish(state); }
        internal void Toggle() { if (Connect()) Send(1, ""); }
        internal bool Show() { return Connect() && Send(6, "") > 0; }
        internal void Hide() { if (Ready) Send(2, ""); }
        internal void DismissCard() { if (Ready) Send(4, ""); }
        internal bool ShowCard(string packet) { return Ready && Send(3, packet) > 0; }
        // QQ owns the user's click on its page; it lets this worker hand the
        // foreground to a window it opens next (the uninstall confirmation).
        internal void AllowForeground() { if (Ready) Send(7, ""); }
        int Send(int command, string text) {
            if (disposed || !ClientUi.IsWindow(parent) || !owner.IsHandleCreated) return 0;
            string wire = command + "\n" + text;
            var data = new Packet { Kind = (UIntPtr)0x42444746, Bytes = (wire.Length + 1) * 2, Text = Marshal.StringToHGlobalUni(wire) };
            try {
                IntPtr result; bool delivered = SendMessageTimeout(parent, 0x004a, owner.Handle, ref data, 3, 3000, out result) != IntPtr.Zero;
                LastSystemError = delivered ? 0 : Marshal.GetLastWin32Error();
                LastResult = delivered ? result.ToInt32() : 0; return LastResult;
            }
            finally { Marshal.FreeHGlobal(data.Text); }
        }
        public void Dispose() {
            if (disposed) return;
            bool detached = !ClientUi.IsWindow(parent) || Send(5, "") > 0 || !Ready;
            disposed = true; server.Dispose();
            // Only generated files in this unique session directory. Retain if
            // QQ is unresponsive and might still be using a texture.
            if (detached) try { foreach (string file in Directory.GetFiles(AssetFolder)) File.Delete(file); Directory.Delete(AssetFolder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
