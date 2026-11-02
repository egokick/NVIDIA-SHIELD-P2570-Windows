using System.Text.Json;

namespace ShieldController;

internal static class CrashDiagnostics
{
    internal static string? Record(Exception error, string checkpoint) => Record(error, checkpoint, OwnerClient.Root);

    internal static string? Record(Exception error, string checkpoint, string dataRoot)
    {
        try
        {
            string directory = Path.Combine(dataRoot, "logs");
            OwnerClient.CreatePrivateDirectory(dataRoot);
            OwnerClient.CreatePrivateDirectory(directory);
            string path = Path.Combine(directory, $"tray-error-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                checkpoint,
                process_id = Environment.ProcessId,
                type = error.GetType().FullName,
                hresult = $"0x{error.HResult:X8}",
                // Native messages may contain identifiers or credentials. Keep
                // exception types, HRESULTs and managed call sites only.
                exceptions = Details(error)
            }, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            try { Console.Error.WriteLine(JsonSerializer.Serialize(new { phase = "tray-error", checkpoint, type = error.GetType().FullName, hresult = $"0x{error.HResult:X8}", diagnostic_write_error = logError.GetType().Name })); } catch { }
            return null;
        }
    }

    private static object[] Details(Exception error)
    {
        var details = new List<object>();
        for (Exception? current = error; current is not null && details.Count < 8; current = current.InnerException)
            details.Add(new { type = current.GetType().FullName, hresult = $"0x{current.HResult:X8}", stack_trace = current.StackTrace });
        return details.ToArray();
    }
}
