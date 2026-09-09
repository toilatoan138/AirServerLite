using System.Windows;
using AirServerLite.Core;

namespace AirServerLite.UI;

public partial class RtxFidelityDialog : Window
{
    public RtxFidelitySettings Settings { get; }

    // Starts true so the Checked/ValueChanged handlers that XAML raises while
    // InitializeComponent() parses IsChecked="True" / Value="..." are ignored
    // until Settings has been assigned. LoadSettingsIntoUI clears it in its finally.
    private bool _suppressEvents = true;

    public event Action<RtxFidelitySettings>? SettingsApplied;

    public RtxFidelityDialog(RtxFidelitySettings currentSettings)
    {
        Settings = currentSettings ?? RtxFidelitySettings.CreateRtxUltra();
        InitializeComponent();
        LoadSettingsIntoUI(Settings);
        _suppressEvents = false;
    }

    private void LoadSettingsIntoUI(RtxFidelitySettings s)
    {
        _suppressEvents = true;
        try
        {
            switch (s.Profile)
            {
                case RtxProfileMode.RtxUltraFidelity:
                    RbPresetUltra.IsChecked = true;
                    break;
                case RtxProfileMode.Balanced:
                    RbPresetBalanced.IsChecked = true;
                    break;
                case RtxProfileMode.PowerSaver:
                    RbPresetPowerSaver.IsChecked = true;
                    break;
            }

            ChkNis.IsChecked = s.EnableNvidiaImageScaling;
            SliderSharpness.Value = s.Sharpness;
            TxtSharpnessVal.Text = $"{s.Sharpness:P0}";

            ChkMemc.IsChecked = s.EnableMotionInterpolation;
            SliderCpuThreads.Value = s.CpuThreadCount;
            TxtCpuThreads.Text = $"{s.CpuThreadCount} Threads";

            ChkVibrance.IsChecked = s.EnableRtxDigitalVibrance;
            SliderVibrance.Value = s.VibranceBoost;
            TxtVibranceVal.Text = $"+{s.VibranceBoost * 100:F0}%";

            ChkStudioAudio.IsChecked = s.EnableStudioAudio;
            ChkTelemetryHud.IsChecked = s.ShowTelemetryHud;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnPresetUltraChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || Settings is null) return;
        var ultra = RtxFidelitySettings.CreateRtxUltra();
        CopySettings(ultra, Settings);
        LoadSettingsIntoUI(Settings);
    }

    private void OnPresetBalancedChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || Settings is null) return;
        var balanced = RtxFidelitySettings.CreateBalanced();
        CopySettings(balanced, Settings);
        LoadSettingsIntoUI(Settings);
    }

    private void OnPresetPowerSaverChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || Settings is null) return;
        var power = RtxFidelitySettings.CreatePowerSaver();
        CopySettings(power, Settings);
        LoadSettingsIntoUI(Settings);
    }

    private static void CopySettings(RtxFidelitySettings src, RtxFidelitySettings dst)
    {
        dst.Profile = src.Profile;
        dst.UpscaleMode = src.UpscaleMode;
        dst.EnableNvidiaImageScaling = src.EnableNvidiaImageScaling;
        dst.Sharpness = src.Sharpness;
        dst.EnableMotionInterpolation = src.EnableMotionInterpolation;
        dst.TargetFps = src.TargetFps;
        dst.CpuThreadCount = src.CpuThreadCount;
        dst.EnableRtxDigitalVibrance = src.EnableRtxDigitalVibrance;
        dst.VibranceBoost = src.VibranceBoost;
        dst.EnableStudioAudio = src.EnableStudioAudio;
        dst.EnableSpatialAudio = src.EnableSpatialAudio;
        dst.EnableBassEnhancer = src.EnableBassEnhancer;
        dst.ShowTelemetryHud = src.ShowTelemetryHud;
    }

    private void OnSharpnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        float val = (float)SliderSharpness.Value;
        TxtSharpnessVal.Text = $"{val:P0}";
        Settings.Sharpness = val;
    }

    private void OnCpuThreadsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        int threads = (int)SliderCpuThreads.Value;
        TxtCpuThreads.Text = $"{threads} Threads";
        Settings.CpuThreadCount = threads;
    }

    private void OnVibranceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        float val = (float)SliderVibrance.Value;
        TxtVibranceVal.Text = $"+{val * 100:F0}%";
        Settings.VibranceBoost = val;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        Settings.EnableNvidiaImageScaling = ChkNis.IsChecked == true;
        Settings.EnableMotionInterpolation = ChkMemc.IsChecked == true;
        Settings.TargetFps = Settings.EnableMotionInterpolation ? 120 : 60;
        Settings.EnableRtxDigitalVibrance = ChkVibrance.IsChecked == true;
        Settings.EnableStudioAudio = ChkStudioAudio.IsChecked == true;
        Settings.ShowTelemetryHud = ChkTelemetryHud.IsChecked == true;
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        OnSettingChanged(sender, e);
        SettingsApplied?.Invoke(Settings);
        DialogResult = true;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
