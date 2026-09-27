using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QqmBetterDownload {
    // Also compiled into the installer: keep this file free of other project types.
    // QQ Music components are identified by their code, not their file bytes.
    // Tencent re-signs unchanged modules (22.71 ships a CommonFunction.dll built
    // in 2024 and signed again in 2026), which changes every whole-file hash.
    public sealed class PeImage {
        readonly byte[] data;
        readonly int checksum, security = -1, sectionTable, sectionCount;
        readonly long exportRva, certificate, certificateSize;
        public string CodeHash { get; private set; }
        public static PeImage Read(string path) {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) return Read(file);
        }
        public static PeImage Read(Stream file) {
            if (file.Length < 0x200 || file.Length > 64L * 1024 * 1024) throw Invalid();
            var bytes = new byte[file.Length]; int read = 0; file.Position = 0;
            while (read < bytes.Length) { int got = file.Read(bytes, read, bytes.Length - read); if (got == 0) throw new EndOfStreamException("组件文件不完整。"); read += got; }
            return new PeImage(bytes);
        }
        static InvalidDataException Invalid() { return new InvalidDataException("不是有效的 Windows 程序组件。"); }
        uint U32(long at) { if (at < 0 || at > data.Length - 4) throw Invalid(); return BitConverter.ToUInt32(data, (int)at); }
        int U16(long at) { if (at < 0 || at > data.Length - 2) throw Invalid(); return BitConverter.ToUInt16(data, (int)at); }
        PeImage(byte[] image) {
            data = image;
            long pe = U32(0x3c);
            if (data[0] != 'M' || data[1] != 'Z' || pe < 0x40 || pe > data.Length - 0x100 || U32(pe) != 0x4550) throw Invalid();
            long optional = pe + 24; int optionalSize = U16(pe + 20), magic = U16(optional);
            if (magic != 0x10b && magic != 0x20b) throw Invalid();
            long directories = optional + (magic == 0x10b ? 96 : 112);
            if (directories > optional + optionalSize || optional + optionalSize > data.Length) throw Invalid();
            long present = Math.Min(U32(optional + (magic == 0x10b ? 92 : 108)), (optional + optionalSize - directories) / 8);
            checksum = (int)optional + 64;
            if (present > 0) exportRva = U32(directories);
            if (present > 4) {
                security = (int)directories + 32; certificate = U32(security); certificateSize = U32(security + 4);
                if (certificateSize != 0 && (certificate < security + 8 || certificate > data.Length || certificateSize > data.Length - certificate)) throw Invalid();
            }
            sectionCount = U16(pe + 6); sectionTable = (int)(optional + optionalSize);
            if (sectionCount > 96 || (long)sectionTable + 40L * sectionCount > data.Length) throw Invalid();
            CodeHash = Hash();
        }
        // SHA-256 of every byte except the checksum, the certificate directory
        // entry and the certificate table: the same bytes a signature covers.
        string Hash() {
            using (var sha = SHA256.Create()) {
                Add(sha, 0, checksum);
                if (security < 0) Add(sha, checksum + 4, data.Length);
                else {
                    Add(sha, checksum + 4, security);
                    if (certificateSize == 0) Add(sha, security + 8, data.Length);
                    else { Add(sha, security + 8, (int)certificate); Add(sha, (int)(certificate + certificateSize), data.Length); }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        void Add(SHA256 sha, int from, int to) { if (to > from) sha.TransformBlock(data, from, to - from, null, 0); }
        long Offset(long rva) {
            for (int i = 0; i < sectionCount; i++) {
                int at = sectionTable + 40 * i;
                long size = Math.Max(U32(at + 8), U32(at + 16)), start = U32(at + 12), raw = U32(at + 20);
                if (rva >= start && rva < start + size) { long offset = rva - start + raw; if (offset < data.Length) return offset; }
            }
            return -1;
        }
        public bool HasExport(int ordinal) {
            long table = exportRva == 0 ? -1 : Offset(exportRva);
            if (table < 0) return false;
            long index = ordinal - (long)U32(table + 16), count = U32(table + 20), functions = Offset(U32(table + 28));
            return index >= 0 && index < count && index < 65536 && functions >= 0 && U32(functions + 4 * index) != 0;
        }
    }

    public static class Authenticode {
        public const string Tencent = "Tencent Technology (Shenzhen) Company Limited";
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo { public uint Size; public string Path; public IntPtr Handle, Subject; }
        [StructLayout(LayoutKind.Sequential)]
        struct TrustData {
            public uint Size; public IntPtr PolicyCallback, SipClient; public uint Ui, Revocation, Union; public IntPtr File;
            public uint StateAction; public IntPtr State, Url; public uint Flags, Context; public IntPtr Settings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)] static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        static Guid Verify = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        // Returns the signer of a valid embedded signature, or null. Offline:
        // no revocation or URL retrieval, so identifying a component never
        // touches the network.
        public static string Signer(string path) {
            var info = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = Path.GetFullPath(path) };
            IntPtr file = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo))); bool marshaled = false;
            try {
                Marshal.StructureToPtr(info, file, false); marshaled = true;
                // UI none, revocation none, file choice, cache-only URL retrieval.
                var trust = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Revocation = 0, Union = 1, File = file, StateAction = 1, Flags = 0x1000 | 0x10 };
                int result = WinVerifyTrust(IntPtr.Zero, ref Verify, ref trust);
                trust.StateAction = 2; WinVerifyTrust(IntPtr.Zero, ref Verify, ref trust);
                if (result != 0) return null;
            } finally { if (marshaled) Marshal.DestroyStructure(file, typeof(FileInfo)); Marshal.FreeHGlobal(file); }
            X509Certificate2 signer = null;
            try { signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); return signer.GetNameInfo(X509NameType.SimpleName, false); }
            catch (CryptographicException) { return null; }
            finally { if (signer != null) signer.Reset(); }
        }
        public static bool IsTencent(string path) { return Signer(path) == Tencent; }
    }

    public enum Support { Missing, Verified, Candidate, Unsupported }

    public static class ClientCompatibility {
        // CommonFunction.dll: code built 2024-02-26, verified in QQ Music 22.71 x86.
        static readonly string[] KeyInterfaces = { "4F15D261BAD9BD5DBE59ED8F678A82F68486E0B6F1A774103F32163FAE60017D" };
        // GF.dll (built 2025-05-15) and Common.dll (built 2025-04-27) define the
        // private interfaces and vtable slots that native/gf_ui.c relies on.
        static readonly string[] GfCores = { "D9E299AC625521C213E290A6F1A99D7919EE2699E32D0FA076346DAB7DA3E710" };
        static readonly string[] CommonCores = { "B11411AD95C9955C789FD4B6ED701473B853EA91252E89BCFDD41F2021ADC29C" };
        // QQMusic_GFWrapper.dll is rebuilt with every release. Other builds are
        // used through decorated exports (they encode the signatures) plus the
        // runtime object checks in gf_ui.c.
        static readonly string[] Wrappers = { "FF66ECA3EB579876B97C22239ABEC0DD336F025617593BEBAEFFE5BA634EACB3" };
        public const int KeyOrdinal = 12;
        public static bool KnownKeyInterface(string code) { return Array.IndexOf(KeyInterfaces, code) >= 0; }
        static string Code(string path) { try { return File.Exists(path) ? PeImage.Read(path).CodeHash : null; } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } catch (InvalidDataException) { return ""; } }
        public static Support Keys(string client, out string reason) {
            string dll = Path.Combine(client ?? "", "CommonFunction.dll");
            PeImage image;
            try { if (!File.Exists(dll)) { reason = "未找到 QQ 音乐本地组件，新格式下载会保留等待适配。"; return Support.Missing; } image = PeImage.Read(dll); }
            catch (Exception e) {
                if (!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is InvalidDataException)) throw;
                reason = "无法读取 QQ 音乐本地组件：" + e.Message; return Support.Unsupported;
            }
            if (KnownKeyInterface(image.CodeHash)) { reason = ""; return Support.Verified; }
            if (!image.HasExport(KeyOrdinal)) { reason = "此版本 QQ 音乐的本地组件缺少已知接口，新格式下载会保留等待适配。"; return Support.Unsupported; }
            if (!Authenticode.IsTencent(dll)) { reason = "此版本 QQ 音乐的本地组件没有有效的腾讯签名，已停止调用；新格式下载会保留。"; return Support.Unsupported; }
            reason = "本地组件是尚未实测的版本：已通过签名和接口检查，转换时逐项校验结果。"; return Support.Candidate;
        }
        public static Support Interface(string client, out string reason) {
            string gf = Code(Path.Combine(client ?? "", "GF.dll")), common = Code(Path.Combine(client ?? "", "Common.dll"));
            string wrapperPath = Path.Combine(client ?? "", "QQMusic_GFWrapper.dll"), wrapper = Code(wrapperPath);
            if (gf == null || common == null || wrapper == null) { reason = "未找到 QQ 音乐界面组件，设置改在独立窗口打开。"; return Support.Missing; }
            if (Array.IndexOf(GfCores, gf) < 0 || Array.IndexOf(CommonCores, common) < 0) { reason = "QQ 音乐的界面组件已更新，入口和卡片暂未适配，设置改在独立窗口打开。"; return Support.Unsupported; }
            if (Array.IndexOf(Wrappers, wrapper) >= 0) { reason = ""; return Support.Verified; }
            if (wrapper.Length == 0 || !Authenticode.IsTencent(wrapperPath)) { reason = "QQ 音乐的界面组件没有有效的腾讯签名，入口和卡片已停用，设置改在独立窗口打开。"; return Support.Unsupported; }
            reason = ""; return Support.Candidate;
        }
        // Short installer label for the detected client.
        public static string Label(Support keys, Support ui) {
            if (keys == Support.Missing && ui == Support.Missing) return "未识别组件";
            string conversion = keys == Support.Verified ? "已适配" : keys == Support.Candidate ? "自动识别" : "新格式未适配";
            return ui == Support.Verified || ui == Support.Candidate ? conversion : conversion + " · 界面未适配";
        }
    }
}
