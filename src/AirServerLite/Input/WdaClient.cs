using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirServerLite.Core;

namespace AirServerLite.Input;

public sealed record WdaWindowSize(double Width, double Height);

/// <summary>
/// Talks to a WebDriverAgentRunner instance on the phone, reached through a usbmuxd TCP
/// tunnel (iproxy) so the traffic never touches Wi-Fi - that is what keeps input latency in
/// the single-digit milliseconds while the video comes over the air.
///
/// Gestures go through the W3C Actions endpoint rather than WDA's older /wda/tap and
/// /wda/dragfromtoforduration helpers. Actions is the one API that has stayed stable across
/// WDA versions and it expresses press duration and multi-point paths precisely, which the
/// legacy helpers cannot. The legacy endpoints remain as a fallback because a few WDA builds
/// shipped with a broken Actions implementation.
/// </summary>
public sealed class WdaClient : IDisposable
{
    private const string Tag = "wda";

    private readonly HttpClient _http;
    private readonly InputSettings _settings;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private string? _sessionId;
    private WdaWindowSize? _windowSize;
    private DateTime _windowSizeFetched = DateTime.MinValue;
    private bool _useLegacyGestures;

    public bool IsConnected => _sessionId is not null;
    public string? SessionId => _sessionId;
    public Uri? BaseAddress => _http.BaseAddress;

    public event Action<string>? StatusChanged;

    public WdaClient(InputSettings settings, string? overrideBaseUrl = null)
    {
        _settings = settings;
        var baseUrl = !string.IsNullOrWhiteSpace(overrideBaseUrl) ? overrideBaseUrl : settings.WdaBaseUrl;
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "http://" + baseUrl;
        }

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMilliseconds(settings.RequestTimeoutMs)
        };
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    public static async Task<string?> ProbeUrlAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var client = new HttpClient { Timeout = timeout };
            var uri = new Uri(new Uri(url.TrimEnd('/') + "/"), "status");
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return url;
            }
        }
        catch
        {
            // Not reachable, connection refused, or timed out
        }
        return null;
    }

    public static async Task<string?> ProbeCandidatesAsync(IEnumerable<string> candidateUrls, TimeSpan timeout, CancellationToken ct = default)
    {
        var validUrls = candidateUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct()
            .ToList();

        if (validUrls.Count == 0) return null;

        var tasks = validUrls.Select(u => ProbeUrlAsync(u, timeout, ct)).ToList();

        string? found = null;
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(completed);
            try
            {
                var res = await completed.ConfigureAwait(false);
                if (res != null && found == null)
                {
                    found = res;
                    break;
                }
            }
            catch { }
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch { }
        }

        return found;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = await GetJsonAsync("status", ct).ConfigureAwait(false);
            if (status is null)
            {
                Report("WDA not reachable at " + _http.BaseAddress);
                return false;
            }

            var device = status["value"]?["device"]?.ToString() ?? "?";
            var version = status["value"]?["build"]?["version"]?.ToString() ?? "?";
            Log.Info(Tag, $"WebDriverAgent reachable (device={device}, build={version})");

            // WDA often already has a session open from a previous run; reuse it rather than
            // creating a second one, which some builds reject.
            var existing = status["sessionId"]?.ToString();
            if (!string.IsNullOrWhiteSpace(existing) && existing != "null")
            {
                _sessionId = existing;
                Log.Info(Tag, "Reusing existing WDA session " + _sessionId);
            }
            else
            {
                var payload = new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["alwaysMatch"] = new JsonObject(),
                        ["firstMatch"] = new JsonArray(new JsonObject())
                    }
                };

                var created = await PostJsonAsync("session", payload, ct).ConfigureAwait(false);
                _sessionId = created?["sessionId"]?.ToString()
                             ?? created?["value"]?["sessionId"]?.ToString();

                if (string.IsNullOrWhiteSpace(_sessionId))
                {
                    Report("WDA responded but would not create a session");
                    return false;
                }
                Log.Info(Tag, "Created WDA session " + _sessionId);
            }

            await RefreshWindowSizeAsync(ct).ConfigureAwait(false);
            Report($"Connected  ({_windowSize?.Width:F0}x{_windowSize?.Height:F0} pt)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "WDA connect failed", ex);
            Report("Connect failed: " + ex.Message);
            return false;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Window size in points. Re-fetched periodically because it changes with orientation,
    /// and a stale value silently mirrors every tap to the wrong place.
    /// </summary>
    public async Task<WdaWindowSize?> GetWindowSizeAsync(CancellationToken ct = default)
    {
        if (_windowSize is not null && (DateTime.UtcNow - _windowSizeFetched).TotalSeconds < 2)
            return _windowSize;

        await RefreshWindowSizeAsync(ct).ConfigureAwait(false);
        return _windowSize;
    }

    private async Task RefreshWindowSizeAsync(CancellationToken ct)
    {
        if (_sessionId is null) return;

        var json = await GetJsonAsync($"session/{_sessionId}/window/size", ct).ConfigureAwait(false);
        var value = json?["value"];
        if (value is null) return;

        var w = value["width"]?.GetValue<double>() ?? 0;
        var h = value["height"]?.GetValue<double>() ?? 0;
        if (w <= 0 || h <= 0) return;

        if (_windowSize is null || Math.Abs(_windowSize.Width - w) > 0.5 ||
            Math.Abs(_windowSize.Height - h) > 0.5)
            Log.Info(Tag, $"Device window size {w}x{h} pt");

        _windowSize = new WdaWindowSize(w, h);
        _windowSizeFetched = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------- gestures

    public Task TapAsync(double x, double y, CancellationToken ct = default)
        => _useLegacyGestures
            ? LegacyTapAsync(x, y, ct)
            : PerformActionsAsync(BuildTap(x, y, 40), ct);

    public Task LongPressAsync(double x, double y, int durationMs, CancellationToken ct = default)
        => PerformActionsAsync(BuildTap(x, y, durationMs), ct);

    /// <summary>
    /// A swipe expressed as intermediate move steps. iOS's gesture recogniser needs to see
    /// motion over time: a single move from A to B is interpreted as a jump and scrolls
    /// nothing, which is the classic "my swipe does nothing" bug.
    /// </summary>
    public Task SwipeAsync(double x1, double y1, double x2, double y2, int durationMs,
                           CancellationToken ct = default)
    {
        if (_useLegacyGestures) return LegacyDragAsync(x1, y1, x2, y2, durationMs, ct);

        const int steps = 10;
        var seq = new JsonArray
        {
            new JsonObject { ["type"] = "pointerMove", ["duration"] = 0, ["x"] = x1, ["y"] = y1 },
            new JsonObject { ["type"] = "pointerDown", ["button"] = 0 }
        };

        var stepMs = Math.Max(1, durationMs / steps);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            seq.Add(new JsonObject
            {
                ["type"] = "pointerMove",
                ["duration"] = stepMs,
                ["origin"] = "viewport",
                ["x"] = x1 + (x2 - x1) * t,
                ["y"] = y1 + (y2 - y1) * t
            });
        }

        seq.Add(new JsonObject { ["type"] = "pointerUp", ["button"] = 0 });
        return PerformActionsAsync(seq, ct);
    }

    private static JsonArray BuildTap(double x, double y, int holdMs) => new()
    {
        new JsonObject { ["type"] = "pointerMove", ["duration"] = 0, ["x"] = x, ["y"] = y },
        new JsonObject { ["type"] = "pointerDown", ["button"] = 0 },
        new JsonObject { ["type"] = "pause", ["duration"] = holdMs },
        new JsonObject { ["type"] = "pointerUp", ["button"] = 0 }
    };

    private async Task PerformActionsAsync(JsonArray pointerActions, CancellationToken ct)
    {
        if (_sessionId is null) return;

        var body = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "pointer",
                    ["id"] = "finger1",
                    ["parameters"] = new JsonObject { ["pointerType"] = "touch" },
                    ["actions"] = pointerActions
                }
            }
        };

        var result = await PostJsonAsync($"session/{_sessionId}/actions", body, ct).ConfigureAwait(false);

        if (result is null && !_useLegacyGestures)
        {
            Log.Warn(Tag, "W3C Actions endpoint failed - falling back to legacy WDA gestures");
            _useLegacyGestures = true;
        }
    }

    private Task LegacyTapAsync(double x, double y, CancellationToken ct)
        => PostJsonAsync($"session/{_sessionId}/wda/tap/0",
                         new JsonObject { ["x"] = x, ["y"] = y }, ct);

    private Task LegacyDragAsync(double x1, double y1, double x2, double y2, int durationMs,
                                 CancellationToken ct)
        => PostJsonAsync($"session/{_sessionId}/wda/dragfromtoforduration", new JsonObject
        {
            ["fromX"] = x1,
            ["fromY"] = y1,
            ["toX"] = x2,
            ["toY"] = y2,
            ["duration"] = durationMs / 1000.0
        }, ct);

    // ---------------------------------------------------------------- keyboard & buttons

    public Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        if (_sessionId is null || text.Length == 0) return Task.CompletedTask;

        var chars = new JsonArray();
        foreach (var c in text) chars.Add(c.ToString());

        return PostJsonAsync($"session/{_sessionId}/wda/keys",
                             new JsonObject { ["value"] = chars }, ct);
    }

    /// <summary>name: home | volumeUp | volumeDown.</summary>
    public Task PressButtonAsync(string name, CancellationToken ct = default)
        => PostJsonAsync($"session/{_sessionId}/wda/pressButton",
                         new JsonObject { ["name"] = name }, ct);

    public Task HomeAsync(CancellationToken ct = default)
        => PostJsonAsync("wda/homescreen", new JsonObject(), ct);

    public Task LockAsync(CancellationToken ct = default)
        => PostJsonAsync($"session/{_sessionId}/wda/lock", new JsonObject(), ct);

    public Task UnlockAsync(CancellationToken ct = default)
        => PostJsonAsync($"session/{_sessionId}/wda/unlock", new JsonObject(), ct);

    // ---------------------------------------------------------------- transport

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn(Tag, $"GET {path} -> {(int)resp.StatusCode}: {Truncate(text)}");
                return null;
            }
            return JsonNode.Parse(text);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn(Tag, $"GET {path} timed out after {_settings.RequestTimeoutMs} ms");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(Tag, $"GET {path} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonNode?> PostJsonAsync(string path, JsonNode body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn(Tag, $"POST {path} -> {(int)resp.StatusCode}: {Truncate(text)}");

                // WDA invalidates the session when the app under test dies. Drop ours so the
                // next gesture reconnects instead of failing forever.
                if (text.Contains("session", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains("terminated", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn(Tag, "WDA session was terminated - will reconnect on next use");
                    _sessionId = null;
                    Report("Session lost - reconnecting");
                }
                return null;
            }

            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn(Tag, $"POST {path} timed out after {_settings.RequestTimeoutMs} ms");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(Tag, $"POST {path} failed: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s.Replace("\n", " ") : s[..max].Replace("\n", " ") + "...";

    private void Report(string status)
    {
        Log.Info(Tag, status);
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _http.Dispose();
        _sessionLock.Dispose();
    }
}
