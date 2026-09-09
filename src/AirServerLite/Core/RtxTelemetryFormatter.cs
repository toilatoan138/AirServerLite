namespace AirServerLite.Core;

/// <summary>
/// Formats real-time performance telemetry for RTX GPU &amp; Intel 14th Gen CPU fidelity pipeline.
/// </summary>
public static class RtxTelemetryFormatter
{
    public static string FormatFps(double rawFps, bool isMemcEnabled)
    {
        double displayFps = isMemcEnabled ? rawFps * 2.0 : rawFps;
        string tag = isMemcEnabled ? " [120Hz MEMC]" : "";
        return $"{displayFps:F1} FPS{tag}";
    }

    public static string FormatHardwareInfo(string decoderMode, int cpuThreads)
    {
        string gpuTag = (decoderMode.Contains("D3D11", StringComparison.OrdinalIgnoreCase) ||
                         decoderMode.Contains("NVDEC", StringComparison.OrdinalIgnoreCase))
            ? "RTX 4060 (NVDEC D3D11)"
            : $"GPU ({decoderMode})";
        return $"{gpuTag} | i7-14650HX {cpuThreads}T AVX2";
    }

    public static string FormatEnhancements(RtxFidelitySettings settings)
    {
        var parts = new List<string>();
        if (settings.EnableNvidiaImageScaling)
        {
            parts.Add($"NIS {settings.Sharpness:P0}");
        }
        if (settings.EnableRtxDigitalVibrance)
        {
            parts.Add($"Vibrance +{settings.VibranceBoost * 100:F0}%");
        }
        if (settings.EnableStudioAudio)
        {
            parts.Add("3D Studio Audio");
        }
        return parts.Count > 0 ? string.Join(" | ", parts) : "Stock Raw";
    }

    public static string FormatUniversalHardware(HardwareProfile profile, string decoderMode)
    {
        string gpuTag;
        if (profile.GpuVendor == GpuVendor.Nvidia)
            gpuTag = $"{profile.GpuName} (NVDEC {decoderMode})";
        else if (profile.GpuVendor == GpuVendor.Amd)
            gpuTag = $"{profile.GpuName} (AMF {decoderMode})";
        else if (profile.GpuVendor == GpuVendor.Intel)
            gpuTag = $"{profile.GpuName} (QSV {decoderMode})";
        else
            gpuTag = $"{profile.GpuName} ({decoderMode})";

        string simdTag = profile.HasAvx2 ? "AVX2" : (profile.HasSse41 ? "SSE4.1" : "SIMD");
        return $"{gpuTag} | {profile.CpuName} {profile.CpuLogicalCores}T {simdTag}";
    }

    public static string FormatFullSummary(double rawFps, string decoderMode, RtxFidelitySettings settings)
    {
        return $"{FormatFps(rawFps, settings.EnableMotionInterpolation)} | {FormatHardwareInfo(decoderMode, settings.CpuThreadCount)} | {FormatEnhancements(settings)}";
    }
}
