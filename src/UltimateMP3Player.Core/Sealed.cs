using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// The listening statistics as saved (a profile's all-time counts in stats.dat, the months of ListeningLog): compressed,
// encrypted and signed, so a text editor can't change the plays or the time (a changed file isn't read: its previous version
// is). In memory the counts are kept XORed with masks drawn at every start, so a memory editor searching for a play count or a
// time doesn't find them. Everything stays on the device: this makes cheating hard, not impossible.
public static class Sealed
{
    private static readonly byte[] Magic = "UMPS1"u8.ToArray();
    private static readonly byte[] EncKey = SHA256.HashData("Ultimate MP3 Player - listening statistics - encryption"u8);
    private static readonly byte[] MacKey = SHA256.HashData("Ultimate MP3 Player - listening statistics - signature"u8);
    private const int IvLength = 16, MacLength = 32;

    public static readonly int IntMask = RandomNumberGenerator.GetInt32(1, int.MaxValue);
    public static readonly long LongMask = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));

    public static int Mask(int value) => value ^ IntMask;
    public static long Mask(double value) => BitConverter.DoubleToInt64Bits(value) ^ LongMask;
    public static double Unmask(long value) => BitConverter.Int64BitsToDouble(value ^ LongMask);

    public static byte[] Protect(byte[] data)
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest)) gz.Write(data);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        using var aes = Aes.Create();
        aes.Key = EncKey;
        var cipher = aes.EncryptCbc(packed.ToArray(), iv);
        var body = new byte[Magic.Length + IvLength + cipher.Length];
        Magic.CopyTo(body, 0);
        iv.CopyTo(body, Magic.Length);
        cipher.CopyTo(body, Magic.Length + IvLength);
        return body.Concat(HMACSHA256.HashData(MacKey, body)).ToArray();
    }

    // Null: not one of these, damaged, or changed by hand.
    public static byte[]? Unprotect(byte[] file)
    {
        if (file.Length < Magic.Length + IvLength + MacLength || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        var body = file.AsSpan(0, file.Length - MacLength);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(MacKey, body), file.AsSpan(file.Length - MacLength))) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = EncKey;
            var packed = aes.DecryptCbc(body[(Magic.Length + IvLength)..], body.Slice(Magic.Length, IvLength));
            using var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gz.CopyTo(plain);
            return plain.ToArray();
        }
        catch (CryptographicException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    // Like JsonStore.Save: atomic, the previous version kept as .bak.
    public static void Save<T>(string path, T value)
    {
        if (JsonStore.Frozen) return;
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonStore.Options)));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
        else File.Move(tmp, path);
    }

    // The file, or its previous version when it can't be read (kept aside as .unreadable); null when neither is there or readable.
    public static T? Load<T>(string path) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            if (!File.Exists(p)) continue;
            try
            {
                if (Unprotect(File.ReadAllBytes(p)) is { } json && JsonSerializer.Deserialize<T>(json, JsonStore.Options) is { } value) return value;
            }
            catch (JsonException) { }
            catch (IOException) { }
            try { File.Copy(p, p + ".unreadable", true); } catch { }
        }
        return null;
    }
}
