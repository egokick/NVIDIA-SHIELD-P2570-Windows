using System.Diagnostics;

// A successful complete validation grants a short lease, never an unlimited
// keepalive. Both the full validation and the lease are bounded by five seconds.
internal sealed class OwnerHeartbeatLease
{
    internal const int IntervalMilliseconds = 500;
    internal const int ValidationMilliseconds = 5000;
    private readonly object gate = new();
    private long lastValidated;
    private long? validationStarted;
    private bool revoked;
    internal OwnerHeartbeatLease(long now) => lastValidated = now;
    private bool Valid(long now)
    {
        if (revoked || now < lastValidated || now - lastValidated >= ValidationMilliseconds ||
            (validationStarted.HasValue && now - validationStarted.Value >= ValidationMilliseconds))
        { revoked = true; return false; }
        return true;
    }
    internal bool CanHeartbeat(long now) { lock (gate) return Valid(now); }
    internal void BeginValidation(long now)
    {
        lock (gate)
        {
            if (!Valid(now) || validationStarted.HasValue) throw new TimeoutException("Owner validation lease expired or validation already active.");
            validationStarted = now;
        }
    }
    internal void CompleteValidation(long now)
    {
        lock (gate)
        {
            if (!Valid(now) || !validationStarted.HasValue) throw new TimeoutException("Owner validation completed outside its lease.");
            lastValidated = now; validationStarted = null;
        }
    }
    internal void Revoke() { lock (gate) revoked = true; }
    internal static int SelfTest()
    {
        int count = 0;
        void Check(bool condition) { count++; if (!condition) throw new InvalidOperationException("Owner heartbeat lease check failed."); }
        var slow = new OwnerHeartbeatLease(0); slow.BeginValidation(0);
        for (int time = 0; time <= 3500; time += IntervalMilliseconds) Check(slow.CanHeartbeat(time));
        slow.CompleteValidation(3900); Check(slow.CanHeartbeat(4000));
        slow.BeginValidation(4000); slow.CompleteValidation(7900); Check(slow.CanHeartbeat(8000));
        var hung = new OwnerHeartbeatLease(0); hung.BeginValidation(0);
        Check(hung.CanHeartbeat(4500) && !hung.CanHeartbeat(5000));
        bool lateRejected = false; try { hung.CompleteValidation(5001); } catch (TimeoutException) { lateRejected = true; }
        Check(lateRejected && !hung.CanHeartbeat(5500));
        foreach (string reason in new[] { "channel-changed", "mac-changed", "uplink-changed", "stop", "launcher-exited" })
        {
            var changed = new OwnerHeartbeatLease(0); changed.BeginValidation(0); changed.Revoke();
            Check(!changed.CanHeartbeat(1));
            lateRejected = false; try { changed.CompleteValidation(2); } catch (TimeoutException) { lateRejected = true; }
            Check(lateRejected);
        }
        Check(!new OwnerHeartbeatLease(0).CanHeartbeat(5000));
        return count;
    }
}

internal static partial class Program
{
    private sealed class HostValidationException(string stage, Exception error) : InvalidOperationException("Owned network validation failed.", error)
    { internal string Stage { get; } = stage; }

    private static string ValidateOwnedHost(Prepared prepared, Guid groupId, byte[] mac, int channel, CancellationToken cancellation)
    {
        string stage = "verify-hotspot-on";
        void Step(string next) { cancellation.ThrowIfCancellationRequested(); stage = next; }
        try
        {
            Step("verify-hotspot-on");
            var state = prepared.Manager.TetheringOperationalState;
            if (state != Windows.Networking.NetworkOperators.TetheringOperationalState.On) throw new InvalidOperationException("Owned hotspot no longer On.");
            Step("verify-active-go");
            if (!ChannelInspection.ActiveGroups().SequenceEqual(new[] { groupId })) throw new InvalidOperationException("Owned GO changed.");
            Step("verify-go-mac");
            if (!GroupMac(groupId).SequenceEqual(mac)) throw new InvalidOperationException("Owned GO MAC changed.");
            Step("verify-uplink");
            if (UplinkState() != prepared.UplinkAddressState) throw new InvalidOperationException("Uplink changed.");
            Step("verify-go-channel");
            if (ChannelInspection.Read(groupId) != channel) throw new InvalidOperationException("Owned hotspot channel changed.");
            Step("verify-go-ssid");
            if (!ChannelInspection.ReadSsidEquals(groupId, config.Ssid)) throw new InvalidOperationException("Owned hotspot SSID changed.");
            cancellation.ThrowIfCancellationRequested();
            return state.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new HostValidationException(stage, error); }
    }

    private static async Task KeepChildAlive(Process child, HostResult result, OwnerHeartbeatLease lease, Stopwatch lifetime, CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested && !child.HasExited)
            {
                if (ShouldStop) { result.StopRequested = true; return; }
                if (!lease.CanHeartbeat(lifetime.ElapsedMilliseconds)) throw new TimeoutException("Owner validation lease expired.");
                // This loop contains no WinRT/WLAN query and never writes logs.
                await child.StandardInput.WriteAsync("alive\n").WaitAsync(TimeSpan.FromMilliseconds(500), stop.Token);
                await child.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(500), stop.Token);
                result.Heartbeats++;
                await Task.Delay(OwnerHeartbeatLease.IntervalMilliseconds, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) when (child.HasExited) { }
        finally
        {
            lease.Revoke(); stop.Cancel();
            try { child.StandardInput.Close(); } catch { }
        }
    }
}
