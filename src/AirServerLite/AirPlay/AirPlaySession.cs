using System.IO;
using System.Net;
using System.Net.Sockets;
using AirServerLite.AirPlay.Crypto;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Audio;
using AirServerLite.Core;
using AirServerLite.Discovery;
using Claunia.PropertyList;

namespace AirServerLite.AirPlay;

/// <summary>
/// One TCP connection from the iPhone to port 7000, and all the state that hangs off it.
///
/// The full mirroring handshake, in order:
///   GET  /info          - we advertise our capabilities and virtual display size
///   POST /pair-setup    - hand over our long-term Ed25519 public key
///   POST /pair-verify   - two-stage X25519 exchange, yields the shared secret
///   POST /fp-setup      - two-message FairPlay SAP exchange
///   SETUP (phase 1)     - carries the FairPlay-wrapped AES key, we return event/timing ports
///   SETUP (phase 2)     - declares the streams, we open the data port and return it
///   RECORD              - video starts flowing on the data port
///   TEARDOWN            - session over
/// </summary>
public sealed class AirPlaySession : IDisposable
{
    private const string Tag = "rtsp";
    private const string ServerName = "AirTunes/220.68";

    private readonly TcpClient _client;
    private readonly DeviceIdentity _identity;
    private readonly IPAddress _bindAddress;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts;

    private readonly PairVerifySession _pairing;
    private FairPlay? _fairPlay;
    private byte[]? _fairPlayAesKey;
    private byte[]? _audioIv;

    private MirrorStreamReceiver? _mirror;
    private AudioStreamReceiver? _audioReceiver;
    private AudioDecoder? _audioDecoder;
    private AudioPlayer? _audioPlayer;
    private AudioJitterBuffer? _audioJitterBuffer;
    private float _volume = 1.0f;
    private bool _isMuted;
    private TcpListener? _eventListener;
    private UdpClient? _timingSocket;
    private AesCtr? _controlCipher;
    private bool _channelEncrypted;
    private bool _disposed;

    public string RemoteEndPoint { get; }

    /// <summary>Remote IP without the port - the identity a device keeps across its several
    /// connections.</summary>
    public IPAddress RemoteAddress { get; }

    /// <summary>True once the RTSP loop has exited, so the server can reap the session.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Raised once video packets can start flowing.</summary>
    public event Action<MirrorStreamReceiver>? MirrorStarted;

    /// <summary>Raised when an AirPlay Media stream (e.g. YouTube / Safari) starts.</summary>
    public event Action<MediaSession>? MediaPlayStarted;

    /// <summary>Raised when an AirPlay Media stream stops.</summary>
    public event Action? MediaPlayStopped;

    public event Action<AirPlaySession>? Ended;

    public AirPlaySession(TcpClient client, DeviceIdentity identity, IPAddress bindAddress,
                          AppSettings settings, CancellationToken parentToken)
    {
        _client = client;
        _identity = identity;
        _bindAddress = bindAddress;
        _settings = settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _pairing = new PairVerifySession(identity);
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
    }

    public async Task RunAsync()
    {
        Log.Info(Tag, "Session opened from " + RemoteEndPoint);
        try
        {
            _client.NoDelay = true;
            await using var stream = _client.GetStream();
            var reader = new RtspReader(stream);

            while (!_cts.IsCancellationRequested)
            {
                RtspRequest? request;
                try
                {
                    request = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }

                if (request is null) break;

                if (_settings.Logging.TraceRtsp)
                    Log.Debug(Tag, "<< " + request);

                RtspResponse response;
                try
                {
                    response = Handle(request);
                }
                catch (FairPlayUnavailableException ex)
                {
                    Log.Error(Tag, "FairPlay unavailable: " + ex.Message);
                    response = RtspResponse.Status(request, 500, "FairPlay Unavailable");
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, "Handler threw for " + request.Method + " " + request.Path, ex);
                    response = RtspResponse.Status(request, 500, "Internal Error");
                }

                var bytes = response.Serialize(request.CSeq, ServerName);

                if (_channelEncrypted && _controlCipher is not null)
                    _controlCipher.Process(bytes, 0, bytes.Length);

                await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);

                if (_settings.Logging.TraceRtsp)
                    Log.Debug(Tag, $">> {response.StatusCode} {response.ReasonPhrase} ({response.Body.Length}B body)");

                // Right after pair-verify completes, the channel may or may not switch to
                // AES-CTR depending on the iOS build. Guessing wrong bricks the session, so
                // we peek at the next four bytes and decide from evidence.
                if (_pairing.Completed && !_channelEncrypted && request.Path.Contains("pair-verify"))
                    await SniffChannelEncryptionAsync(reader).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Session loop failed", ex);
        }
        finally
        {
            IsFinished = true;
            Log.Info(Tag, "Session closed: " + RemoteEndPoint);
            Ended?.Invoke(this);
            Dispose();
        }
    }

    private async Task SniffChannelEncryptionAsync(RtspReader reader)
    {
        var probe = new byte[4];
        var got = await reader.ReadRawAsync(probe, 0, 4, _cts.Token).ConfigureAwait(false);
        if (got < 4) return;

        if (RtspReader.LooksLikePlaintext(probe))
        {
            Log.Info(Tag, "Control channel stays in the clear after pair-verify");
            reader.Pushback(probe);
            return;
        }

        _controlCipher = _pairing.CreateControlChannelCipher();

        // Decrypting the probe advances the keystream exactly 4 positions, which is what the
        // sender did too - so the counter stays aligned.
        var decryptCipher = _pairing.CreateControlChannelCipher();
        decryptCipher.Process(probe, 0, 4);

        if (!RtspReader.LooksLikePlaintext(probe))
        {
            Log.Warn(Tag, "Post-pair-verify bytes decode as neither plaintext nor AES-CTR: " +
                          Log.Hex(probe));
        }
        else
        {
            Log.Info(Tag, "Control channel switched to AES-128-CTR");
        }

        _channelEncrypted = true;
        reader.DecryptInPlace = decryptCipher.Process;
        reader.Pushback(probe);
    }

    private MediaSession? _mediaSession;

    private RtspResponse Handle(RtspRequest req)
    {
        var path = req.Path;

        return (req.Method.ToUpperInvariant(), path) switch
        {
            ("GET", "/info") => HandleInfo(req),
            ("GET", "/server-info") => HandleServerInfo(req),
            ("POST", "/pair-setup") => HandlePairSetup(req),
            ("POST", "/pair-verify") => HandlePairVerify(req),
            ("POST", "/fp-setup") => HandleFpSetup(req),
            ("POST", "/reverse") => HandleReverse(req),
            ("SETUP", _) => HandleSetup(req),
            ("RECORD", _) => HandleRecord(req),
            ("TEARDOWN", _) => HandleTeardown(req),
            ("OPTIONS", _) => HandleOptions(req),
            ("POST", "/play") => HandlePlay(req),
            ("GET", "/playback-info") => HandlePlaybackInfo(req),
            ("POST", "/rate") => HandleRate(req),
            ("POST", "/scrub") => HandleScrub(req),
            ("POST", "/stop") => HandleStop(req),
            ("PUT", "/setProperty") => RtspResponse.Ok(req),
            ("POST", "/getProperty") => HandleGetProperty(req),
            ("POST", "/action") => RtspResponse.Ok(req),
            ("POST", "/feedback") => RtspResponse.Ok(req),
            ("POST", "/audioMode") => HandleAudioMode(req),
            ("GET_PARAMETER", _) => HandleGetParameter(req),
            ("SET_PARAMETER", _) => HandleSetParameter(req),
            ("FLUSH", _) => RtspResponse.Ok(req),
            _ => Unhandled(req)
        };
    }

    private static RtspResponse Unhandled(RtspRequest req)
    {
        Log.Warn(Tag, $"Unhandled {req.Method} {req.Path} - replying 200 to keep the session alive");
        return RtspResponse.Ok(req);
    }

    private RtspResponse HandleServerInfo(RtspRequest req)
    {
        Log.Info(Tag, "GET /server-info received");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>deviceid</key>
                <string>{_identity.DeviceId}</string>
                <key>features</key>
                <integer>{Discovery.MdnsAdvertiser.FeaturesInt}</integer>
                <key>model</key>
                <string>{DeviceIdentity.ModelName}</string>
                <key>name</key>
                <string>{_identity.Name}</string>
                <key>protovers</key>
                <string>1.1</string>
                <key>srcvers</key>
                <string>{DeviceIdentity.SourceVersion}</string>
                <key>statusFlags</key>
                <integer>68</integer>
                <key>pi</key>
                <string>{_identity.PairingUuid}</string>
                <key>vv</key>
                <integer>2</integer>
            </dict>
            </plist>
            """;
        var r = RtspResponse.Ok(req);
        r.Body = System.Text.Encoding.UTF8.GetBytes(xml);
        r.Headers["Content-Type"] = "text/x-apple-plist+xml";
        return r;
    }

    private static RtspResponse HandleReverse(RtspRequest req)
    {
        Log.Info(Tag, "POST /reverse received - upgrading connection");
        var r = RtspResponse.Status(req, 101, "Switching Protocols");
        r.Headers["Upgrade"] = "PTTH/1.0";
        r.Headers["Connection"] = "Upgrade";
        return r;
    }

    private static RtspResponse HandleGetProperty(RtspRequest req)
    {
        Log.Info(Tag, $"POST /getProperty received: {req.Path}");
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict/>
            </plist>
            """;
        var r = RtspResponse.Ok(req);
        r.Body = System.Text.Encoding.UTF8.GetBytes(xml);
        r.Headers["Content-Type"] = "text/x-apple-plist+xml";
        return r;
    }

    private RtspResponse HandleOptions(RtspRequest req)
    {
        var r = RtspResponse.Ok(req);
        r.Headers["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, " +
                              "OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET";
        return r;
    }

    private RtspResponse HandlePlay(RtspRequest req)
    {
        Log.Info(Tag, "POST /play received");
        if (req.Body.Length > 0)
        {
            try
            {
                Log.Debug(Tag, $"POST /play body ({req.Body.Length}B): {System.Text.Encoding.UTF8.GetString(req.Body)}");
            }
            catch { }
        }
        var plist = PlistHelper.Parse(req.Body);
        string? location = null;
        float startPos = 0f;
        string? uuid = null;
        string? clientProcName = null;

        if (plist != null)
        {
            location = plist.GetString("Content-Location");
            startPos = (float)(plist.GetDouble("Start-Position-Seconds") ?? 0.0);
            uuid = plist.GetString("uuid");
            clientProcName = plist.GetString("clientProcName");
        }

        if (string.IsNullOrEmpty(location))
        {
            if (req.Headers.TryGetValue("Content-Location", out var locHeader))
                location = locHeader;
            else if (req.Body.Length > 0)
            {
                var text = System.Text.Encoding.UTF8.GetString(req.Body);
                var match = System.Text.RegularExpressions.Regex.Match(text, @"Content-Location:\s*(.+)");
                if (match.Success) location = match.Groups[1].Value.Trim();
            }
        }

        location ??= "http://localhost/airplay-stream";
        _mediaSession = new MediaSession(location, startPos)
        {
            Uuid = uuid,
            ClientProcName = clientProcName
        };

        Log.Info(Tag, $"Media Play: location={location}, startPos={startPos}s, client={clientProcName}");
        MediaPlayStarted?.Invoke(_mediaSession);

        var r = RtspResponse.Ok(req);
        if (req.Headers.TryGetValue("X-Apple-Session-ID", out var sessId))
            r.Headers["X-Apple-Session-ID"] = sessId;
        return r;
    }

    private RtspResponse HandlePlaybackInfo(RtspRequest req)
    {
        _mediaSession ??= new MediaSession("http://localhost/airplay-stream");
        var xml = _mediaSession.BuildPlaybackInfoXml();
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);

        var r = RtspResponse.Ok(req);
        r.Body = bytes;
        r.Headers["Content-Type"] = "text/x-apple-plist+xml";
        return r;
    }

    private RtspResponse HandleRate(RtspRequest req)
    {
        var rateStr = req.GetQueryParam("value") ?? "1.0";
        if (float.TryParse(rateStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            if (_mediaSession != null) _mediaSession.Rate = rate;
            Log.Info(Tag, $"Media rate changed: {rate}");
        }
        return RtspResponse.Ok(req);
    }

    private RtspResponse HandleScrub(RtspRequest req)
    {
        var posStr = req.GetQueryParam("position") ?? "0.0";
        if (float.TryParse(posStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos))
        {
            if (_mediaSession != null) _mediaSession.PositionSeconds = pos;
            Log.Info(Tag, $"Media scrubbed to: {pos}s");
        }
        return RtspResponse.Ok(req);
    }

    private RtspResponse HandleStop(RtspRequest req)
    {
        Log.Info(Tag, "POST /stop received");
        _mediaSession = null;
        MediaPlayStopped?.Invoke();
        return RtspResponse.Ok(req);
    }

    private RtspResponse HandleAudioMode(RtspRequest req)
    {
        string mode = "default";
        if (req.Body.Length > 0)
        {
            try
            {
                var plist = PlistHelper.Parse(req.Body);
                var extracted = plist.GetString("audioMode");
                if (!string.IsNullOrEmpty(extracted))
                {
                    mode = extracted;
                }
                else
                {
                    var text = System.Text.Encoding.UTF8.GetString(req.Body);
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"<key>audioMode</key>\s*<string>([^<]+)</string>");
                    if (match.Success) mode = match.Groups[1].Value;
                }
            }
            catch { }
        }

        Log.Info(Tag, $"POST /audioMode received: mode={mode}");
        var r = RtspResponse.Ok(req);
        if (req.Headers.TryGetValue("X-Apple-Session-ID", out var sessId))
            r.Headers["X-Apple-Session-ID"] = sessId;
        return r;
    }

    /// <summary>
    /// GET /info. The "displays" entry is what tells iOS the geometry to encode at - so this
    /// is where mirroring resolution is actually chosen.
    /// </summary>
    private RtspResponse HandleInfo(RtspRequest req)
    {
        var display = new NSDictionary
        {
            { "width", 1920 },
            { "height", 1080 },
            { "widthPixels", 1920 },
            { "heightPixels", 1080 },
            { "widthPhysical", 0 },
            { "heightPhysical", 0 },
            { "refreshRate", 60 },
            { "maxFPS", 60 },
            { "overscanned", false },
            { "rotation", false },
            { "features", 14 },
            { "uuid", _identity.PairingUuid }
        };

        var audioLatencies = new NSArray(
            new NSDictionary
            {
                { "type", 100 },
                { "audioType", "default" },
                { "inputLatencyMicros", 0 },
                { "outputLatencyMicros", 0 }
            },
            new NSDictionary
            {
                { "type", 101 },
                { "audioType", "default" },
                { "inputLatencyMicros", 0 },
                { "outputLatencyMicros", 0 }
            }
        );

        var audioFormats = new NSArray(
            new NSDictionary
            {
                { "type", 100 },
                { "audioInputFormats", 0x3fffffc },
                { "audioOutputFormats", 0x3fffffc }
            },
            new NSDictionary
            {
                { "type", 101 },
                { "audioInputFormats", 0x3fffffc },
                { "audioOutputFormats", 0x3fffffc }
            }
        );

        var info = new NSDictionary
        {
            { "deviceID", _identity.DeviceId },
            { "macAddress", _identity.DeviceId },
            { "features", Discovery.MdnsAdvertiser.FeaturesInt },
            { "statusFlags", 68 },
            { "keepAliveLowPower", true },
            { "keepAliveSendStatsAsBody", true },
            { "model", DeviceIdentity.ModelName },
            { "name", _identity.Name },
            { "sourceVersion", DeviceIdentity.SourceVersion },
            { "protocolVersion", "1.1" },
            { "pi", _identity.PairingUuid },
            { "psi", _identity.PairingUuid },
            { "pk", new NSData(_identity.Ed25519PublicKey) },
            { "vv", 2 },
            { "initialVolume", _volume },
            { "audioLatencies", audioLatencies },
            { "audioFormats", audioFormats },
            { "displays", new NSArray(display) }
        };

        return RtspResponse.Plist(req, PlistHelper.ToBinary(info));
    }

    private RtspResponse HandlePairSetup(RtspRequest req)
        => RtspResponse.Binary(req, _pairing.HandlePairSetup(req.Body));

    private RtspResponse HandlePairVerify(RtspRequest req)
    {
        var body = _pairing.HandlePairVerify(req.Body);
        if (body is null) return RtspResponse.Status(req, 400, "Bad Request");

        if (_pairing.SharedSecret.Length == 32)
            DeviceKeyCache.RememberSharedSecret(RemoteAddress, _pairing.SharedSecret);

        return RtspResponse.Binary(req, body);
    }

    private RtspResponse HandleFpSetup(RtspRequest req)
    {
        _fairPlay ??= new FairPlay();
        var response = _fairPlay.Setup(req.Body);
        return RtspResponse.Binary(req, response);
    }

    private RtspResponse HandleSetup(RtspRequest req)
    {
        var plist = PlistHelper.Parse(req.Body);
        if (plist is null)
        {
            Log.Warn(Tag, "SETUP with unparseable body");
            return RtspResponse.Status(req, 400, "Bad Request");
        }

        Log.Debug(Tag, "SETUP plist:\n" + PlistHelper.Describe(plist));

        var streams = plist.GetArray("streams");
        return streams is null ? SetupPhase1(req, plist) : SetupPhase2(req, streams);
    }

    /// <summary>Phase 1 carries the FairPlay-wrapped session key and asks for our ports.</summary>
    private RtspResponse SetupPhase1(RtspRequest req, NSDictionary plist)
    {
        var ekey = plist.GetData("ekey");
        if (ekey is not null)
        {
            if (_fairPlay is null)
            {
                Log.Error(Tag, "SETUP delivered an ekey but /fp-setup never ran");
                return RtspResponse.Status(req, 400, "Bad Request");
            }

            try
            {
                var rawAesKey = _fairPlay.DecryptKey(ekey);
                var sharedSecret = _pairing.SharedSecret.Length == 32
                    ? _pairing.SharedSecret
                    : DeviceKeyCache.TryGetSharedSecret(RemoteAddress);

                if (sharedSecret is not null && sharedSecret.Length == 32)
                {
                    // Matches UxPlay raop_handlers.h lines 841-847:
                    // When pairing (pair-verify) is established, the 16-byte FairPlay-decrypted
                    // AES key is hashed with the 32-byte X25519 shared ECDH secret:
                    //   aesKey = SHA-512( rawAesKey[16] || sharedSecret[32] )[0..15]
                    var hashBuf = new byte[16 + 32];
                    Buffer.BlockCopy(rawAesKey, 0, hashBuf, 0, 16);
                    Buffer.BlockCopy(sharedSecret, 0, hashBuf, 16, 32);
                    _fairPlayAesKey = System.Security.Cryptography.SHA512.HashData(hashBuf)[..16];
                    Log.Debug(Tag, $"Hashed FairPlay AES key with X25519 shared secret -> {Convert.ToHexString(_fairPlayAesKey)}");
                }
                else
                {
                    _fairPlayAesKey = rawAesKey;
                    Log.Warn(Tag, "No X25519 shared secret available; using raw FairPlay AES key");
                }

                DeviceKeyCache.Remember(RemoteAddress, _fairPlayAesKey);
                Log.Info(Tag, "Session AES key recovered from ekey");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "Could not unwrap ekey", ex);
                return RtspResponse.Status(req, 400, "Bad Request");
            }
        }
        else
        {
            Log.Warn(Tag, "SETUP phase 1 had no ekey field");
        }

        var eiv = plist.GetData("eiv");
        if (eiv is not null && eiv.Length == 16)
        {
            _audioIv = eiv;
            DeviceKeyCache.RememberAudioIv(RemoteAddress, _audioIv);
            Log.Info(Tag, $"Extracted audio IV (eiv): {Convert.ToHexString(_audioIv)} and stored in DeviceKeyCache");
        }

        _eventListener = NetUtil.BindEphemeralTcp(_bindAddress, out var eventPort);
        _ = AcceptEventChannelAsync(_eventListener, _cts.Token);

        _timingSocket = NetUtil.BindEphemeralUdp(_bindAddress, out var timingPort);

        Log.Info(Tag, $"SETUP phase 1 -> eventPort={eventPort} timingPort={timingPort}");

        var response = new NSDictionary
        {
            { "eventPort", eventPort },
            { "timingPort", timingPort },
            { "timingPeerInfo", new NSDictionary
                {
                    { "Addresses", new NSArray(new NSString(_bindAddress.ToString())) },
                    { "ID", _identity.PairingUuid }
                }
            }
        };

        return RtspResponse.Plist(req, PlistHelper.ToBinary(response));
    }

    /// <summary>Phase 2 declares the streams; we open the data port for the video one.</summary>
    private RtspResponse SetupPhase2(RtspRequest req, NSArray streams)
    {
        var responseStreams = new List<NSObject>();

        for (var i = 0; i < streams.Count; i++)
        {
            if (streams[i] is not NSDictionary s) continue;

            var type = s.GetInt("type") ?? -1;
            Log.Info(Tag, $"SETUP stream type={type}");

            switch (type)
            {
                case 110: // screen mirroring (H.264)
                {
                    // iOS usually declares the streams on a different connection from the one
                    // that unwrapped the FairPlay key, so fall back to the key this device
                    // published moments ago on its other connection.
                    var aesKey = _fairPlayAesKey;

                    if (aesKey is null)
                    {
                        aesKey = DeviceKeyCache.TryGet(RemoteAddress);
                        if (aesKey is null)
                        {
                            Log.Error(Tag, "Mirror stream requested but no AES key is available " +
                                           "on this connection or cached for " + RemoteAddress);
                            return RtspResponse.Status(req, 400, "Bad Request");
                        }
                        Log.Info(Tag, "Using the AES key cached from this device's other connection");
                    }

                    // Signed in the plist; the key derivation needs the unsigned decimal form.
                    var rawId = s.GetInt("streamConnectionID") ?? 0;
                    var streamId = unchecked((ulong)rawId);

                    var cipher = new MirrorCipher(aesKey, streamId);
                    _mirror = new MirrorStreamReceiver(_bindAddress, cipher);
                    _mirror.Start();

                    responseStreams.Add(new NSDictionary
                    {
                        { "type", 110 },
                        { "dataPort", _mirror.Port }
                    });

                    Log.Info(Tag, $"Mirror stream ready on data port {_mirror.Port}");
                    MirrorStarted?.Invoke(_mirror);
                    break;
                }

                case 96: // AAC-ELD audio
                {
                    var aesKey = _fairPlayAesKey ?? DeviceKeyCache.TryGet(RemoteAddress);
                    var audioIv = _audioIv ?? DeviceKeyCache.TryGetAudioIv(RemoteAddress);
                    if (audioIv is null && s.GetData("eiv") is { Length: 16 } streamEiv)
                    {
                        audioIv = streamEiv;
                    }

                    if (aesKey is not null)
                    {
                        var audioFormat = s.GetInt("audioFormat") ?? 0;
                        var spf = (int)(s.GetInt("spf") ?? 480);
                        var sr = (int)(s.GetInt("sr") ?? 44100);
                        var ct = (int)(s.GetInt("ct") ?? 0);

                        var codecType = (audioFormat == 262144 || spf == 352 || ct == 2)
                            ? AudioCodecType.Alac
                            : AudioCodecType.AacEld;

                        Log.Info(Tag, $"Stream 96 audio codec detected: {codecType} (format=0x{audioFormat:X}, spf={spf}, sr={sr}, ct={ct})");

                        _audioReceiver = new AudioStreamReceiver(_bindAddress, aesKey, audioIv);
                        _audioDecoder = new AudioDecoder(codecType, sr, 2, spf);
                        _audioPlayer = new AudioPlayer();
                        _audioPlayer.Volume = _volume;
                        _audioPlayer.IsMuted = _isMuted;

                        // Jitter buffer sits between decoder and player to absorb network timing variance
                        _audioJitterBuffer = new AudioJitterBuffer(preBufferFrames: 3);
                        _audioPlayer.AttachJitterBuffer(_audioJitterBuffer);

                        _audioReceiver.PacketReady += pkt =>
                        {
                            var dec = _audioDecoder;
                            var jitter = _audioJitterBuffer;
                            if (dec != null && jitter != null &&
                                dec.TryDecode(pkt.Data, pkt.DataLength, out var pcm, out var pcmLen, out var isPooled))
                            {
                                jitter.Write(pkt.SequenceNumber, pkt.Timestamp, pcm, pcmLen, isPooled);
                            }
                            pkt.ReturnBuffer(); // Always return the pooled input buffer
                        };
                        _audioReceiver.Start();

                        responseStreams.Add(new NSDictionary
                        {
                            { "type", 96 },
                            { "dataPort", _audioReceiver.DataPort },
                            { "controlPort", _audioReceiver.ControlPort }
                        });

                        Log.Info(Tag, $"Audio stream pipeline active on {_audioReceiver.DataPort}/{_audioReceiver.ControlPort} (IV={Convert.ToHexString(audioIv ?? new byte[16])})");
                    }
                    else
                    {
                        // Fallback drain if key is absent
                        var audio = NetUtil.BindEphemeralUdp(_bindAddress, out var audioDataPort);
                        var control = NetUtil.BindEphemeralUdp(_bindAddress, out var audioControlPort);
                        _ = DrainUdpAsync(audio, "audio-data", _cts.Token);
                        _ = DrainUdpAsync(control, "audio-control", _cts.Token);

                        responseStreams.Add(new NSDictionary
                        {
                            { "type", 96 },
                            { "dataPort", audioDataPort },
                            { "controlPort", audioControlPort }
                        });

                        Log.Warn(Tag, $"No AES key available for audio stream; draining on {audioDataPort}/{audioControlPort}");
                    }
                    break;
                }

                default:
                    Log.Warn(Tag, "Ignoring unsupported stream type " + type);
                    break;
            }
        }

        var response = new NSDictionary { { "streams", new NSArray(responseStreams.ToArray()) } };
        return RtspResponse.Plist(req, PlistHelper.ToBinary(response));
    }

    private RtspResponse HandleRecord(RtspRequest req)
    {
        Log.Info(Tag, "RECORD - stream is live");
        var r = RtspResponse.Ok(req);
        r.Headers["Audio-Latency"] = "0";
        return r;
    }

    private RtspResponse HandleTeardown(RtspRequest req)
    {
        Log.Info(Tag, "TEARDOWN received");

        bool teardownAudio = false;
        bool teardownVideo = false;

        if (req.Body.Length > 0)
        {
            var plist = PlistHelper.Parse(req.Body);
            if (plist != null)
            {
                Log.Debug(Tag, "TEARDOWN plist:\n" + PlistHelper.Describe(plist));
                if (plist.Get("streams") is NSArray streams)
                {
                    for (int i = 0; i < streams.Count; i++)
                    {
                        if (streams[i] is NSDictionary s)
                        {
                            var type = s.GetInt("type") ?? -1;
                            if (type == 96) teardownAudio = true;
                            else if (type == 110) teardownVideo = true;
                        }
                    }
                }
            }
        }

        // If no specific stream was requested, tear down both
        if (!teardownAudio && !teardownVideo)
        {
            teardownAudio = true;
            teardownVideo = true;
        }

        Log.Info(Tag, $"TEARDOWN stream(s): audio={teardownAudio}, video={teardownVideo}");

        if (teardownVideo)
        {
            _mirror?.Dispose();
            _mirror = null;
        }

        if (teardownAudio)
        {
            _audioReceiver?.Dispose();
            _audioReceiver = null;
            _audioDecoder?.Dispose();
            _audioDecoder = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;
        }

        return RtspResponse.Ok(req);
    }

    private static RtspResponse HandleGetParameter(RtspRequest req)
    {
        var r = RtspResponse.Ok(req);
        r.Body = System.Text.Encoding.ASCII.GetBytes("volume: 0.000000\r\n");
        r.Headers["Content-Type"] = "text/parameters";
        return r;
    }

    private RtspResponse HandleSetParameter(RtspRequest req)
    {
        if (req.ContentType.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase))
        {
            var text = System.Text.Encoding.ASCII.GetString(req.Body).Trim();
            Log.Debug(Tag, "SET_PARAMETER " + text);
            if (text.StartsWith("volume:", StringComparison.OrdinalIgnoreCase))
            {
                var valStr = text.Substring(7).Trim();
                if (float.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dbVol))
                {
                    // dbVol ranges from 0.0 dB down to -30.0 dB (or -144 for mute)
                    float linearVol;
                    if (dbVol <= -30.0f) linearVol = 0f;
                    else linearVol = Math.Clamp((dbVol + 30.0f) / 30.0f, 0f, 1f);
                    _volume = linearVol;
                    _isMuted = linearVol <= 0f;
                    if (_audioPlayer != null) _audioPlayer.Volume = linearVol;
                }
            }
        }
        return RtspResponse.Ok(req);
    }

    public void SetAudioVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (_audioPlayer != null) _audioPlayer.Volume = _volume;
    }

    public void SetAudioMute(bool mute)
    {
        _isMuted = mute;
        if (_audioPlayer != null) _audioPlayer.IsMuted = mute;
    }

    /// <summary>
    /// iOS opens a reverse "event" connection and expects it to stay open for the lifetime of
    /// the session. We never send on it, but closing it early ends mirroring.
    /// </summary>
    private static async Task AcceptEventChannelAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            using var c = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            Log.Info(Tag, "Event channel connected from " + c.Client.RemoteEndPoint);

            var buf = new byte[4096];
            var s = c.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                Log.Trace(Tag, $"event channel: {n} bytes");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug(Tag, "Event channel ended: " + ex.Message); }
    }

    private static async Task DrainUdpAsync(UdpClient udp, string name, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
                await udp.ReceiveAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Trace(Tag, $"{name} drain ended: {ex.Message}"); }
        finally { udp.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        _mirror?.Dispose();
        _audioReceiver?.Dispose();
        _audioJitterBuffer?.Dispose();
        _audioDecoder?.Dispose();
        _audioPlayer?.Dispose();
        _eventListener?.Stop();
        _timingSocket?.Dispose();
        _controlCipher?.Dispose();
        _fairPlay?.Dispose();
        _pairing.Dispose();
        try { _client.Dispose(); } catch { }
        _cts.Dispose();
    }
}
