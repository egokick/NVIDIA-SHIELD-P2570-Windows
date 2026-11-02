using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ShieldController;

internal sealed record DisplayCalibration(int Width, int Height, double Dpi, double? DiagonalInches, string PhysicalSizeSource, double DefaultGain)
{
    internal string Title => (DiagonalInches is double inches ? inches.ToString("0.#", CultureInfo.CurrentCulture) + "″ · " : "") + $"{Width} × {Height}";
    internal string Detail => PhysicalSizeSource == "driver-reported"
        ? "Auto-calibrated to display size and resolution."
        : "Auto-calibrated to resolution; size estimated from Windows scaling.";

    internal static async Task<DisplayCalibration> ReadAsync()
    {
        string executable = Path.Combine(OwnerClient.Root, "bin", "data-host", "shield-controller-datahost.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--display-calibration");
        using var process = Process.Start(start) ?? throw new IOException("Display calibration could not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2));
            if (process.ExitCode != 0) throw new IOException("Display calibration was unavailable.");
            return Parse(await output);
        }
        catch
        {
            // This helper only queries display metrics; it owns no radio/session.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            throw;
        }
    }
    internal static DisplayCalibration Parse(string json)
    {
        if (json.Length > 16384) throw new InvalidDataException("Display calibration response is too large.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
        double dpi = root.GetProperty("dpi").GetDouble(), gain = root.GetProperty("default_gain").GetDouble();
        string source = root.GetProperty("physical_size_source").GetString() ?? "";
        double? diagonal = root.TryGetProperty("diagonal_inches", out var size) && size.ValueKind != JsonValueKind.Null ? size.GetDouble() : null;
        if (width is < 1 or > 65536 || height is < 1 or > 65536 || !double.IsFinite(dpi) || dpi is < 48 or > 960 || !double.IsFinite(gain) || gain <= 0 || gain > 100 ||
            (diagonal is double inches && (!double.IsFinite(inches) || inches is < 3 or > 300)) || source is not ("driver-reported" or "dpi-estimate"))
            throw new InvalidDataException("Display calibration values are invalid.");
        return new(width, height, dpi, diagonal, source, gain);
    }
}
