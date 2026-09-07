using System.IO;
using System.Text;
using AirServerLite.Core;

namespace AirServerLite.AirPlay;

/// <summary>
/// Incremental RTSP/HTTP framer over a raw stream.
///
/// Two things make this less trivial than it looks:
///
/// 1. After pair-verify, some iOS versions switch the whole connection to AES-128-CTR with a
///    keystream that is *continuous across messages*. We therefore cannot decrypt "one
///    request at a time" - we must transform exactly the bytes we consume, in order, or the
///    counter desynchronises and everything afterwards is noise.
///
/// 2. Whether that switch happens depends on the iOS build. Rather than guess, the connection
///    sniffs the first bytes after pair-verify (see <see cref="Pushback"/>) and only enables
///    decryption if they do not look like a plaintext method token.
/// </summary>
public sealed class RtspReader
{
    private readonly Stream _stream;
    private readonly Queue<byte> _pushback = new();
    private readonly byte[] _one = new byte[1];

    /// <summary>Set once the channel becomes encrypted. Transforms bytes in place, in order.</summary>
    public Action<byte[], int, int>? DecryptInPlace { get; set; }

    public RtspReader(Stream stream) => _stream = stream;

    /// <summary>Return already-decrypted bytes to the head of the stream.</summary>
    public void Pushback(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _pushback.Enqueue(b);
    }

    /// <summary>Read raw bytes bypassing the decrypt hook - used only by the sniffing probe.</summary>
    public async Task<int> ReadRawAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var got = 0;
        while (got < count && _pushback.Count > 0) buffer[offset + got++] = _pushback.Dequeue();
        while (got < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset + got, count - got), ct)
                                 .ConfigureAwait(false);
            if (n == 0) break;
            got += n;
        }
        return got;
    }

    private async Task<int> ReadOneAsync(CancellationToken ct)
    {
        if (_pushback.Count > 0) return _pushback.Dequeue();

        var n = await _stream.ReadAsync(_one.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (n == 0) return -1;
        DecryptInPlace?.Invoke(_one, 0, 1);
        return _one[0];
    }

    public async Task<RtspRequest?> ReadAsync(CancellationToken ct)
    {
        var header = new List<byte>(512);
        var matched = 0; // progress through CRLF CRLF

        while (true)
        {
            var read = await ReadOneAsync(ct).ConfigureAwait(false);
            if (read < 0)
            {
                if (header.Count == 0) return null; // clean close between messages
                throw new EndOfStreamException("Connection closed mid-header");
            }

            var b = (byte)read;
            header.Add(b);

            matched = (matched, b) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0
            };

            if (matched == 4) break;

            if (header.Count > 64 * 1024)
                throw new InvalidDataException("RTSP header exceeded 64 KiB - stream desynchronised");
        }

        var headerText = Encoding.ASCII.GetString(header.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            throw new InvalidDataException("Empty RTSP request line");

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            throw new InvalidDataException("Malformed request line: " + lines[0]);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var clRaw) &&
            int.TryParse(clRaw, out var contentLength) && contentLength > 0)
        {
            if (contentLength > 32 * 1024 * 1024)
                throw new InvalidDataException("Absurd Content-Length " + contentLength);

            body = new byte[contentLength];
            var got = 0;

            while (got < contentLength && _pushback.Count > 0) body[got++] = _pushback.Dequeue();

            while (got < contentLength)
            {
                var n = await _stream.ReadAsync(body.AsMemory(got, contentLength - got), ct)
                                     .ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("Connection closed mid-body");
                DecryptInPlace?.Invoke(body, got, n);
                got += n;
            }
        }

        return new RtspRequest
        {
            Method = parts[0],
            Uri = parts[1],
            Protocol = parts[2],
            Headers = headers,
            Body = body
        };
    }

    private static readonly string[] KnownMethods =
    {
        "GET ", "POST", "OPTI", "SETU", "RECO", "TEAR", "SET_", "GET_",
        "ANNO", "FLUS", "PLAY", "PAUS", "PUT "
    };

    /// <summary>True if the 4-byte probe looks like the start of a plaintext request line.</summary>
    public static bool LooksLikePlaintext(ReadOnlySpan<byte> probe)
    {
        if (probe.Length < 4) return false;
        var s = Encoding.ASCII.GetString(probe[..4]);
        return KnownMethods.Contains(s, StringComparer.Ordinal);
    }
}
