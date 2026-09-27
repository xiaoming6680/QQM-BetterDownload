using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QqmBetterDownload {
    // Derives the machine-bound MMKV store name and key with QQ Music's own
    // CommonFunction.dll (export ordinal 12). A verified build runs in this
    // process. Any other Tencent-signed build runs in a throwaway child process,
    // so a changed interface can only crash or stall that child.
    internal static class KeyInterface {
        internal sealed class Result { internal string Store = "", Key = ""; internal bool Verified; }
        internal const string ChildSwitch = "--derive-store";
        // Tests route the child through their own executable and stand-in DLLs.
        internal static string HelperSwitch = ChildSwitch;
        internal static bool AllowUnsigned { get; set; }
        internal static int Timeout = 15000;
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32")] static extern uint SetErrorMode(uint mode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Derive(IntPtr seed, IntPtr name);
        const int Buffer = 1024;
        internal static Result Load(string dll) {
            if (ClientCompatibility.KnownKeyInterface(PeImage.Read(dll).CodeHash)) return Here(dll, true);
            string helper; using (var self = Process.GetCurrentProcess()) helper = self.MainModule.FileName;
            return Isolated(dll, helper, HelperSwitch, Timeout);
        }
        internal static Result Here(string dll, bool requireKnown) {
            if (IntPtr.Size != 4) throw new NotSupportedException("请使用 x86 构建。");
            // Deny writes while the component is identified and mapped, so the
            // code that runs is the code that was checked.
            using (var file = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var image = PeImage.Read(file); bool known = ClientCompatibility.KnownKeyInterface(image.CodeHash);
                if (!known && requireKnown) throw new NotSupportedException("此 QQ 音乐版本的本地接口尚未适配，下载任务会保留。");
                if (!known && !image.HasExport(ClientCompatibility.KeyOrdinal)) throw new NotSupportedException("此版本 QQ 音乐的本地组件缺少已知接口，新格式下载会保留等待适配。");
                if (!known && !AllowUnsigned && !Authenticode.IsTencent(dll)) throw new NotSupportedException("此版本 QQ 音乐的本地组件没有有效的腾讯签名，已停止调用；新格式下载会保留。");
                IntPtr module = LoadLibraryEx(dll, IntPtr.Zero, 0x1100); // DLL directory + System32 only
                if (module == IntPtr.Zero) throw new IOException("无法加载 QQ 音乐本地组件，错误 " + Marshal.GetLastWin32Error());
                // QQ passes 33- and 68-byte buffers; larger ones cost nothing.
                IntPtr seed = Marshal.AllocHGlobal(Buffer), name = Marshal.AllocHGlobal(Buffer);
                try {
                    Clear(seed); Clear(name);
                    IntPtr address = GetProcAddress(module, (IntPtr)ClientCompatibility.KeyOrdinal);
                    if (address == IntPtr.Zero) throw new NotSupportedException("本地组件接口缺失。");
                    ((Derive)Marshal.GetDelegateForFunctionPointer(address, typeof(Derive)))(seed, name);
                    return Check(Text(seed), Text(name), known);
                } finally { Clear(seed); Clear(name); Marshal.FreeHGlobal(seed); Marshal.FreeHGlobal(name); FreeLibrary(module); }
            }
        }
        static void Clear(IntPtr buffer) { Marshal.Copy(new byte[Buffer], 0, buffer, Buffer); }
        static string Text(IntPtr buffer) {
            var bytes = new byte[Buffer]; Marshal.Copy(buffer, bytes, 0, Buffer);
            int length = Array.IndexOf(bytes, (byte)0);
            string text = length < 0 ? "" : Encoding.ASCII.GetString(bytes, 0, length);
            Array.Clear(bytes, 0, bytes.Length); return text;
        }
        // Every build must return the verified shape: a 32-character hex key and
        // a plain file name for the store under %APPDATA%\Tencent\QQMusic.
        internal static Result Check(string key, string store, bool verified) {
            if (key == null || key.Length != 32 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("本地存储参数无效。");
            if (store == null || store.Length < 1 || store.Length > 63 || store == "." || store == ".." || !store.All(c => c < 128 && (Char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))) throw new InvalidDataException("本地存储名称无效。");
            return new Result { Key = key, Store = store, Verified = verified };
        }
        internal static Result Isolated(string dll, string helper, string argument, int timeout) {
            var start = new ProcessStartInfo(helper, argument + " \"" + Path.GetFullPath(dll) + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = new UTF8Encoding(false) };
            string text; int exit;
            using (var child = Process.Start(start)) {
                Task<string> output = child.StandardOutput.ReadToEndAsync();
                if (!child.WaitForExit(timeout)) {
                    try { child.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    child.WaitForExit(5000);
                    throw new IOException("QQ 音乐本地接口响应超时，新格式下载会保留并稍后重试。");
                }
                child.WaitForExit(); exit = child.ExitCode;
                text = output.Wait(5000) ? output.Result : "";
            }
            Dictionary<string, object> reply = null;
            try { reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); } catch (ArgumentException) { } catch (InvalidOperationException) { }
            object store, key, error, unsupported;
            if (exit == 0 && reply != null && reply.TryGetValue("store", out store) && reply.TryGetValue("key", out key)) return Check(key as string, store as string, false);
            if (reply != null && reply.TryGetValue("error", out error) && error is string) {
                if (reply.TryGetValue("unsupported", out unsupported) && true.Equals(unsupported)) throw new NotSupportedException((string)error);
                throw new IOException((string)error);
            }
            // A crash means the interface changed; do not keep calling it.
            throw new NotSupportedException("此版本 QQ 音乐的本地接口调用失败（退出码 0x" + exit.ToString("X8") + "），新格式下载会保留等待适配。");
        }
        // Entry point of the child process: BetterDownload.exe --derive-store DLL.
        [HandleProcessCorruptedStateExceptions]
        internal static int ChildMain(string dll) {
            SetErrorMode(0x8003); // no crash, critical-error or open-file dialogs
            var json = new JavaScriptSerializer(); string reply; int code;
            try {
                var result = Here(Path.GetFullPath(dll), false);
                reply = json.Serialize(new { store = result.Store, key = result.Key }); code = 0;
            } catch (Exception e) {
                bool fault = e is AccessViolationException || e is SEHException;
                reply = json.Serialize(new { error = fault ? "此版本 QQ 音乐的本地接口调用异常，新格式下载会保留等待适配。" : e.Message, unsupported = fault || e is NotSupportedException || e is InvalidDataException }); code = 3;
            }
            byte[] bytes = new UTF8Encoding(false).GetBytes(reply);
            using (var output = Console.OpenStandardOutput()) output.Write(bytes, 0, bytes.Length);
            return code;
        }
    }
    // No injection or account credentials: QQ Music's component only derives
    // the machine-bound MMKV store name and key. All database access is read-only.
    public sealed class LocalKeys : IDisposable {
        byte[] key;
        public readonly string StorePath;
        // False when the local interface is a Tencent-signed build that has not
        // been verified yet; its output is then checked before it is trusted.
        public readonly bool Verified;
        public static string FindClient() {
            foreach (var process in Process.GetProcessesByName("QQMusic")) {
                using (process) { try { return Path.GetDirectoryName(process.MainModule.FileName); } catch { } }
            }
            // QQ Music may be installed on any drive; its installer records the
            // location in the 32-bit registry view.
            foreach (string[] entry in new[] { new[] { "SOFTWARE\\Tencent\\QQMusic", "Install" }, new[] { "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\QQMusic", "InstallLocation" } }) {
                foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser }) {
                    try {
                        using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry32))
                        using (var key = root.OpenSubKey(entry[0])) {
                            string path = key == null ? null : key.GetValue(entry[1]) as string;
                            if (!String.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "QQMusic.exe"))) return path.TrimEnd('\\');
                        }
                    } catch (Exception) { }
                }
            }
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            foreach (string root in roots) {
                string path = Path.Combine(root, "Tencent", "QQMusic");
                if (File.Exists(Path.Combine(path, "QQMusic.exe"))) return path;
            }
            return "";
        }
        public LocalKeys(string client, string storeFolder = null) {
            if (IntPtr.Size != 4) throw new NotSupportedException("请使用 x86 构建。");
            string dll = Path.GetFullPath(Path.Combine(client, "CommonFunction.dll"));
            if (!File.Exists(dll)) throw new NotSupportedException("未找到 QQ 音乐本地组件，新格式下载会保留等待适配；文件内带密钥的旧格式仍可转换。");
            var derived = KeyInterface.Load(dll);
            Verified = derived.Verified;
            key = Encoding.ASCII.GetBytes(derived.Key.Substring(0, 16));
            StorePath = Path.Combine(storeFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "QQMusic"), derived.Store);
        }
        public Dictionary<string, string> Read() {
            if (!File.Exists(StorePath)) return new Dictionary<string, string>(StringComparer.Ordinal);
            byte[] before = ReadShared(StorePath + ".crc", 4096);
            byte[] data = ReadShared(StorePath, 64 * 1024 * 1024);
            byte[] after = ReadShared(StorePath + ".crc", 4096);
            if (!before.SequenceEqual(after)) throw new IOException("下载记录正在更新，稍后重试。");
            try { return Mmkv.Decode(data, before, key); }
            catch (Exception e) {
                // The store's checksum covers the ciphertext only. Records that do
                // not parse (or decode as UTF-8) mean an unverified interface
                // returned the wrong key.
                if (Verified || !(e is InvalidDataException || e is ArgumentException)) throw;
                throw new NotSupportedException("此版本 QQ 音乐的本地接口返回的密钥无法读取下载记录，新格式下载会保留等待适配。");
            }
        }
        static byte[] ReadShared(string path, int limit) {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (file.Length > limit) throw new InvalidDataException("本地记录超过读取上限。");
                return Binary.Read(file, checked((int)file.Length));
            }
        }
        public void Dispose() { if (key != null) Array.Clear(key, 0, key.Length); }
    }
    public static class Binary {
        public static byte[] Read(Stream stream, int count) {
            byte[] result = new byte[count]; int n = 0;
            while (n < count) { int got = stream.Read(result, n, count - n); if (got == 0) throw new EndOfStreamException("文件尚未完整写入。"); n += got; }
            return result;
        }
        public static uint U32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }
        public static uint Crc32(byte[] b, int offset, int count) {
            uint crc = 0xffffffff;
            for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ b[i]) & 255] ^ (crc >> 8);
            return ~crc;
        }
        static readonly uint[] CrcTable = MakeTable();
        static uint[] MakeTable() {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int n = 0; n < 8; n++) c = (c & 1) == 0 ? c >> 1 : (c >> 1) ^ 0xedb88320; table[i] = c; }
            return table;
        }
        public static int Varint(byte[] b, ref int p) {
            uint value = 0;
            for (int shift = 0; shift <= 28; shift += 7) {
                if (p >= b.Length) throw new InvalidDataException("MMKV 数据不完整。");
                byte v = b[p++];
                if (shift == 28 && (v & 0xf0) != 0) throw new InvalidDataException("MMKV 长度溢出。");
                value |= (uint)(v & 127) << shift;
                if (v < 128) { if (value > int.MaxValue) throw new InvalidDataException("MMKV 长度无效。"); return (int)value; }
            }
            throw new InvalidDataException("MMKV varint 无效。");
        }
        public static byte[] Field(byte[] b, ref int p) {
            int n = Varint(b, ref p);
            if (n > b.Length - p) throw new InvalidDataException("MMKV 字段越界。");
            byte[] value = new byte[n]; Buffer.BlockCopy(b, p, value, 0, n); p += n; return value;
        }
    }
    public static class Mmkv {
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        public static Dictionary<string, string> Decode(byte[] data, byte[] meta, byte[] key) {
            if (meta.Length < 112 || data.Length < 4 || key.Length != 16) throw new InvalidDataException("MMKV 头部无效。");
            uint version = Binary.U32(meta, 4);
            if (version < 1 || version > 4 || (version == 4 && BitConverter.ToUInt64(meta, 104) != 0)) throw new NotSupportedException("不支持的 MMKV 版本或自动过期标志。");
            uint size = version >= 3 ? Binary.U32(meta, 28) : Binary.U32(data, 0);
            if (size > data.Length - 4 || size != Binary.U32(data, 0) || Binary.Crc32(data, 4, (int)size) != Binary.U32(meta, 0)) throw new IOException("下载记录校验未通过，稍后重试。");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (size == 0) return result;
            byte[] iv = version >= 2 ? meta.Skip(12).Take(16).ToArray() : (byte[])key.Clone();
            byte[] plain = Cfb(data, 4, (int)size, key, iv);
            try {
                int pos = 0; Binary.Varint(plain, ref pos); // random size holder, not the record count
                while (pos < plain.Length) {
                    string name = Utf8.GetString(Binary.Field(plain, ref pos));
                    if (name.Length == 0) continue;
                    byte[] value = Binary.Field(plain, ref pos);
                    if (value.Length == 0) { result.Remove(name); continue; }
                    if (!AudioFile.IsEncrypted(name)) continue;
                    int p = 0; byte[] text = Binary.Field(value, ref p);
                    if (p != value.Length || text.Length < 16 || text.Length > 16384) throw new InvalidDataException("下载密钥记录格式无效。");
                    result[name] = Utf8.GetString(text);
                    Array.Clear(value, 0, value.Length); Array.Clear(text, 0, text.Length);
                }
                return result;
            } finally { Array.Clear(plain, 0, plain.Length); }
        }
        // MMKV uses AES-CFB128. .NET Framework's CFB feedback defaults to 8 bits,
        // so implement the 128-bit feedback using its standard AES primitive.
        public static byte[] Cfb(byte[] input, int offset, int length, byte[] key, byte[] iv) {
            byte[] output = new byte[length], feedback = (byte[])iv.Clone(), block = new byte[16];
            using (var aes = Aes.Create()) {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
                using (var enc = aes.CreateEncryptor()) for (int p = 0; p < length; p += 16) {
                    enc.TransformBlock(feedback, 0, 16, block, 0); int n = Math.Min(16, length - p);
                    for (int j = 0; j < n; j++) { byte c = input[offset + p + j]; output[p + j] = (byte)(c ^ block[j]); feedback[j] = c; }
                }
            }
            Array.Clear(feedback, 0, 16); Array.Clear(block, 0, 16); return output;
        }
    }
}
