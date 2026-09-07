using System.IO;
using AirServerLite.Core;
using Claunia.PropertyList;

namespace AirServerLite.AirPlay;

/// <summary>
/// Thin, defensive wrapper over plist-cil. iOS sends both binary ("bplist00") and XML plists
/// depending on the endpoint, and drops fields without warning between releases - so every
/// accessor here returns a nullable/default rather than throwing.
/// </summary>
public static class PlistHelper
{
    private const string Tag = "plist";

    public static NSDictionary? Parse(byte[] data)
    {
        if (data.Length == 0) return null;
        try
        {
            return PropertyListParser.Parse(data) as NSDictionary;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"Failed to parse {data.Length}-byte plist: {ex.Message}");
            return null;
        }
    }

    public static byte[] ToBinary(NSDictionary dict)
    {
        using var ms = new MemoryStream();
        BinaryPropertyListWriter.Write(ms, dict);
        return ms.ToArray();
    }

    public static NSObject? Get(this NSDictionary? dict, string key)
        => dict is not null && dict.TryGetValue(key, out var v) ? v : null;

    public static byte[]? GetData(this NSDictionary? dict, string key)
        => Get(dict, key) is NSData d ? d.Bytes : null;

    public static string? GetString(this NSDictionary? dict, string key)
        => Get(dict, key) switch
        {
            NSString s => s.Content,
            NSNumber n => n.ToString(),
            _ => null
        };

    public static long? GetInt(this NSDictionary? dict, string key)
    {
        return Get(dict, key) switch
        {
            NSNumber n => n.ToLong(),
            NSString s when long.TryParse(s.Content, out var v) => v,
            _ => null
        };
    }

    public static bool GetBool(this NSDictionary? dict, string key, bool fallback = false)
        => Get(dict, key) switch
        {
            NSNumber n => n.ToBool(),
            _ => fallback
        };

    public static NSArray? GetArray(this NSDictionary? dict, string key)
        => Get(dict, key) as NSArray;

    /// <summary>Log a plist as an indented tree - indispensable when a new iOS build
    /// changes the SETUP payload shape.</summary>
    public static string Describe(NSDictionary? dict, int maxDepth = 3)
    {
        if (dict is null) return "(null)";
        var sb = new System.Text.StringBuilder();
        Describe(dict, sb, 0, maxDepth);
        return sb.ToString();
    }

    private static void Describe(NSObject obj, System.Text.StringBuilder sb, int depth, int maxDepth)
    {
        var pad = new string(' ', depth * 2);
        switch (obj)
        {
            case NSDictionary d:
                if (depth >= maxDepth) { sb.Append(pad).Append("{...}\n"); return; }
                foreach (var kv in d)
                {
                    sb.Append(pad).Append(kv.Key).Append(':');
                    if (kv.Value is NSDictionary or NSArray)
                    {
                        sb.Append('\n');
                        Describe(kv.Value, sb, depth + 1, maxDepth);
                    }
                    else
                    {
                        sb.Append(' ').Append(Scalar(kv.Value)).Append('\n');
                    }
                }
                break;

            case NSArray a:
                if (depth >= maxDepth) { sb.Append(pad).Append("[...]\n"); return; }
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(pad).Append('[').Append(i).Append("]\n");
                    Describe(a[i], sb, depth + 1, maxDepth);
                }
                break;

            default:
                sb.Append(pad).Append(Scalar(obj)).Append('\n');
                break;
        }
    }

    private static string Scalar(NSObject o) => o switch
    {
        NSData d => $"<{d.Bytes.Length} bytes> {Log.Hex(d.Bytes, 12)}",
        NSString s => '"' + s.Content + '"',
        NSNumber n => n.ToString(),
        _ => o.ToString() ?? "?"
    };
}
