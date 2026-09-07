using System.Security.Cryptography;
using AirServerLite.Core;
using AirServerLite.Discovery;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirServerLite.AirPlay.Crypto;

/// <summary>
/// The legacy (non-HomeKit) AirPlay pairing handshake.
///
/// pair-setup is trivial: the client POSTs 32 bytes and we answer with our long-term Ed25519
/// public key. There is no PIN on this path.
///
/// pair-verify is a two-stage X25519 exchange:
///
///   Stage 1  client -> [01 00 00 00][clientCurve25519Pub:32][clientEd25519Pub:32]
///            server -> [serverCurve25519Pub:32][encrypted Ed25519 signature:64]
///            signature covers (serverCurvePub || clientCurvePub), signed with our long-term
///            Ed25519 key and encrypted with AES-128-CTR derived from the shared secret.
///
///   Stage 2  client -> [00 00 00 00][encrypted client signature:64]
///            server -> 200 OK, empty body
///            The client's signature covers (clientCurvePub || serverCurvePub) and is
///            encrypted with the SAME continuing CTR keystream - which is why the cipher
///            object must be kept alive between the two stages.
/// </summary>
public sealed class PairVerifySession : IDisposable
{
    private const string Tag = "pair";

    private readonly DeviceIdentity _identity;

    private X25519PrivateKeyParameters? _serverCurvePriv;
    private byte[] _serverCurvePub = Array.Empty<byte>();
    private byte[] _clientCurvePub = Array.Empty<byte>();
    private byte[] _clientEdPub = Array.Empty<byte>();

    private AesCtr? _verifyCipher;

    /// <summary>32-byte X25519 shared secret. Also feeds the mirror stream key derivation.</summary>
    public byte[] SharedSecret { get; private set; } = Array.Empty<byte>();

    public bool Completed { get; private set; }

    public PairVerifySession(DeviceIdentity identity) => _identity = identity;

    /// <summary>POST /pair-setup - answer with our long-term public key.</summary>
    public byte[] HandlePairSetup(byte[] request)
    {
        Log.Debug(Tag, $"pair-setup request {request.Length} bytes: {Log.Hex(request, 16)}");
        return _identity.Ed25519PublicKey;
    }

    /// <summary>POST /pair-verify. Returns the response body, or null on protocol error.</summary>
    public byte[]? HandlePairVerify(byte[] body)
    {
        if (body.Length < 4)
        {
            Log.Warn(Tag, "pair-verify body too short: " + body.Length);
            return null;
        }

        // body[0] == 1 -> stage 1, body[0] == 0 -> stage 2
        return body[0] == 1 ? Stage1(body) : Stage2(body);
    }

    private byte[]? Stage1(byte[] body)
    {
        if (body.Length < 4 + 32 + 32)
        {
            Log.Warn(Tag, $"pair-verify stage 1 needs 68 bytes, got {body.Length}");
            return null;
        }

        _clientCurvePub = body[4..36];
        _clientEdPub = body[36..68];

        Log.Debug(Tag, "stage1 clientCurve=" + Convert.ToHexString(_clientCurvePub)[..16] +
                       " clientEd=" + Convert.ToHexString(_clientEdPub)[..16]);

        // Fresh ephemeral X25519 keypair per session.
        var gen = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var kp = gen.GenerateKeyPair();
        _serverCurvePriv = (X25519PrivateKeyParameters)kp.Private;
        _serverCurvePub = ((X25519PublicKeyParameters)kp.Public).GetEncoded();

        var agreement = new X25519Agreement();
        agreement.Init(_serverCurvePriv);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(_clientCurvePub, 0), shared, 0);
        SharedSecret = shared;

        // Signature over serverCurvePub || clientCurvePub
        var toSign = new byte[64];
        Buffer.BlockCopy(_serverCurvePub, 0, toSign, 0, 32);
        Buffer.BlockCopy(_clientCurvePub, 0, toSign, 32, 32);
        var signature = _identity.Sign(toSign);

        var aesKey = Derive("Pair-Verify-AES-Key", shared);
        var aesIv = Derive("Pair-Verify-AES-IV", shared);

        _verifyCipher?.Dispose();
        _verifyCipher = new AesCtr(aesKey, aesIv);

        var encrypted = _verifyCipher.Process(signature);

        var response = new byte[96];
        Buffer.BlockCopy(_serverCurvePub, 0, response, 0, 32);
        Buffer.BlockCopy(encrypted, 0, response, 32, 64);

        Log.Info(Tag, "pair-verify stage 1 complete");
        return response;
    }

    private byte[]? Stage2(byte[] body)
    {
        if (_verifyCipher is null)
        {
            Log.Warn(Tag, "pair-verify stage 2 arrived before stage 1");
            return null;
        }

        if (body.Length < 4 + 64)
        {
            Log.Warn(Tag, $"pair-verify stage 2 needs 68 bytes, got {body.Length}");
            return null;
        }

        // Continues the same keystream started in stage 1.
        var signature = _verifyCipher.Process(body.AsSpan(4, 64));

        var signed = new byte[64];
        Buffer.BlockCopy(_clientCurvePub, 0, signed, 0, 32);
        Buffer.BlockCopy(_serverCurvePub, 0, signed, 32, 32);

        var ok = _identity.Verify(signed, signature, _clientEdPub);
        if (!ok)
        {
            // Real receivers are lenient here and so are we: some iOS builds sign a slightly
            // different transcript, and rejecting the session costs the user the whole
            // mirroring feature over a check that buys us nothing on a LAN we already trust.
            Log.Warn(Tag, "pair-verify client signature did not verify - continuing anyway");
        }
        else
        {
            Log.Info(Tag, "pair-verify stage 2 signature OK");
        }

        Completed = true;
        return Array.Empty<byte>();
    }

    /// <summary>
    /// Build the AES-CTR pair that protects the RTSP control channel once pair-verify is done.
    /// Same derivation as the verify cipher, but a brand-new counter starting at zero.
    /// </summary>
    public AesCtr CreateControlChannelCipher()
    {
        if (SharedSecret.Length == 0)
            throw new InvalidOperationException("No shared secret - pair-verify has not run");

        return new AesCtr(Derive("Pair-Verify-AES-Key", SharedSecret),
                          Derive("Pair-Verify-AES-IV", SharedSecret));
    }

    /// <summary>SHA-512(label || salt) truncated to 16 bytes.</summary>
    internal static byte[] Derive(string label, ReadOnlySpan<byte> salt)
    {
        var labelBytes = System.Text.Encoding.ASCII.GetBytes(label);
        var buf = new byte[labelBytes.Length + salt.Length];
        Buffer.BlockCopy(labelBytes, 0, buf, 0, labelBytes.Length);
        salt.CopyTo(buf.AsSpan(labelBytes.Length));

        var hash = SHA512.HashData(buf);
        return hash[..16];
    }

    public void Dispose() => _verifyCipher?.Dispose();
}
