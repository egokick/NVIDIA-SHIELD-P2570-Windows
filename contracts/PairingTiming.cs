// Match the discovery and connection windows in Microsoft's WCN sample.
// The supervisor and UI allow additional time for setup and bounded cleanup.
internal static class PairingTiming
{
    internal static readonly TimeSpan Discovery = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan Provisioning = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan WorkerDiscoveryBudget = Discovery + TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan WorkerPairBudget = Discovery + Provisioning + TimeSpan.FromSeconds(20);
    // Native WCN setup has one attempt, with time for preflight and cleanup.
    internal static readonly TimeSpan DiscoveryCommandBudget = WorkerDiscoveryBudget + TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PairCommandBudget = WorkerPairBudget + TimeSpan.FromSeconds(60);
}
