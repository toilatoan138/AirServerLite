using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using AirServerLite.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirServerLite.Discovery;

/// <summary>
/// The stable identity we present to iOS. The Ed25519 keypair MUST persist across restarts:
/// iOS caches the public key ("pk" TXT record) after the first pair-verify and will refuse
/// to connect - usually with a silent timeout - if it changes underneath an existing pairing.
/// </summary>
public sealed class DeviceIdentity
{
    public const string ModelName = "AppleTV3,2";
    public const string SourceVersion = "220.68";

    /// <summary>MAC-shaped device id, e.g. "AA:BB:CC:DD:EE:FF".</summary>
    public string DeviceId { get; }

    /// <summary>Long-term Ed25519 secret (32 bytes seed).</summary>
    public byte[] Ed25519PrivateSeed { get; }

    /// <summary>Long-term Ed25519 public key (32 bytes) - published as the "pk" TXT record.</summary>
    public byte[] Ed25519PublicKey { get; }

    public string PublicKeyHex { get; }

    /// <summary>Stable "pi" / "psi" / "gid" UUID.</summary>
    public string PairingUuid { get; }

    public string Name { get; }

    private DeviceIdentity(string name, string deviceId, byte[] seed, byte[] pub, string uuid)
    {
        Name = name;
        DeviceId = deviceId;
        Ed25519PrivateSeed = seed;
        Ed25519PublicKey = pub;
        PairingUuid = uuid;
        PublicKeyHex = Convert.ToHexString(pub).ToLowerInvariant();
    }

    private sealed class Persisted
    {
        public string DeviceId { get; set; } = "";
        public string Seed { get; set; } = "";
        public string Uuid { get; set; } = "";
    }

    public static DeviceIdentity LoadOrCreate(string name, PhysicalAddress mac)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "device-identity.json");
        var deviceId = NetUtil.FormatMac(mac);

        try
        {
            if (File.Exists(path))
            {
                var p = System.Text.Json.JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path));
                if (p is not null && p.Seed.Length == 64)
                {
                    var seed = Convert.FromHexString(p.Seed);
                    var pub = DerivePublicKey(seed);
                    Log.Info("identity", $"Loaded persisted identity deviceid={p.DeviceId} pk={Convert.ToHexString(pub)[..16]}...");
                    return new DeviceIdentity(name,
                        string.IsNullOrWhiteSpace(p.DeviceId) ? deviceId : p.DeviceId,
                        seed, pub,
                        string.IsNullOrWhiteSpace(p.Uuid) ? Guid.NewGuid().ToString() : p.Uuid);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("identity", "Could not read device-identity.json, regenerating: " + ex.Message);
        }

        var newSeed = RandomNumberGenerator.GetBytes(32);
        var newPub = DerivePublicKey(newSeed);
        var newUuid = Guid.NewGuid().ToString();

        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                new Persisted
                {
                    DeviceId = deviceId,
                    Seed = Convert.ToHexString(newSeed),
                    Uuid = newUuid
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log.Info("identity", "Generated new device identity -> " + path);
        }
        catch (Exception ex)
        {
            Log.Warn("identity", "Could not persist identity (will regenerate next launch): " + ex.Message);
        }

        return new DeviceIdentity(name, deviceId, newSeed, newPub, newUuid);
    }

    private static byte[] DerivePublicKey(byte[] seed)
    {
        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        return priv.GeneratePublicKey().GetEncoded();
    }

    public byte[] Sign(byte[] message)
    {
        var signer = SignerUtilities.GetSigner("Ed25519");
        signer.Init(true, new Ed25519PrivateKeyParameters(Ed25519PrivateSeed, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public bool Verify(byte[] message, byte[] signature, byte[] publicKey)
    {
        try
        {
            var verifier = SignerUtilities.GetSigner("Ed25519");
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception ex)
        {
            Log.Warn("identity", "Ed25519 verify failed: " + ex.Message);
            return false;
        }
    }
}
