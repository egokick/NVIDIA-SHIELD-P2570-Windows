using System.Diagnostics;
using System.Text.Json;

internal enum SetupStage { Welcome, Checking, Installing, Downloading, ConnectionTool, GamepadDriver, Verifying, Ready, Restart, RestartRetry, Attention, SettingsHelp, Error }
internal enum DriverSetupResult { Ready, Restart, RestartRetry }
internal sealed record SetupView(SetupStage Stage, string Title, string Message, int Percent = 0, string? SettingsUri = null);
internal sealed record SetupRequirements(IReadOnlyDictionary<string, bool> Items, bool RecoveryRequired)
{
    internal bool DriverReady(string id) => Items.TryGetValue(id, out bool ready) && ready;
    internal static SetupRequirements Parse(string text)
    {
        JsonElement value = default;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("phase", out var phase) && phase.GetString() == "capabilities")
                value = document.RootElement.GetProperty("detail").Clone();
        }
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The setup check did not finish. Click Try again.");
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in value.GetProperty("requirements").EnumerateArray())
            if (!result.TryAdd(entry.GetProperty("id").GetString()!, entry.GetProperty("available").GetBoolean()))
                throw new InvalidDataException("The setup check returned duplicate requirements.");
        foreach (string id in new[] { "architecture", "windows", "npcap", "vigem", "package", "location", "wifi", "pairing-radio", "uplink" })
            if (!result.ContainsKey(id)) throw new InvalidDataException("The setup check is incomplete. Run the latest SHIELD Setup again.");
        return new(result, value.GetProperty("recovery_required").GetBoolean());
    }
    internal SetupView? Attention()
    {
        if (!DriverReady("architecture")) return new(SetupStage.Attention, "An Intel or AMD PC is needed", "This Windows release supports x64 PCs. ARM-based Windows PCs are not supported.", 100);
        if (!DriverReady("windows")) return new(SetupStage.Attention, "A newer Windows is needed", "Update to Windows 11 24H2 or later, then run Setup again.", 100, "ms-settings:windowsupdate");
        if (!DriverReady("package")) return new(SetupStage.Error, "Let's repair the app", "Some app files could not be checked. Click Try again to replace them.");
        if (RecoveryRequired) return new(SetupStage.Attention, "Finish the previous connection", "Open SHIELD Controller and use Recover. Then run Setup again.", 100);
        if (!DriverReady("location")) return new(SetupStage.Attention, "Allow Wi-Fi access", "In Windows Settings, turn on Location services and let desktop apps access your location. Then come back and click Check again.", 100, "ms-settings:privacy-location");
        if (!DriverReady("wifi")) return new(SetupStage.Attention, "Turn on your PC's Wi-Fi", "Your app is installed. Turn on Wi-Fi in Windows Settings, then come back and click Check again.", 100, "ms-settings:network-wifi");
        if (!DriverReady("pairing-radio")) return new(SetupStage.Attention, "Choose one Wi-Fi adapter", "Your app is installed. Keep one Wi-Fi adapter enabled for pairing, then click Check again.", 100, "ms-settings:network-advancedsettings");
        if (!DriverReady("uplink")) return new(SetupStage.Attention, "Connect your PC to the internet", "Your app is installed. Connect Ethernet or Wi-Fi, then come back and click Check again.", 100, "ms-settings:network-status");
        return null;
    }
}

internal interface ISetupOperations
{
    Task InstallApp();
    Task<SetupRequirements> Check();
    Task<bool> CheckNativeDrivers();
    Task<DriverSetupResult> InstallDrivers(Action<SetupView> progress);
}
internal sealed class SetupFlow(ISetupOperations operations)
{
    internal bool AppInstalled { get; private set; }
    internal async Task<SetupView> Run(Action<SetupView> progress)
    {
        try
        {
            progress(new(SetupStage.Installing, "Making room for play", "Installing the SHIELD app…", 15));
            await operations.InstallApp(); AppInstalled = true;
            progress(new(SetupStage.Checking, "Checking your PC", "Looking for the tools your controller needs…", 35));
            var before = await operations.Check();
            if (!before.DriverReady("npcap") || !before.DriverReady("vigem"))
            {
                var driverResult = await operations.InstallDrivers(progress);
                if (driverResult == DriverSetupResult.RestartRetry)
                    return new(SetupStage.RestartRetry, "Restart, then try Setup again", "Windows could not finish the connection tool. Restart Windows, then run SHIELD Setup again. Your app is installed.", 100);
                if (driverResult == DriverSetupResult.Restart)
                    return new(SetupStage.Restart, "One restart, then play", "Windows needs to finish installing a driver. Restart when you are ready, then open SHIELD Controller from Start.", 100);
            }
            progress(new(SetupStage.Verifying, "Almost there", "Checking that the app and drivers are ready…", 90));
            var after = await operations.Check();
            if (!after.DriverReady("npcap") || !after.DriverReady("vigem"))
                return new(SetupStage.Error, "One tool still needs setup", "Click Try again to finish the missing driver. You do not need to find or download anything yourself.");
            if (!await operations.CheckNativeDrivers())
                return new(SetupStage.Error, "Windows needs a driver repair", "A driver is installed but could not open. Restart Windows and try Setup again. If it still fails, open SHIELD Controller for help.");
            return after.Attention() ?? new(SetupStage.Ready, "Ready to pair!", "Unplug your controller from the PC. The app will show you which button to hold.", 100);
        }
        catch (Exception error) { Program.ReportSetupError(error); return new(SetupStage.Error, "Let's finish your setup", Friendly(error)); }
    }
    private static string Friendly(Exception error) => error switch {
        TimeoutException => "A Windows check took too long. Close any other installer, then click Try again.",
        System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => "Windows approval was cancelled. Click Try again, then click Yes when Windows asks.",
        _ => string.IsNullOrWhiteSpace(error.Message) ? "Setup could not finish. Click Try again." : error.Message
    };
}

internal sealed class NativeSetupOperations : ISetupOperations
{
    public Task InstallApp() => Task.Run(Program.Install);
    public async Task<bool> CheckNativeDrivers()
    {
        string executable = Path.Combine(Program.InstallationRoot, "bin", "data-host", "shield-controller-datahost.exe");
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--check-drivers" } }) ?? throw new IOException("Windows could not check the controller drivers.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { if (!process.HasExited) process.Kill(); throw; }
        await errors; string text = await output;
        if (process.ExitCode != 0 || text.Length > 4096) return false;
        using var document = JsonDocument.Parse(text); var value = document.RootElement;
        return value.GetProperty("npcap").GetBoolean() && value.GetProperty("vigem").GetBoolean()
            && !value.GetProperty("starts_or_stops_ap").GetBoolean() && !value.GetProperty("virtual_pad_created").GetBoolean();
    }
    public async Task<SetupRequirements> Check()
    {
        string executable = Path.Combine(Program.InstallationRoot, "bin", "owner", "ShieldControllerOwner.exe");
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--mode", "capabilities" } }) ?? throw new IOException("The app check could not start. Click Try again.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (TimeoutException) { if (!process.HasExited) process.Kill(); throw; } // Read-only capability process, never the wireless owner.
        await errors; string text = await output;
        if (process.ExitCode != 0 || text.Length > 262144) throw new IOException("The app check could not finish. Click Try again.");
        return SetupRequirements.Parse(text);
    }
    public async Task<DriverSetupResult> InstallDrivers(Action<SetupView> progress)
    {
        string helper = Path.Combine(Program.InstallationRoot, "bin", "Install-Dependencies.ps1");
        string resultName = "dependency-result-" + Guid.NewGuid().ToString("N") + ".json";
        string result = Path.Combine(Program.InstallationRoot, resultName);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Component", "All", "-ResultFileName", resultName }) start.ArgumentList.Add(arg);
        progress(new(SetupStage.Downloading, "Getting your connection tool", "Setup will open the official Npcap installer. Click I Agree and Install; the right options are already selected.", 45));
        using var process = Process.Start(start) ?? throw new IOException("Driver setup could not start. Click Try again.");
        var errors = process.StandardError.ReadToEndAsync();
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.Length > 4096) continue;
            try {
                using var document = JsonDocument.Parse(line); var value = document.RootElement;
                if (value.GetProperty("phase").GetString() != "dependency-progress") continue;
                string? component = value.GetProperty("component").GetString(), step = value.GetProperty("step").GetString();
                if (component == "Npcap" && step == "install") progress(new(SetupStage.ConnectionTool, "One quick approval", "In the Npcap window, click I Agree, Install, then Finish. The correct options are already selected.", 55));
                else if (component == "ViGEmBus" && step == "install") progress(new(SetupStage.GamepadDriver, "Your gamepad driver", "Click Yes if Windows asks. The gamepad driver installs automatically.", 75));
                else if (step == "verify") progress(new(SetupStage.Verifying, "Checking the official driver", "Verifying its signature before opening it…", component == "Npcap" ? 48 : 70));
            } catch (JsonException) { } catch (KeyNotFoundException) { }
        }
        await process.WaitForExitAsync(); await errors;
        if (process.ExitCode == 1618) throw new IOException("Another SHIELD driver setup is running. Finish that window, then click Try again.");
        if (!File.Exists(result) || new FileInfo(result).Length > 16384) throw new IOException("Windows did not return a setup result. Click Try again.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(result));
        if (!receipt.RootElement.GetProperty("ok").GetBoolean()) throw new IOException(receipt.RootElement.GetProperty("message").GetString() ?? "Driver setup did not finish. Click Try again.");
        if (process.ExitCode == 350 && receipt.RootElement.GetProperty("restart_retry_required").GetBoolean()) return DriverSetupResult.RestartRetry;
        if (process.ExitCode == 3010 && receipt.RootElement.GetProperty("restart_required").GetBoolean()) return DriverSetupResult.Restart;
        if (process.ExitCode != 0) throw new IOException("Driver setup did not finish. Click Try again.");
        return DriverSetupResult.Ready;
    }
}

internal static class SetupFlowTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition) { checks++; if (!condition) throw new InvalidDataException("Guided setup flow check failed."); }
        SetupRequirements State(bool npcap = true, bool vigem = true, string? missing = null) {
            var values = new Dictionary<string, bool> { ["architecture"] = true, ["windows"] = true, ["package"] = true, ["npcap"] = npcap, ["vigem"] = vigem, ["wifi"] = true, ["location"] = true, ["pairing-radio"] = true, ["uplink"] = true };
            if (missing is not null) values[missing] = false; return new(values, false);
        }
        foreach (bool installed in new[] { false, true }) {
            var operations = new Fake { Before = State(installed, installed), After = State() }; var flow = new SetupFlow(operations);
            var result = flow.Run(_ => { }).GetAwaiter().GetResult();
            Check(result.Stage == SetupStage.Ready && flow.AppInstalled && operations.Installs == 1);
            Check(operations.DriverInstalls == (installed ? 0 : 1));
        }
        var restart = new Fake { Before = State(false), After = State(false), Restart = DriverSetupResult.Restart };
        Check(new SetupFlow(restart).Run(_ => { }).GetAwaiter().GetResult().Stage == SetupStage.Restart && restart.Checks == 1);
        restart.Restart = DriverSetupResult.RestartRetry;
        Check(new SetupFlow(restart).Run(_ => { }).GetAwaiter().GetResult().Stage == SetupStage.RestartRetry);
        var retry = new Fake { Before = State(false), After = State(), Failure = new IOException("Download unavailable") };
        var retryFlow = new SetupFlow(retry); Check(retryFlow.Run(_ => { }).GetAwaiter().GetResult().Stage == SetupStage.Error);
        retry.Failure = null; Check(retryFlow.Run(_ => { }).GetAwaiter().GetResult().Stage == SetupStage.Ready && retry.Installs == 2);
        var rejected = new Fake { Before = State(false), After = State(false) };
        Check(new SetupFlow(rejected).Run(_ => { }).GetAwaiter().GetResult().Stage == SetupStage.Error);
        var nativeFailure = new Fake { Before = State(), After = State(), NativeReady = false };
        Check(new SetupFlow(nativeFailure).Run(_ => { }).GetAwaiter().GetResult().Stage == SetupStage.Error);
        foreach (var (id, uri) in new[] { ("location", "ms-settings:privacy-location"), ("wifi", "ms-settings:network-wifi"), ("pairing-radio", "ms-settings:network-advancedsettings"), ("uplink", "ms-settings:network-status") }) {
            var ops = new Fake { Before = State(), After = State(missing: id) }; var flow = new SetupFlow(ops); var result = flow.Run(_ => { }).GetAwaiter().GetResult();
            Check(result.Stage == SetupStage.Attention && result.SettingsUri == uri && flow.AppInstalled);
        }
        var denied = State(missing: "location"); ((Dictionary<string,bool>)denied.Items)["wifi"] = false;
        Check(denied.Attention()!.SettingsUri == "ms-settings:privacy-location");
        var cancelled = new Fake { Before = State(false), After = State(), Failure = new System.ComponentModel.Win32Exception(1223) };
        Check(new SetupFlow(cancelled).Run(_ => { }).GetAwaiter().GetResult().Message.Contains("cancelled"));
        bool invalid = false; try { SetupRequirements.Parse("{\"phase\":\"capabilities\",\"detail\":{\"requirements\":[],\"recovery_required\":false}}"); } catch (InvalidDataException) { invalid = true; } Check(invalid);
        string SerializeState(IEnumerable<KeyValuePair<string,bool>> items) => JsonSerializer.Serialize(new { phase = "capabilities", detail = new { requirements = items.Select(pair => new { id = pair.Key, label = pair.Key, available = pair.Value }), recovery_required = false } });
        Check(SetupRequirements.Parse(SerializeState(State().Items)).Attention() is null);
        foreach (string id in new[] { "architecture", "location", "pairing-radio" }) {
            invalid = false; try { SetupRequirements.Parse(SerializeState(State().Items.Where(pair => pair.Key != id))); } catch (InvalidDataException) { invalid = true; } Check(invalid);
        }
        Check(SetupRequirements.Parse(SerializeState(denied.Items)).Attention()!.SettingsUri == "ms-settings:privacy-location");
        using (var form = new SetupForm(new Fake { Before = State(), After = State() }, launch: false, preview: true)) {
            var attention = denied.Attention()!; form.Apply(attention);
            form.OpenSettings(_ => throw new System.ComponentModel.Win32Exception(2));
            Check(form.CurrentView.Stage == SetupStage.SettingsHelp && form.CurrentView.SettingsUri == attention.SettingsUri && form.CurrentView.Message.Contains("Windows + I"));
            int opens = 0; form.OpenSettings(uri => { Check(uri == attention.SettingsUri); opens++; });
            Check(opens == 1 && !form.Visible && !form.ShowInTaskbar);
        }
        return checks;
    }
    private sealed class Fake : ISetupOperations
    {
        internal required SetupRequirements Before, After; internal DriverSetupResult Restart; internal Exception? Failure; internal bool NativeReady = true;
        internal int Installs, DriverInstalls, Checks; private int attemptChecks;
        public Task InstallApp() { Installs++; attemptChecks = 0; return Task.CompletedTask; }
        public Task<SetupRequirements> Check() { Checks++; return Task.FromResult(++attemptChecks == 1 ? Before : After); }
        public Task<bool> CheckNativeDrivers() => Task.FromResult(NativeReady);
        public Task<DriverSetupResult> InstallDrivers(Action<SetupView> progress) { DriverInstalls++; if (Failure is not null) throw Failure; return Task.FromResult(Restart); }
    }
}
