using System.Text;

namespace AirServerLite.AirPlay;

/// <summary>
/// iOS multiplexes plain HTTP/1.1 ("GET /info") and RTSP/1.0 ("SETUP rtsp://...") over the
/// same TCP connection on port 7000. Both share the exact same wire format - request line,
/// CRLF headers, blank line, Content-Length body - so one parser handles both. The only
/// thing that matters is echoing back the same protocol token the client used, and always
/// echoing CSeq.
/// </summary>
public sealed class RtspRequest
{
    public string Method { get; init; } = "";
    public string Uri { get; init; } = "";
    public string Protocol { get; init; } = "RTSP/1.0";
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = Array.Empty<byte>();

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string ContentType => Header("Content-Type") ?? "";
    public string CSeq => Header("CSeq") ?? "0";

    /// <summary>Path without query string, e.g. "/fp-setup".</summary>
    public string Path
    {
        get
        {
            var u = Uri;
            var q = u.IndexOf('?');
            if (q >= 0) u = u[..q];
            // RTSP URIs are absolute: rtsp://10.0.0.5/12345678
            if (u.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var slash = u.IndexOf('/', u.IndexOf("//", StringComparison.Ordinal) + 2);
                u = slash >= 0 ? u[slash..] : "/";
            }
            return u;
        }
    }

    public override string ToString() => $"{Method} {Uri} {Protocol} (CSeq={CSeq}, body={Body.Length}B)";
}

public sealed class RtspResponse
{
    public int StatusCode { get; set; } = 200;
    public string ReasonPhrase { get; set; } = "OK";
    public string Protocol { get; set; } = "RTSP/1.0";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public static RtspResponse Ok(RtspRequest req) => new()
    {
        Protocol = req.Protocol,
        StatusCode = 200,
        ReasonPhrase = "OK"
    };

    public static RtspResponse Binary(RtspRequest req, byte[] body, string contentType = "application/octet-stream")
    {
        var r = Ok(req);
        r.Body = body;
        r.Headers["Content-Type"] = contentType;
        return r;
    }

    public static RtspResponse Plist(RtspRequest req, byte[] binaryPlist)
        => Binary(req, binaryPlist, "application/x-apple-binary-plist");

    public static RtspResponse Status(RtspRequest req, int code, string reason)
        => new() { Protocol = req.Protocol, StatusCode = code, ReasonPhrase = reason };

    public byte[] Serialize(string cseq, string serverName)
    {
        var sb = new StringBuilder(256);
        sb.Append(Protocol).Append(' ').Append(StatusCode).Append(' ').Append(ReasonPhrase).Append("\r\n");
        sb.Append("CSeq: ").Append(cseq).Append("\r\n");
        sb.Append("Server: ").Append(serverName).Append("\r\n");

        foreach (var kv in Headers)
        {
            if (kv.Key.Equals("CSeq", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        }

        sb.Append("Content-Length: ").Append(Body.Length).Append("\r\n\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        if (Body.Length == 0) return head;

        var buf = new byte[head.Length + Body.Length];
        Buffer.BlockCopy(head, 0, buf, 0, head.Length);
        Buffer.BlockCopy(Body, 0, buf, head.Length, Body.Length);
        return buf;
    }
}
