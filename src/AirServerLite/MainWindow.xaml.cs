using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirServerLite.AirPlay;
using AirServerLite.AirPlay.Crypto;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;
using AirServerLite.Discovery;
using AirServerLite.Input;
using AirServerLite.UI;
using AirServerLite.Video;
using Microsoft.Web.WebView2.Core;

namespace AirServerLite;

public partial class MainWindow : Window
{
    private const string LogTag = "ui";

    private readonly AppSettings _settings;
    private readonly CoordinateMapper _mapper = new();

    private DeviceIdentity? _identity;
    private MdnsAdvertiser? _mdns;
    private DialServer? _dialServer;
    private AirPlayServer? _server;
    private VideoPipeline? _pipeline;
    private WriteableBitmap? _bitmap;

    private IProxyHost? _iproxy;
    private WdaClient? _wda;
    private InputRouter? _input;
    private IPAddress? _lastClientAddress;

    private bool _running;
    private volatile bool _frameWaiting;

    private bool _isFullscreen;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
    private WindowState _prevWindowState = WindowState.Normal;
    private Rect _prevBounds;
    private System.Windows.Threading.DispatcherTimer? _hudTimer;
    private bool _isLandscape;
    private float _currentVolume = 1.0f;

    private bool _isWebPlayerActive;
    private AirPlay.MediaSession? _activeMediaSession;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        Log.MinLevel = _settings.ParsedLogLevel;

        Log.Emitted += OnLogEmitted;

        Title = $"AirServer-LITE - {_settings.DeviceName}";

        Loaded += OnLoaded;
        Closing += OnClosing;

        _hudTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hudTimer.Tick += (s, e) =>
        {
            _hudTimer.Stop();
            if (_isFullscreen) OverlayHud.Visibility = Visibility.Collapsed;
        };

        // Pull frames on the WPF render tick instead of a timer: it is already synchronised
        // with the compositor, so a frame drawn here is on screen at the very next vblank
        // with no extra scheduling delay.
        CompositionTarget.Rendering += OnRendering;

        // Elevate WPF animation and composition clock to 120 FPS for high-refresh displays
        try
        {
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline),
                new FrameworkPropertyMetadata(120));
        }
        catch { }
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Ensure full Direct3D hardware acceleration is active
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        int renderingTier = RenderCapability.Tier >> 16;
        Log.Info(LogTag, $"Direct3D GPU Hardware Rendering Tier: {renderingTier} (Tier 2 = Full Hardware Acceleration)");

        VideoHost.Focus();
        RunPreflight();
    }

    /// <summary>
    /// Check the two external dependencies before the user hits Start, so a missing DLL shows
    /// up as a sentence in the UI rather than a failed mirroring attempt ten minutes later.
    /// </summary>
    private void RunPreflight()
    {
        var problems = new List<string>();

        if (_settings.LoadError is not null)
            problems.Add("appsettings.json could not be parsed, defaults are in use: " +
                         _settings.LoadError);

        FairPlay.Probe();
        if (!FairPlay.IsAvailable)
            problems.Add("FairPlay: " + FairPlay.UnavailableReason);

        if (!FFmpegLoader.Initialize(_settings.Video.FFmpegPath))
            problems.Add("FFmpeg: " + FFmpegLoader.LoadError);

        if (NetUtil.IsAppleBonjourServiceRunning())
            problems.Add("Apple's Bonjour Service is running and may hide this device from " +
                         "Control Centre. Stop it with: sc stop \"Bonjour Service\"");

        if (problems.Count == 0)
        {
            SetStatus("Ready - " + FFmpegLoader.Version, ready: true);
            PlaceholderHint.Text =
                "Start the receiver, then pick this device in Control Centre → Screen Mirroring.";
        }
        else
        {
            SetStatus("Not ready - see the notes below", ready: false);
            PlaceholderHint.Text = string.Join("\n\n", problems);
            BtnStart.IsEnabled = FairPlay.IsAvailable && FFmpegLoader.IsLoaded;
            foreach (var p in problems) Log.Warn(LogTag, "preflight: " + p);
        }
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_running) StopReceiver();
        else StartReceiver();
    }

    private void StartReceiver()
    {
        try
        {
            var nic = NetUtil.PickInterface(_settings.PreferredInterface);
            if (nic is null)
            {
                SetStatus("No usable network interface", ready: false);
                return;
            }

            _identity = DeviceIdentity.LoadOrCreate(_settings.DeviceName, nic.Mac);

            _pipeline = new VideoPipeline(_settings.Video.MaxQueuedFrames);
            _pipeline.FrameAvailable += OnPipelineFrameAvailable;
            _pipeline.StatsUpdated += line => Dispatcher.BeginInvoke(() => StatsText.Text = line);
            _pipeline.Start();

            _server = new AirPlayServer(_identity, nic.Address, _settings);
            _server.MirrorStarted += OnMirrorStarted;
            _server.MediaPlayStarted += OnMediaPlayStarted;
            _server.MediaPlayStopped += OnMediaPlayStopped;
            _server.SessionStateChanged += state =>
                Dispatcher.BeginInvoke(() => SetStatus(state + " - " + nic.Address, ready: true));
            _server.Start();

            _mdns = new MdnsAdvertiser(_identity, nic.Address, _settings.AirPlayPort,
                                       _settings.AdvertiseRaop);
            _mdns.Start();

            try
            {
                _dialServer = new DialServer(nic.Address, _settings.DeviceName + " (YouTube)", _identity.DeviceId);
                _dialServer.YouTubeCastRequested += OnYouTubeCastRequested;
                _dialServer.YouTubeCastStopped += OnYouTubeCastStopped;
                _dialServer.Start();
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, "DIAL server failed to start: " + ex.Message);
            }

            _running = true;
            BtnStart.Content = "Stop receiver";
            SetStatus($"Advertising on {nic.Address} - waiting for a device", ready: true);

            if (_settings.Input.Enabled) _ = ProbeAndConnectWdaAsync();
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "Failed to start the receiver", ex);
            SetStatus("Start failed: " + ex.Message, ready: false);
            StopReceiver();
        }
    }

    private void StopReceiver()
    {
        try
        {
            try
            {
                _dialServer?.Stop();
                _dialServer?.Dispose();
            }
            catch { }
            _dialServer = null;

            _mdns?.Dispose();
            _server?.Dispose();
            _pipeline?.Dispose();
            _input?.Dispose();
            _wda?.Dispose();
            _iproxy?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "Error during shutdown", ex);
        }
        finally
        {
            _dialServer = null;
            _mdns = null;
            _server = null;
            _pipeline = null;
            _input = null;
            _wda = null;
            _iproxy = null;
            _bitmap = null;
            _running = false;

            VideoImage.Source = null;
            ShowWebPlayer(false);
            Placeholder.Visibility = Visibility.Visible;
            BtnStart.Content = "Start receiver";
            StatsText.Text = "";
            ChkInput.IsChecked = false;
            SetStatus("Stopped", ready: false);
        }
    }

    private void OnMirrorStarted(MirrorStreamReceiver receiver)
    {
        if (_server?.LastClientAddress is not null)
        {
            _lastClientAddress = _server.LastClientAddress;
        }
        _pipeline?.Attach(receiver);

        receiver.Closed += reason =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isWebPlayerActive)
                {
                    _bitmap = null;
                    VideoImage.Source = null;
                    VideoImage.Visibility = Visibility.Collapsed;
                    Placeholder.Visibility = Visibility.Visible;
                }
                SetStatus(_running ? "Ready - waiting for a device" : "Stopped", ready: _running);
            });
        };

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isWebPlayerActive)
            {
                Placeholder.Visibility = Visibility.Collapsed;
                VideoImage.Visibility = Visibility.Visible;
            }
            SetStatus("Mirroring", ready: true);
        });
    }

    private async Task EnsureWebView2InitializedAsync()
    {
        if (WebPlayer.CoreWebView2 != null) return;

        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AirServerLite", "webview2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await WebPlayer.EnsureCoreWebView2Async(env);

            if (WebPlayer.CoreWebView2 != null)
            {
                WebPlayer.CoreWebView2.Settings.IsStatusBarEnabled = false;
                WebPlayer.CoreWebView2.Settings.AreDevToolsEnabled = false;
                WebPlayer.CoreWebView2.Settings.IsZoomControlEnabled = false;
            }

            WebPlayer.WebMessageReceived += OnWebPlayerMessageReceived;

            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "youtube_player.html");
            if (!File.Exists(htmlPath))
            {
                try
                {
                    var cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AirServerLite", "Assets");
                    Directory.CreateDirectory(cacheDir);
                    var cachedPath = Path.Combine(cacheDir, "youtube_player.html");

                    using var stream = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("AirServerLite.Assets.youtube_player.html");
                    if (stream != null)
                    {
                        using var fs = File.Create(cachedPath);
                        stream.CopyTo(fs);
                        htmlPath = cachedPath;
                        Log.Info(LogTag, "Extracted embedded youtube_player.html to " + htmlPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, "Failed to extract embedded youtube_player.html: " + ex.Message);
                }
            }

            if (File.Exists(htmlPath))
            {
                WebPlayer.Source = new Uri(htmlPath);
            }
            else
            {
                Log.Warn(LogTag, "youtube_player.html not found at " + htmlPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "Failed to initialize WebView2", ex);
        }
    }

    private void ShowWebPlayer(bool show)
    {
        _isWebPlayerActive = show;
        if (show)
        {
            WebPlayer.Visibility = Visibility.Visible;
            VideoImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Collapsed;
            BtnStopMedia.Visibility = Visibility.Visible;
            BtnHudStopMedia.Visibility = Visibility.Visible;
            BtnHudPlayPause.Visibility = Visibility.Visible;
        }
        else
        {
            WebPlayer.Visibility = Visibility.Collapsed;
            BtnStopMedia.Visibility = Visibility.Collapsed;
            BtnHudStopMedia.Visibility = Visibility.Collapsed;
            BtnHudPlayPause.Visibility = Visibility.Collapsed;

            if (WebPlayer.CoreWebView2 != null)
            {
                _ = WebPlayer.ExecuteScriptAsync("pauseMedia()");
            }

            if (_pipeline != null && VideoImage.Source != null)
            {
                VideoImage.Visibility = Visibility.Visible;
                Placeholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                VideoImage.Visibility = Visibility.Collapsed;
                Placeholder.Visibility = Visibility.Visible;
            }
        }
    }

    private async void OnYouTubeCastRequested(YouTubeCastLaunchArgs args)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            await EnsureWebView2InitializedAsync();
            ShowWebPlayer(true);

            if (!string.IsNullOrEmpty(args.VideoId))
            {
                SetStatus($"Đang phát YouTube: {args.VideoId}", ready: true);
                var startSec = args.StartSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WebPlayer.ExecuteScriptAsync($"loadYouTubeVideo('{args.VideoId}', {startSec})");
            }
            else if (!string.IsNullOrEmpty(args.PairingCode))
            {
                SetStatus($"YouTube TV: {args.PairingCode}", ready: true);
                WebPlayer.Source = new Uri("https://www.youtube.com/tv");
            }
            else
            {
                SetStatus("Đang kết nối YouTube Cast", ready: true);
            }
        });
    }

    private void OnYouTubeCastStopped()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ShowWebPlayer(false);
            SetStatus("Đã dừng YouTube Cast", ready: true);
        });
    }

    private async void OnMediaPlayStarted(AirPlay.MediaSession session)
    {
        _activeMediaSession = session;
        await Dispatcher.InvokeAsync(async () =>
        {
            await EnsureWebView2InitializedAsync();
            ShowWebPlayer(true);

            var videoId = DialServer.ExtractVideoId(session.ContentLocation);
            if (!string.IsNullOrEmpty(videoId))
            {
                SetStatus($"AirPlay YouTube: {videoId}", ready: true);
                var startSec = session.PositionSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WebPlayer.ExecuteScriptAsync($"loadYouTubeVideo('{videoId}', {startSec})");
            }
            else if (Uri.TryCreate(session.ContentLocation, UriKind.Absolute, out var uri))
            {
                SetStatus($"AirPlay Media: {uri.Host}", ready: true);
                WebPlayer.Source = uri;
            }
            else
            {
                SetStatus("AirPlay Media đang phát", ready: true);
            }
        });
    }

    private void OnMediaPlayStopped()
    {
        _activeMediaSession = null;
        Dispatcher.BeginInvoke(() =>
        {
            ShowWebPlayer(false);
            SetStatus("Đã dừng AirPlay Media", ready: true);
        });
    }

    private void OnWebPlayerMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeElem))
            {
                var type = typeElem.GetString();
                if (type == "timeUpdate" || type == "stateChange")
                {
                    if (root.TryGetProperty("currentTime", out var curElem) &&
                        root.TryGetProperty("duration", out var durElem))
                    {
                        var cur = curElem.GetSingle();
                        var dur = durElem.GetSingle();
                        if (_activeMediaSession != null)
                        {
                            _activeMediaSession.PositionSeconds = cur;
                            if (dur > 0) _activeMediaSession.DurationSeconds = dur;
                        }
                    }
                    if (type == "stateChange" && root.TryGetProperty("state", out var stateElem))
                    {
                        var state = stateElem.GetString();
                        if (_activeMediaSession != null)
                        {
                            _activeMediaSession.Rate = state == "playing" ? 1.0f : 0.0f;
                        }
                    }
                }
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- input wiring

    private List<string> GetWdaProbeUrls()
    {
        var urls = new List<string>
        {
            $"http://127.0.0.1:{_settings.Input.LocalPort}"
        };

        if (!string.IsNullOrWhiteSpace(_settings.Input.WdaBaseUrl))
        {
            urls.Add(_settings.Input.WdaBaseUrl);
        }

        var clientIp = _server?.LastClientAddress ?? _lastClientAddress;
        if (clientIp is not null && !IPAddress.IsLoopback(clientIp))
        {
            urls.Add($"http://{clientIp}:{_settings.Input.DevicePort}");
        }

        return urls.Distinct().ToList();
    }

    private async Task<bool> ProbeAndConnectWdaAsync(CancellationToken ct = default)
    {
        try
        {
            _iproxy ??= new IProxyHost(_settings.Input);
            if (_settings.Input.AutoStartIProxy)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _iproxy.EnsureStartedAsync(ct).ConfigureAwait(false);
                    }
                    catch { }
                }, ct);
            }

            var candidates = GetWdaProbeUrls();
            Log.Debug(LogTag, "Probing WDA at: " + string.Join(", ", candidates));

            var foundUrl = await WdaClient.ProbeCandidatesAsync(candidates, TimeSpan.FromMilliseconds(1500), ct);
            if (foundUrl is null)
            {
                Log.Debug(LogTag, "No WDA server answered on port " + _settings.Input.DevicePort + " (reverse mouse control inactive)");
                return false;
            }

            Log.Info(LogTag, "Found WDA at " + foundUrl + ", connecting...");
            _wda?.Dispose();
            _wda = new WdaClient(_settings.Input, foundUrl);
            _wda.StatusChanged += s => Dispatcher.BeginInvoke(() => Log.Info(LogTag, "WDA: " + s));

            if (!await _wda.ConnectAsync(ct))
            {
                Log.Warn(LogTag, "WDA reachable at " + foundUrl + " but connection handshake failed");
                return false;
            }

            _input?.Dispose();
            _input = new InputRouter(_wda, _mapper, _settings.Input);
            _input.ActionPerformed += a => Dispatcher.BeginInvoke(() => StatusText.Text = "Input: " + a);
            _input.Enabled = true;

            await Dispatcher.BeginInvoke(() =>
            {
                ChkInput.IsChecked = true;
                SetStatus("Input connected (" + foundUrl + ")", ready: true);
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "Probe/Connect WDA error", ex);
            return false;
        }
    }

    private void ShowInputGuide()
    {
        var guide = new InputGuideWindow(GetWdaProbeUrls(), async () => await ProbeAndConnectWdaAsync())
        {
            Owner = this
        };

        if (guide.ShowDialog() == true)
        {
            ChkInput.IsChecked = true;
            if (_input is not null) _input.Enabled = true;
        }
    }

    private async Task<bool> EnsureInputReadyAsync()
    {
        if (_input is not null && _wda is not null && _wda.IsConnected)
        {
            if (!_input.Enabled)
            {
                _input.Enabled = true;
                ChkInput.IsChecked = true;
            }
            return true;
        }

        SetStatus("Dò tìm cổng WDA (8100)...", ready: true);
        var connected = await ProbeAndConnectWdaAsync();
        if (!connected)
        {
            ChkInput.IsChecked = false;
            ShowInputGuide();
            return false;
        }
        return true;
    }

    private async void OnInputToggled(object sender, RoutedEventArgs e)
    {
        if (ChkInput.IsChecked == true)
        {
            if (_input is not null && _wda is not null && _wda.IsConnected)
            {
                _input.Enabled = true;
                return;
            }

            SetStatus("Dò tìm cổng WDA (8100)...", ready: true);
            var connected = await ProbeAndConnectWdaAsync();
            if (!connected)
            {
                ChkInput.IsChecked = false;
                ShowInputGuide();
            }
        }
        else
        {
            if (_input is not null) _input.Enabled = false;
        }
    }

    private async void OnHome(object sender, RoutedEventArgs e)
    {
        if (await EnsureInputReadyAsync()) _input?.PressHome();
    }

    private async void OnVolumeUp(object sender, RoutedEventArgs e)
    {
        if (await EnsureInputReadyAsync()) _input?.PressVolumeUp();
    }

    private async void OnVolumeDown(object sender, RoutedEventArgs e)
    {
        if (await EnsureInputReadyAsync()) _input?.PressVolumeDown();
    }

    private async void OnLock(object sender, RoutedEventArgs e)
    {
        if (await EnsureInputReadyAsync()) _input?.PressLock();
    }

    // ---------------------------------------------------------------- rendering

    private void OnPipelineFrameAvailable()
    {
        _frameWaiting = true;
        // Direct zero-delay presentation pass to Direct3D backbuffer
        Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderFrameIfWaiting);
    }

    private void RenderFrameIfWaiting()
    {
        if (!_frameWaiting || _pipeline is null) return;
        OnRendering(null, EventArgs.Empty);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_frameWaiting || _pipeline is null) return;
        _frameWaiting = false;

        var frame = _pipeline.AcquireFrame();
        if (frame is null) return;

        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width ||
                _bitmap.PixelHeight != frame.Height)
            {
                _isLandscape = frame.Width > frame.Height;
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96,
                                              PixelFormats.Bgra32, null);
                VideoImage.Source = _bitmap;
                _mapper.UpdateVideoSize(frame.Width, frame.Height);
                Placeholder.Visibility = Visibility.Collapsed;
                Log.Info(LogTag, $"Display surface {frame.Width}x{frame.Height} (Landscape: {_isLandscape})");
            }

            if (!_isWebPlayerActive)
            {
                if (VideoImage.Visibility != Visibility.Visible)
                    VideoImage.Visibility = Visibility.Visible;
                if (Placeholder.Visibility != Visibility.Collapsed)
                    Placeholder.Visibility = Visibility.Collapsed;
            }

            _bitmap.Lock();
            try
            {
                unsafe
                {
                    fixed (byte* src = frame.Pixels)
                    {
                        Buffer.MemoryCopy(src, (void*)_bitmap.BackBuffer,
                                          (long)_bitmap.BackBufferStride * frame.Height,
                                          (long)frame.Stride * frame.Height);
                    }
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
            }
            finally
            {
                _bitmap.Unlock();
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "Blit failed", ex);
        }
    }

    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e)
        => _mapper.UpdateControlSize(e.NewSize.Width, e.NewSize.Height);

    // ---------------------------------------------------------------- fullscreen & cinema

    private void ToggleFullscreen(bool? enable = null)
    {
        var target = enable ?? !_isFullscreen;
        if (target == _isFullscreen) return;
        _isFullscreen = target;

        if (_isFullscreen)
        {
            _prevWindowStyle = WindowStyle;
            _prevWindowState = WindowState;
            _prevBounds = new Rect(Left, Top, ActualWidth, ActualHeight);

            ToolbarBorder.Visibility = Visibility.Collapsed;
            StatusBarBorder.Visibility = Visibility.Collapsed;
            LogExpander.Visibility = Visibility.Collapsed;

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;

            BtnFullscreen.Content = "✕ Exit FS";
            ShowHudBriefly();
            Log.Info(LogTag, "Entered Cinema Fullscreen mode");
        }
        else
        {
            OverlayHud.Visibility = Visibility.Collapsed;
            _hudTimer?.Stop();

            WindowStyle = _prevWindowStyle;
            WindowState = _prevWindowState;
            if (_prevBounds.Width > 100 && _prevBounds.Height > 100)
            {
                Left = _prevBounds.Left;
                Top = _prevBounds.Top;
                Width = _prevBounds.Width;
                Height = _prevBounds.Height;
            }

            ToolbarBorder.Visibility = Visibility.Visible;
            StatusBarBorder.Visibility = Visibility.Visible;
            LogExpander.Visibility = Visibility.Visible;

            BtnFullscreen.Content = "⛶ Fullscreen";
            Log.Info(LogTag, "Exited Fullscreen mode");
        }
    }

    private void ShowHudBriefly()
    {
        if (!_isFullscreen) return;
        OverlayHud.Visibility = Visibility.Visible;
        _hudTimer?.Stop();
        _hudTimer?.Start();
    }

    private void OnFullscreenClicked(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnExitFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen(false);

    private void OnVideoMouseMove(object sender, MouseEventArgs e)
    {
        if (_isFullscreen) ShowHudBriefly();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyVolume((float)e.NewValue);
    }

    private void OnHudVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyVolume((float)e.NewValue);
    }

    private void ApplyVolume(float volume)
    {
        _currentVolume = Math.Clamp(volume, 0.0f, 1.0f);
        if (SliderHudVolume != null && Math.Abs(SliderHudVolume.Value - _currentVolume) > 0.01)
            SliderHudVolume.Value = _currentVolume;
        if (SliderVolume != null && Math.Abs(SliderVolume.Value - _currentVolume) > 0.01)
            SliderVolume.Value = _currentVolume;
        _server?.BroadcastAudioVolume(_currentVolume);

        if (WebPlayer?.CoreWebView2 != null)
        {
            var volStr = _currentVolume.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = WebPlayer.ExecuteScriptAsync($"setMediaVolume({volStr})");
        }
    }

    private void OnStopMediaClicked(object sender, RoutedEventArgs e)
    {
        ShowWebPlayer(false);
        _dialServer?.SetAppRunning(false);
    }

    private void OnHudStopMedia(object sender, RoutedEventArgs e)
    {
        ShowWebPlayer(false);
        _dialServer?.SetAppRunning(false);
    }

    private void OnHudPlayPause(object sender, RoutedEventArgs e)
    {
        if (WebPlayer?.CoreWebView2 != null)
        {
            _ = WebPlayer.ExecuteScriptAsync("if(player && typeof player.getPlayerState === 'function') { if(player.getPlayerState() === 1) pauseMedia(); else playMedia(); }");
        }
    }

    public async Task OpenYouTubeTvAsync()
    {
        await EnsureWebView2InitializedAsync();
        if (WebPlayer?.CoreWebView2 != null)
        {
            WebPlayer.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/4.0 Chrome/76.0.3809.146 TV Safari/537.36";
        }
        ShowWebPlayer(true);
        SetStatus("YouTube TV (Mở app YouTube > Cài đặt > Xem trên TV > Nhập mã TV)", ready: true);
        if (WebPlayer != null)
        {
            WebPlayer.Source = new Uri("https://www.youtube.com/tv");
        }
    }

    private async void OnTvCodeClicked(object sender, RoutedEventArgs e)
    {
        await OpenYouTubeTvAsync();
    }

    private void OnQuickPlayClicked(object sender, RoutedEventArgs e)
    {
        ShowQuickPlayDialog();
    }

    private void ShowQuickPlayDialog()
    {
        var inputWindow = new Window
        {
            Title = "Phát YouTube / Media URL",
            Width = 490,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("Bg"),
            ResizeMode = ResizeMode.NoResize
        };

        var sp = new StackPanel { Margin = new Thickness(16) };
        var label = new TextBlock
        {
            Text = "Dán link YouTube (hoặc video ID) để phát ngay trên màn hình:",
            Foreground = (Brush)FindResource("Text"),
            Margin = new Thickness(0, 0, 0, 8),
            FontWeight = FontWeights.SemiBold
        };
        var txtUrl = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("Panel"),
            Foreground = (Brush)FindResource("Text")
        };
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOpenFile = new Button { Content = "📂 Chọn video từ máy", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var btnTvMode = new Button { Content = "📺 YouTube TV (Mã TV)", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var btnPlay = new Button { Content = "▶ Phát video", Width = 95, Margin = new Thickness(0, 0, 8, 0) };
        var btnCancel = new Button { Content = "Đóng", Width = 70 };

        btnOpenFile.Click += async (s, ev) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Chọn file video để phát",
                Filter = "Video Files|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.m4v;*.ts|All Files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                inputWindow.Close();
                await PlayLocalVideoFileAsync(dlg.FileName);
            }
        };

        btnPlay.Click += async (s, ev) =>
        {
            var url = txtUrl.Text.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                inputWindow.Close();
                await EnsureWebView2InitializedAsync();
                ShowWebPlayer(true);

                var videoId = DialServer.ExtractVideoId(url);
                if (!string.IsNullOrEmpty(videoId))
                {
                    SetStatus($"Đang phát YouTube: {videoId}", ready: true);
                    await WebPlayer.ExecuteScriptAsync($"loadYouTubeVideo('{videoId}', 0)");
                }
                else if (File.Exists(url) && DialServer.IsSupportedVideoFile(url))
                {
                    await PlayLocalVideoFileAsync(url);
                }
                else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    SetStatus($"Đang phát: {uri.Host}", ready: true);
                    WebPlayer.Source = uri;
                }
            }
        };

        btnTvMode.Click += async (s, ev) =>
        {
            inputWindow.Close();
            await OpenYouTubeTvAsync();
        };

        btnCancel.Click += (s, ev) => inputWindow.Close();

        btnPanel.Children.Add(btnOpenFile);
        btnPanel.Children.Add(btnTvMode);
        btnPanel.Children.Add(btnPlay);
        btnPanel.Children.Add(btnCancel);
        sp.Children.Add(label);
        sp.Children.Add(txtUrl);
        sp.Children.Add(btnPanel);
        inputWindow.Content = sp;
        inputWindow.ShowDialog();
    }

    public async Task PlayLocalVideoFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;
        await EnsureWebView2InitializedAsync();
        ShowWebPlayer(true);
        SetStatus($"Đang phát file: {Path.GetFileName(filePath)}", ready: true);
        var fileUri = new Uri(filePath).AbsoluteUri;
        if (WebPlayer != null)
        {
            WebPlayer.Source = new Uri(fileUri);
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && DialServer.IsSupportedVideoFile(files[0]))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && DialServer.IsSupportedVideoFile(files[0]))
            {
                await PlayLocalVideoFileAsync(files[0]);
            }
        }
    }

    // ---------------------------------------------------------------- pointer & keys

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            return;
        }

        VideoHost.Focus();
        VideoHost.CaptureMouse();
        _input?.OnMouseDown(e.GetPosition(VideoHost), MouseButton.Left);
    }

    private void OnVideoMouseUp(object sender, MouseButtonEventArgs e)
    {
        VideoHost.ReleaseMouseCapture();
        _input?.OnMouseUp(e.GetPosition(VideoHost), MouseButton.Left);
    }

    private void OnVideoRightDown(object sender, MouseButtonEventArgs e)
        => _input?.OnMouseDown(e.GetPosition(VideoHost), MouseButton.Right);

    private void OnVideoWheel(object sender, MouseWheelEventArgs e)
        => _input?.OnMouseWheel(e.GetPosition(VideoHost), e.Delta);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Alt) != 0))
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (_isFullscreen && e.Key == Key.Escape)
        {
            ToggleFullscreen(false);
            e.Handled = true;
            return;
        }

        if (_input is not null && _input.OnKeyDown(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        _input?.OnTextInput(e.Text);
        base.OnPreviewTextInput(e);
    }

    // ---------------------------------------------------------------- chrome

    private void SetStatus(string text, bool ready)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(ready ? "Good" : "Bad");
    }

    private void OnLogEmitted(LogEntry entry)
    {
        if (entry.Level < LogLevel.Info) return;

        Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add($"{entry.Time:HH:mm:ss} [{entry.Tag}] {entry.Message}");
            while (LogList.Items.Count > 500) LogList.Items.RemoveAt(0);
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        Log.Emitted -= OnLogEmitted;
        StopReceiver();
    }
}
