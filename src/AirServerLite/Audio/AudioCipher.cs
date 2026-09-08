using System.Security.Cryptography;

namespace AirServerLite.Audio;

/// <summary>
/// Handles AirPlay audio packet decryption.
/// Per the AirPlay / RAOP protocol specification (e.g. UxPlay raop_buffer.c),
/// RTP audio payload is encrypted using AES-128-CBC with the 16-byte session AES key.
/// Each packet resets the CBC initialization vector (IV) to all zeros.
/// Trailing bytes not aligned to a 16-byte boundary are transmitted in the clear.
/// </summary>
public sealed class AudioCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _zeroIv = new byte[16];
    private ICryptoTransform _decryptor;
    private bool _disposed;

    public AudioCipher(byte[] aesKey)
    {
        if (aesKey == null || aesKey.Length != 16)
            throw new ArgumentException("Session AES key must be exactly 16 bytes", nameof(aesKey));

        _aes = Aes.Create();
        _aes.Key = aesKey;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _aes.IV = _zeroIv;
        _decryptor = _aes.CreateDecryptor(_aes.Key, _zeroIv);
    }

    public void Decrypt(byte[] input, int inputOffset, int count, byte[] output, int outputOffset)
    {
        if (_disposed || count <= 0) return;

        var encryptedLen = (count / 16) * 16;
        var remainder = count - encryptedLen;

        if (encryptedLen > 0)
        {
            // Reset IV to zeros for each packet
            _decryptor.Dispose();
            _decryptor = _aes.CreateDecryptor(_aes.Key, _zeroIv);
            _decryptor.TransformBlock(input, inputOffset, encryptedLen, output, outputOffset);
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
        _decryptor.Dispose();
        _aes.Dispose();
    }
}
