using System.Security.Cryptography;
using System.Text;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    private static async Task<int> SelfTest()
    {
        int count = ConnectionSettings.SelfTest();
        void Check(bool value) { if (!value) throw new InvalidOperationException("Offline contract check failed."); count++; }
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111"); var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Check(ChooseUplink([first, second], first, second) == first);
        Check(ChooseUplink([first, second], null, second) == second);
        Check(ChooseUplink([second], first, second) == second);
        Check(ChooseUplink([second, first], null, null) == first);
        Check(ChooseUplink([], first, second) is null);
        Check(WifiPermissionDenied(new UnauthorizedAccessException()));
        Check(WifiPermissionDenied(new System.Runtime.InteropServices.COMException("Denied", unchecked((int)0x80070005))));
        Check(!WifiPermissionDenied(new TimeoutException()) && !WifiPermissionDenied(new IOException()));
        Check(NpcapConfigured(true, true, null) && NpcapConfigured(true, true, 0));
        Check(!NpcapConfigured(true, true, 1) && !NpcapConfigured(true, true, "0"));
        Check(!NpcapConfigured(false, true, null) && !NpcapConfigured(true, false, 0));
        const string valid = "{\"schema\":1,\"wifi_guid\":\"11111111-1111-1111-1111-111111111111\",\"uplink_guid\":\"22222222-2222-2222-2222-222222222222\",\"controller_mac\":[2,1,2,3,4,5],\"channel\":36,\"ssid\":\"DIRECT-TEST\",\"passphrase\":\"SyntheticOnly123\"}";
        byte[] bytes = Encoding.UTF8.GetBytes(valid); var value = Configuration.Parse(bytes);
        Check(value.Channel == 36 && value.ControllerMac.SequenceEqual(new[] { 2, 1, 2, 3, 4, 5 }));
        Check(value.SourceSha256 == Convert.ToHexString(SHA256.HashData(bytes)));
        Check(value.ToString() == "Configuration(<redacted>)");
        string recoveryJson = System.Text.Json.JsonSerializer.Serialize(new { schema = 1, session_id = new string('a', 32), config_sha256 = value.SourceSha256,
            owner_pid = 123, owner_creation_filetime = 456UL, original_idle_timeout_enabled = true, owned_start_success = false });
        Check(RecoveryState.Parse(Encoding.UTF8.GetBytes(recoveryJson), value).OwnerPid == 123);
        foreach (int channel in new[] { 6, 36, 48 }) {
            var state = new RecoveryState { SessionId = new string('a', 32), ConfigHash = value.SourceSha256, OwnerPid = 123, OwnerCreation = 456,
                OwnedStart = true, GroupId = Guid.NewGuid(), GroupMac = [2, 1, 2, 3, 4, 5], Channel = channel };
            Check(RecoveryState.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state), value).Channel == channel);
        }
        foreach (string invalidRecovery in new[] {
            recoveryJson.Replace("\"owner_pid\":123", "\"owner_pid\":0"),
            recoveryJson.Replace("\"owned_start_success\":false", "\"owned_start_success\":false,\"idle_disable_planned\":true"),
            recoveryJson.Replace("\"owned_start_success\":false", "\"owned_start_success\":false,\"child_pid\":123"),
            recoveryJson.Replace("\"owned_start_success\":false", "\"owned_start_success\":false,\"child_creation_filetime\":123"),
            recoveryJson.Replace("\"schema\":1", "\"schema\":1,\"schema\":1") })
        { bool rejected = false; try { RecoveryState.Parse(Encoding.UTF8.GetBytes(invalidRecovery), value); } catch (FormatException) { rejected = true; } Check(rejected); }
        foreach (string invalid in new[] { valid.Replace("\"schema\":1", "\"schema\":2"), valid.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"),
            valid.Replace("\"schema\":1", "\"schema\":1,\"unknown\":1"), valid.Replace("[2,1,2,3,4,5]", "[3,1,2,3,4,5]"), valid.Replace("[2,1,2,3,4,5]", "null"),
            valid.Replace("36", "149"), valid.Replace("SyntheticOnly123", "short"), valid.Replace("DIRECT-TEST", "DIRECT-\u00e9"), valid.Replace("\"passphrase\":\"SyntheticOnly123\"", "\"passphrase\":null") })
        { bool rejected = false; try { Configuration.Parse(Encoding.UTF8.GetBytes(invalid)); } catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { rejected = true; } Check(rejected); }
        Check(OwnsSuccessfulStart(TetheringOperationStatus.Success) && !OwnsSuccessfulStart(TetheringOperationStatus.AlreadyOn));
        Check(EmptyHotspot(TetheringOperationalState.Off, 0) && !EmptyHotspot(TetheringOperationalState.Off, 1));
        var late = await CancelAndSettle(() => throw new InvalidOperationException(), Task.FromResult(7), TimeSpan.FromMilliseconds(1));
        Check(late.HasResult && late.Result == 7 && late.CancelCallFailed && !late.Pending);
        var pending = new TaskCompletionSource<int>();
        var unresponsive = await CancelAndSettle(() => { }, pending.Task, TimeSpan.FromMilliseconds(1));
        Check(!unresponsive.HasResult && unresponsive.Pending);
        var cancelled = await CancelAndSettle(() => { }, Task.FromCanceled<int>(new CancellationToken(true)), TimeSpan.FromMilliseconds(1));
        Check(!cancelled.HasResult && !cancelled.Pending);
        Check(new UTF8Encoding(false).GetBytes("alive\n").SequenceEqual(new byte[] { 97, 108, 105, 118, 101, 10 }));
        Check(ChannelInspection.IsExpectedChannel(36) && ChannelInspection.IsExpectedChannel(11) && !ChannelInspection.IsExpectedChannel(149));
        byte[] attributes = new byte[604]; attributes[0] = 1; attributes[520] = 4; Encoding.ASCII.GetBytes("TEST").CopyTo(attributes, 524);
        Check(ChannelInspection.SsidEquals(attributes, "TEST") && !ChannelInspection.SsidEquals(attributes, "test"));
        attributes[520] = 33; bool malformed = false;
        try { ChannelInspection.SsidEquals(attributes, "TEST"); } catch (InvalidOperationException) { malformed = true; } Check(malformed);
        count += RawPskSelfTest();
        Check(ValidMac([2, 1, 2, 3, 4, 5]) && !ValidMac([3, 1, 2, 3, 4, 5]) && !ValidMac([0, 0, 0, 0, 0, 0]));
        string escaped = Profile(Configuration.Parse(Encoding.UTF8.GetBytes(valid.Replace("DIRECT-TEST", "DIRECT-&test"))));
        var profile = System.Xml.Linq.XDocument.Parse(escaped);
        System.Xml.Linq.XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        Check(profile.Descendants(ns + "name").Last().Value == "DIRECT-&test" && profile.Descendants(ns + "keyType").Single().Value == "networkKey" && profile.Descendants(ns + "keyMaterial").Single().Value.Length == 64);
        Check(ForgetAllowed(false, 0, TetheringOperationalState.Off, 0));
        Check(!ForgetAllowed(true, 0, TetheringOperationalState.Off, 0) && !ForgetAllowed(false, 1, TetheringOperationalState.Off, 0) && !ForgetAllowed(false, 0, TetheringOperationalState.On, 0) && !ForgetAllowed(false, 0, TetheringOperationalState.Off, 1));
        var readiness = new HostResult();
        Check(ApplyReadiness("expected-controller-session", null, readiness) is null && readiness.XinputSlot is null && readiness.ExpectedControllerSession);
        Check(ApplyReadiness("xinput-ready", 0, readiness) == "connected" && readiness.XinputSlot == 0);
        Check(ApplyReadiness("stale-input-released", null, readiness) == "disconnected" && readiness.XinputSlot is null);
        Check(ApplyReadiness("xinput-ready", 0, readiness) == "connected" && readiness.XinputSlot == 0);
        Check(ApplyReadiness("expected-controller-session", null, readiness) == "disconnected" && readiness.XinputSlot is null);
        bool slotRejected = false; try { ApplyReadiness("xinput-ready", 4, readiness); } catch (InvalidDataException) { slotRejected = true; } Check(slotRejected);
        Log("selftest-passed", new { checks = count, native_radio_calls = 0, configuration_loaded = false });
        return 0;
    }
}
