using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections.Generic;

namespace BetterDownloadSetup {
    internal static class SelfTest {
        static int passed;
        static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
        internal static int Run() {
            string root = Path.Combine(Path.GetTempPath(), "BetterDownload-Setup-Test-" + Guid.NewGuid().ToString("N"));
            var assembly = Assembly.GetExecutingAssembly(); string id;
            using (var zip = assembly.GetManifestResourceStream("payload.zip")) using (var json = assembly.GetManifestResourceStream("payload.json")) id = Deployment.Stage(root, zip, json);
            var state = new Installation { current = id, versions = new List<string> { id } };
            Deployment.Write(Path.Combine(root, "installation.json"), state);
            string installed = Deployment.Select(root); Check(Deployment.Validate(installed).files.Count >= 3, "package validates");
            using (var zip = assembly.GetManifestResourceStream("payload.zip")) using (var json = assembly.GetManifestResourceStream("payload.json")) Check(Deployment.Stage(root, zip, json) == id, "reinstall is idempotent");
            foreach (string invalid in new[] { "../other", "..", "C:\\other", "good/evil", "file:stream" }) {
                bool refused = false; try { Deployment.Child(root, invalid); } catch { refused = true; } Check(refused, "path escape rejected");
            }
            string bad = Path.Combine(root, "versions", "corrupt"); Directory.CreateDirectory(bad);
            foreach (var file in Directory.GetFiles(installed)) File.Copy(file, Path.Combine(bad, Path.GetFileName(file)));
            File.AppendAllText(Path.Combine(bad, "BetterDownload.exe"), "corruption");
            state.previous = id; state.current = "corrupt"; Deployment.Write(Path.Combine(root, "installation.json"), state);
            Check(Deployment.Select(root) == installed && Deployment.State(root).current == id, "corrupt active version rolls back");
            byte[] raw; using (var stream = assembly.GetManifestResourceStream("payload.zip")) using (var memory = new MemoryStream()) { stream.CopyTo(memory); raw = memory.ToArray(); }
            bool malformed = false;
            try { using (var json = assembly.GetManifestResourceStream("payload.json")) Deployment.Stage(root, new MemoryStream(raw.Take(80).ToArray()), json); } catch (InvalidDataException) { malformed = true; }
            Check(malformed, "truncated package rejected");
            Check(Deployment.Select(root) == installed, "bad package does not change active version");
            // The same version can be repaired even when its previous directory
            // has been altered; no in-place overwrite or damaged backup reuse.
            File.AppendAllText(Path.Combine(installed, "BetterDownload.exe"), "corruption");
            string repaired;
            using (var zip = assembly.GetManifestResourceStream("payload.zip")) using (var json = assembly.GetManifestResourceStream("payload.json")) repaired = Deployment.Stage(root, zip, json);
            Check(repaired != id && Deployment.Validate(Deployment.VersionPath(root, repaired)).files.Count >= 3, "damaged version repaired into fresh directory");
            Check(File.ReadAllBytes(Path.Combine(installed, "BetterDownload.exe")).Length > new FileInfo(Path.Combine(Deployment.VersionPath(root, repaired), "BetterDownload.exe")).Length, "repair preserves changed files");
            installed = Deployment.VersionPath(root, repaired); id = repaired;
            File.WriteAllText(Path.Combine(root, "settings.json"), "preserve me");
            File.WriteAllText(Path.Combine(root, "receipts.json"), "preserve me");
            File.WriteAllText(Path.Combine(installed, "user-note.txt"), "preserve me");
            Deployment.RemoveOwnedVersion(root, id);
            Check(File.ReadAllText(Path.Combine(root, "settings.json")) == "preserve me" && File.ReadAllText(Path.Combine(root, "receipts.json")) == "preserve me", "uninstall preserves settings and receipts");
            Check(File.ReadAllText(Path.Combine(installed, "user-note.txt")) == "preserve me", "uninstall preserves unknown files");
            Check(!File.Exists(Path.Combine(installed, "BetterDownload.exe")), "owned binary removed");
            // Delete only this generated test tree, after resolving containment.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("test cleanup path invalid");
            Deployment.NoLinks(root); Directory.Delete(root, true);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup-tests.txt"), "PASS " + passed + " deployment assertions", Encoding.UTF8);
            Console.WriteLine("PASS " + passed + " deployment assertions"); return 0;
        }
    }
}
