using System.Collections.Concurrent;
using System.Net;
using AirServerLite.Core;

namespace AirServerLite.AirPlay;

/// <summary>
/// Carries the FairPlay session key between the several TCP connections one iOS device opens
/// for a single mirroring session.
///
/// iOS completes pair-verify, fp-setup and SETUP phase 1 (the message carrying "ekey") on one
/// connection, then opens a separate connection to declare the video streams. The connection
/// that has to build the mirror cipher is therefore usually NOT the one that unwrapped the
/// FairPlay key, so the key has to outlive the connection that recovered it.
///
/// Entries are keyed by the device's IP and expire quickly: they exist to bridge a handshake
/// that spans seconds, not to remember a device between sessions. A stale key would silently
/// decrypt video into noise, which is far harder to diagnose than a missing key.
/// </summary>
public static class DeviceKeyCache
{
    private const string Tag = "keycache";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private sealed record Entry(byte[]? AesKey, byte[]? SharedSecret, byte[]? AudioIv, DateTime StoredUtc);

    private static readonly ConcurrentDictionary<string, Entry> Store = new();

    public static void Remember(IPAddress device, byte[] aesKey)
    {
        var key = device.ToString();
        Store.AddOrUpdate(
            key,
            _ => new Entry(aesKey, null, null, DateTime.UtcNow),
            (_, existing) => new Entry(aesKey, existing.SharedSecret, existing.AudioIv, DateTime.UtcNow));
        Log.Debug(Tag, $"Stored the FairPlay AES key for {key}");
    }

    public static void RememberSharedSecret(IPAddress device, byte[] sharedSecret)
    {
        var key = device.ToString();
        Store.AddOrUpdate(
            key,
            _ => new Entry(null, sharedSecret, null, DateTime.UtcNow),
            (_, existing) => new Entry(existing.AesKey, sharedSecret, existing.AudioIv, DateTime.UtcNow));
        Log.Debug(Tag, $"Stored X25519 shared secret for {key}");
    }

    public static void RememberAudioIv(IPAddress device, byte[] audioIv)
    {
        var key = device.ToString();
        Store.AddOrUpdate(
            key,
            _ => new Entry(null, null, (byte[])audioIv.Clone(), DateTime.UtcNow),
            (_, existing) => new Entry(existing.AesKey, existing.SharedSecret, (byte[])audioIv.Clone(), DateTime.UtcNow));
        Log.Debug(Tag, $"Stored audio IV for {key}");
    }

    /// <summary>Returns the most recent key for this device, or null when nothing usable is cached.</summary>
    public static byte[]? TryGet(IPAddress device)
    {
        var key = device.ToString();
        if (!Store.TryGetValue(key, out var e) || e.AesKey is null) return null;

        var age = DateTime.UtcNow - e.StoredUtc;
        if (age > Ttl)
        {
            Store.TryRemove(key, out _);
            Log.Debug(Tag, $"Discarded the key for {key} (age {age.TotalSeconds:F0}s exceeds TTL)");
            return null;
        }

        Log.Debug(Tag, $"Reusing the key cached for {key} {age.TotalSeconds:F1}s ago");
        return e.AesKey;
    }

    public static byte[]? TryGetSharedSecret(IPAddress device)
    {
        var key = device.ToString();
        if (!Store.TryGetValue(key, out var e) || e.SharedSecret is null) return null;

        var age = DateTime.UtcNow - e.StoredUtc;
        if (age > Ttl)
        {
            Store.TryRemove(key, out _);
            Log.Debug(Tag, $"Discarded shared secret for {key} (age {age.TotalSeconds:F0}s exceeds TTL)");
            return null;
        }

        return e.SharedSecret;
    }

    public static byte[]? TryGetAudioIv(IPAddress device)
    {
        var key = device.ToString();
        if (!Store.TryGetValue(key, out var e) || e.AudioIv is null) return null;

        var age = DateTime.UtcNow - e.StoredUtc;
        if (age > Ttl)
        {
            Store.TryRemove(key, out _);
            Log.Debug(Tag, $"Discarded audio IV for {key} (age {age.TotalSeconds:F0}s exceeds TTL)");
            return null;
        }

        return (byte[])e.AudioIv.Clone();
    }

    public static void Forget(IPAddress device)
    {
        if (Store.TryRemove(device.ToString(), out _))
            Log.Debug(Tag, "Cleared the cached key for " + device);
    }

    public static void Clear()
    {
        Store.Clear();
        Log.Debug(Tag, "Cleared all cached keys");
    }
}
