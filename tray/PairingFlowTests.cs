namespace ShieldController;

internal static class PairingFlowTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Pairing flow check failed: " + name);
        }
        static bool Throws<T>(Func<Task> operation) where T : Exception
        {
            try { operation().GetAwaiter().GetResult(); return false; }
            catch (T) { return true; }
        }
        static Capabilities Ready(bool paired = true, bool recovery = false, bool driver = true) =>
            new(paired, recovery, [new("driver", "Required driver", driver)], [], [], "wifi", "uplink");
        static PairingResult Find(FakeService service, Action<string>? progress = null) =>
            PairingFlow.FindAndPair(service, "wifi", "uplink", progress ?? (_ => { })).GetAwaiter().GetResult();

        Candidate first = new("first", "First controller"), second = new("second", "Second controller");
        var none = new FakeService([], Ready());
        PairingResult result = Find(none);
        Check(result.Choices.Length == 0 && result.Capabilities is null && none.Calls.SequenceEqual(["discover"]),
            "No result never pairs or checks a nonexistent pairing");

        var multiple = new FakeService([first, second], Ready());
        result = Find(multiple);
        Check(result.Choices.SequenceEqual([first, second]) && result.Capabilities is null && multiple.Calls.SequenceEqual(["discover"]),
            "Multiple controllers require a choice and never choose the first");

        var unique = new FakeService([first], Ready());
        var progress = new List<string>();
        result = Find(unique, progress.Add);
        Check(unique.Calls.SequenceEqual(["discover", "pair", "capabilities"]) && unique.PairedTarget == first,
            "One controller pairs its exact identity before checking durable state");
        Check(unique.Wifi == "wifi" && unique.Uplink == "uplink", "Selected adapters reach the service unchanged");
        Check(progress.SequenceEqual(["Searching", "Pairing"]), "Progress follows actual operations");
        Check(result.Choices.Length == 0 && ReferenceEquals(result.Capabilities, unique.Capabilities),
            "Successful pairing returns the new capability report");

        var selected = new FakeService([first, second], Ready());
        result = PairingFlow.PairSelected(selected, second, "wifi", "uplink", _ => { }).GetAwaiter().GetResult();
        Check(selected.PairedTarget == second && selected.Calls.SequenceEqual(["pair", "capabilities"]),
            "An explicit choice pairs only that controller without rediscovery");

        var discoveryFails = new FakeService([first], Ready()) { DiscoverError = new IOException("Discovery failed") };
        Check(Throws<IOException>(() => PairingFlow.FindAndPair(discoveryFails, "wifi", "uplink", _ => { })) &&
            discoveryFails.Calls.SequenceEqual(["discover"]), "Discovery failure cannot provision a controller");

        var pairingFails = new FakeService([first], Ready()) { PairError = new IOException("Pairing failed") };
        Check(Throws<IOException>(() => PairingFlow.FindAndPair(pairingFails, "wifi", "uplink", _ => { })) &&
            pairingFails.Calls.SequenceEqual(["discover", "pair"]), "Pair failure stops before capability checks");

        var capabilityFails = new FakeService([first], Ready()) { CapabilityError = new IOException("Check failed") };
        Check(Throws<IOException>(() => PairingFlow.FindAndPair(capabilityFails, "wifi", "uplink", _ => { })),
            "Failed capability checks do not invent a saved pairing");

        var notSaved = new FakeService([first], Ready(paired: false));
        Check(Throws<InvalidDataException>(() => PairingFlow.FindAndPair(notSaved, "wifi", "uplink", _ => { })),
            "A successful pair call without a durable receipt is rejected");

        Check(PairingFlow.CanStart(Ready()), "A valid pairing with ready requirements can start");
        Check(!PairingFlow.CanStart(null), "Missing capabilities cannot start");
        Check(!PairingFlow.CanStart(Ready(paired: false)), "An unpaired controller cannot start");
        Check(!PairingFlow.CanStart(Ready(recovery: true)), "Pending recovery prevents start");
        Check(!PairingFlow.CanStart(Ready(driver: false)), "A missing requirement prevents start");
        Check(!PairingFlow.CanStart(new(true, false, [], [], [], null, null)), "An empty requirements report cannot start");

        var needsRecovery = new FakeService([first], Ready(recovery: true));
        result = Find(needsRecovery);
        Check(result.Capabilities?.Paired == true && !PairingFlow.CanStart(result.Capabilities),
            "Pairing may be saved while recovery still prevents automatic connection");

        bool continueSetup = false;
        var alreadyClosing = new FakeService([first], Ready());
        Check(Throws<OperationCanceledException>(() => PairingFlow.FindAndPair(alreadyClosing, "wifi", "uplink", _ => { }, () => continueSetup)) &&
            alreadyClosing.Calls.Count == 0, "Closing before setup starts cannot launch discovery");

        continueSetup = true;
        var closingAfterDiscovery = new FakeService([first], Ready()) { AfterDiscovery = () => continueSetup = false };
        Check(Throws<OperationCanceledException>(() => PairingFlow.FindAndPair(closingAfterDiscovery, "wifi", "uplink", _ => { }, () => continueSetup)) &&
            closingAfterDiscovery.Calls.SequenceEqual(["discover"]), "Exit after discovery cannot start provisioning");

        continueSetup = true;
        var closingAfterPair = new FakeService([first], Ready()) { AfterPair = () => continueSetup = false };
        Check(Throws<OperationCanceledException>(() => PairingFlow.FindAndPair(closingAfterPair, "wifi", "uplink", _ => { }, () => continueSetup)) &&
            closingAfterPair.Calls.SequenceEqual(["discover", "pair"]), "Exit after pairing cannot start capability checks");
        return checks;
    }

    private sealed class FakeService(Candidate[] found, Capabilities capabilities) : IPairingService
    {
        internal List<string> Calls { get; } = [];
        internal Capabilities Capabilities { get; } = capabilities;
        internal Candidate? PairedTarget { get; private set; }
        internal string? Wifi { get; private set; }
        internal string? Uplink { get; private set; }
        internal Exception? DiscoverError { get; init; }
        internal Exception? PairError { get; init; }
        internal Exception? CapabilityError { get; init; }
        internal Action? AfterDiscovery { get; init; }
        internal Action? AfterPair { get; init; }

        public Task<Candidate[]> Discover(string wifi, string uplink)
        {
            Calls.Add("discover"); Wifi = wifi; Uplink = uplink;
            AfterDiscovery?.Invoke();
            return DiscoverError is null ? Task.FromResult(found) : Task.FromException<Candidate[]>(DiscoverError);
        }

        public Task Pair(Candidate target, string wifi, string uplink)
        {
            Calls.Add("pair"); PairedTarget = target; Wifi = wifi; Uplink = uplink;
            AfterPair?.Invoke();
            return PairError is null ? Task.CompletedTask : Task.FromException(PairError);
        }

        public Task<Capabilities> GetCapabilities()
        {
            Calls.Add("capabilities");
            return CapabilityError is null ? Task.FromResult(Capabilities) : Task.FromException<Capabilities>(CapabilityError);
        }
    }
}
