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

    private bool _isFullscreen;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
    private WindowState _prevWindowState = WindowState.Normal;
    private Rect _prevBounds;
    private System.Windows.Threading.DispatcherTimer? _hudTimer;
    private bool _isLandscape;
    private float _currentVolume = 1.0f;

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
            _server.MediaPlayStarted += OnMediaPlayStarted;
            _server.MediaPlayStopped += OnMediaPlayStopped;
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

    private void OnMediaPlayStarted(AirPlay.MediaSession session)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Placeholder.Visibility = Visibility.Collapsed;
            SetStatus($"Media Streaming: {session.ClientProcName ?? "YouTube"}", ready: true);
            Log.Info(LogTag, $"AirPlay Media streaming: {session.ContentLocation}");
        });
    }

    private void OnMediaPlayStopped()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SetStatus("Media stopped", ready: true);
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
                var newIsLandscape = frame.Width > frame.Height;
                if (!_isFullscreen && newIsLandscape != _isLandscape)
                {
                    _isLandscape = newIsLandscape;
                    if (_isLandscape && Width < Height)
                    {
                        var temp = Width;
                        Width = Math.Max(Height, 880);
                        Height = Math.Max(temp, 560);
                    }
                    else if (!_isLandscape && Width > Height)
                    {
                        var temp = Width;
                        Width = Math.Min(Height, 680);
                        Height = Math.Max(temp, 880);
                    }
                }

                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96,
                                              PixelFormats.Bgra32, null);
                VideoImage.Source = _bitmap;
                _mapper.UpdateVideoSize(frame.Width, frame.Height);
                Placeholder.Visibility = Visibility.Collapsed;
                Log.Info(LogTag, $"Display surface {frame.Width}x{frame.Height} (Landscape: {_isLandscape})");
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
        _currentVolume = (float)e.NewValue;
        if (SliderHudVolume != null && Math.Abs(SliderHudVolume.Value - _currentVolume) > 0.01)
            SliderHudVolume.Value = _currentVolume;
        _server?.BroadcastAudioVolume(_currentVolume);
    }

    private void OnHudVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _currentVolume = (float)e.NewValue;
        if (SliderVolume != null && Math.Abs(SliderVolume.Value - _currentVolume) > 0.01)
            SliderVolume.Value = _currentVolume;
        _server?.BroadcastAudioVolume(_currentVolume);
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
            Width = 460,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("Bg"),
            ResizeMode = ResizeMode.NoResize
        };

        var sp = new StackPanel { Margin = new Thickness(16) };
        var label = new TextBlock
        {
            Text = "Dán link YouTube (hoặc video URL) để mở:",
            Foreground = (Brush)FindResource("Text"),
            Margin = new Thickness(0, 0, 0, 8),
            FontWeight = FontWeights.SemiBold
        };
        var txtUrl = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("Panel"),
            Foreground = (Brush)FindResource("Text")
        };
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnPlay = new Button { Content = "Mở video", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var btnCancel = new Button { Content = "Đóng", Width = 70 };

        btnPlay.Click += (s, ev) =>
        {
            var url = txtUrl.Text.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    Log.Info(LogTag, "Opened YouTube URL: " + url);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, "Could not launch URL: " + ex.Message);
                }
                inputWindow.Close();
            }
        };
        btnCancel.Click += (s, ev) => inputWindow.Close();

        btnPanel.Children.Add(btnPlay);
        btnPanel.Children.Add(btnCancel);
        sp.Children.Add(label);
        sp.Children.Add(txtUrl);
        sp.Children.Add(btnPanel);
        inputWindow.Content = sp;
        inputWindow.ShowDialog();
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
