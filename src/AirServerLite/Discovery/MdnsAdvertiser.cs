using System.Net;
using AirServerLite.Core;
using Makaretu.Dns;

namespace AirServerLite.Discovery;

/// <summary>
/// Broadcasts the two records iOS looks for in Control Center:
///   _airplay._tcp  - the mirroring/video service (the one that makes the entry appear)
///   _raop._tcp     - the audio (Remote Audio Output Protocol) service
///
/// The TXT record contents are not cosmetic. iOS parses "features" and "srcvers" to decide
/// which protocol dialect to speak; get them wrong and the phone either ignores the device
/// entirely or opens an RTSP session and immediately tears it down.
///
/// The values below mirror the well-known RPiPlay/UxPlay profile, which presents as an
/// Apple TV 3rd gen (AppleTV3,2) speaking srcvers 220.68. That combination keeps iOS on the
/// legacy (non-HomeKit) pairing path, which is the only one a receiver can implement
/// without an Apple-issued certificate.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    // features bitfield, low,high. Starting from the full 0x5A7FFFF7, three bits are masked
    // out so that iOS never treats us as an AirPlay *video* target:
    //
    //   bit 0  Video           - off
    //   bit 2  VideoFairPlay   - off
    //   bit 4  VideoHTTPLiveStreams (HLS) - off
    //
    // With those off, an app like YouTube plays the video locally on the phone and the frames
    // reach us through the screen mirror. Leave any of them on and YouTube instead attempts an
    // in-app handoff (mlhls:// over PTTH), which we cannot satisfy because the media is behind
    // FairPlay/Widevine DRM - the visible result is a playback error on the phone and a video
    // that never appears in the mirror.
    //
    // Bits that must stay ON, and why:
    //   bit 7  screen mirroring
    //   bit 8  screen rotation - iOS needs this to re-negotiate geometry when a video goes
    //          fullscreen landscape. Masking it is what broke "tap a video and nothing shows".
    //   bit 9  audio
    //   bit 11 audio redundancy - iOS then sends redundantAudio=2. Kept ON because this is the
    //          combination that has actually been observed working end to end; see the note in
    //          AudioStreamReceiver before changing it.
    //
    // NOTE: UxPlay ships "0x5A7FFEE6,0x0" (lib/dnssdint.h). We deliberately differ: UxPlay is a
    // general-purpose mirror receiver, whereas these values are tuned for the in-app-video case
    // (bit 2 off, high word kept). Do not "align with UxPlay" without re-testing YouTube
    // playback - that swap is exactly what regressed this once already.
    public const string Features = "0x5A7FFFE2,0x1E";
    public const long FeaturesInt = 0x1E5A7FFFE2L;

    private readonly DeviceIdentity _identity;
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly bool _advertiseRaop;

    private ServiceDiscovery? _sd;
    private ServiceProfile? _airplayProfile;
    private ServiceProfile? _raopProfile;
    private ServiceProfile? _miracastProfile;
    private ServiceProfile? _googlecastProfile;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public MdnsAdvertiser(DeviceIdentity identity, IPAddress address, int port, bool advertiseRaop)
    {
        _identity = identity;
        _address = address;
        _port = port;
        _advertiseRaop = advertiseRaop;
    }

    public void Start()
    {
        if (IsRunning) return;

        if (NetUtil.IsAppleBonjourServiceRunning())
            Log.Warn("mdns",
                "Apple 'Bonjour Service' (mDNSResponder.exe) is running. It owns UDP 5353 and may " +
                "suppress our announcements. If the iPhone does not see the device, stop that " +
                "service: sc stop \"Bonjour Service\"");

        try
        {
            _sd = new ServiceDiscovery();

            _airplayProfile = BuildAirPlayProfile();
            _sd.Advertise(_airplayProfile);
            _sd.Announce(_airplayProfile);
            Log.Info("mdns", $"Advertised {_airplayProfile.FullyQualifiedName} at {_address}:{_port}");

            if (_advertiseRaop)
            {
                _raopProfile = BuildRaopProfile();
                _sd.Advertise(_raopProfile);
                _sd.Announce(_raopProfile);
                Log.Info("mdns", $"Advertised {_raopProfile.FullyQualifiedName}");
            }

            try
            {
                _miracastProfile = BuildMiracastProfile();
                _sd.Advertise(_miracastProfile);
                _sd.Announce(_miracastProfile);
                Log.Info("mdns", $"Advertised Miracast MICE: {_miracastProfile.FullyQualifiedName}");
            }
            catch (Exception ex)
            {
                Log.Warn("mdns", $"Miracast mDNS advertisement warning: {ex.Message}");
            }

            try
            {
                _googlecastProfile = BuildGoogleCastProfile();
                _sd.Advertise(_googlecastProfile);
                _sd.Announce(_googlecastProfile);
                Log.Info("mdns", $"Advertised Google Cast: {_googlecastProfile.FullyQualifiedName}");
            }
            catch (Exception ex)
            {
                Log.Warn("mdns", $"Google Cast mDNS advertisement warning: {ex.Message}");
            }

            IsRunning = true;
        }
        catch (Exception ex)
        {
            Log.Error("mdns", "Failed to start mDNS advertiser", ex);
            Stop();
            throw;
        }
    }

    private ServiceProfile BuildAirPlayProfile()
    {
        var p = new ServiceProfile(_identity.Name, "_airplay._tcp", (ushort)_port, new[] { _address });

        // Clear the library's default "txtvers=1" so we control the record exactly.
        p.Resources.RemoveAll(r => r is TXTRecord);
        var txt = new TXTRecord { Name = p.FullyQualifiedName, TTL = TimeSpan.FromMinutes(75) };
        void Add(string k, string v) => txt.Strings.Add(k + "=" + v);

        Add("acl", "0");
        Add("deviceid", _identity.DeviceId);
        Add("features", Features);
        Add("rsf", "0x0");
        Add("fv", "p20.78.5");
        Add("flags", "0x4");
        Add("model", DeviceIdentity.ModelName);
        Add("manufacturer", "AirServer-LITE");
        Add("serialNumber", _identity.DeviceId.Replace(":", ""));
        Add("protovers", "1.1");
        Add("srcvers", DeviceIdentity.SourceVersion);
        Add("pi", _identity.PairingUuid);
        Add("psi", _identity.PairingUuid);
        Add("gid", _identity.PairingUuid);
        Add("gcgl", "0");
        Add("pk", _identity.PublicKeyHex);
        Add("vv", "2");

        p.Resources.Add(txt);
        return p;
    }

    private ServiceProfile BuildRaopProfile()
    {
        // RAOP instance names are conventionally "<deviceid-without-colons>@<name>".
        var instance = _identity.DeviceId.Replace(":", "") + "@" + _identity.Name;
        var p = new ServiceProfile(instance, "_raop._tcp", (ushort)_port, new[] { _address });

        p.Resources.RemoveAll(r => r is TXTRecord);
        var txt = new TXTRecord { Name = p.FullyQualifiedName, TTL = TimeSpan.FromMinutes(75) };
        void Add(string k, string v) => txt.Strings.Add(k + "=" + v);

        Add("txtvers", "1");
        Add("ch", "2");          // channels
        Add("cn", "0,1,2,3");    // audio codecs: PCM, ALAC, AAC, AAC-ELD
        Add("et", "0,3,5");      // encryption types: none, FairPlay, FairPlay SAPv2.5
        Add("md", "0,1,2");      // metadata: text, artwork, progress
        Add("sr", "44100");
        Add("ss", "16");
        Add("vs", DeviceIdentity.SourceVersion);
        Add("tp", "UDP");
        Add("vn", "65537");
        Add("da", "true");
        Add("sv", "false");
        Add("sf", "0x4");
        Add("ft", Features);
        Add("am", DeviceIdentity.ModelName);
        Add("pk", _identity.PublicKeyHex);

        p.Resources.Add(txt);
        return p;
    }

    /// <summary>
    /// Re-announce. iOS caches negative results aggressively; after a network change or a
    /// failed session, an explicit re-announce gets the entry back in Control Center much
    /// faster than waiting for the next periodic broadcast.
    /// </summary>
    public void ReAnnounce()
    {
        try
        {
            if (_sd is null) return;
            if (_airplayProfile is not null) _sd.Announce(_airplayProfile);
            if (_raopProfile is not null) _sd.Announce(_raopProfile);
            if (_miracastProfile is not null) _sd.Announce(_miracastProfile);
            if (_googlecastProfile is not null) _sd.Announce(_googlecastProfile);
            Log.Debug("mdns", "Re-announced services");
        }
        catch (Exception ex)
        {
            Log.Warn("mdns", "Re-announce failed: " + ex.Message);
        }
    }

    public void Stop()
    {
        try
        {
            if (_sd is not null)
            {
                if (_airplayProfile is not null) _sd.Unadvertise(_airplayProfile);
                if (_raopProfile is not null) _sd.Unadvertise(_raopProfile);
                if (_miracastProfile is not null) _sd.Unadvertise(_miracastProfile);
                if (_googlecastProfile is not null) _sd.Unadvertise(_googlecastProfile);
                _sd.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("mdns", "Error during mDNS shutdown: " + ex.Message);
        }
        finally
        {
            _sd = null;
            _airplayProfile = null;
            _raopProfile = null;
            _miracastProfile = null;
            _googlecastProfile = null;
            IsRunning = false;
        }
    }

    public static string[] BuildMiracastTxtRecords(string friendlyName)
    {
        return new[]
        {
            "wfd-version=1.0",
            "wfd-id=00:11:22:33:44:55",
            $"friendly-name={friendlyName}"
        };
    }

    public static string[] BuildGoogleCastTxtRecords(string friendlyName, string deviceUuid)
    {
        return new[]
        {
            $"id={deviceUuid}",
            $"fn={friendlyName}",
            "md=Chromecast",
            "rs=",
            "st=0",
            "ca=4101"
        };
    }

    private ServiceProfile BuildMiracastProfile()
    {
        var p = new ServiceProfile(_identity.Name, "_display._tcp", 7236, new[] { _address });
        p.Resources.RemoveAll(r => r is TXTRecord);
        var txt = new TXTRecord { Name = p.FullyQualifiedName, TTL = TimeSpan.FromMinutes(75) };
        foreach (var item in BuildMiracastTxtRecords(_identity.Name))
        {
            txt.Strings.Add(item);
        }
        p.Resources.Add(txt);
        return p;
    }

    private ServiceProfile BuildGoogleCastProfile()
    {
        var p = new ServiceProfile(_identity.Name, "_googlecast._tcp", 8009, new[] { _address });
        p.Resources.RemoveAll(r => r is TXTRecord);
        var txt = new TXTRecord { Name = p.FullyQualifiedName, TTL = TimeSpan.FromMinutes(75) };
        foreach (var item in BuildGoogleCastTxtRecords(_identity.Name, _identity.PairingUuid))
        {
            txt.Strings.Add(item);
        }
        p.Resources.Add(txt);
        return p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
