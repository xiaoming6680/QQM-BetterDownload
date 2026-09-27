using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace QqmBetterDownload {
    // Cross-version identification and the isolated key interface. The stand-in
    // DLLs are original and unsigned (tests/FakeKeys.c); no client files are used
    // except by the opt-in --real-client check, which prints no keys or names.
    internal static class CompatibilityTests {
        static string Exe { get { using (var self = Process.GetCurrentProcess()) return self.MainModule.FileName; } }
        static int SecurityEntry(byte[] data) {
            int pe = BitConverter.ToInt32(data, 0x3c), optional = pe + 24;
            return optional + (BitConverter.ToUInt16(data, optional) == 0x10b ? 96 : 112) + 32;
        }
        static int FirstSection(byte[] data) { int pe = BitConverter.ToInt32(data, 0x3c); return BitConverter.ToInt32(data, pe + 24 + BitConverter.ToUInt16(data, pe + 20) + 20); }
        static string FileHash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }
        static string Code(byte[] data) { return PeImage.Read(new MemoryStream(data)).CodeHash; }
        static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        // Same code with another certificate blob and checksum, as re-signing does.
        internal static byte[] Resign(byte[] data, byte[] certificate) {
            int entry = SecurityEntry(data), offset = BitConverter.ToInt32(data, entry), size = BitConverter.ToInt32(data, entry + 4), end = size == 0 ? data.Length : offset;
            byte[] result = new byte[end + (certificate == null ? 0 : certificate.Length)];
            Buffer.BlockCopy(data, 0, result, 0, end);
            if (certificate != null) Buffer.BlockCopy(certificate, 0, result, end, certificate.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(certificate == null ? 0 : end), 0, result, entry, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(certificate == null ? 0 : certificate.Length), 0, result, entry + 4, 4);
            result[BitConverter.ToInt32(data, 0x3c) + 24 + 64] ^= 0x5a;
            return result;
        }
        internal static void Run(Action<bool,string> check, string folder) {
            string dir = AppDomain.CurrentDomain.BaseDirectory, work = Path.Combine(folder, "compat"), exe = Exe; Directory.CreateDirectory(work);
            byte[] signed = File.ReadAllBytes(Path.Combine(dir, "WebView2Loader.dll")), stripped = Resign(signed, null), resigned = Resign(signed, Enumerable.Repeat((byte)0x33, 2048).ToArray());
            string code = Code(signed);
            check(Code(stripped) == code && Code(resigned) == code, "re-signing must not change a component's code identity");
            check(FileHash(stripped) != FileHash(signed) && FileHash(resigned) != FileHash(signed), "re-signing changes the whole-file hash");
            byte[] tampered = (byte[])signed.Clone(); tampered[FirstSection(signed) + 64] ^= 1;
            check(Code(tampered) != code, "any code change must change the identity");
            byte[] broken = (byte[])signed.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(signed.Length - 8), 0, broken, SecurityEntry(signed), 4); Buffer.BlockCopy(BitConverter.GetBytes(4096), 0, broken, SecurityEntry(signed) + 4, 4);
            check(Throws<InvalidDataException>(() => Code(broken)) && Throws<InvalidDataException>(() => Code(Encoding.ASCII.GetBytes(new string('x', 4096)))) && Throws<InvalidDataException>(() => Code(signed.Take(300).ToArray())), "malformed components rejected");
            string copy = Path.Combine(work, "signed.dll"), strippedPath = Path.Combine(work, "stripped.dll"), tamperedPath = Path.Combine(work, "tampered.dll");
            File.WriteAllBytes(copy, signed); File.WriteAllBytes(strippedPath, stripped); File.WriteAllBytes(tamperedPath, tampered);
            check(Authenticode.Signer(copy) == "Microsoft Corporation" && !Authenticode.IsTencent(copy), "a valid signature names its signer offline");
            check(Authenticode.Signer(strippedPath) == null && Authenticode.Signer(tamperedPath) == null && Authenticode.Signer(Path.Combine(dir, "FakeKeys.dll")) == null, "unsigned or modified components have no signer");
            var fake = PeImage.Read(Path.Combine(dir, "FakeKeys.dll"));
            check(fake.HasExport(12) && !fake.HasExport(11) && !fake.HasExport(13), "ordinal-only interface export located");

            check(KeyInterface.Check("0123456789abcdef0123456789ABCDEF", "Store-1_a.cah", true).Store == "Store-1_a.cah", "verified output shape accepted");
            foreach (var bad in new[] { new[] { "0123456789abcdef0123456789ABCDE", "a.cah" }, new[] { "0123456789abcdef0123456789ABCDEG", "a.cah" }, new[] { "0123456789abcdef0123456789ABCDEF", "..\\a.cah" }, new[] { "0123456789abcdef0123456789ABCDEF", ".." }, new[] { "0123456789abcdef0123456789ABCDEF", "存储.cah" }, new[] { "0123456789abcdef0123456789ABCDEF", "" } })
                check(Throws<InvalidDataException>(() => KeyInterface.Check(bad[0], bad[1], false)), "malformed interface output rejected: " + bad[0] + "/" + bad[1]);
            var result = KeyInterface.Isolated(Path.Combine(dir, "FakeKeys.dll"), exe, "--derive-store-test", 15000);
            check(result.Store == "FakeStore.cah" && result.Key == "0123456789abcdef0123456789ABCDEF" && !result.Verified, "an unverified interface runs in a child and reports back");
            check(Throws<NotSupportedException>(() => KeyInterface.Isolated(Path.Combine(dir, "FakeKeys.dll"), exe, KeyInterface.ChildSwitch, 15000)), "the product's child refuses a component without a Tencent signature");
            check(Throws<NotSupportedException>(() => KeyInterface.Isolated(Path.Combine(dir, "FakeKeysInvalid.dll"), exe, "--derive-store-test", 15000)), "malformed child output is unsupported");
            check(Throws<NotSupportedException>(() => KeyInterface.Isolated(Path.Combine(dir, "FakeKeysCrash.dll"), exe, "--derive-store-test", 15000)), "a crashing interface only ends the child");
            check(Throws<NotSupportedException>(() => KeyInterface.Isolated(Path.Combine(dir, "BetterDownloadBridge.dll"), exe, "--derive-store-test", 15000)), "a component without the interface export is never called");
            string hang = Path.Combine(work, "hang.dll"); File.Copy(Path.Combine(dir, "FakeKeysHang.dll"), hang);
            var clock = Stopwatch.StartNew();
            check(Throws<IOException>(() => KeyInterface.Isolated(hang, exe, "--derive-store-test", 1500)) && clock.ElapsedMilliseconds < 10000, "a stalled interface times out");
            bool released = false;
            for (int i = 0; i < 50 && !released; i++) { try { File.Delete(hang); released = true; } catch (IOException) { Thread.Sleep(100); } catch (UnauthorizedAccessException) { Thread.Sleep(100); } }
            check(released, "the stalled child is terminated");

            string client = Path.Combine(work, "client"), store = Path.Combine(work, "store"), ekey = Convert.ToBase64String(Enumerable.Range(0, 64).Select(n => (byte)(n * 7 + 1)).ToArray());
            Directory.CreateDirectory(client); Directory.CreateDirectory(store); File.Copy(Path.Combine(dir, "FakeKeys.dll"), Path.Combine(client, "CommonFunction.dll"));
            KeyInterface.HelperSwitch = "--derive-store-test";
            try {
                WriteStore(Path.Combine(store, "FakeStore.cah"), "0123456789abcdef", new[] { Pair("fixture.mflac", ekey), Pair("settings.flag", "not a download"), Pair("removed.mgg", ekey), Pair("removed.mgg", null) });
                using (var keys = new DownloadKeys(client, store)) {
                    var map = keys.Read();
                    check(keys.Available && !keys.Verified && keys.Notice.Length > 0, "an unverified interface is enabled and disclosed");
                    check(map.Count == 1 && map["fixture.mflac"] == ekey, "an unverified interface reads the download keys");
                }
                WriteStore(Path.Combine(store, "FakeStore.cah"), "fedcba9876543210", new[] { Pair("fixture.mflac", ekey) });
                using (var keys = new LocalKeys(client, store)) check(Throws<NotSupportedException>(() => keys.Read()), "a wrong key from an unverified interface is not trusted");
            } finally { KeyInterface.HelperSwitch = KeyInterface.ChildSwitch; }
            string reason, empty = Path.Combine(work, "empty-client"), unsigned = Path.Combine(work, "unsigned-client");
            Directory.CreateDirectory(empty); Directory.CreateDirectory(unsigned);
            using (var keys = new DownloadKeys(empty, store)) check(!keys.Available && keys.Notice.Length > 0 && keys.Read().Count == 0, "a missing interface keeps old formats working and says why");
            check(ClientCompatibility.Keys(empty, out reason) == Support.Missing && ClientCompatibility.Interface(empty, out reason) == Support.Missing, "missing components detected");
            foreach (string name in new[] { "CommonFunction.dll", "GF.dll", "Common.dll", "QQMusic_GFWrapper.dll" }) File.Copy(Path.Combine(dir, "FakeKeys.dll"), Path.Combine(unsigned, name));
            check(ClientCompatibility.Keys(unsigned, out reason) == Support.Unsupported && reason.Contains("签名"), "an unsigned key interface is never called");
            check(ClientCompatibility.Interface(unsigned, out reason) == Support.Unsupported && reason.Length > 0, "unknown GF code disables the private UI");
            check(ClientCompatibility.Label(Support.Verified, Support.Verified) == "已适配" && ClientCompatibility.Label(Support.Verified, Support.Candidate) == "已适配" &&
                ClientCompatibility.Label(Support.Candidate, Support.Verified) == "自动识别" && ClientCompatibility.Label(Support.Verified, Support.Unsupported) == "已适配 · 界面未适配" &&
                ClientCompatibility.Label(Support.Unsupported, Support.Missing) == "新格式未适配 · 界面未适配" && ClientCompatibility.Label(Support.Missing, Support.Missing) == "未识别组件", "installer labels follow both layers");
        }
        static KeyValuePair<string,string> Pair(string name, string text) { return new KeyValuePair<string,string>(name, text); }
        static void Varint(Stream stream, int value) { uint v = (uint)value; while (v >= 128) { stream.WriteByte((byte)(v | 128)); v >>= 7; } stream.WriteByte((byte)v); }
        // A synthetic MMKV v3 store: AES-128-CFB records, CRC over the ciphertext.
        static void WriteStore(string path, string key, IEnumerable<KeyValuePair<string,string>> entries) {
            var plain = new MemoryStream(); Varint(plain, 0);
            foreach (var entry in entries) {
                byte[] name = Encoding.UTF8.GetBytes(entry.Key); Varint(plain, name.Length); plain.Write(name, 0, name.Length);
                if (entry.Value == null) { Varint(plain, 0); continue; }
                byte[] text = Encoding.UTF8.GetBytes(entry.Value); var value = new MemoryStream(); Varint(value, text.Length); value.Write(text, 0, text.Length);
                Varint(plain, (int)value.Length); plain.Write(value.ToArray(), 0, (int)value.Length);
            }
            byte[] data = plain.ToArray(), cipher = new byte[data.Length], iv = Enumerable.Range(0, 16).Select(n => (byte)(n * 13 + 5)).ToArray(), feedback = (byte[])iv.Clone(), block = new byte[16];
            using (var aes = Aes.Create()) {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = Encoding.ASCII.GetBytes(key);
                using (var encryptor = aes.CreateEncryptor()) for (int p = 0; p < data.Length; p += 16) {
                    encryptor.TransformBlock(feedback, 0, 16, block, 0);
                    for (int j = 0; j < Math.Min(16, data.Length - p); j++) { cipher[p + j] = (byte)(data[p + j] ^ block[j]); feedback[j] = cipher[p + j]; }
                }
            }
            byte[] file = new byte[4096], meta = new byte[4096];
            Buffer.BlockCopy(BitConverter.GetBytes(cipher.Length), 0, file, 0, 4); Buffer.BlockCopy(cipher, 0, file, 4, cipher.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(Binary.Crc32(file, 4, cipher.Length)), 0, meta, 0, 4); Buffer.BlockCopy(BitConverter.GetBytes(3), 0, meta, 4, 4);
            Buffer.BlockCopy(iv, 0, meta, 12, 16); Buffer.BlockCopy(BitConverter.GetBytes(cipher.Length), 0, meta, 28, 4);
            File.WriteAllBytes(path, file); File.WriteAllBytes(path + ".crc", meta);
        }
        static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
        static void Until(Func<bool> ready, int ms, Func<string> step) { var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.ElapsedMilliseconds > ms) throw new Exception("Fallback window timed out: " + step()); Pump(20); } }
        static string Script(FallbackWindow window, string script) { var work = window.Evaluate(script); Until(() => work.IsCompleted, 10000, () => "script"); return work.GetAwaiter().GetResult(); }
        // The window QQ builds without an adapted UI use: the same loopback page only.
        internal static void Fallback(Action<bool,string> check, string folder) {
            string html;
            using (var input = typeof(Program).Assembly.GetManifestResourceStream("settings.html")) using (var reader = new StreamReader(input)) html = reader.ReadToEnd();
            int ready = 0;
            using (var server = new LocalPageServer(html, delegate(string name, string value) { if (name == "ready") Interlocked.Increment(ref ready); }))
            using (var icon = BrandIcon.Load())
            using (var window = new FallbackWindow(server.Url, Path.Combine(folder, "fallback-webview"), icon, 1)) {
                window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-10000, -10000); window.ShowInTaskbar = false;
                server.Publish("{\"version\":\"test\",\"visible\":true,\"openSequence\":1,\"enabled\":true,\"notify\":\"all\",\"style\":\"standard\",\"stay\":4000,\"state\":\"watching\",\"notice\":\"界面未适配说明\",\"history\":[]}");
                window.Present();
                // The page's script reports "ready" before navigation completes.
                Until(() => (Volatile.Read(ref ready) > 0 && window.PageLoaded) || window.Problem.Length > 0, 20000, () => "page load; ready=" + Volatile.Read(ref ready) + " loaded=" + window.PageLoaded + " problem=" + window.Problem);
                check(Volatile.Read(ref ready) > 0 && window.PageLoaded, "fallback window loads the loopback settings page " + window.Problem);
                Until(() => Script(window, "document.querySelector('[data-note]').textContent==='界面未适配说明'") == "true", 10000, () => "notice: " + Script(window, "JSON.stringify([document.readyState, document.body.dataset.ready, (document.querySelector('[data-note]')||{}).textContent, location.href])"));
                check(true, "fallback page shows which layer is not adapted");
                Script(window, "location.href='https://example.invalid/';1"); Pump(600);
                check(Script(window, "location.href") == "\"" + server.Url + "\"", "fallback window refuses other pages");
                window.Close(); Pump(100);
                check(!window.IsDisposed && !window.Visible, "closing the fallback window only hides it");
                window.Shutdown(); Pump(100);
                check(window.IsDisposed, "worker shutdown disposes the fallback window");
            }
        }
        // Opt-in check against an installed client; prints no keys or store names.
        internal static void RealClient(Action<bool,string> check, string client) {
            string reason, dll = Path.Combine(client, "CommonFunction.dll");
            check(ClientCompatibility.Keys(client, out reason) == Support.Verified, "real key interface identified by code " + reason);
            check(ClientCompatibility.Interface(client, out reason) == Support.Verified, "real GF components identified by code " + reason);
            byte[] original = File.ReadAllBytes(dll);
            check(ClientCompatibility.KnownKeyInterface(Code(original)) && FileHash(original) != Code(original), "the real component's file hash differs from its code identity");
            check(ClientCompatibility.KnownKeyInterface(Code(Resign(original, null))), "the real component keeps its identity without its signature");
            string temp = Path.Combine(Path.GetTempPath(), "BetterDownload-Compat-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try {
                byte[] changed = (byte[])original.Clone(); changed[BitConverter.ToInt32(original, 0x3c) + 8] ^= 1; // PE time stamp: another build
                File.WriteAllBytes(Path.Combine(temp, "CommonFunction.dll"), changed);
                check(ClientCompatibility.Keys(temp, out reason) == Support.Unsupported && reason.Contains("签名"), "a modified component loses its Tencent signature and is never called");
            } finally { Directory.Delete(temp, true); }
            var here = KeyInterface.Here(dll, true); var child = KeyInterface.Isolated(dll, Exe, KeyInterface.ChildSwitch, 15000);
            check(here.Verified && here.Store == child.Store && here.Key == child.Key, "in-process and isolated derivation agree");
            check(Authenticode.IsTencent(Path.Combine(client, "QQMusic_GFWrapper.dll")), "the real wrapper carries a valid Tencent signature");
            using (var keys = new LocalKeys(client)) { int count = keys.Read().Count; check(keys.Verified, "real store readable"); Console.WriteLine("Real store: " + count + " download keys (names and keys not printed)."); }
        }
    }
}
