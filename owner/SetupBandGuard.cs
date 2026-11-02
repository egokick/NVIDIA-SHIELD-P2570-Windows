using System.Diagnostics;

// WCN has no public band selector. Verify its actual GO before accepting
// discovery results. A confirmed GO may have an unavailable channel (query error 5023).
// Pairing may use 2.4 GHz; input remains separately limited to verified 5 GHz.
internal sealed class SetupBandGuard
{
    internal bool Verified { get; private set; }
    private int? lastGroupCount;
    internal void Observe(int groupCount, int? channel, bool channelUnavailable = false)
    {
        lastGroupCount = groupCount;
        if (groupCount > 1) throw new PairingNetworkException(PairingNetworkFailureReason.AmbiguousGroups, groupCount: groupCount);
        if (groupCount == 1 && (channel.HasValue || channelUnavailable))
        {
            if (channel.HasValue) ConnectionSettings.RequirePairingChannel(channel, groupCount);
            Verified = true;
        }
    }
    internal void Complete()
    {
        if (!Verified) throw new PairingNetworkException(lastGroupCount == 1 ? PairingNetworkFailureReason.ChannelUnavailable : PairingNetworkFailureReason.NoGroup, groupCount: lastGroupCount);
    }

    internal static async Task Monitor(Func<bool> workerExited, CancellationToken cancellation)
    {
        var guard = new SetupBandGuard(); var startup = Stopwatch.StartNew();
        while (!workerExited())
        {
            cancellation.ThrowIfCancellationRequested();
            Guid[] groups = ChannelInspection.ActiveGroups(); int? channel = null; bool channelUnavailable = false;
            if (groups.Length == 1)
            {
                try { channel = await Task.Run(() => ChannelInspection.ReadPairing(groups[0])).WaitAsync(TimeSpan.FromSeconds(2), cancellation); channelUnavailable = !channel.HasValue; }
                catch (PairingNetworkException ex) when (ex.Reason == PairingNetworkFailureReason.NoGroup && workerExited()) { break; } // WCN releases its GO on normal worker exit.
                catch (PairingNetworkException) { throw; }
                catch (InvalidOperationException) { if (guard.Verified) throw new PairingNetworkException(PairingNetworkFailureReason.ChannelUnavailable, groupCount: groups.Length); } // The port can still be starting.
                catch (TimeoutException) { throw new PairingNetworkException(PairingNetworkFailureReason.ChannelQueryTimedOut, groupCount: groups.Length); }
            }
            guard.Observe(groups.Length, channel, channelUnavailable);
            if (!guard.Verified && startup.Elapsed > TimeSpan.FromSeconds(15)) guard.Complete();
            await Task.Delay(100, cancellation);
        }
        guard.Complete();
    }

    internal static int SelfTest()
    {
        int count = 0;
        void Check(bool value) { count++; if (!value) throw new InvalidOperationException("Setup band diagnostics check failed."); }
        void Reject(Action action, PairingNetworkFailureReason reason, string code, int? channel, int? groups)
        {
            PairingNetworkException? failure = null;
            try { action(); } catch (PairingNetworkException ex) { failure = ex; }
            Check(failure is not null && failure.Reason == reason && failure.ReasonCode == code && failure.Channel == channel && failure.GroupCount == groups);
        }
        var guard = new SetupBandGuard();
        Reject(guard.Complete, PairingNetworkFailureReason.NoGroup, "no-group", null, null);
        guard.Observe(0, null);
        Reject(guard.Complete, PairingNetworkFailureReason.NoGroup, "no-group", null, 0);
        guard.Observe(1, null);
        Reject(guard.Complete, PairingNetworkFailureReason.ChannelUnavailable, "channel-unavailable", null, 1);
        guard.Observe(1, null, channelUnavailable: true); guard.Complete(); Check(guard.Verified);
        Reject(() => guard.Observe(2, null), PairingNetworkFailureReason.AmbiguousGroups, "ambiguous-groups", null, 2);
        guard.Observe(1, 11); guard.Complete(); Check(guard.Verified);
        Reject(() => guard.Observe(1, 64), PairingNetworkFailureReason.UnsupportedChannel, "unsupported-channel", 64, 1);
        guard.Observe(1, 36); guard.Complete(); Check(guard.Verified);
        guard.Observe(1, 6); guard.Complete(); Check(guard.Verified);
        // A previously valid channel must not suppress an unsupported observation.
        Reject(() => guard.Observe(1, 0), PairingNetworkFailureReason.UnsupportedChannel, "unsupported-channel", 0, 1);
        return count;
    }
}
