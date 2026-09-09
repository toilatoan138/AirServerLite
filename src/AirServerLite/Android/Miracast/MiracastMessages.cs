namespace AirServerLite.Android.Miracast;

public static class MiracastMessages
{
    public const string WfdVersion = "org.wfa.wfd1.0";

    // 1080p60 & 720p60 H.264 profile baseline/high supported
    public const string DefaultVideoFormats = "00 00 02 02 00000040 00000000 00000000 00 0000 0000 00 none none";
    public const string DefaultAudioCodecs = "LPCM 00000002 00, AAC 00000001 00";

    public static string BuildResponse(int cseq, string status = "200 OK", string? contentType = null, string? body = null, string? extraHeaders = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"RTSP/1.0 {status}\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
        {
            sb.Append(extraHeaders);
            if (!extraHeaders.EndsWith("\r\n")) sb.Append("\r\n");
        }
        if (!string.IsNullOrEmpty(body))
        {
            sb.Append($"Content-Type: {contentType ?? "text/parameters"}\r\n");
            sb.Append($"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n\r\n");
            sb.Append(body);
        }
        else
        {
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
