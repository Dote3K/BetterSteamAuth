using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace BetterSteamAuth.Core.Security;

/// <summary>Thrown when a password or passkey doesn't unlock the data.</summary>
public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("Wrong password.")
    {
    }

    public WrongPasswordException(string message) : base(message)
    {
    }

    public WrongPasswordException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A data key wrapped (encrypted) with a password-derived key.</summary>
public sealed record WrappedKey(byte[] Salt, byte[] Nonce, byte[] Ciphertext, KdfParameters Kdf);

/// <summary>
/// Two-level vault encryption: password ─Argon2id→ KEK ─AES-GCM→ DEK, and the DEK
/// encrypts each maFile with AES-256-GCM.
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int SaltSize = 16;
    public const int NonceSize = 12; // AesGcm.NonceByteSizes.MaxSize
    public const int TagSize = 16;   // AesGcm.TagByteSizes.MaxSize

    /// <summary>Creates a new random data key.</summary>
    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Derives the key-encryption key from a password.</summary>
    public static byte[] DeriveKey(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, KdfParameters kdf)
    {
        ArgumentNullException.ThrowIfNull(kdf);

        byte[] passwordBytes = new byte[Encoding.UTF8.GetByteCount(password)];
        try
        {
            Encoding.UTF8.GetBytes(password, passwordBytes);
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt.ToArray(),
                MemorySize = kdf.MemoryKiB,
                Iterations = kdf.Iterations,
                DegreeOfParallelism = kdf.Parallelism,
            };
            return argon.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>Wraps <paramref name="dataKey"/> with a key derived from <paramref name="password"/>.</summary>
    public static WrappedKey WrapDataKey(ReadOnlySpan<byte> dataKey, ReadOnlySpan<char> password, KdfParameters kdf)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] kek = DeriveKey(password, salt, kdf);
        try
        {
            var (nonce, ciphertext) = Seal(kek, dataKey, associatedData: salt);
            return new WrappedKey(salt, nonce, ciphertext, kdf);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Recovers the data key. Throws <see cref="WrongPasswordException"/> on a wrong password.</summary>
    public static byte[] UnwrapDataKey(WrappedKey wrapped, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(wrapped);

        byte[] kek = DeriveKey(password, wrapped.Salt, wrapped.Kdf);
        try
        {
            return Open(kek, wrapped.Nonce, wrapped.Ciphertext, associatedData: wrapped.Salt);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new WrongPasswordException("Wrong password.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Encrypts with AES-256-GCM. Returns the nonce and ciphertext‖tag.</summary>
    public static (byte[] Nonce, byte[] Ciphertext) Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] output = new byte[plaintext.Length + TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), associatedData);
        return (nonce, output);
    }

    /// <summary>Decrypts ciphertext‖tag. Throws <see cref="AuthenticationTagMismatchException"/> if tampered.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ciphertext.Length < TagSize)
        {
            throw new AuthenticationTagMismatchException();
        }

        int length = ciphertext.Length - TagSize;
        byte[] plaintext = new byte[length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext[..length], ciphertext[length..], plaintext, associatedData);
        return plaintext;
    }
}
