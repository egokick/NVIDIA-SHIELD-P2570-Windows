using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;

internal static class ChannelInspection
{
    internal sealed class QueryException(uint opcode, uint error, uint dataSize, bool dataPresent)
        : InvalidOperationException($"WLAN query {opcode} failed ({error}); size {dataSize}, data present {dataPresent}.")
    {
        internal uint Opcode { get; } = opcode;
        internal uint Error { get; } = error;
    }
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern uint WlanQueryInterface(nint handle, in Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint type);
    [DllImport("wlanapi.dll", ExactSpelling = true)] static extern void WlanFreeMemory(nint data);
    // Only active IEEE 802.11 interfaces can be GOs. Avoid WLAN RPCs for
    // Ethernet/VPN/filter interfaces, while checking every active wireless port.
    internal static Guid[] ActiveGroups() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
            Guid.TryParse(n.Id, out var id) && IsGroupOwner(id))
        .Select(n => Guid.Parse(n.Id)).ToArray();
    // WiFiCx drivers can give every port the physical radio's description.
    // Query the role rather than matching a vendor or localized display name.
    internal static bool IsGroupOwnerMode(int? mode) => mode == 0x20; // DOT11_OPERATION_MODE_WFD_GROUP_OWNER
    internal static bool IsGroupOwner(Guid id)
    {
        if (WlanOpenHandle(2, 0, out _, out nint handle) != 0) throw new InvalidOperationException("WLAN read handle unavailable.");
        try
        {
            uint error = WlanQueryInterface(handle, in id, 12 /* current_operation_mode */, 0, out uint size, out nint data, out _);
            try { return error == 0 && data != 0 && size == 4 && IsGroupOwnerMode(Marshal.ReadInt32(data)); }
            finally { if (data != 0) WlanFreeMemory(data); }
        }
        finally { WlanCloseHandle(handle, 0); }
    }
    internal static bool IsExpectedChannel(int channel) => ConnectionSettings.IsFiveGhzChannel(channel);
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
                    if (error != 0 || data == 0 || size != 4) throw new QueryException(opcode, error, size, data != 0);
                    return Marshal.ReadInt32(data);
                }
                finally { if (data != 0) WlanFreeMemory(data); }
            }
            if (Query(6) != 1) throw new InvalidOperationException("GO is not connected.");
            return Query(8);
        }
        finally { WlanCloseHandle(handle, 0); }
    }

    // WCN can expose an active GO while its channel query returns ERROR_INVALID_STATE.
    // Only pairing permits that unavailable observation; Read remains strict for input.
    internal static int? ReadPairing(Guid expectedGroup) => ReadPairing(expectedGroup, ActiveGroups, Read);
    private static int? ReadPairing(Guid expectedGroup, Func<Guid[]> activeGroups, Func<Guid, int> read)
    {
        void RequireSameGroup()
        {
            Guid[] groups = activeGroups();
            if (groups.Length != 1) throw new PairingNetworkException(groups.Length == 0 ? PairingNetworkFailureReason.NoGroup : PairingNetworkFailureReason.AmbiguousGroups, groupCount: groups.Length);
            if (groups[0] != expectedGroup) throw new PairingNetworkException(PairingNetworkFailureReason.NetworkChanged, groupCount: groups.Length);
        }
        RequireSameGroup();
        int? channel;
        try { channel = read(expectedGroup); }
        catch (QueryException ex) when (ex.Opcode == 8 && ex.Error == 5023) { channel = null; }
        RequireSameGroup();
        return channel;
    }

    internal static int PairingChannelSelfTest()
    {
        int count = 0;
        void Check(bool value) { count++; if (!value) throw new InvalidOperationException("Pairing channel observation check failed."); }
        Guid group = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Check(ReadPairing(group, () => [group], _ => 11) == 11);
        Check(ReadPairing(group, () => [group], _ => throw new QueryException(8, 5023, 0, false)) is null);
        foreach (var (opcode, error, size, data) in new (uint, uint, uint, bool)[] { (6, 5023, 0, false), (8, 5, 0, false), (8, 0, 0, false), (8, 0, 8, true) })
        {
            bool rejected = false;
            try { ReadPairing(group, () => [group], _ => throw new QueryException(opcode, error, size, data)); }
            catch (QueryException) { rejected = true; }
            Check(rejected);
        }
        foreach (Guid[] changed in new Guid[][] { [], [other], [group, other] })
        {
            int reads = 0; bool rejected = false;
            try { ReadPairing(group, () => ++reads == 1 ? [group] : changed, _ => throw new QueryException(8, 5023, 0, false)); }
            catch (PairingNetworkException) { rejected = true; }
            Check(rejected);
        }
        bool queried = false, missingRejected = false;
        try { ReadPairing(group, () => [], _ => { queried = true; return 36; }); }
        catch (PairingNetworkException) { missingRejected = true; }
        Check(missingRejected && !queried);
        return count;
    }
}
