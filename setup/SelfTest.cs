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
            Deployment.RequireSharedLocation(root);
            Check(Directory.Exists(root), "installation directory is visible at its declared path");
            Deployment.RequireSameLocation(root, "\\\\?\\" + root.ToUpperInvariant() + "\\");
            Check(true, "physical path comparison accepts Windows prefix and casing");
            bool redirected = false;
            try { Deployment.RequireSameLocation(root, Path.Combine(Path.GetTempPath(), "Packages", "PrivateCache", Path.GetFileName(root))); }
            catch (IOException) { redirected = true; }
            Check(redirected, "private redirected installation rejected before activation");
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
            // Only when the user ticks it: BetterDownload's own data goes, other files stay.
            foreach (string name in Deployment.DataFiles) File.WriteAllText(Path.Combine(root, name), "data");
            foreach (string name in Deployment.DataFolders) { Directory.CreateDirectory(Path.Combine(root, name, "nested")); File.WriteAllText(Path.Combine(root, name, "nested", "data.bin"), "data"); }
            File.WriteAllText(Path.Combine(root, "user-note.txt"), "preserve me");
            Check(Deployment.RemoveData(root), "chosen data removal completes");
            Check(Deployment.DataFiles.All(n => !File.Exists(Path.Combine(root, n))) && Deployment.DataFolders.All(n => !Directory.Exists(Path.Combine(root, n))), "settings, receipts, status and caches removed");
            Check(File.ReadAllText(Path.Combine(root, "user-note.txt")) == "preserve me", "data removal keeps unknown files");
            string outside = Path.Combine(Path.GetTempPath(), "BetterDownload-Setup-Outside-" + Guid.NewGuid().ToString("N")), junction = Path.Combine(root, "webview");
            Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })) { mklink.StandardOutput.ReadToEnd(); mklink.WaitForExit(); }
            Check(Directory.Exists(junction) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "test junction created");
            // Left in place and never followed, without failing the rest: a pending
            // cleanup must finish instead of retrying at every sign-in.
            Check(Deployment.RemoveData(root) && Directory.Exists(junction) && File.ReadAllText(Path.Combine(outside, "keep.txt")) == "keep", "a link in the data folder is left alone, never followed");
            Directory.Delete(junction); Directory.Delete(outside, true);
            string empty = Path.Combine(Path.GetTempPath(), "BetterDownload-Setup-Empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(empty, "versions")); Deployment.RemoveEmpty(empty);
            Check(!Directory.Exists(empty), "emptied installation folder removed");
            Deployment.RemoveEmpty(root); Check(File.Exists(Path.Combine(root, "user-note.txt")), "folder with other files kept");
            // Upgrades remove only the settings shortcut earlier versions made.
            string launcher = Path.Combine(root, "BetterDownload-Setup.exe"), other = Path.Combine(root, "Other.exe");
            File.WriteAllText(launcher, ""); File.WriteAllText(other, "");
            Func<string, string, string, string> link = delegate(string name, string target, string arguments) {
                string path = Path.Combine(root, name); dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath = target; shortcut.Arguments = arguments; shortcut.Save(); return path;
            };
            string legacy = link("legacy.lnk", launcher, "--settings"), plain = link("plain.lnk", launcher, ""), foreign = link("foreign.lnk", other, "--settings");
            Check(Deployment.RemoveSettingsShortcut(legacy, launcher) && !File.Exists(legacy), "legacy settings shortcut removed");
            Check(!Deployment.RemoveSettingsShortcut(plain, launcher) && !Deployment.RemoveSettingsShortcut(foreign, launcher) && File.Exists(plain) && File.Exists(foreign), "other shortcuts kept");
            // Delete only this generated test tree, after resolving containment.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("test cleanup path invalid");
            Deployment.NoLinks(root); Directory.Delete(root, true);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup-tests.txt"), "PASS " + passed + " deployment assertions", Encoding.UTF8);
            Console.WriteLine("PASS " + passed + " deployment assertions"); return 0;
        }
    }
}
