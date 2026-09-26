using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace QqmBetterDownload {
    // No injection or account credentials. The verified local DLL only derives the
    // machine-bound MMKV store name and key. All database access below is read-only.
    public sealed class LocalKeys : IDisposable {
        const string SupportedHash = "A28EBDD9EDA2D3DFBFC540578681BC71425136858E29BC07A22EAD142510187D";
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void Derive([Out] byte[] seed, [Out] byte[] name);
        byte[] key;
        public readonly string StorePath;
        public static string FindClient() {
            foreach (var process in Process.GetProcessesByName("QQMusic")) {
                using (process) { try { return Path.GetDirectoryName(process.MainModule.FileName); } catch { } }
            }
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            foreach (string root in roots) {
                string path = Path.Combine(root, "Tencent", "QQMusic");
                if (File.Exists(Path.Combine(path, "QQMusic.exe"))) return path;
            }
            return "";
        }
        public LocalKeys(string client) {
            if (IntPtr.Size != 4) throw new NotSupportedException("请使用 x86 构建。");
            string dll = Path.GetFullPath(Path.Combine(client, "CommonFunction.dll"));
            using (var stream = File.OpenRead(dll)) using (var sha = SHA256.Create()) {
                string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (hash != SupportedHash) throw new NotSupportedException("此 QQ 音乐版本的本地接口尚未适配，下载任务会保留。当前已验证 22.71 x86。");
            }
            IntPtr module = LoadLibraryEx(dll, IntPtr.Zero, 0x1100); // DLL directory + System32 only
            if (module == IntPtr.Zero) throw new IOException("无法加载 QQ 音乐本地组件，错误 " + Marshal.GetLastWin32Error());
            byte[] seed = new byte[33], name = new byte[64];
            try {
                IntPtr address = GetProcAddress(module, (IntPtr)12);
                if (address == IntPtr.Zero) throw new NotSupportedException("本地组件接口缺失。");
                ((Derive)Marshal.GetDelegateForFunctionPointer(address, typeof(Derive)))(seed, name);
                int length = Array.IndexOf(name, (byte)0);
                if (length < 1 || length >= 64 || Array.IndexOf(seed, (byte)0) != 32) throw new InvalidDataException("本地存储参数无效。");
                string store = Encoding.ASCII.GetString(name, 0, length);
                if (store != Path.GetFileName(store) || store.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || store == "." || store == "..") throw new InvalidDataException("本地存储名称无效。");
                key = seed.Take(16).ToArray();
                StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "QQMusic", store);
            } finally { Array.Clear(seed, 0, seed.Length); Array.Clear(name, 0, name.Length); FreeLibrary(module); }
        }
        public Dictionary<string, string> Read() {
            if (!File.Exists(StorePath)) return new Dictionary<string, string>(StringComparer.Ordinal);
            byte[] before = ReadShared(StorePath + ".crc", 4096);
            byte[] data = ReadShared(StorePath, 64 * 1024 * 1024);
            byte[] after = ReadShared(StorePath + ".crc", 4096);
            if (!before.SequenceEqual(after)) throw new IOException("下载记录正在更新，稍后重试。");
            return Mmkv.Decode(data, before, key);
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
