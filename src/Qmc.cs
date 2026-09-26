// QMC2 algorithm adapted from QM Unlock (MIT). See THIRD-PARTY-NOTICES.md.
using System;
using System.IO;
using System.Text;
using System.Linq;

namespace QqmBetterDownload {
    public sealed class Qmc : IDisposable {
        readonly byte[] key, box;
        readonly uint hash;
        static readonly byte[] Simple = { 0x69, 0x56, 0x46, 0x38, 0x2b, 0x20, 0x15, 0x0b };
        public Qmc(string ekey) : this(Derive(ekey)) { }
        public Qmc(byte[] rawKey) {
            if (rawKey.Length < 16 || rawKey.Length > 4096) throw new InvalidDataException("歌曲密钥长度无效。");
            key = (byte[])rawKey.Clone(); int n = key.Length;
            if (n <= 300) return;
            box = new byte[n]; for (int i = 0; i < n; i++) box[i] = (byte)i;
            int j = 0; for (int i = 0; i < n; i++) { j = (j + box[i] + key[i]) % n; Swap(box, i, j); }
            uint h = 1;
            foreach (byte b in key) { if (b == 0) continue; uint next = unchecked(h * b); if (next == 0 || next <= h) break; h = next; }
            hash = h;
        }
        static void Swap(byte[] b, int i, int j) { byte x = b[i]; b[i] = b[j]; b[j] = x; }
        int Skip(long segment) { byte seed = key[segment % key.Length]; return seed == 0 ? 0 : (int)((long)(((double)hash / ((segment + 1) * seed)) * 100) % key.Length); }
        public void Transform(byte[] data, int count, long offset) {
            if (offset < 0 || count < 0 || count > data.Length) throw new ArgumentOutOfRangeException();
            if (box == null) {
                for (int i = 0; i < count; i++) {
                    long pos = offset + i; if (pos > 0x7fff) pos %= 0x7fff;
                    int index = (int)((pos * pos + 71214) % key.Length), shift = ((index & 7) + 4) % 8;
                    // Deliberately not a rotate: this is QQ Music's historical mapL.
                    data[i] ^= (byte)((key[index] << shift) | (key[index] >> shift));
                }
                return;
            }
            int done = 0;
            while (done < count && offset < 128) { data[done++] ^= key[Skip(offset++)]; }
            while (done < count) {
                int n = Math.Min(count - done, 5120 - (int)(offset % 5120));
                byte[] state = (byte[])box.Clone(); int j = 0, k = 0, skip = (int)(offset % 5120) + Skip(offset / 5120);
                for (int i = -skip; i < n; i++) {
                    j = (j + 1) % key.Length; k = (k + state[j]) % key.Length; Swap(state, j, k);
                    if (i >= 0) data[done + i] ^= state[(state[j] + state[k]) % key.Length];
                }
                done += n; offset += n;
            }
        }
        static byte[] Base64(string s) { s = s.Trim(); return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '=')); }
        public static byte[] Derive(string ekey) {
            byte[] decoded = Base64(ekey), prefix = Encoding.ASCII.GetBytes("QQMusic EncV2,Key:");
            if (decoded.Take(prefix.Length).SequenceEqual(prefix)) {
                decoded = Tea(decoded.Skip(prefix.Length).ToArray(), Encoding.ASCII.GetBytes("386ZJY!@#*$%^&)("));
                decoded = Tea(decoded, Encoding.ASCII.GetBytes("**#!(#$%&^a1cZ,T"));
                decoded = Base64(Encoding.ASCII.GetString(decoded));
            }
            if (decoded.Length < 16 || decoded.Length > 4096) throw new InvalidDataException("歌曲密钥格式无效。");
            byte[] tea = new byte[16]; for (int i = 0; i < 8; i++) { tea[2 * i] = Simple[i]; tea[2 * i + 1] = decoded[i]; }
            try { return decoded.Take(8).Concat(Tea(decoded.Skip(8).ToArray(), tea)).ToArray(); }
            catch (InvalidDataException) { return decoded; } // some clients store the already-unwrapped key
        }
        static uint Be(byte[] b, int p) { return (uint)b[p] << 24 | (uint)b[p + 1] << 16 | (uint)b[p + 2] << 8 | b[p + 3]; }
        static void Put(byte[] b, int p, uint v) { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }
        static void Block(byte[] b, uint[] k) {
            unchecked {
                uint a = Be(b, 0), c = Be(b, 4), sum = 0xe3779b90;
                for (int i = 0; i < 16; i++) { c -= ((a << 4) + k[2]) ^ (a + sum) ^ ((a >> 5) + k[3]); a -= ((c << 4) + k[0]) ^ (c + sum) ^ ((c >> 5) + k[1]); sum -= 0x9e3779b9; }
                Put(b, 0, a); Put(b, 4, c);
            }
        }
        public static byte[] Tea(byte[] data, byte[] key) {
            if (data.Length < 16 || data.Length % 8 != 0 || key.Length != 16) throw new InvalidDataException("TEA 长度无效。");
            uint[] k = { Be(key, 0), Be(key, 4), Be(key, 8), Be(key, 12) };
            byte[] dest = data.Take(8).ToArray(), previous = new byte[8], current = data.Take(8).ToArray();
            Block(dest, k); int pad = dest[0] & 7, length = data.Length - pad - 10;
            if (length < 0) throw new InvalidDataException("TEA 填充无效。");
            byte[] output = new byte[length]; int pos = 8, index = 1 + pad, written = -2;
            while (written < length + 7) {
                if (index == 8) {
                    if (pos + 8 > data.Length) throw new InvalidDataException("TEA 数据不完整。");
                    Buffer.BlockCopy(current, 0, previous, 0, 8); Buffer.BlockCopy(data, pos, current, 0, 8);
                    for (int j = 0; j < 8; j++) dest[j] ^= current[j]; Block(dest, k); pos += 8; index = 0;
                }
                byte value = (byte)(dest[index] ^ previous[index]); index++;
                if (written >= 0 && written < length) output[written] = value;
                else if (written >= length && value != 0) throw new InvalidDataException("TEA 校验无效。");
                written++;
            }
            return output;
        }
        public void Dispose() { Array.Clear(key, 0, key.Length); if (box != null) Array.Clear(box, 0, box.Length); }
    }
}
