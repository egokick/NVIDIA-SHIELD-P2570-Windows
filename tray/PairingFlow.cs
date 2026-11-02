namespace ShieldController;

internal interface IPairingService
{
    Task<Candidate[]> Discover(string wifi, string uplink);
    Task Pair(Candidate target, string wifi, string uplink);
    Task<Capabilities> GetCapabilities();
}

internal sealed record PairingResult(Candidate[] Choices, Capabilities? Capabilities);

// The user asks to pair a flashing controller once. Discovery may choose it
// automatically only when there is exactly one verified result.
internal static class PairingFlow
{
    internal static async Task<PairingResult> FindAndPair(
        IPairingService service, string wifi, string uplink, Action<string> progress, Func<bool>? continueSetup = null)
    {
        RequireContinuation(continueSetup);
        progress("Searching");
        Candidate[] found = await service.Discover(wifi, uplink);
        return found.Length == 1
            ? await PairSelected(service, found[0], wifi, uplink, progress, continueSetup)
            : new(found, null);
    }

    internal static async Task<PairingResult> PairSelected(
        IPairingService service, Candidate target, string wifi, string uplink, Action<string> progress, Func<bool>? continueSetup = null)
    {
        RequireContinuation(continueSetup);
        progress("Pairing");
        await service.Pair(target, wifi, uplink);
        RequireContinuation(continueSetup);
        Capabilities capabilities = await service.GetCapabilities();
        if (!capabilities.Paired)
            throw new InvalidDataException("Pairing was not saved. Try pairing the controller again.");
        return new([], capabilities);
    }

    private static void RequireContinuation(Func<bool>? continueSetup)
    {
        // Exit waits for an active command to finish its own cleanup. It must
        // also prevent this flow from starting another command afterwards.
        if (continueSetup?.Invoke() == false) throw new OperationCanceledException("Setup is closing.");
    }

    // A successful provisioning callback alone is not permission to start.
    // Require the durable pairing and a complete, nonempty requirements report.
    internal static bool CanStart(Capabilities? capabilities) =>
        capabilities is { Paired: true, RecoveryRequired: false } &&
        capabilities.Requirements.Length > 0 &&
        capabilities.Requirements.All(requirement => requirement.Available);
}
