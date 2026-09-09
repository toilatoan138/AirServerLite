namespace AirServerLite.Core;

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Generic
}

public enum HardwareTier
{
    HighPerformance,
    Balanced,
    LowPower
}

public sealed class HardwareProfile
{
    public string CpuName { get; set; } = "Generic CPU";
    public string GpuName { get; set; } = "Generic GPU";
    public GpuVendor GpuVendor { get; set; } = GpuVendor.Generic;
    public int CpuLogicalCores { get; set; } = Environment.ProcessorCount;
    public bool HasAvx2 { get; set; }
    public bool HasSse41 { get; set; }
    public bool HasArmAdvSimd { get; set; }
    public HardwareTier Tier { get; set; } = HardwareTier.Balanced;

    public string DisplaySummary => $"{GpuName} | {CpuName} ({CpuLogicalCores}T)";
}
