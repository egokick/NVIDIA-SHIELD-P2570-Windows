using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

internal static partial class Program
{
    private static string SetupRoot => Path.Combine(Root, "setup");
    private static string SetupActive => Path.Combine(SetupRoot, "active.json");
    private static string ProvisionReceipt => Path.Combine(Root, "provision.json");
    private static bool ProvisionMatches(Configuration value)
    {
        try
        {
            using var document = JsonDocument.Parse(PrivateFiles.Read(ProvisionReceipt)); var p = document.RootElement;
            return p.GetProperty("schema").GetInt32() == 1 && p.GetProperty("pairing_success").GetBoolean() &&
                p.GetProperty("identity_confirmed").GetBoolean() && p.GetProperty("connect_success_callback_observed").GetBoolean() &&
                p.GetProperty("pc_cleanup_verified").GetBoolean() && p.GetProperty("channel").GetInt32() == value.Channel &&
                p.GetProperty("config_sha256").GetString() == value.SourceSha256;
        }
        catch { return false; }
    }
    private static async Task<int> Capabilities()
    {
        checkpoint = "capabilities";
        var requirements = new List<object>();
        void Requirement(string id, string label, bool available) => requirements.Add(new { id, label, available });
        bool supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);
        Requirement("windows", "Windows 11 24H2 (build 26100) or later", supported);
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap\Parameters", false))
            Requirement("npcap", "Npcap installed with non-administrator capture enabled", key?.GetValue("AdminOnly") is int adminOnly && adminOnly == 0 && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll")));
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus", false))
            Requirement("vigem", "ViGEmBus virtual Xbox controller driver", key is not null);
        bool packageOkay = false; try { package = PackageManifest.Load(); packageOkay = true; } catch { }
        Requirement("package", "Installed application files match their package manifest", packageOkay);
        var names = NetworkInterface.GetAllNetworkInterfaces().Where(n => Guid.TryParse(n.Id, out _)).ToDictionary(n => Guid.Parse(n.Id), n => n.Name);
        var wifi = new List<object>(); var uplinks = new List<object>();
        if (supported)
        {
            try
            {
                var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                foreach (var a in adapters) { Guid id = a.NetworkAdapter.NetworkAdapterId; wifi.Add(new { guid = id, name = names.GetValueOrDefault(id, "Wi-Fi") }); }
                Requirement("wifi", "Wi-Fi adapter available; Windows Location access may be required", wifi.Count > 0);
                Requirement("pairing-radio", "One enabled Wi-Fi adapter during first pairing", wifi.Count == 1);
            }
            catch { Requirement("wifi", "Allow Windows Location access so the application can enumerate Wi-Fi", false); }
            try
            {
                foreach (var p in NetworkInformation.GetConnectionProfiles().Where(p => p.NetworkAdapter is not null && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess).GroupBy(p => p.NetworkAdapter.NetworkAdapterId).Select(g => g.First()))
                { Guid id = p.NetworkAdapter.NetworkAdapterId; uplinks.Add(new { guid = id, name = names.GetValueOrDefault(id, "Internet connection") }); }
            }
            catch { }
        }
        Requirement("uplink", "Active Internet connection required by the current Windows Mobile Hotspot path", uplinks.Count > 0);
        Configuration? configured = null; try { configured = Configuration.Load(PrivateFiles.Config); } catch { }
        bool paired = configured is not null && ProvisionMatches(configured);
        Requirement("channel", "A 5 GHz Wi-Fi Direct group on channel 36 is required (verified at Start)", true);
        Log("capabilities", new { paired, recovery_required = File.Exists(PrivateFiles.Recovery) || File.Exists(SetupActive), requirements,
            wifi_adapters = wifi, uplink_adapters = uplinks, selected_wifi_guid = configured?.Wifi, selected_uplink_guid = configured?.Uplink,
            runtime_supported = supported, channel = 36, offline_uplink_supported = false });
        return 0;
    }
}
