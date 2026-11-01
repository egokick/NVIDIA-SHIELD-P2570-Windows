using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    private static async Task<int> Recover()
    {
        checkpoint = "explicit-recovery";
        if (!File.Exists(PrivateFiles.Recovery)) { Log("recovery-not-needed", new { mutations = 0 }); return 0; }
        var prior = RecoveryState.Load(config);
        if (LauncherLifetime.MatchesLive(prior.OwnerPid, prior.OwnerCreation)) throw new InvalidOperationException("Recorded owner is alive. Use Stop.");
        if (prior.ChildPid.HasValue && prior.ChildCreation.HasValue)
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (LauncherLifetime.MatchesLive(prior.ChildPid.Value, prior.ChildCreation.Value) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (LauncherLifetime.MatchesLive(prior.ChildPid.Value, prior.ChildCreation.Value)) throw new InvalidOperationException("Recorded data host is still alive. Recovery will not stop its AP.");
        }
        var profiles = NetworkInformation.GetConnectionProfiles();
        var profile = profiles.SingleOrDefault(p => p.NetworkAdapter?.NetworkAdapterId == config.Uplink)
            ?? throw new InvalidOperationException("Configured Uplink connection profile unavailable for recovery.");
        var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        var selectedAdapters = adapters.Where(a => a.NetworkAdapter.NetworkAdapterId == config.Wifi).ToArray();
        if (selectedAdapters.Length != 1) throw new InvalidOperationException("Selected Wi-Fi adapter unavailable.");
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile, selectedAdapters[0].NetworkAdapter);
        bool off = EmptyHotspot(manager.TetheringOperationalState, manager.ClientCount) && ChannelInspection.ActiveGroups().Length == 0;
        if (!off)
        {
            if (!prior.OwnedStart || prior.GroupId is not Guid group || prior.GroupMac is null ||
                manager.TetheringOperationalState != TetheringOperationalState.On || !ChannelInspection.ActiveGroups().SequenceEqual(new[] { group }) ||
                !GroupMac(group).SequenceEqual(prior.GroupMac.Select(b => (byte)b)) || ChannelInspection.Read(group) != config.Channel ||
                !await Task.Run(() => ChannelInspection.ReadSsidEquals(group, config.Ssid)).WaitAsync(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Active AP does not match the recorded owner. Turn it off explicitly before Recover.");
        }
        var policy = IdleTimeoutPolicy.ForRecovery(prior.OriginalIdleTimeout, prior.IdleDisablePlanned);
        bool stopPending = false, stopSucceeded = off;
        try
        {
            if (!off)
            {
                Log("recover-stopping-recorded-hotspot", new { exact_recorded_group_verified = true });
                var operation = manager.StopTetheringAsync(); var task = operation.AsTask();
                NetworkOperatorTetheringOperationResult result;
                try { result = await task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch
                {
                    var late = await CancelAndSettle(() => operation.Cancel(), task, TimeSpan.FromSeconds(2));
                    stopPending = late.Pending;
                    if (!late.HasResult) throw;
                    result = late.Result!;
                }
                stopSucceeded = result.Status == TetheringOperationStatus.Success;
            }
        }
        catch (Exception ex) { Log("recovery-stop-error", new { type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}", pending = stopPending }); }
        finally { await policy.Restore(); }
        off = EmptyHotspot(manager.TetheringOperationalState, manager.ClientCount) && ChannelInspection.ActiveGroups().Length == 0;
        bool okay = stopSucceeded && !stopPending && off && policy.Restored && !policy.Pending;
        if (okay) File.Delete(PrivateFiles.Recovery);
        Log("recovery-finished", new { ok = okay, hotspot_off = off, idle_timeout_policy_restored = policy.Restored, recovery_required = !okay, stop_pending = stopPending });
        return okay ? 0 : 1;
    }
}
