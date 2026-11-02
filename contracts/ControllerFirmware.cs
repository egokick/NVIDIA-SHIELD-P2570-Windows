using System.Globalization;
using System.Text.RegularExpressions;

internal static class ControllerFirmware
{
    // Same decimal byte format used by SHIELD and the transport's decoder.
    internal static bool IsValid(string? version)
    {
        if (version is null || version.Length > 27 || !Regex.IsMatch(version,
            @"\A[0-9]{1,3}\.[0-9]{2,3}/[0-9]{1,3}/[0-9]{1,3}\.[0-9]{2,3}/[0-9]{1,3}\.[0-9]{2,3}\z")) return false;
        string[] values = version.Split(['.', '/']);
        if (!values.All(value => byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        byte[] parts = values.Select(value => byte.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return parts.Any(value => value != 0) && version == string.Create(CultureInfo.InvariantCulture,
            $"{parts[0]}.{parts[1]:00}/{parts[2]}/{parts[3]}.{parts[4]:00}/{parts[5]}.{parts[6]:00}");
    }
}
