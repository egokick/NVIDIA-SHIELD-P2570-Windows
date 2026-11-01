using System.Text;

internal static class BoundedHistory
{
    private const long MaximumBytes = 1024 * 1024;
    internal static void Append(string path, string line)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Oversized operational metadata row.");
        if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumBytes)
            File.Move(path, path + ".previous", true);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }
}
