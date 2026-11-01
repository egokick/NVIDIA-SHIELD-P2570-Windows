using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

internal static partial class Program
{
    internal static bool ForgetAllowed(bool recoveryPending, int activeGroups, TetheringOperationalState state, uint clients) =>
        !recoveryPending && activeGroups == 0 && EmptyHotspot(state, clients);

    private static int Forget()
    {
        checkpoint = "forget-controller";
        if (File.Exists(PrivateFiles.Recovery) || File.Exists(SetupActive))
            throw new InvalidOperationException("Resolve cleanup before forgetting the controller.");
        string[] paths = [PrivateFiles.Config, ProvisionReceipt, Path.Combine(SetupRoot, "candidate.json"), Path.Combine(SetupRoot, "discovery.json")];
        int groups = ChannelInspection.ActiveGroups().Length;
        if (groups != 0) throw new InvalidOperationException("Stop all hotspots before forgetting.");
        var profile = NetworkInformation.GetInternetConnectionProfile() ?? NetworkInformation.GetConnectionProfiles().FirstOrDefault();
        if (profile is null)
        {
            // Nothing to delete is a successful no-op; otherwise require the
            // supported API to prove Off before touching any saved credentials.
            if (paths.Any(File.Exists)) throw new InvalidOperationException("Connect a network so Windows hotspot state can be checked.");
        }
        else
        {
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (!ForgetAllowed(false, groups, manager.TetheringOperationalState, manager.ClientCount))
                throw new InvalidOperationException("Stop the active hotspot before forgetting.");
        }
        foreach (string path in paths.Where(File.Exists))
        {
            PrivateFiles.RequirePrivate(Path.GetDirectoryName(path)!, true);
            PrivateFiles.RequirePrivate(path, false);
        }
        int removed = 0;
        foreach (string path in paths.Where(File.Exists)) { File.Delete(path); removed++; }
        if (paths.Any(File.Exists)) throw new IOException("Some saved controller state remains.");
        Log("pairing-forgotten", new { removed_files = removed, logs_preserved = true, controller_settings_changed = false, radio_mutations = 0 }, true);
        return 0;
    }
}
