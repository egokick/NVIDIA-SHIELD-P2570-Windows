internal static class ConnectionSettings
{
    internal const string FiveGhzRequirement = "Controller input currently only works on 5 GHz.";
    internal const string PairingBandExplanation = "Controller input uses 5 GHz. Windows may briefly use 2.4 GHz for pairing.";
    internal const string FiveGhzOnlyMessage = FiveGhzRequirement + " Windows could not verify a supported 5 GHz network for the controller.";
    internal const string SupportedChannelsMessage = "Supported 5 GHz channels: 36–48 and 149–165.";

    internal static FiveGhzFailureText DescribeFiveGhzFailure(string? reason, int? channel = null, bool supportsFive = false, bool pairing = true)
    {
        string network = pairing ? "pairing network" : "controller network";
        string heading = reason == "unsupported-hardware" ? "5 GHz required" : pairing ? "5 GHz pairing unavailable" : "5 GHz connection unavailable";
        string explanation = reason switch {
            "unsupported-hardware" => "Windows reports that this adapter cannot create a 5 GHz controller network.",
            "observed-2.4-ghz" => (supportsFive ? "Your adapter supports 5 GHz, but Windows created the " : "Windows created the ") + network + " on 2.4 GHz" + (channel is >= 1 and <= 14 ? $" (channel {channel})" : "") + ".",
            "unsupported-5-ghz-channel" when channel is >= 32 and <= 177 && !IsFiveGhzChannel(channel.Value) => $"Windows created the {network} on 5 GHz channel {channel}, which this app does not support.",
            "unsupported-5-ghz-channel" => $"Windows selected a 5 GHz channel for the {network} that this app does not support.",
            "unsupported-channel" => $"Windows selected an unsupported channel for the {network}.",
            "no-group" => "Windows did not expose a controller network whose channel the app could verify.",
            "ambiguous-groups" => "Windows exposed multiple controller networks, so the app could not verify which one to use.",
            "channel-query-timeout" => "Windows did not answer the channel check in time. The app could not verify the controller network's band.",
            _ => "The app could not read and verify the controller network's channel. This does not mean your adapter lacks 5 GHz."
        };
        return new(heading, explanation + " " + FiveGhzRequirement);
    }

    // WCN may use 2.4 GHz while provisioning credentials. Input is checked separately.
    internal static bool ValidChannel(int channel) => channel is >= 1 and <= 11 || IsFiveGhzChannel(channel);
    internal static bool IsPairingChannel(int channel) => ValidChannel(channel);
    internal static void RequirePairingChannel(int? channel, int? groupCount = null)
    {
        if (channel.HasValue && IsPairingChannel(channel.Value)) return;
        throw new PairingNetworkException(channel.HasValue ? PairingNetworkFailureReason.UnsupportedChannel : PairingNetworkFailureReason.ChannelUnavailable, channel, groupCount);
    }
    // P2570 FCC report NEI-FICP-3-1404C046 also certifies channels 149–165.
    internal static bool IsFiveGhzChannel(int channel) => channel is 36 or 40 or 44 or 48 or 149 or 153 or 157 or 161 or 165;
    internal static bool Matches(int channel, int band) => band == 5 && IsFiveGhzChannel(channel);
    internal static int Select(bool fiveSupported) => fiveSupported ? 5 : throw new FiveGhzRequiredException(FiveGhzFailureReason.UnsupportedHardware);
    internal static void RequireFiveGhzChannel(int? channel, int? groupCount = null)
    {
        if (channel.HasValue && IsFiveGhzChannel(channel.Value)) return;
        var reason = channel switch {
            null => FiveGhzFailureReason.ChannelUnavailable,
            >= 1 and <= 14 => FiveGhzFailureReason.TwoPointFourGhz,
            >= 32 and <= 177 => FiveGhzFailureReason.UnsupportedFiveGhzChannel,
            _ => FiveGhzFailureReason.UnsupportedChannel
        };
        throw new FiveGhzRequiredException(reason, channel, groupCount);
    }

    internal static int SelfTest()
    {
        int count = 0;
        void Check(bool value) { count++; if (!value) throw new InvalidOperationException("5 GHz policy check failed."); }
        Check(Select(true) == 5);
        bool rejected = false; try { Select(false); } catch (FiveGhzRequiredException ex) { rejected = ex.Reason == FiveGhzFailureReason.UnsupportedHardware && ex.ReasonCode == "unsupported-hardware" && ex.Channel is null && ex.GroupCount is null; } Check(rejected);
        foreach (int channel in new[] { 36, 40, 44, 48, 149, 153, 157, 161, 165 }) { RequireFiveGhzChannel(channel); Check(Matches(channel, 5)); }
        foreach (var (channel, reason) in new (int?, FiveGhzFailureReason)[] {
            (null, FiveGhzFailureReason.ChannelUnavailable), (0, FiveGhzFailureReason.UnsupportedChannel),
            (1, FiveGhzFailureReason.TwoPointFourGhz), (6, FiveGhzFailureReason.TwoPointFourGhz),
            (11, FiveGhzFailureReason.TwoPointFourGhz), (14, FiveGhzFailureReason.TwoPointFourGhz),
            (52, FiveGhzFailureReason.UnsupportedFiveGhzChannel), (64, FiveGhzFailureReason.UnsupportedFiveGhzChannel) }) {
            rejected = false;
            try { RequireFiveGhzChannel(channel, 1); }
            catch (FiveGhzRequiredException ex) { rejected = ex.Reason == reason && ex.Channel == channel && ex.GroupCount == 1; }
            Check(rejected);
        }
        Check(!Matches(6, 2) && !Matches(36, 2));
        Check(ValidChannel(6));
        foreach (int channel in new[] { 1, 6, 11, 36, 149, 165 }) { RequirePairingChannel(channel); Check(IsPairingChannel(channel)); }
        foreach (int? channel in new int?[] { null, 0, 12, 14, 64 }) {
            rejected = false;
            try { RequirePairingChannel(channel, 1); }
            catch (PairingNetworkException ex) { rejected = ex.Channel == channel && ex.GroupCount == 1 && ex.Reason == (channel.HasValue ? PairingNetworkFailureReason.UnsupportedChannel : PairingNetworkFailureReason.ChannelUnavailable); }
            Check(rejected);
        }
        return count;
    }
}

internal enum PairingNetworkFailureReason
{
    UnsupportedChannel, NoGroup, AmbiguousGroups, ChannelUnavailable, ChannelQueryTimedOut, NetworkChanged
}

internal sealed class PairingNetworkException(PairingNetworkFailureReason reason, int? channel = null, int? groupCount = null)
    : InvalidOperationException("Windows could not verify the temporary pairing network. " + ConnectionSettings.PairingBandExplanation)
{
    internal PairingNetworkFailureReason Reason { get; } = reason;
    internal int? Channel { get; } = channel;
    internal int? GroupCount { get; } = groupCount;
    internal string ReasonCode => Reason switch {
        PairingNetworkFailureReason.UnsupportedChannel => "unsupported-channel",
        PairingNetworkFailureReason.NoGroup => "no-group",
        PairingNetworkFailureReason.AmbiguousGroups => "ambiguous-groups",
        PairingNetworkFailureReason.ChannelUnavailable => "channel-unavailable",
        PairingNetworkFailureReason.ChannelQueryTimedOut => "channel-query-timeout",
        PairingNetworkFailureReason.NetworkChanged => "network-changed",
        _ => "unspecified"
    };
}

internal sealed record FiveGhzFailureText(string Heading, string Message);

internal enum FiveGhzFailureReason
{
    Unspecified, UnsupportedHardware, TwoPointFourGhz, UnsupportedFiveGhzChannel,
    UnsupportedChannel, NoGroup, AmbiguousGroups, ChannelUnavailable, ChannelQueryTimedOut
}

internal sealed class FiveGhzRequiredException(FiveGhzFailureReason reason = FiveGhzFailureReason.Unspecified, int? channel = null, int? groupCount = null)
    : InvalidOperationException(ConnectionSettings.FiveGhzOnlyMessage)
{
    internal FiveGhzFailureReason Reason { get; } = reason;
    internal int? Channel { get; } = channel;
    internal int? GroupCount { get; } = groupCount;
    internal string ReasonCode => Reason switch {
        FiveGhzFailureReason.UnsupportedHardware => "unsupported-hardware",
        FiveGhzFailureReason.TwoPointFourGhz => "observed-2.4-ghz",
        FiveGhzFailureReason.UnsupportedFiveGhzChannel => "unsupported-5-ghz-channel",
        FiveGhzFailureReason.UnsupportedChannel => "unsupported-channel",
        FiveGhzFailureReason.NoGroup => "no-group",
        FiveGhzFailureReason.AmbiguousGroups => "ambiguous-groups",
        FiveGhzFailureReason.ChannelUnavailable => "channel-unavailable",
        FiveGhzFailureReason.ChannelQueryTimedOut => "channel-query-timeout",
        _ => "unspecified"
    };
}
