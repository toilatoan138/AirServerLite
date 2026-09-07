using System.Security.Cryptography;

namespace AirServerLite.AirPlay.Crypto;

/// <summary>
/// AES-128 in counter mode, implemented directly over an ECB block encryptor.
///
/// We do not use BouncyCastle's SicBlockCipher here on purpose: both AirPlay uses need
/// byte-granular streaming with a counter that survives across calls, and the mirror stream
/// additionally needs an explicit "discard the rest of the current keystream block" operation
/// that a BufferedBlockCipher does not expose. Forty lines of our own is clearer than fighting
/// the abstraction.
///
/// Note CTR is symmetric - Encrypt and Decrypt are the same operation.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _keystreamPos = 16; // 16 == exhausted, generate a fresh block on next use
    private readonly Aes _aes;

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes", nameof(key));
        if (iv.Length != 16) throw new ArgumentException("CTR IV must be 16 bytes", nameof(iv));

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.KeySize = 128;
        _aes.Key = key.ToArray();
        _ecb = _aes.CreateEncryptor();

        iv.CopyTo(_counter);
    }

    /// <summary>
    /// Drop the unused tail of the current keystream block so the next byte processed starts
    /// on a fresh block. The AirPlay mirror video stream requires this at every packet
    /// boundary; the RTSP control channel must NOT use it.
    /// </summary>
    public void StartFreshBlock() => _keystreamPos = 16;

    public void Process(byte[] data, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (_keystreamPos == 16)
            {
                _ecb.TransformBlock(_counter, 0, 16, _keystream, 0);
                IncrementCounter();
                _keystreamPos = 0;
            }
            data[offset + i] ^= _keystream[_keystreamPos++];
        }
    }

    public byte[] Process(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Process(copy, 0, copy.Length);
        return copy;
    }

    private void IncrementCounter()
    {
        // Big-endian increment over the full 16-byte block, matching OpenSSL's
        // AES_ctr128_encrypt, which is what every AirPlay reference implementation uses.
        for (var i = 15; i >= 0; i--)
            if (++_counter[i] != 0) break;
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }
}
