using System.Security.Cryptography;

namespace AirServerLite.Audio;

/// <summary>
/// Handles AirPlay audio packet decryption.
/// Per the AirPlay / RAOP protocol specification (e.g. UxPlay raop_buffer.c),
/// RTP audio payload is encrypted using AES-128-CBC with the 16-byte session AES key.
/// Each packet resets the CBC initialization vector (IV) to all zeros.
/// Trailing bytes not aligned to a 16-byte boundary are transmitted in the clear.
///
/// Optimized: Uses .NET 8 span-based Aes.DecryptCbc() to eliminate per-packet
/// ICryptoTransform allocation (~50μs overhead removed per packet).
/// </summary>
public sealed class AudioCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _iv;
    private bool _disposed;

    public AudioCipher(byte[] aesKey, byte[]? iv = null)
    {
        if (aesKey == null || aesKey.Length != 16)
            throw new ArgumentException("Session AES key must be exactly 16 bytes", nameof(aesKey));

        _iv = (iv != null && iv.Length == 16) ? (byte[])iv.Clone() : new byte[16];
        _aes = Aes.Create();
        _aes.Key = aesKey;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
    }

    public void Decrypt(byte[] input, int inputOffset, int count, byte[] output, int outputOffset)
    {
        if (_disposed || count <= 0) return;

        var encryptedLen = (count / 16) * 16;
        var remainder = count - encryptedLen;

        if (encryptedLen > 0)
        {
            // .NET 8 span-based API: no ICryptoTransform allocation, IV is reset internally per call.
            // This eliminates the ~50μs CreateDecryptor() overhead on every single audio packet.
            var ciphertext = input.AsSpan(inputOffset, encryptedLen);
            var destination = output.AsSpan(outputOffset, encryptedLen);
            _aes.DecryptCbc(ciphertext, _iv, destination, PaddingMode.None);
        }

        if (remainder > 0)
        {
            Buffer.BlockCopy(input, inputOffset + encryptedLen, output, outputOffset + encryptedLen, remainder);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aes.Dispose();
    }
}
