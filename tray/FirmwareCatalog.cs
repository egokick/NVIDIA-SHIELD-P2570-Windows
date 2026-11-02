namespace ShieldController;

internal sealed record FirmwareEntry(string Id, string Title, string Evidence, string Notes, string SourceTitle, string SourceUrl, string? Version = null);

internal static class FirmwareCatalog
{
    // Curated offline references for the original Wi-Fi P2570 only. SHIELD TV
    // software versions and the 2017 Bluetooth controller are different products.
    internal static readonly IReadOnlyList<FirmwareEntry> Entries = Array.AsReadOnly(new[] {
        new FirmwareEntry("published-1.96", "v1.96/99/3.71/0.32", "Listed by NVIDIA",
            "NVIDIA lists this complete firmware version for the original SHIELD controller (2015). The slash-separated values make up one firmware version.\n\n" +
            "The cited release history does not describe changes specific to this controller version or establish its original release date. A detailed changelog is unavailable.\n\n" +
            "This listing alone does not establish whether it is newer than the firmware on your controller or improves compatibility with this app.",
            "NVIDIA release history", "https://www.nvidia.com/en-us/shield/software-update/", "1.96/99/3.71/0.32"),
        new FirmwareEntry("observed-ozmo-81", "Ozmo 81 · partial version", "Tested by this project",
            "This project's compatibility notes record successful wireless gamepad input with a P2570 reporting Ozmo firmware 81.\n\n" +
            "Only the Ozmo component was recorded. The remaining firmware values and the release date are unknown, so this is not a complete firmware version.\n\n" +
            "No NVIDIA changelog has been matched to this observation. Successful testing describes compatibility; it does not describe what this firmware release changed.",
            "Project compatibility notes", "https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/blob/main/docs/compatibility.md"),
        new FirmwareEntry("tablet-3.0-update", "2015 pairing update · version unlisted", "Changes documented by NVIDIA",
            "NVIDIA's SHIELD tablet software 3.0 notes announce a firmware update for the original controller with improved pairing and other optimizations.\n\n" +
            "The notes do not identify the controller's firmware version. This update cannot be assigned to either version listed above. SHIELD tablet software 3.0 is the tablet's version, not the controller's.\n\n" +
            "NVIDIA describes installing this controller update over Wi-Fi from the tablet's update notification.",
            "NVIDIA tablet 3.0 release notes", "https://support-shield.nvidia.com/tablet-release-notes/3.0/")
    });

    internal static FirmwareEntry Detected(string version) => new("detected", "v" + version, "Read from your controller",
        "This complete version was read from the controller during the current wireless connection.\n\n" +
        "It is not in this app's bundled version history. No verified changelog is available for it.\n\n" +
        "The history is incomplete. A version missing from the list does not mean that it is unsupported or that an update is required.",
        "NVIDIA controller update help", "https://nvidia.custhelp.com/app/answers/detail/a_id/4311/related/1", version);
}
