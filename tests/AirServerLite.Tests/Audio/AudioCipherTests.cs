using System.Security.Cryptography;
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioCipherTests
{
    [Fact]
    public void AudioCipher_DecryptPacket_ZeroIvCbc_DecryptsAccurately()
    {
        var key = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var plaintext = new byte[32];
        for (int i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i * 7);

        // Encrypt with AES-CBC zero IV
        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();
            ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        using var cipher = new AudioCipher(key);
        var decrypted = new byte[32];
        cipher.Decrypt(ciphertext, 0, ciphertext.Length, decrypted, 0);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AudioCipher_DecryptPacket_CustomEiv_DecryptsAccurately()
    {
        var key = new byte[16] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10 };
        var eiv = new byte[16] { 0x3F, 0x16, 0x03, 0xAF, 0x18, 0x12, 0x72, 0x8F, 0x51, 0xAF, 0x5E, 0x4E, 0x11, 0x22, 0x33, 0x44 };
        var plaintext = new byte[48];
        for (int i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i ^ 0x5A);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = eiv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();
            ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        using var cipher = new AudioCipher(key, eiv);
        var decrypted = new byte[48];
        cipher.Decrypt(ciphertext, 0, ciphertext.Length, decrypted, 0);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AudioCipher_DecryptMultiplePackets_ResetsIvEachPacket()
    {
        var key = new byte[16] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00 };
        var eiv = new byte[16] { 0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C };
        var plaintext = new byte[32];
        for (int i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i + 1);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = eiv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();
            ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        using var cipher = new AudioCipher(key, eiv);

        // First packet
        var dec1 = new byte[32];
        cipher.Decrypt(ciphertext, 0, ciphertext.Length, dec1, 0);
        Assert.Equal(plaintext, dec1);

        // Second packet (must decrypt with IV reset to eiv, not continued CBC state)
        var dec2 = new byte[32];
        cipher.Decrypt(ciphertext, 0, ciphertext.Length, dec2, 0);
        Assert.Equal(plaintext, dec2);
    }
}
