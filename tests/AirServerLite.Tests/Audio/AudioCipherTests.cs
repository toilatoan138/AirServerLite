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
}
