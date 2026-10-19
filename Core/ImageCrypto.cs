using System.Security.Cryptography;
using System.Text;

namespace DiskImager.Core;

/// <summary>How an image is encrypted (stored in its header; nothing here reveals the password).</summary>
public sealed class EncryptionInfo
{
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    /// <summary>HMAC of a fixed text with the derived key: tells a wrong password from a damaged file.</summary>
    public string KeyCheck { get; set; } = "";
}

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("That password doesn't open this backup.") { }
}

public sealed class ImageLockedException : Exception
{
    public ImageLockedException() : base("This backup is encrypted. Enter its password to use it.") { }
}

/// <summary>
/// Keys for an encrypted image set. The password goes through PBKDF2 (SHA-256) into two keys: one encrypts
/// each block with AES-256-GCM (random nonce; the block number and set ID are authenticated, so blocks can't
/// be swapped), the other makes the per-block checksums (HMAC), so checksums reveal nothing about the data.
/// Every image in a set (the full and its incrementals) uses the same salt and so the same keys.
/// </summary>
public sealed class ImageKey : IDisposable
{
    public const int DefaultIterations = 600_000;
    private const int NonceSize = 12, TagSize = 16;
    private static readonly byte[] CheckText = Encoding.ASCII.GetBytes("DiskImager key check v1");

    private readonly byte[] _enc, _mac;
    private readonly AesGcm _aes;

    public EncryptionInfo Info { get; }
    public static int Overhead => NonceSize + TagSize;

    private ImageKey(byte[] enc, byte[] mac, EncryptionInfo info)
    {
        _enc = enc;
        _mac = mac;
        Info = info;
        _aes = new AesGcm(_enc, TagSize);
    }

    /// <summary>New keys with a fresh salt (for a new full backup).</summary>
    public static ImageKey Create(string password, int iterations = DefaultIterations)
    {
        var info = new EncryptionInfo { Iterations = iterations, Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) };
        var key = Derive(password, info);
        info.KeyCheck = Convert.ToBase64String(HMACSHA256.HashData(key._mac, CheckText));
        return key;
    }

    /// <summary>Keys for an existing set; throws <see cref="WrongPasswordException"/> if the password doesn't match.</summary>
    public static ImageKey Open(string password, EncryptionInfo info)
    {
        var key = Derive(password, info);
        var check = HMACSHA256.HashData(key._mac, CheckText);
        if (!CryptographicOperations.FixedTimeEquals(check, Convert.FromBase64String(info.KeyCheck)))
        {
            key.Dispose();
            throw new WrongPasswordException();
        }
        return key;
    }

    private static ImageKey Derive(string password, EncryptionInfo info)
    {
        if (string.IsNullOrEmpty(password)) throw new WrongPasswordException();
        if (info.Algorithm != "AES-256-GCM" || info.Kdf != "PBKDF2-SHA256" || info.Iterations < 10_000)
            throw new InvalidDataException("This backup uses an encryption method this version of DiskImager doesn't support.");
        var both = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(info.Salt), info.Iterations, HashAlgorithmName.SHA256, 64);
        return new ImageKey(both[..32], both[32..], info);
    }

    /// <summary>Per-block checksum for encrypted images (first 16 bytes of an HMAC-SHA256).</summary>
    public byte[] Hash(byte[] data, int length) => HMACSHA256.HashData(_mac, data.AsSpan(0, length))[..16];

    private static byte[] Aad(long blockIndex, Guid chainId)
    {
        var aad = new byte[24];
        BitConverter.TryWriteBytes(aad, blockIndex);
        chainId.TryWriteBytes(aad.AsSpan(8));
        return aad;
    }

    /// <summary>nonce | ciphertext | tag</summary>
    public byte[] Seal(ReadOnlySpan<byte> plain, long blockIndex, Guid chainId)
    {
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        lock (_aes)
            _aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize), Aad(blockIndex, chainId));
        return output;
    }

    /// <summary>Decrypts a sealed block into <paramref name="plain"/>; returns its length. Throws if it was altered.</summary>
    public int Open(ReadOnlySpan<byte> sealedData, long blockIndex, Guid chainId, Span<byte> plain)
    {
        int len = sealedData.Length - NonceSize - TagSize;
        if (len < 0) throw new InvalidDataException("Encrypted block is too short.");
        try
        {
            lock (_aes)
                _aes.Decrypt(sealedData[..NonceSize], sealedData.Slice(NonceSize, len), sealedData.Slice(NonceSize + len, TagSize), plain[..len], Aad(blockIndex, chainId));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException($"Block {blockIndex} is damaged or was altered (encryption check failed).");
        }
        return len;
    }

    public void Dispose()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_enc);
        CryptographicOperations.ZeroMemory(_mac);
    }
}
