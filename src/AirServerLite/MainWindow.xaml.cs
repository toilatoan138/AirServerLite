using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirServerLite.AirPlay;
using AirServerLite.AirPlay.Crypto;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;
using AirServerLite.Discovery;
using AirServerLite.Input;
using AirServerLite.UI;
using AirServerLite.Video;

namespace AirServerLite;

public partial class MainWindow : Window
{
    private const string LogTag = "ui";

    private readonly AppSettings _settings;
    private readonly CoordinateMapper _mapper = new();

    private DeviceIdentity? _identity;
    private MdnsAdvertiser? _mdns;
    private AirPlayServer? _server;
    private VideoPipeline? _pipeline;
    private WriteableBitmap? _bitmap;

    private IProxyHost? _iproxy;
    private WdaClient? _wda;
    private InputRouter? _input;
    private IPAddress? _lastClientAddress;

    private bool _running;
    private volatile bool _frameWaiting;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        Log.MinLevel = _settings.ParsedLogLevel;

        Log.Emitted += OnLogEmitted;

        Title = $"AirServer-LITE - {_settings.DeviceName}";

        Loaded += OnLoaded;
        Closing += OnClosing;

        // Pull frames on the WPF render tick instead of a timer: it is already synchronised
        // with the compositor, so a frame drawn here is on screen at the very next vblank
        // with no extra scheduling delay.
        CompositionTarget.Rendering += OnRendering;
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
            _pipeline.FrameAvailable += () => _frameWaiting = true;
            _pipeline.StatsUpdated += line => Dispatcher.BeginInvoke(() => StatsText.Text = line);
            _pipeline.Start();

            _server = new AirPlayServer(_identity, nic.Address, _settings);
            _server.MirrorStarted += OnMirrorStarted;
            _server.SessionStateChanged += state =>
                Dispatcher.BeginInvoke(() => SetStatus(state + " - " + nic.Address, ready: true));
            _server.Start();

            _mdns = new MdnsAdvertiser(_identity, nic.Address, _settings.AirPlayPort,
                                       _settings.AdvertiseRaop);
            _mdns.Start();

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
            _mdns = null;
            _server = null;
            _pipeline = null;
            _input = null;
            _wda = null;
            _iproxy = null;
            _bitmap = null;
            _running = false;

            VideoImage.Source = null;
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
        Dispatcher.BeginInvoke(() =>
        {
            Placeholder.Visibility = Visibility.Collapsed;
            SetStatus("Mirroring", ready: true);
        });
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
                _ = _iproxy.EnsureStartedAsync(ct);
            }

            var candidates = GetWdaProbeUrls();
            Log.Info(LogTag, "Probing WDA at: " + string.Join(", ", candidates));

            var foundUrl = await WdaClient.ProbeCandidatesAsync(candidates, TimeSpan.FromMilliseconds(1500), ct);
            if (foundUrl is null)
            {
                Log.Warn(LogTag, "No WDA server answered on port " + _settings.Input.DevicePort);
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
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96,
                                              PixelFormats.Bgra32, null);
                VideoImage.Source = _bitmap;
                _mapper.UpdateVideoSize(frame.Width, frame.Height);
                Placeholder.Visibility = Visibility.Collapsed;
                Log.Info(LogTag, $"Display surface {frame.Width}x{frame.Height}");
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
                                frame.Pixels, frame.Stride, 0);
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "Blit failed", ex);
        }
    }

    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e)
        => _mapper.UpdateControlSize(e.NewSize.Width, e.NewSize.Height);

    // ---------------------------------------------------------------- pointer & keys

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
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
