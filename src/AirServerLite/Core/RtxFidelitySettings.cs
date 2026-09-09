namespace AirServerLite.Core;

public enum RtxProfileMode
{
    RtxUltraFidelity,
    Balanced,
    PowerSaver
}

public enum RtxUpscaleMode
{
    FastBilinear,
    Bicubic,
    NvidiaDirectionalLanczos
}

public sealed class RtxFidelitySettings
{
    public RtxProfileMode Profile { get; set; } = RtxProfileMode.RtxUltraFidelity;
    public RtxUpscaleMode UpscaleMode { get; set; } = RtxUpscaleMode.NvidiaDirectionalLanczos;

    // NVIDIA Image Scaling (NIS) & Sharpness
    public bool EnableNvidiaImageScaling { get; set; } = true;
    public float Sharpness { get; set; } = 0.80f; // 0.0 to 1.0

    // Intel 14th Gen 24-Thread Motion Frame Interpolation (MEMC)
    public bool EnableMotionInterpolation { get; set; } = true;
    public int TargetFps { get; set; } = 120;
    public int CpuThreadCount { get; set; } = Math.Clamp(Environment.ProcessorCount, 8, 24);

    // RTX Digital Vibrance & OLED Gamut
    public bool EnableRtxDigitalVibrance { get; set; } = true;
    public float VibranceBoost { get; set; } = 0.30f;

    // Studio Audio DSP
    public bool EnableStudioAudio { get; set; } = true;
    public bool EnableSpatialAudio { get; set; } = true;
    public bool EnableBassEnhancer { get; set; } = true;

    // Telemetry HUD
    public bool ShowTelemetryHud { get; set; } = true;

    public static RtxFidelitySettings CreateRtxUltra() => new()
    {
        Profile = RtxProfileMode.RtxUltraFidelity,
        UpscaleMode = RtxUpscaleMode.NvidiaDirectionalLanczos,
        EnableNvidiaImageScaling = true,
        Sharpness = 0.85f,
        EnableMotionInterpolation = true,
        TargetFps = 120,
        CpuThreadCount = Math.Clamp(Environment.ProcessorCount, 8, 24),
        EnableRtxDigitalVibrance = true,
        VibranceBoost = 0.35f,
        EnableStudioAudio = true,
        EnableSpatialAudio = true,
        EnableBassEnhancer = true,
        ShowTelemetryHud = true
    };

    public static RtxFidelitySettings CreateBalanced() => new()
    {
        Profile = RtxProfileMode.Balanced,
        UpscaleMode = RtxUpscaleMode.Bicubic,
        EnableNvidiaImageScaling = true,
        Sharpness = 0.40f,
        EnableMotionInterpolation = false,
        TargetFps = 60,
        CpuThreadCount = 4,
        EnableRtxDigitalVibrance = false,
        VibranceBoost = 0.0f,
        EnableStudioAudio = false,
        EnableSpatialAudio = false,
        EnableBassEnhancer = false,
        ShowTelemetryHud = false
    };

    public static RtxFidelitySettings CreatePowerSaver() => new()
    {
        Profile = RtxProfileMode.PowerSaver,
        UpscaleMode = RtxUpscaleMode.FastBilinear,
        EnableNvidiaImageScaling = false,
        Sharpness = 0.0f,
        EnableMotionInterpolation = false,
        TargetFps = 60,
        CpuThreadCount = 2,
        EnableRtxDigitalVibrance = false,
        VibranceBoost = 0.0f,
        EnableStudioAudio = false,
        EnableSpatialAudio = false,
        EnableBassEnhancer = false,
        ShowTelemetryHud = false
    };
}
