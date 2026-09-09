using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace AirServerLite.Core;

public static class HardwareDetector
{
    private static HardwareProfile? _cachedProfile;

    public static HardwareProfile Detect()
    {
        if (_cachedProfile != null) return _cachedProfile;

        var profile = new HardwareProfile
        {
            CpuLogicalCores = Environment.ProcessorCount,
            HasAvx2 = Avx2.IsSupported,
            HasSse41 = Sse41.IsSupported,
            HasArmAdvSimd = AdvSimd.IsSupported,
            CpuName = QueryCpuName(),
            GpuName = QueryGpuName()
        };

        profile.GpuVendor = ParseVendor(profile.GpuName);
        profile.Tier = CalculateTier(profile);

        _cachedProfile = profile;
        return profile;
    }

    public static GpuVendor ParseVendor(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return GpuVendor.Generic;
        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("GTX", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Nvidia;

        if (gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Amd;

        if (gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("UHD", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Intel;

        return GpuVendor.Generic;
    }

    public static HardwareTier CalculateTier(HardwareProfile p)
    {
        // Low power: 4 or fewer threads, or no AVX2 on x86, or Microsoft Basic Display
        if (p.CpuLogicalCores <= 4 || (!p.HasAvx2 && !p.HasArmAdvSimd) || p.GpuVendor == GpuVendor.Generic)
            return HardwareTier.LowPower;

        // High performance: discrete GPU or modern high-thread CPU (>= 12 threads)
        if (p.CpuLogicalCores >= 12 && (p.GpuVendor == GpuVendor.Nvidia || p.GpuVendor == GpuVendor.Amd || p.GpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase)))
            return HardwareTier.HighPerformance;

        return HardwareTier.Balanced;
    }

    public static RtxFidelitySettings GetRecommendedSettings(HardwareProfile p)
    {
        p.Tier = CalculateTier(p);
        switch (p.Tier)
        {
            case HardwareTier.HighPerformance:
                var ultra = RtxFidelitySettings.CreateRtxUltra();
                ultra.CpuThreadCount = Math.Clamp(p.CpuLogicalCores, 4, 32);
                return ultra;

            case HardwareTier.Balanced:
                var balanced = RtxFidelitySettings.CreateBalanced();
                balanced.CpuThreadCount = Math.Clamp(p.CpuLogicalCores / 2, 2, 8);
                return balanced;

            case HardwareTier.LowPower:
            default:
                var power = RtxFidelitySettings.CreatePowerSaver();
                power.CpuThreadCount = Math.Clamp(p.CpuLogicalCores, 1, 4);
                return power;
        }
    }

    private static string QueryCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "x64 Processor";
    }

    private static string QueryGpuName()
    {
        try
        {
            // Iterate display adapter class keys to find dedicated GPU first, else integrated
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey != null)
            {
                string? firstFound = null;
                foreach (var sub in baseKey.GetSubKeyNames())
                {
                    if (sub.Length == 4 && char.IsDigit(sub[0]))
                    {
                        using var cardKey = baseKey.OpenSubKey(sub);
                        var desc = cardKey?.GetValue("DriverDesc") as string;
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            firstFound ??= desc;
                            // Prioritize discrete GPUs over integrated
                            if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                desc.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                            {
                                return desc.Trim();
                            }
                        }
                    }
                }
                if (firstFound != null) return firstFound.Trim();
            }
        }
        catch { }
        return "Direct3D 11 Display Adapter";
    }
}
