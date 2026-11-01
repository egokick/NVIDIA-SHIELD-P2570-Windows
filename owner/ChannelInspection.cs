using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Text;

internal static class ChannelInspection
{
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern uint WlanQueryInterface(nint handle, in Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint type);
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern void WlanFreeMemory(nint data);
    internal static Guid[] ActiveGroups() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && IsBaseGroup(n.Description))
        .Select(n => Guid.Parse(n.Id)).ToArray();
    internal static bool IsBaseGroup(string name) => Regex.IsMatch(name, @"\AMicrosoft Wi-Fi Direct Virtual Adapter(?: #\d+)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool IsExpectedChannel(int channel) => channel == 36;
    internal static bool SsidEquals(ReadOnlySpan<byte> attributes, string expected)
    {
        if (attributes.Length < 604 || BinaryPrimitives.ReadUInt32LittleEndian(attributes) != 1) throw new InvalidOperationException("Invalid connection attributes.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(attributes.Slice(520, 4));
        if (length > 32) throw new InvalidOperationException("Invalid SSID length.");
        return attributes.Slice(524, (int)length).SequenceEqual(Encoding.ASCII.GetBytes(expected));
    }
    internal static bool ReadSsidEquals(Guid id, string expected)
    {
        if (WlanOpenHandle(2, 0, out _, out nint handle) != 0) throw new InvalidOperationException("WLAN read handle unavailable.");
        try
        {
            uint error = WlanQueryInterface(handle, in id, 7, 0, out uint size, out nint data, out _);
            try
            {
                if (error != 0 || data == 0 || size < 604) throw new InvalidOperationException("GO connection attributes unavailable.");
                byte[] bytes = new byte[604]; Marshal.Copy(data, bytes, 0, bytes.Length);
                return SsidEquals(bytes, expected);
            }
            finally { if (data != 0) WlanFreeMemory(data); }
        }
        finally { WlanCloseHandle(handle, 0); }
    }
    internal static int Read(Guid id)
    {
        if (WlanOpenHandle(2, 0, out _, out nint handle) != 0) throw new InvalidOperationException("WLAN read handle unavailable.");
        try
        {
            int Query(uint opcode)
            {
                uint error = WlanQueryInterface(handle, in id, opcode, 0, out uint size, out nint data, out _);
                try
                {
                    if (error != 0 || data == 0 || size != 4) throw new InvalidOperationException($"WLAN query failed ({error}).");
                    return Marshal.ReadInt32(data);
                }
                finally { if (data != 0) WlanFreeMemory(data); }
            }
            if (Query(6) != 1) throw new InvalidOperationException("GO is not connected.");
            return Query(8);
        }
        finally { WlanCloseHandle(handle, 0); }
    }
}
