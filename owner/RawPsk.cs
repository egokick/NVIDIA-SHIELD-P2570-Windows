using System.Security.Cryptography;
using System.Text;

internal static partial class Program
{
    // WPA2-Personal PSK for the SAME candidate SSID/passphrase. This changes only
    // the WCN profile key representation; it is not a new credential generator.
    private static string DeriveRawPsk(string passphrase, string ssid)
    {
        if (passphrase.Length is < 8 or > 63 || ssid.Length is < 1 or > 32 ||
            passphrase.Any(c => c < 32 || c > 126) || ssid.Any(c => c < 32 || c > 126))
            throw new InvalidOperationException("Expected exact printable ASCII WPA2 inputs.");
        byte[] passwordBytes = Encoding.ASCII.GetBytes(passphrase);
        byte[] ssidBytes = Encoding.ASCII.GetBytes(ssid);
        byte[]? key = null;
        try
        {
            key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, ssidBytes, 4096, HashAlgorithmName.SHA1, 32);
            return Convert.ToHexString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(ssidBytes);
            if (key != null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static int RawPskSelfTest()
    {
        int checks = 0;
        void Check(bool okay) { if (!okay) throw new InvalidOperationException("Raw PSK offline check failed."); ++checks; }
        // Public synthetic vectors independently checked with Python hashlib.
        Check(DeriveRawPsk("password", "IEEE") == "F42C6FC52DF0EBEF9EBB4B90B38A5F902E83FE1B135A70E23AED762E9710A12E");
        Check(DeriveRawPsk("ThisIsAPassword", "ThisIsASSID") == "0DC0D6EB90555ED6419756B9A15EC3E3209B63DF707DD508D14581F8982721AF");
        Check(DeriveRawPsk("password", "IEEE") != DeriveRawPsk("password", "ieee"));
        foreach (var input in new[] { ("short", "IEEE"), (new string('x', 64), "IEEE"), ("password", ""), ("password", new string('x', 33)), ("password", "DIRECT-\u00e9"), ("passw\u00f6rd", "IEEE") })
        {
            bool rejected = false;
            try { DeriveRawPsk(input.Item1, input.Item2); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
        }
        return checks;
    }
}
