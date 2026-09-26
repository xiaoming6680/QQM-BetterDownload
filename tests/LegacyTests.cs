using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading;
namespace QqmBetterDownload {
    internal static class LegacyTests {
        internal static void Run(Action<bool,string> check, string folder) {
            byte[] plain = File.ReadAllBytes(Path.Combine(folder, "tagged.flac"));
            string key = Convert.ToBase64String(Enumerable.Range(0, 257).Select(i => (byte)(i * 17 + 3)).ToArray());
            var root = Path.Combine(folder, "legacy"); Directory.CreateDirectory(root);
            foreach (bool qtag in new[] { false, true }) {
                byte[] encrypted = (byte[])plain.Clone(); using (var qmc = new Qmc(key)) qmc.Transform(encrypted, encrypted.Length, 0);
                byte[] footer = Encoding.ASCII.GetBytes(key + (qtag ? ",12345,2" : ""));
                byte[] size = BitConverter.GetBytes(footer.Length); if (qtag) Array.Reverse(size);
                string path = Path.Combine(root, qtag ? "qtag.mflac" : "v1.mflac");
                byte[] original = encrypted.Concat(footer).Concat(size).Concat(qtag ? Encoding.ASCII.GetBytes("QTag") : new byte[0]).ToArray(); File.WriteAllBytes(path, original);
                var converter = new Converter(Path.Combine(folder, "legacy-receipts.json"));
                var result = converter.ConvertFile(root, path, new Dictionary<string,string>(), null, CancellationToken.None);
                check(File.ReadAllBytes(result.Output).SequenceEqual(plain), "legacy output validated and byte-identical " + qtag);
                check(File.ReadAllBytes(path).SequenceEqual(original), "legacy source preserved " + qtag);
            }
            using (var keys = new DownloadKeys(Path.Combine(folder, "no-client"))) check(!keys.Available && keys.Read().Count == 0, "legacy queue works without an installed local interface");
            byte[] broken = Encoding.ASCII.GetBytes("short-and-invalid-data-without-a-footer");
            bool rejected = false; try { AudioFile.Footer(new MemoryStream(broken)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "malformed footer rejected");
        }
    }
}
