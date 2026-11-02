using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

internal static partial class Program
{
    internal static Guid? ChooseUplink(IEnumerable<Guid> eligible, Guid? saved, Guid? preferred)
    {
        var ids = eligible.ToHashSet();
        return saved.HasValue && ids.Contains(saved.Value) ? saved : preferred.HasValue && ids.Contains(preferred.Value) ? preferred : ids.Order().Select(id => (Guid?)id).FirstOrDefault();
    }
    internal static bool WifiPermissionDenied(Exception error) => error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005);
    internal static bool NpcapConfigured(bool serviceExists, bool libraryExists, object? adminOnly) => serviceExists && libraryExists && (adminOnly is null || adminOnly is int value && value == 0);
    private static string SetupRoot => Path.Combine(Root, "setup");
    private static string SetupActive => Path.Combine(SetupRoot, "active.json");
    private static string ProvisionReceipt => Path.Combine(Root, "provision.json");
    private static bool ProvisionMatches(Configuration value)
    {
        try
        {
            byte[] bytes = PrivateFiles.Read(ProvisionReceipt);
            using var document = JsonDocument.Parse(bytes); var p = document.RootElement;
            if (p.GetProperty("schema").GetInt32() == 2) return ImportedPairingReceipt.Matches(bytes, value, DateTimeOffset.UtcNow);
            return p.GetProperty("schema").GetInt32() == 1 && p.GetProperty("pairing_success").GetBoolean() &&
                p.GetProperty("identity_confirmed").GetBoolean() && p.GetProperty("connect_success_callback_observed").GetBoolean() &&
                p.GetProperty("pc_cleanup_verified").GetBoolean() && p.GetProperty("channel").GetInt32() == value.Channel &&
                p.GetProperty("config_sha256").GetString() == value.SourceSha256;
        }
        catch { return false; }
    }
    private static async Task<int> Capabilities(bool installationOnly = false)
    {
        checkpoint = "capabilities";
        var requirements = new List<object>();
        void Requirement(string id, string label, bool available) => requirements.Add(new { id, label, available });
        bool windowsSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);
        bool architectureSupported = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64;
        bool supported = windowsSupported && architectureSupported;
        Requirement("architecture", "An Intel or AMD x64 PC is required; Windows on ARM is not supported", architectureSupported);
        Requirement("windows", "Windows 11 24H2 (build 26100) or later", windowsSupported);
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap", false))
        using (var key = service?.OpenSubKey("Parameters", false))
            // Npcap itself treats an absent AdminOnly value as zero. Do not
            // reject an otherwise installed driver and trap users in setup.
            Requirement("npcap", "Npcap installed with non-administrator capture enabled", NpcapConfigured(service is not null, File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll")), key?.GetValue("AdminOnly")));
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus", false))
            Requirement("vigem", "ViGEmBus virtual Xbox controller driver", key is not null);
        bool packageOkay = false; try { package = PackageManifest.Load(); packageOkay = true; } catch { }
        Requirement("package", "Installed application files match their package manifest", packageOkay);
        // Installing files/drivers does not need Wi-Fi discovery or location
        // consent. Defer radio checks to the user's explicit pairing action.
        if (installationOnly)
        {
            Log("capabilities", new { paired = false, recovery_required = false, requirements });
            return 0;
        }
        var names = NetworkInterface.GetAllNetworkInterfaces().Where(n => Guid.TryParse(n.Id, out _)).ToDictionary(n => Guid.Parse(n.Id), n => n.Name);
        var wifi = new List<object>(); var uplinks = new List<object>(); var uplinkIds = new HashSet<Guid>();
        Guid? defaultUplink = null;
        if (supported)
        {
            try
            {
                var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                foreach (var a in adapters) { Guid id = a.NetworkAdapter.NetworkAdapterId; wifi.Add(new { guid = id, name = names.GetValueOrDefault(id, "Wi-Fi") }); }
                Requirement("location", "Windows allows Wi-Fi access", true);
                Requirement("wifi", "A Wi-Fi adapter is turned on", wifi.Count > 0);
                Requirement("pairing-radio", "One enabled Wi-Fi adapter during first pairing", wifi.Count == 1);
            }
            catch (Exception error)
            {
                bool denied = WifiPermissionDenied(error);
                Requirement("location", "Allow Location access in Windows Settings so the controller can use Wi-Fi", !denied);
                Requirement("wifi", denied ? "Wi-Fi access is waiting for Windows permission" : "Turn on Wi-Fi and check that Windows can see the adapter", false);
                Requirement("pairing-radio", "One enabled Wi-Fi adapter during first pairing", false);
            }
            try
            {
                defaultUplink = NetworkInformation.GetInternetConnectionProfile()?.NetworkAdapter?.NetworkAdapterId;
                foreach (var p in NetworkInformation.GetConnectionProfiles().Where(p => p.NetworkAdapter is not null && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess).GroupBy(p => p.NetworkAdapter.NetworkAdapterId).Select(g => g.First()))
                { Guid id = p.NetworkAdapter.NetworkAdapterId; uplinkIds.Add(id); uplinks.Add(new { guid = id, name = names.GetValueOrDefault(id, "Internet connection") }); }
            }
            catch { }
        }
        else
        {
            Requirement("location", "Wi-Fi access requires a supported Windows version and architecture", false);
            Requirement("wifi", "A Wi-Fi adapter is turned on", false);
            Requirement("pairing-radio", "One enabled Wi-Fi adapter during first pairing", false);
        }
        Requirement("uplink", "Active Internet connection required by the current Windows Mobile Hotspot path", uplinks.Count > 0);
        Configuration? configured = null; try { configured = Configuration.Load(PrivateFiles.Config); } catch { }
        Configuration? selection = configured;
        if (selection is null) { try { selection = Configuration.Load(Path.Combine(SetupRoot, "candidate.json")); } catch { } }
        bool paired = configured is not null && ProvisionMatches(configured);
        Requirement("channel", "Controller input requires 5 GHz; temporary pairing may use 2.4 GHz or 5 GHz", true);
        Log("capabilities", new { paired, recovery_required = File.Exists(PrivateFiles.Recovery) || File.Exists(SetupActive), requirements,
            wifi_adapters = wifi, uplink_adapters = uplinks, selected_wifi_guid = selection?.Wifi,
            selected_uplink_guid = ChooseUplink(uplinkIds, selection?.Uplink, defaultUplink),
            runtime_supported = supported, band_preference = "5ghz-only", offline_uplink_supported = false });
        return 0;
    }
}
