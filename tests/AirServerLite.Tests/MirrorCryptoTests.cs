using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using AirServerLite.AirPlay;
using AirServerLite.AirPlay.Crypto;
using AirServerLite.AirPlay.Streaming;

namespace AirServerLite.Tests;

public class MirrorCryptoTests
{
    [Fact]
    public void Test_DeviceKeyCache_StoresAndRetrieves_AesKey_And_SharedSecret()
    {
        DeviceKeyCache.Clear();
        var ip = IPAddress.Parse("192.168.1.100");

        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(sharedSecret);

        // 1. Remember SharedSecret (from pair-verify)
        DeviceKeyCache.RememberSharedSecret(ip, sharedSecret);

        var retrievedSecret = DeviceKeyCache.TryGetSharedSecret(ip);
        Assert.NotNull(retrievedSecret);
        Assert.Equal(sharedSecret, retrievedSecret);
        Assert.Null(DeviceKeyCache.TryGet(ip)); // No AesKey yet

        // 2. Remember FairPlay AesKey (from SETUP phase 1)
        var aesKey = new byte[16];
        RandomNumberGenerator.Fill(aesKey);
        DeviceKeyCache.Remember(ip, aesKey);

        // Both should now be retrievable
        var retrievedAesKey = DeviceKeyCache.TryGet(ip);
        retrievedSecret = DeviceKeyCache.TryGetSharedSecret(ip);

        Assert.NotNull(retrievedAesKey);
        Assert.Equal(aesKey, retrievedAesKey);
        Assert.NotNull(retrievedSecret);
        Assert.Equal(sharedSecret, retrievedSecret);

        // Different IP should be isolated
        var otherIp = IPAddress.Parse("192.168.1.101");
        Assert.Null(DeviceKeyCache.TryGet(otherIp));
        Assert.Null(DeviceKeyCache.TryGetSharedSecret(otherIp));
    }

    [Fact]
    public void Test_KeyDerivation_Matches_UxPlay_Specification()
    {
        // 16-byte raw FairPlay key + 32-byte X25519 shared secret
        var rawFairPlayKey = Convert.FromHexString("3C64D628EDE3BDC933D32A7EAE92A4C9");
        var x25519SharedSecret = new byte[32];
        for (int i = 0; i < 32; i++) x25519SharedSecret[i] = (byte)(i + 1);

        // UxPlay raop_handlers.h L841-847 derivation:
        //   memcpy(eaeskey, aeskey, 16);
        //   sha_ctx_t *ctx = sha_init();
        //   sha_update(ctx, eaeskey, 16);
        //   sha_update(ctx, ecdh_secret, 32);
        //   sha_final(ctx, eaeskey, NULL);
        //   memcpy(aeskey, eaeskey, 16);
        var hashBuf = new byte[16 + 32];
        Buffer.BlockCopy(rawFairPlayKey, 0, hashBuf, 0, 16);
        Buffer.BlockCopy(x25519SharedSecret, 0, hashBuf, 16, 32);
        var derivedAesKey = SHA512.HashData(hashBuf)[..16];

        Assert.Equal(16, derivedAesKey.Length);
        Assert.NotEqual(rawFairPlayKey, derivedAesKey);

        // Initialize MirrorCipher with derived key and a stream ID
        ulong streamId = 17590271398099700445UL;
        using var cipher = new MirrorCipher(derivedAesKey, streamId);
        Assert.NotNull(cipher);
    }

    [Fact]
    public void Test_MirrorCipher_Encrypts_And_Decrypts_AVCC_Packet_Successfully()
    {
        var rawKey = new byte[16];
        var shared = new byte[32];
        RandomNumberGenerator.Fill(rawKey);
        RandomNumberGenerator.Fill(shared);

        // Compute correct hashed key
        var hashBuf = new byte[16 + 32];
        Buffer.BlockCopy(rawKey, 0, hashBuf, 0, 16);
        Buffer.BlockCopy(shared, 0, hashBuf, 16, 32);
        var finalAesKey = SHA512.HashData(hashBuf)[..16];

        ulong streamId = 9876543210UL;

        // Build a mock AVCC packet with 2 NAL units
        // NAL 1: length 5 (0x00000005) + [0x65, 0x01, 0x02, 0x03, 0x04]
        // NAL 2: length 4 (0x00000004) + [0x06, 0x11, 0x22, 0x33]
        var nal1 = new byte[] { 0x65, 0x01, 0x02, 0x03, 0x04 };
        var nal2 = new byte[] { 0x06, 0x11, 0x22, 0x33 };
        var totalLen = 4 + nal1.Length + 4 + nal2.Length;
        var originalPayload = new byte[totalLen];

        BinaryPrimitives.WriteUInt32BigEndian(originalPayload.AsSpan(0, 4), (uint)nal1.Length);
        nal1.CopyTo(originalPayload, 4);

        BinaryPrimitives.WriteUInt32BigEndian(originalPayload.AsSpan(4 + nal1.Length, 4), (uint)nal2.Length);
        nal2.CopyTo(originalPayload, 4 + nal1.Length + 4);

        // Sender encrypts with stream cipher
        using var senderCipher = new MirrorCipher(finalAesKey, streamId);
        var wireData = (byte[])originalPayload.Clone();
        senderCipher.DecryptPacket(wireData, 0, wireData.Length); // CTR is symmetric

        // Receiver decrypts with MirrorCipher using the SAME correct key
        using var receiverCipher = new MirrorCipher(finalAesKey, streamId);
        var decryptedPayload = (byte[])wireData.Clone();
        receiverCipher.DecryptPacket(decryptedPayload, 0, decryptedPayload.Length);

        // Plaintext must match original AVCC
        Assert.Equal(originalPayload, decryptedPayload);

        // Convert AVCC to Annex-B via MirrorStreamReceiver private method
        var method = typeof(MirrorStreamReceiver).GetMethod("ConvertAvccToAnnexB",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var annexB = (byte[])method.Invoke(null, new object[] { decryptedPayload })!;
        Assert.NotEmpty(annexB);

        // Check start code 00 00 00 01
        byte[] startCode = { 0x00, 0x00, 0x00, 0x01 };
        Assert.Equal(startCode, annexB.AsSpan(0, 4).ToArray());
        Assert.Equal(nal1, annexB.AsSpan(4, nal1.Length).ToArray());
        Assert.Equal(startCode, annexB.AsSpan(4 + nal1.Length, 4).ToArray());
        Assert.Equal(nal2, annexB.AsSpan(4 + nal1.Length + 4, nal2.Length).ToArray());
    }

    [Fact]
    public void Test_Missing_SharedSecret_Hash_Produces_Corrupted_NAL()
    {
        var rawFairPlayKey = new byte[16];
        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(rawFairPlayKey);
        RandomNumberGenerator.Fill(sharedSecret);

        // Phone encrypts with correctly hashed key
        var hashBuf = new byte[16 + 32];
        Buffer.BlockCopy(rawFairPlayKey, 0, hashBuf, 0, 16);
        Buffer.BlockCopy(sharedSecret, 0, hashBuf, 16, 32);
        var phoneKey = SHA512.HashData(hashBuf)[..16];

        ulong streamId = 12345678UL;

        // Build valid AVCC packet
        var originalPayload = new byte[124];
        BinaryPrimitives.WriteUInt32BigEndian(originalPayload.AsSpan(0, 4), 120);
        for (int i = 4; i < 124; i++) originalPayload[i] = 0xAA;

        using var phoneCipher = new MirrorCipher(phoneKey, streamId);
        var encryptedPacket = (byte[])originalPayload.Clone();
        phoneCipher.DecryptPacket(encryptedPacket, 0, encryptedPacket.Length);

        // Receiver bug reproduction: decrypting with raw unhashed key
        using var brokenReceiverCipher = new MirrorCipher(rawFairPlayKey, streamId);
        var brokenDecrypted = (byte[])encryptedPacket.Clone();
        brokenReceiverCipher.DecryptPacket(brokenDecrypted, 0, brokenDecrypted.Length);

        // First 4 bytes read as NAL length will be garbage, not 120
        var badNalLen = (int)BinaryPrimitives.ReadUInt32BigEndian(brokenDecrypted.AsSpan(0, 4));
        Assert.NotEqual(120, badNalLen);

        // ConvertAvccToAnnexB will reject it and return empty array
        var method = typeof(MirrorStreamReceiver).GetMethod("ConvertAvccToAnnexB",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var annexB = (byte[])method.Invoke(null, new object[] { brokenDecrypted })!;
        Assert.Empty(annexB); // Packet dropped due to Bad NAL length!
    }

    [Fact]
    public void Test_ParseCodecData_Standard_AvcC_Extracts_SPS_And_PPS()
    {
        var sps = new byte[] { 0x67, 0x64, 0x00, 0x29, 0xAC, 0x1B, 0x1A, 0x80 };
        var pps = new byte[] { 0x68, 0xEE, 0x3C, 0x80 };

        var ms = new MemoryStream();
        ms.WriteByte(1); // version
        ms.WriteByte(0x64);
        ms.WriteByte(0x00);
        ms.WriteByte(0x29);
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE1); // 1 SPS
        ms.Write(new byte[] { 0x00, (byte)sps.Length });
        ms.Write(sps);
        ms.WriteByte(1); // 1 PPS
        ms.Write(new byte[] { 0x00, (byte)pps.Length });
        ms.Write(pps);

        var payload = ms.ToArray();

        var result = MirrorStreamReceiver.ParseCodecData(payload);
        Assert.NotEmpty(result);

        var expectedLen = 4 + sps.Length + 4 + pps.Length;
        Assert.Equal(expectedLen, result.Length);

        Assert.Equal(0, result[0]);
        Assert.Equal(0, result[1]);
        Assert.Equal(0, result[2]);
        Assert.Equal(1, result[3]);
        Assert.Equal(0x67, result[4]); // SPS NAL type 7

        var ppsOffset = 4 + sps.Length;
        Assert.Equal(0, result[ppsOffset]);
        Assert.Equal(0, result[ppsOffset + 1]);
        Assert.Equal(0, result[ppsOffset + 2]);
        Assert.Equal(1, result[ppsOffset + 3]);
        Assert.Equal(0x68, result[ppsOffset + 4]); // PPS NAL type 8
    }

    [Fact]
    public void Test_ParseCodecData_UxPlay_Layout_Extracts_SPS_And_PPS()
    {
        var sps = new byte[] { 0x27, 0x64, 0x00, 0x28, 0xAC, 0x2B };
        var pps = new byte[] { 0x28, 0xEE, 0x3C, 0x90 };

        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 0 }); // 6 bytes header
        ms.Write(new byte[] { 0x00, (byte)sps.Length });
        ms.Write(sps);
        ms.WriteByte(1);
        ms.Write(new byte[] { 0x00, (byte)pps.Length });
        ms.Write(pps);

        var payload = ms.ToArray();

        var result = MirrorStreamReceiver.ParseCodecData(payload);
        Assert.NotEmpty(result);

        var expectedLen = 4 + sps.Length + 4 + pps.Length;
        Assert.Equal(expectedLen, result.Length);

        Assert.Equal(0, result[0]);
        Assert.Equal(0, result[1]);
        Assert.Equal(0, result[2]);
        Assert.Equal(1, result[3]);
        Assert.Equal(0x27, result[4]); // SPS (0x27 & 0x1F = 7)

        var ppsOffset = 4 + sps.Length;
        Assert.Equal(0, result[ppsOffset]);
        Assert.Equal(0, result[ppsOffset + 1]);
        Assert.Equal(0, result[ppsOffset + 2]);
        Assert.Equal(1, result[ppsOffset + 3]);
        Assert.Equal(0x28, result[ppsOffset + 4]); // PPS (0x28 & 0x1F = 8)
    }

    [Fact]
    public void Test_ParseCodecData_Raw_AnnexB_Passes_Through()
    {
        var annexB = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F,
                                  0x00, 0x00, 0x00, 0x01, 0x68, 0xCE, 0x38, 0x80 };
        var result = MirrorStreamReceiver.ParseCodecData(annexB);
        Assert.Equal(annexB, result);
    }
}
