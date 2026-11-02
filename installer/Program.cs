using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

internal static class Program
{
    const string Product = "NVIDIA SHIELD P2570 for Windows";
    const string Folder = "NVIDIA-SHIELD-P2570-Windows";
    const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NVIDIA-SHIELD-P2570-Windows";
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
    static string Tray => Path.Combine(Root, "bin", "tray", "ShieldController.exe");
    static string checkpoint = "startup";
    static string? packageFile;

    static void Stage(string value, string? relative = null) { checkpoint = value; packageFile = relative; }
    static void ReportError(Exception error)
    {
        // Installer operations never load pairing contents. Preserve the first
        // failure even if rollback or staging cleanup encounters another error.
        try { Console.Error.WriteLine(JsonSerializer.Serialize(new { phase = "installer-error", stage = checkpoint, package_file = packageFile, type = error.GetType().Name, hresult = $"0x{error.HResult:X8}", message = error.Message })); }
        catch { /* Diagnostic output must not replace the installation error. */ }
    }

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.SequenceEqual(new[] { "--selftest" }) || args.Length == 3 && args[0] == "--selftest" && args[1] == "--fixture-root")
        {
            Stage("selftest");
            try { return SelfTest(args.Length == 3 ? args[2] : null); }
            catch (Exception ex) { ReportError(ex); return 1; }
        }
        if (args.Any(a => a != "--silent" && a != "--no-launch")) return 2;
        bool silent = args.Contains("--silent"), launch = !args.Contains("--no-launch");
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || !Environment.Is64BitOperatingSystem)
                throw new InvalidOperationException("Windows 11 version 24H2 or later (64-bit) is required.");
            if (silent) { Install(); if (launch) Launch(); return 0; }
            using var form = new Form { Text = Product + " — Setup", ClientSize = new Size(540, 310), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, Font = new Font("Segoe UI", 10), Icon = SystemIcons.Application };
            var title = new Label { Text = "Use your SHIELD controller wirelessly", Location = new Point(24,24), Size = new Size(492,36), Font = new Font("Segoe UI",15,FontStyle.Bold) };
            var body = new Label { Text = "Install the tray app, then follow its pairing guide.\n\nRequires a supported Wi-Fi adapter. Npcap and ViGEmBus are checked by the app; missing drivers open their official setup prompts.\n\nInstalls for your Windows account. No subscription or account needed.", Location = new Point(24,72), Size = new Size(492,148) };
            var start = new CheckBox { Text = "Open the app after installation", Checked = launch, Location = new Point(24,232), Size = new Size(300,28) };
            var install = new Button { Text = "Install", Location = new Point(390,260), Size = new Size(126,34) };
            bool installing = false;
            form.FormClosing += (_, e) => { if (installing) e.Cancel = true; };
            form.Controls.AddRange(new Control[] { title, body, start, install });
            install.Click += async (_, _) =>
            {
                installing = true; install.Enabled = false; start.Enabled = false; body.Text = "Installing…";
                try { await Task.Run(Install); body.Text = "Installed. Pairing and connection controls are available from the tray icon."; if (start.Checked) Launch(); installing = false; form.DialogResult = DialogResult.OK; form.Close(); }
                catch (Exception ex) { ReportError(ex); installing = false; body.Text = ex.Message; install.Enabled = true; start.Enabled = true; }
            };
            form.ShowDialog();
            return form.DialogResult == DialogResult.OK ? 0 : 1;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            if (!silent) MessageBox.Show(ex.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void Launch() { Stage("launch-tray"); Process.Start(new ProcessStartInfo(Tray, "--show") { UseShellExecute = true }); }

    // Only package payload files may be written. Configuration and pairing data
    // are deliberately outside this namespace and are never part of a release.
    internal static string Relative(string input)
    {
        if (input.Contains('\\') || input.Contains(':') || input.StartsWith('/') || input.EndsWith('/')) throw new InvalidDataException("Invalid package path.");
        var pieces = input.Split('/');
        if (pieces.Any(p => p is "" or "." or ".." || p.TrimEnd(' ', '.') != p)) throw new InvalidDataException("Invalid package path.");
        if (pieces.Any(p => p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException("Reserved package filename.");
        if (!(input.StartsWith("bin/",StringComparison.Ordinal) || input.StartsWith("licenses/",StringComparison.Ordinal) || input is "package.json" or "README.md" or "LICENSE" or "THIRD-PARTY.md")) throw new InvalidDataException("Package contains an unexpected file.");
        return input.Replace('/', Path.DirectorySeparatorChar);
    }

    static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installation path contains a link. Choose a normal local Windows profile folder.");
    }

    static void Install()
    {
        Stage("installer-lock");
        using var mutex = new Mutex(false, @"Local\NVIDIA-SHIELD-P2570-Installer");
        bool held;
        try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Another installation is running.");
        try
        {
            Stage("installation-root");
            NoLinks(Root);
            Directory.CreateDirectory(Root);
            Stage("private-root-acl");
            MakePrivateRoot();
            // Stop only this installed app, and wait for its normal AP cleanup.
            if (File.Exists(Tray))
            {
                Stage("stop-installed-tray");
                using var stop = Process.Start(new ProcessStartInfo(Tray, "--exit") { UseShellExecute = false, CreateNoWindow = true });
                stop?.WaitForExit(20000);
                Stage("wait-installed-processes");
                var until = DateTime.UtcNow.AddSeconds(30);
                while (OwnedProcessesRunning())
                {
                    if (DateTime.UtcNow > until) throw new IOException("The controller app is still shutting down. Disconnect and exit it, then run Setup again.");
                    Thread.Sleep(200);
                }
            }
            Stage("recovery-preflight");
            if (File.Exists(Path.Combine(Root,"recovery.json")) || File.Exists(Path.Combine(Root,"setup","active.json"))) throw new IOException("The previous controller session needs recovery. Open the installed app and use Recover before reinstalling.");
            Stage("open-payload");
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("This installer does not contain a release payload.");
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            Stage("payload-path-validation");
            var (entries, paths) = ValidatePayloadPaths(zip);
            string stage = Path.Combine(Root,".install-" + Guid.NewGuid().ToString("N"));
            Stage("create-staging-directory");
            Directory.CreateDirectory(stage);
            var installed = new List<string>();
            var oldFiles = new List<string>();
            string backup = Path.Combine(stage,".backup");
            bool preserveBackup = false;
            try
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    Stage("extract-payload-file", paths[i]);
                    if ((entries[i].ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Package links are not allowed.");
                    string output = Path.Combine(stage, paths[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    entries[i].ExtractToFile(output);
                }
                Stage("verify-staged-manifest");
                ValidateStage(stage, paths);
                foreach (string relative in paths)
                {
                    Stage("prepare-installed-file", relative);
                    string target = Path.Combine(Root,relative); NoLinks(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                    {
                        Stage("backup-installed-file", relative);
                        string old = Path.Combine(backup,relative); Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                        File.Move(target,old); oldFiles.Add(relative);
                    }
                    Stage("publish-installed-file", relative);
                    File.Move(Path.Combine(stage,relative),target); installed.Add(relative);
                }
                Stage("create-start-menu-shortcut");
                CreateShortcut();
                Stage("register-uninstaller");
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue("DisplayName", Product); key.SetValue("DisplayVersion", "0.1.0");
                key.SetValue("Publisher", "egokick and contributors"); key.SetValue("InstallLocation", Root);
                key.SetValue("DisplayIcon", Tray); key.SetValue("URLInfoAbout", "https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows");
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
                key.SetValue("UninstallString", $"\"{powershell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Path.Combine(Root,"bin","Uninstall.ps1")}\"");
                key.SetValue("NoModify",1); key.SetValue("NoRepair",1);
                Stage("write-installed-file-list");
                File.WriteAllText(Path.Combine(Root,"installed-files.json"),JsonSerializer.Serialize(paths));
            }
            catch (Exception ex)
            {
                ReportError(ex);
                string failedCheckpoint = checkpoint; string? failedFile = packageFile;
                preserveBackup = !RollbackFiles(Root, backup, installed, oldFiles);
                Stage(failedCheckpoint, failedFile);
                if (preserveBackup) throw new IOException("Installation failed and automatic rollback could not finish. Recovery files were kept at: " + stage, ex);
                throw;
            }
            finally
            {
                // Stage is a freshly generated child of the fixed installation root.
                string priorCheckpoint = checkpoint; string? priorFile = packageFile;
                Stage("remove-staging-directory");
                CleanupStage(stage, preserveBackup);
                Stage(priorCheckpoint, priorFile);
            }
        }
        finally { mutex.ReleaseMutex(); }
    }

    static bool RollbackFiles(string root, string backup, IEnumerable<string> installed, IEnumerable<string> oldFiles)
    {
        bool restored = true;
        foreach (string relative in installed.Reverse())
        {
            Stage("rollback-new-file", relative);
            try { File.Delete(Path.Combine(root,relative)); }
            catch (Exception ex) { restored = false; ReportError(ex); }
        }
        foreach (string relative in oldFiles)
        {
            Stage("restore-old-file", relative);
            try { File.Move(Path.Combine(backup,relative),Path.Combine(root,relative)); }
            catch (Exception ex) { restored = false; ReportError(ex); }
        }
        return restored;
    }

    static void CleanupStage(string stage, bool preserveBackup)
    {
        if (preserveBackup) return;
        NoLinks(stage); Directory.Delete(stage,true);
    }

    static (ZipArchiveEntry[] Entries, string[] Paths) ValidatePayloadPaths(ZipArchive zip)
    {
        var entries = zip.Entries.ToArray();
        var paths = entries.Select(e => Relative(e.FullName)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length || !paths.Contains(Path.Combine("bin","tray","ShieldController.exe"))) throw new InvalidDataException("Invalid or incomplete application package.");
        if (entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Package is unexpectedly large.");
        return (entries, paths);
    }

    static void MakePrivateRoot()
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows account identity is unavailable.");
        var directory = new DirectoryInfo(Root);
        if (!sid.Equals(directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)))) throw new IOException("The installation folder belongs to another Windows account.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        directory.SetAccessControl(acl);
        var read = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var rules = read.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!sid.Equals(read.GetOwner(typeof(SecurityIdentifier))) || rules.Length == 0 || rules.Any(r => !sid.Equals(r.IdentityReference) || r.AccessControlType != AccessControlType.Allow)) throw new IOException("Private installation access could not be verified.");
    }

    static void ValidateStage(string stage, string[] paths)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(stage,"package.json")));
        ValidateManifest(document.RootElement, paths, name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(stage,Relative(name))))));
    }

    static void ValidateManifest(JsonElement manifest, string[] paths, Func<string,string> readHash)
    {
        if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("product").GetString() != Folder) throw new InvalidDataException("Package manifest identity is invalid.");
        var files = manifest.GetProperty("files").EnumerateObject().ToArray();
        if (files.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new InvalidDataException("Duplicate package hashes.");
        var actual = paths.Where(p => p != "package.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(files.Select(p => Relative(p.Name)))) throw new InvalidDataException("Package manifest does not match its files.");
        foreach (var file in files)
        {
            string hash = file.Value.GetString() ?? "";
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !readHash(file.Name).Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A package file failed its integrity check.");
        }
        foreach (string required in new[] { "bin/tray/ShieldController.exe", "bin/owner/ShieldControllerOwner.exe", "bin/owner/ShieldControllerOwner.dll", "bin/data-host/shield-controller-datahost.exe", "bin/pairing/wcn-pair-worker.exe", "bin/Uninstall.ps1", "bin/Install-Dependencies.ps1" })
            if (!actual.Contains(Relative(required))) throw new InvalidDataException("A required application component is missing.");
    }

    static bool OwnedProcessesRunning()
    {
        foreach (string name in new[] { "ShieldController", "ShieldControllerOwner", "shield-controller-datahost", "wcn-pair-worker" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
        {
            try { if (process.MainModule?.FileName?.StartsWith(Path.Combine(Root,"bin") + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) == true) return true; }
            catch (System.ComponentModel.Win32Exception) { throw new IOException("Cannot verify that the existing controller app has exited."); }
        }
        return false;
    }

    static void CreateShortcut()
    {
        string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),Product + ".lnk");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try { dynamic shortcut = shell.CreateShortcut(link); try { shortcut.TargetPath = Tray; shortcut.Arguments = "--show"; shortcut.WorkingDirectory = Path.GetDirectoryName(Tray); shortcut.Description = "Pair and connect your NVIDIA SHIELD P2570 controller"; shortcut.Save(); } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); } }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    static int SelfTest(string? fixtureRoot = null)
    {
        int checks = 0;
        foreach (string value in new[] { "bin/tray/ShieldController.exe", "licenses/GPL-2.0.txt", "package.json" }) { Relative(value); checks++; }
        foreach (string value in new[] { "../secret", "/bin/a", "bin/../../outside", "bin/a:stream", "bin\\a", "config.json", "bin/a.", "bin//a", "bin/./a", "bin/CON.txt", "bin/a?.dll" })
        { bool failed=false; try { Relative(value); } catch (InvalidDataException) { failed=true; } if (!failed) return 1; checks++; }
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("Embedded payload missing."))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var (_, paths) = ValidatePayloadPaths(zip);
            using var manifestStream = zip.GetEntry("package.json")!.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            ValidateManifest(manifest.RootElement, paths, name => { using var stream = zip.GetEntry(name)!.Open(); return Convert.ToHexString(SHA256.HashData(stream)); });
            checks++;
        }
        if (fixtureRoot is not null)
        {
            string fixture = Path.GetFullPath(fixtureRoot);
            if (!Path.IsPathFullyQualified(fixtureRoot) || !fixture.Split(Path.DirectorySeparatorChar).Contains("debug",StringComparer.OrdinalIgnoreCase) || Directory.Exists(fixture) || File.Exists(fixture)) throw new InvalidDataException("Self-test fixture must be a new absolute path under debug.");
            NoLinks(fixture); Directory.CreateDirectory(fixture);
            string root = Path.Combine(fixture,"installed"), stage = Path.Combine(fixture,"stage"), backup = Path.Combine(stage,".backup");
            Directory.CreateDirectory(root); Directory.CreateDirectory(backup);
            Directory.CreateDirectory(Path.Combine(root,"blocked.txt"));
            File.WriteAllText(Path.Combine(backup,"blocked.txt"),"original blocked file");
            File.WriteAllText(Path.Combine(backup,"restored.txt"),"original restored file");
            var previousError = Console.Error; using var diagnostic = new StringWriter();
            bool restored;
            try { Console.SetError(diagnostic); restored = RollbackFiles(root,backup,[],["blocked.txt","restored.txt"]); }
            finally { Console.SetError(previousError); }
            CleanupStage(stage,!restored);
            if (restored || !Directory.Exists(stage) || File.ReadAllText(Path.Combine(backup,"blocked.txt")) != "original blocked file" || File.ReadAllText(Path.Combine(root,"restored.txt")) != "original restored file" || !diagnostic.ToString().Contains("restore-old-file")) throw new InvalidDataException("Rollback did not preserve the failed backup and restore the remaining file.");
            checks++;
            string clean = Path.Combine(fixture,"successful-stage"), cleanBackup = Path.Combine(clean,".backup"); Directory.CreateDirectory(cleanBackup);
            File.WriteAllText(Path.Combine(cleanBackup,"success.txt"),"restored successfully");
            if (!RollbackFiles(root,cleanBackup,[],["success.txt"])) throw new InvalidDataException("Successful rollback failed.");
            CleanupStage(clean,false);
            if (Directory.Exists(clean) || File.ReadAllText(Path.Combine(root,"success.txt")) != "restored successfully") throw new InvalidDataException("Successful rollback cleanup failed.");
            checks++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed=true, checks, embedded_payload_verified=true, rollback_failure_exercised=fixtureRoot is not null, filesystem_changed=fixtureRoot is not null, hardware_opened=false })); return 0;
    }
}
