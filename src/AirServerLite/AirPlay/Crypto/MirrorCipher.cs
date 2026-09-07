using System.Security.Cryptography;
using System.Text;
using AirServerLite.Core;

namespace AirServerLite.AirPlay.Crypto;

/// <summary>
/// Decrypts the H.264 payloads on the mirroring data connection.
///
/// Key schedule, verified against UxPlay's lib/mirror_buffer.c (mirror_buffer_init_aes)
/// and lib/raop_handlers.h (lines 841-847):
///
///   fairplayAesKey = SHA-512( decryptedEKey[16] || x25519SharedSecret[32] )[0..15]
///   keyString      = "AirPlayStreamKey" + decimal(streamConnectionID)
///   ivString       = "AirPlayStreamIV"  + decimal(streamConnectionID)
///   aesKey         = SHA-512( keyString || fairplayAesKey[16] )[0..15]
///   aesIv          = SHA-512( ivString  || fairplayAesKey[16] )[0..15]
///
/// Notice that in UxPlay, the FairPlay AES key (recovered from ekey) is hashed with the
/// X25519 pair-verify shared secret in raop_handlers.h BEFORE being passed to mirror_buffer.
///
/// streamConnectionID arrives as a signed 64-bit integer in the SETUP plist and must be
/// formatted as an *unsigned* decimal string.
///
/// PACKET FRAMING IS NOT PER-PACKET-INDEPENDENT. This is the part that is easy to get wrong
/// by "reasonable-sounding" assumption rather than by reading the reference: video payloads
/// are essentially never an exact multiple of 16 bytes, so every packet has a ragged tail.
/// That tail is NOT sent in the clear - it IS encrypted, using one more keystream block that
/// continues the running counter, and only the bytes this packet needs are consumed from it.
/// The UNUSED remainder of that same keystream block is then reused to decrypt the OPENING
/// bytes of the NEXT packet, before the counter jumps to a fresh block boundary for that
/// packet's own whole-block portion. Skipping this carry-over (as an earlier version of this
/// file did, on the assumption that ragged tails are plaintext) desynchronises the keystream
/// from the very first packet onward, which is why every single packet failed to decrypt.
///
/// Mirrors mirror_buffer_decrypt() in the reference exactly; see the file for the C original.
/// </summary>
public sealed class MirrorCipher : IDisposable
{
    private const string Tag = "mirror-aes";

    private readonly AesCtr _ctr;

    // Reference names: `og` (leftover keystream/decrypted-tail scratch buffer) and
    // `nextDecryptCount` (how many bytes at the tail of _leftover are unused keystream,
    // waiting to open the next packet).
    private readonly byte[] _leftover = new byte[16];
    private int _pendingCount;

    public MirrorCipher(byte[] fairPlayAesKey, ulong streamConnectionId)
    {
        if (fairPlayAesKey.Length != 16)
            throw new ArgumentException("AES key must be 16 bytes", nameof(fairPlayAesKey));

        var idText = streamConnectionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = Hash16("AirPlayStreamKey" + idText, fairPlayAesKey);
        var iv = Hash16("AirPlayStreamIV" + idText, fairPlayAesKey);

        Log.Debug(Tag, $"streamConnectionID={idText} key={Convert.ToHexString(key)} " +
                       $"iv={Convert.ToHexString(iv)}");
        Log.Debug(Tag, $"fairPlayAesKey used for derivation (16B): {Convert.ToHexString(fairPlayAesKey)}");

        _ctr = new AesCtr(key, iv);
    }

    private static byte[] Hash16(string prefix, byte[] fairPlayAesKey)
    {
        var p = Encoding.ASCII.GetBytes(prefix);
        var buf = new byte[p.Length + fairPlayAesKey.Length];
        Buffer.BlockCopy(p, 0, buf, 0, p.Length);
        Buffer.BlockCopy(fairPlayAesKey, 0, buf, p.Length, fairPlayAesKey.Length);
        return SHA512.HashData(buf)[..16];
    }

    /// <summary>
    /// Decrypt one video packet in place. Must be called strictly in wire order for a given
    /// connection - the keystream carry-over between calls makes this stateful, not a pure
    /// per-buffer transform.
    /// </summary>
    public void DecryptPacket(byte[] payload, int offset, int length)
    {
        // Step 1: finish the previous packet's business - consume whatever keystream bytes
        // its ragged tail left over, to decrypt the opening of THIS packet.
        if (_pendingCount > 0)
        {
            var leftoverStart = 16 - _pendingCount;
            for (var i = 0; i < _pendingCount; i++)
                payload[offset + i] ^= _leftover[leftoverStart + i];
        }

        // Step 2: whole 16-byte blocks, freshly aligned, ordinary CTR.
        var encryptLen = ((length - _pendingCount) / 16) * 16;
        _ctr.StartFreshBlock();
        _ctr.Process(payload, offset + _pendingCount, encryptLen);

        // Step 3: the ragged tail. Decrypt it with one more keystream block - deliberately
        // NOT calling StartFreshBlock here, so the counter continues sequentially from the
        // whole-block portion above (which landed exactly on a block boundary already).
        var restLen = (length - _pendingCount) % 16;
        var restStart = offset + length - restLen;
        _pendingCount = 0;

        if (restLen > 0)
        {
            Array.Clear(_leftover, 0, 16);
            Buffer.BlockCopy(payload, restStart, _leftover, 0, restLen);

            // XOR-ing zero bytes exposes the raw keystream in the unused tail of _leftover -
            // that is exactly what we want to save for the next packet's carry-in.
            _ctr.Process(_leftover, 0, 16);

            Buffer.BlockCopy(_leftover, 0, payload, restStart, restLen);
            _pendingCount = 16 - restLen;
        }
    }

    public void Dispose() => _ctr.Dispose();
}
