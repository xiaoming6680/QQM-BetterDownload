using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace QqmBetterDownload {
    internal static class NativeUiCompatibility {
        // Deliberately exact: these interfaces are private COM ABIs, not a
        // promise that every release with the same major version is compatible.
        static readonly string[] Files = { "GF.dll", "QQMusic_GFWrapper.dll", "Common.dll" };
        static readonly string[] Hashes = {
            "98EF2C595BA3FA9F2323FC2D5C0995F49DD329E22F9722901391B1AF45438F3E",
            "0BFD0DA0A2673AAD32C68C5D778F2D22855ADD6255EFB4A55350F1E1747102E2",
            "7C11CC5D90ACB2F7400B3F3CFBB39DD889390158627552BE57D8349F3E12486C"
        };
        internal static bool Supported(string directory) {
            try {
                using (var sha = SHA256.Create()) for (int i = 0; i < Files.Length; i++) {
                    using (var file = File.Open(Path.Combine(directory, Files[i]), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                        if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") != Hashes[i]) return false;
                }
                return true;
            } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
    }
    internal sealed class NativeUiSession : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Packet { internal UIntPtr Kind; internal int Bytes; internal IntPtr Text; }
        [DllImport("user32", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr sender, ref Packet packet, uint flags, uint timeout, out IntPtr result);
        internal static readonly uint ActionMessage = NativeBridge.RegisterWindowMessage("BetterDownload.NativeAction.v1");
        readonly Form owner;
        readonly IntPtr parent;
        readonly LocalPageServer server;
        readonly bool compatible;
        bool disposed;
        internal string AssetFolder { get; private set; }
        internal string Error { get; private set; }
        internal int LastResult { get; private set; }
        internal int LastSystemError { get; private set; }
        internal bool Ready { get { return !disposed && owner.IsHandleCreated && NativeBridge.GetProp(parent, "BetterDownload.NativeUi") != IntPtr.Zero && NativeBridge.GetProp(parent, "BetterDownload.NativeOwner") == owner.Handle; } }
        internal bool SettingsVisible { get { return Ready && NativeBridge.GetProp(parent, "BetterDownload.NativeSettings") != IntPtr.Zero; } }
        internal bool CardVisible { get { return Ready && NativeBridge.GetProp(parent, "BetterDownload.NativeCard") != IntPtr.Zero; } }
        internal NativeUiSession(Form form, IntPtr window, string client, Action<string,string> action) {
            owner = form; parent = window; compatible = NativeUiCompatibility.Supported(client);
            AssetFolder = Path.Combine(Program.DataFolder, "native-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AssetFolder); SafePath.NoLinks(AssetFolder);
            string icon;
            using (var input = typeof(Program).Assembly.GetManifestResourceStream("entry.svg"))
            using (var reader = new StreamReader(input)) icon = reader.ReadToEnd();
            File.WriteAllText(Path.Combine(AssetFolder, "entry.svg"), icon);
            File.WriteAllText(Path.Combine(AssetFolder, "entry-hover.svg"), icon.Replace("<!--background-->", "<rect width=\"36\" height=\"32\" rx=\"6\" fill=\"#808080\" fill-opacity=\".12\"/>"));
            File.WriteAllText(Path.Combine(AssetFolder, "entry-pressed.svg"), icon.Replace("<!--background-->", "<rect width=\"36\" height=\"32\" rx=\"6\" fill=\"#808080\" fill-opacity=\".22\"/>"));
            string html;
            using (var input = typeof(Program).Assembly.GetManifestResourceStream("settings.html"))
            using (var reader = new StreamReader(input)) html = reader.ReadToEnd();
            server = new LocalPageServer(html, delegate(string name, string value) {
                if (disposed || owner.IsDisposed || !owner.IsHandleCreated) return;
                try { owner.BeginInvoke((Action)delegate { if (!disposed && !owner.IsDisposed) action(name, value); }); } catch (InvalidOperationException) { }
            });
            Error = compatible ? "" : "这个版本的 QQ 音乐尚未适配原生界面；请等待 BetterDownload 适配更新。";
        }
        internal bool Connect() {
            if (disposed || !compatible) return false;
            if (Ready) return true;
            bool ok = Send(0, server.Url + "\n" + AssetFolder) > 0;
            Error = ok ? "" : "正在等待 QQ 音乐的原生界面准备就绪。"; return ok;
        }
        internal void Publish(string state) { if (!disposed) server.Publish(state); }
        internal void Toggle() { if (Connect()) Send(1, ""); }
        internal void Show() { if (Connect()) Send(6, ""); }
        internal void Hide() { if (Ready) Send(2, ""); }
        internal void DismissCard() { if (Ready) Send(4, ""); }
        internal bool ShowCard(string packet) { return Ready && Send(3, packet) > 0; }
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
