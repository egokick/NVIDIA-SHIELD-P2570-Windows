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

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.SequenceEqual(new[] { "--selftest" })) return SelfTest();
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
                catch (Exception ex) { installing = false; body.Text = ex.Message; install.Enabled = true; start.Enabled = true; }
            };
            form.ShowDialog();
            return form.DialogResult == DialogResult.OK ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(ex.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void Launch() => Process.Start(new ProcessStartInfo(Tray, "--show") { UseShellExecute = true });

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
        using var mutex = new Mutex(false, @"Local\NVIDIA-SHIELD-P2570-Installer");
        bool held;
        try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Another installation is running.");
        try
        {
            NoLinks(Root);
            Directory.CreateDirectory(Root);
            MakePrivateRoot();
            // Stop only this installed app, and wait for its normal AP cleanup.
            if (File.Exists(Tray))
            {
                using var stop = Process.Start(new ProcessStartInfo(Tray, "--exit") { UseShellExecute = false, CreateNoWindow = true });
                stop?.WaitForExit(20000);
                var until = DateTime.UtcNow.AddSeconds(30);
                while (OwnedProcessesRunning())
                {
                    if (DateTime.UtcNow > until) throw new IOException("The controller app is still shutting down. Disconnect and exit it, then run Setup again.");
                    Thread.Sleep(200);
                }
            }
            if (File.Exists(Path.Combine(Root,"recovery.json")) || File.Exists(Path.Combine(Root,"setup","active.json"))) throw new IOException("The previous controller session needs recovery. Open the installed app and use Recover before reinstalling.");
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("This installer does not contain a release payload.");
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
            var paths = entries.Select(e => Relative(e.FullName)).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length || !paths.Contains(Path.Combine("bin","tray","ShieldController.exe"))) throw new InvalidDataException("Invalid or incomplete application package.");
            if (entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Package is unexpectedly large.");
            string stage = Path.Combine(Root,".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var installed = new List<string>();
            var oldFiles = new List<string>();
            string backup = Path.Combine(stage,".backup");
            try
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if ((entries[i].ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Package links are not allowed.");
                    string output = Path.Combine(stage, paths[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    entries[i].ExtractToFile(output);
                }
                ValidateStage(stage, paths);
                foreach (string relative in paths)
                {
                    string target = Path.Combine(Root,relative); NoLinks(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                    {
                        string old = Path.Combine(backup,relative); Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                        File.Move(target,old); oldFiles.Add(relative);
                    }
                    File.Move(Path.Combine(stage,relative),target); installed.Add(relative);
                }
                CreateShortcut();
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue("DisplayName", Product); key.SetValue("DisplayVersion", "0.1.0");
                key.SetValue("Publisher", "egokick and contributors"); key.SetValue("InstallLocation", Root);
                key.SetValue("DisplayIcon", Tray); key.SetValue("URLInfoAbout", "https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows");
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
                key.SetValue("UninstallString", $"\"{powershell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Path.Combine(Root,"bin","Uninstall.ps1")}\"");
                key.SetValue("NoModify",1); key.SetValue("NoRepair",1);
                File.WriteAllText(Path.Combine(Root,"installed-files.json"),JsonSerializer.Serialize(paths));
            }
            catch
            {
                foreach (string relative in installed.AsEnumerable().Reverse()) File.Delete(Path.Combine(Root,relative));
                foreach (string relative in oldFiles) File.Move(Path.Combine(backup,relative),Path.Combine(Root,relative));
                throw;
            }
            finally
            {
                // Stage is a freshly generated child of the fixed installation root.
                NoLinks(stage); Directory.Delete(stage,true);
            }
        }
        finally { mutex.ReleaseMutex(); }
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
        var manifest = document.RootElement;
        if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("product").GetString() != Folder) throw new InvalidDataException("Package manifest identity is invalid.");
        var files = manifest.GetProperty("files").EnumerateObject().ToArray();
        if (files.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new InvalidDataException("Duplicate package hashes.");
        var actual = paths.Where(p => p != "package.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(files.Select(p => Relative(p.Name)))) throw new InvalidDataException("Package manifest does not match its files.");
        foreach (var file in files)
        {
            string hash = file.Value.GetString() ?? "";
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(stage,Relative(file.Name))))).Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A package file failed its integrity check.");
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

    static int SelfTest()
    {
        int checks = 0;
        foreach (string value in new[] { "bin/tray/ShieldController.exe", "licenses/GPL-2.0.txt", "package.json" }) { Relative(value); checks++; }
        foreach (string value in new[] { "../secret", "/bin/a", "bin/../../outside", "bin/a:stream", "bin\\a", "config.json", "bin/a.", "bin//a", "bin/./a", "bin/CON.txt", "bin/a?.dll" })
        { bool failed=false; try { Relative(value); } catch (InvalidDataException) { failed=true; } if (!failed) return 1; checks++; }
        Console.WriteLine(JsonSerializer.Serialize(new { passed=true, checks, filesystem_changed=false, hardware_opened=false })); return 0;
    }
}
