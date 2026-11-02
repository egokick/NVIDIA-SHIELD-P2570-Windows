using System.Text.Json;

namespace ShieldController;

internal static class FirmwareTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value) { checks++; if (!value) throw new InvalidOperationException("Firmware check failed."); }
        const string known = "1.96/99/3.71/0.32", other = "1.91/81/3.71/0.32";
        Check(ControllerFirmware.IsValid(known) && ControllerFirmware.IsValid(other));
        foreach (string? invalid in new[] { null, "", "1.96", "1.96/999/3.71/0.32", "1.96/99/3.71/0.32\n", "v" + known,
            "0.00/0/0.00/0.00", "1.096/99/3.71/0.32", "1.96/99/3.71/-1.32", "1.96/99/3.71/0.32/0" })
            Check(!ControllerFirmware.IsValid(invalid));
        var model = new StatusModel();
        void Event(string? version, bool ready = true) {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { connected = true, transport_ready = ready, firmware_version = version }));
            model.ApplySession(document.RootElement);
        }
        Event(known); Check(model.FirmwareVersion is null);
        model.PairSucceeded(); Event(known); Check(model.FirmwareVersion == known);
        Event("private arbitrary metadata"); Check(model.FirmwareVersion is null);
        Event(known, ready: false); Check(model.FirmwareVersion is null);
        Event(other); Check(model.FirmwareVersion == other);
        model.SetDisconnected(); Check(model.FirmwareVersion is null);
        Event(known); model.Fail("disconnected"); Check(model.FirmwareVersion is null);
        Event(known); model.BeginPairing(); Check(model.FirmwareVersion is null);
        using var panel = new FirmwarePanel(() => { }, _ => throw new InvalidOperationException("No source link should open during tests."));
        Check(panel.InstalledText.Contains("Connect"));
        panel.UpdateStatus(known, true); Check(panel.InstalledText == "Installed: v" + known);
        panel.Select("observed-ozmo-81"); Check(panel.Selected.Version is null && panel.Selected.Notes.Contains("not a complete firmware version"));
        panel.UpdateStatus(known, true); Check(panel.Selected.Id == "observed-ozmo-81");
        panel.Select("tablet-3.0-update"); Check(panel.Selected.Notes.Contains("improved pairing"));
        panel.OpenEntry("tablet-3.0-update"); Check(panel.IsShowingDetails);
        panel.ShowOverview(); Check(!panel.IsShowingDetails);
        panel.UpdateStatus(other, true); panel.Select("detected"); Check(panel.Selected.Version == other);
        panel.UpdateStatus("2.01/90/3.71/0.32", true); Check(panel.Selected.Id == "detected" && panel.Selected.Version == "2.01/90/3.71/0.32");
        panel.UpdateStatus(null, false); Check(panel.Selected.Id == "published-1.96" && panel.InstalledText.Contains("Connect"));
        panel.UpdateStatus(null, true); Check(panel.InstalledText.Contains("unavailable"));
        panel.UpdateStatus(known, false); Check(panel.InstalledText.Contains("Connect"));
        Check(FirmwareCatalog.Entries.Where(entry => entry.Version is not null).All(entry => ControllerFirmware.IsValid(entry.Version)));
        Check(FirmwareCatalog.Entries.All(entry => new Uri(entry.SourceUrl).Scheme == "https"));
        return checks;
    }
}
