# Universal Hardware Compatibility & AV Stability Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a universal, hardware-adaptive rendering and audio processing engine that runs optimally on ANY PC configuration—including AMD Ryzen / Radeon, Intel Core / Arc / Iris Xe / UHD, NVIDIA GeForce GTX / RTX, and low-end 2-to-4-core CPUs—ensuring crystal-clear audio with zero crackle/popping and buttery-smooth video with zero lag or stuttering.

**Architecture:**
- **Universal Hardware Profiler:** Query GPU vendor (NVIDIA, AMD, Intel, Microsoft) via DXGI/WMI and CPU topology (cores, logical threads, SIMD instruction sets: AVX2, SSE4.1, ARM64 AdvSimd, Scalar). Auto-classifies the host PC into Tier 1 (High Performance), Tier 2 (Balanced), or Tier 3 (Efficiency/Low Power).
- **Universal SIMD Vectorization:** Motion Frame Synthesizer (MEMC) upgraded to support 256-bit AVX2 (Intel/AMD), 128-bit SSE4.1 (legacy x64), 128-bit ARM NEON (Windows on ARM), and Scalar, dynamically scaling from 2 to 64 threads based on actual core count.
- **Resilient Multi-Stage GPU Decoder:** `H264Decoder` implements a 3-tier cascade: Direct3D 11 NVDEC/AMF/QSV -> DXVA2 -> Multi-threaded CPU software decoding (`threads = ProcessorCount`), guaranteeing instant stream playback even on virtual machines or basic drivers.
- **Zero-Crackle Audio DSP & Elastic Drift Sync:** Click-free cosine fade-out and fade-in on underrun (eliminating DC-offset crackles/pops), high-fidelity polyphase/linear sample rate resampler (44.1kHz <-> 48kHz for seamless iOS and Android Miracast interoperability), and zero-crossing micro-elastic clock drift compensation.
- **Integrated AAC/LPCM Miracast Audio Decoder:** Decodes MPEG-TS PES audio into 16-bit stereo PCM via FFmpeg, piping Android audio straight into `AudioPlayer`.
- **Adaptive Universal HUD & UI:** Dynamically adapts labels and presets to actual detected hardware (e.g., "AMD Radeon RX 7800 XT | Ryzen 7 7800X3D" vs "NVIDIA RTX 4060 | Core i7-14650HX" vs "Intel Iris Xe | Core i5-1135G7").

**Tech Stack:** C# 12 / .NET 8 WPF, FFmpeg.AutoGen 9.0 (`avcodec`, `avutil`, `swscale`, `swresample`), Windows `waveOut` API, Direct3D 11, System.Runtime.Intrinsics (AVX2, SSE4.1, ARM AdvSimd), xUnit.

---

### Task 1: Universal Hardware Profiler & Auto-Tuner

**Files:**
- Create: `src/AirServerLite/Core/HardwareDetector.cs`
- Create: `src/AirServerLite/Core/HardwareProfile.cs`
- Test: `tests/AirServerLite.Tests/Core/HardwareDetectorTests.cs`

- [ ] **Step 1: Write failing test for HardwareProfile and HardwareDetector**

```csharp
// tests/AirServerLite.Tests/Core/HardwareDetectorTests.cs
using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.Core;

public class HardwareDetectorTests
{
    [Fact]
    public void DetectHardware_ReturnsValidGpuAndCpuInfo()
    {
        var profile = HardwareDetector.Detect();

        Assert.NotNull(profile);
        Assert.False(string.IsNullOrWhiteSpace(profile.CpuName));
        Assert.False(string.IsNullOrWhiteSpace(profile.GpuName));
        Assert.True(profile.CpuLogicalCores > 0);
        Assert.True(profile.Tier == HardwareTier.HighPerformance ||
                    profile.Tier == HardwareTier.Balanced ||
                    profile.Tier == HardwareTier.LowPower);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7800 XT", GpuVendor.Amd)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Display Adapter", GpuVendor.Generic)]
    public void ParseGpuVendor_IdentifiesVendorsCorrectly(string gpuName, GpuVendor expected)
    {
        var vendor = HardwareDetector.ParseVendor(gpuName);
        Assert.Equal(expected, vendor);
    }

    [Fact]
    public void AutoTuneSettings_LowCoreCount_SelectsLowPowerPreset()
    {
        var lowProfile = new HardwareProfile
        {
            CpuName = "Intel Celeron N4020",
            GpuName = "Intel UHD Graphics 600",
            GpuVendor = GpuVendor.Intel,
            CpuLogicalCores = 2,
            HasAvx2 = false,
            HasSse41 = true
        };

        var settings = HardwareDetector.GetRecommendedSettings(lowProfile);

        Assert.False(settings.EnableMotionInterpolation);
        Assert.Equal(60, settings.TargetFps);
        Assert.Equal(RtxUpscaleMode.FastBilinear, settings.UpscaleMode);
    }

    [Fact]
    public void AutoTuneSettings_HighEndAmd_SelectsUltraWithAmdBranding()
    {
        var amdProfile = new HardwareProfile
        {
            CpuName = "AMD Ryzen 7 7800X3D",
            GpuName = "AMD Radeon RX 7800 XT",
            GpuVendor = GpuVendor.Amd,
            CpuLogicalCores = 16,
            HasAvx2 = true,
            HasSse41 = true
        };

        var settings = HardwareDetector.GetRecommendedSettings(amdProfile);

        Assert.True(settings.EnableMotionInterpolation);
        Assert.Equal(120, settings.TargetFps);
        Assert.Equal(RtxUpscaleMode.NvidiaDirectionalLanczos, settings.UpscaleMode);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~HardwareDetectorTests" -c Debug`
Expected: Compilation failure (types `HardwareDetector`, `HardwareProfile`, `GpuVendor`, `HardwareTier` do not exist).

- [ ] **Step 3: Implement HardwareProfile and HardwareDetector**

Create `src/AirServerLite/Core/HardwareProfile.cs`:
```csharp
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
```

Create `src/AirServerLite/Core/HardwareDetector.cs`:
```csharp
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

    private static HardwareTier CalculateTier(HardwareProfile p)
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
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            var name = key?.GetValue("DriverDesc") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return "Direct3D 11 Display Adapter";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~HardwareDetectorTests" -c Debug`
Expected: PASS (4 tests passed).

- [ ] **Step 5: Commit changes**

```bash
git add src/AirServerLite/Core/HardwareProfile.cs src/AirServerLite/Core/HardwareDetector.cs tests/AirServerLite.Tests/Core/HardwareDetectorTests.cs
git commit -m "feat(hardware): implement universal hardware profiler and auto-tuning engine"
```

---

### Task 2: Universal SIMD Motion Frame Synthesizer (AVX2, SSE4.1, ARM64, Scalar)

**Files:**
- Modify: `src/AirServerLite/Video/Processing/MotionFrameSynthesizer.cs`
- Modify: `tests/AirServerLite.Tests/Video/MotionFrameSynthesizerTests.cs`

- [ ] **Step 1: Write tests for dynamic thread scaling and fallback**

Add to `tests/AirServerLite.Tests/Video/MotionFrameSynthesizerTests.cs`:
```csharp
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public void Synthesize_ArbitraryThreadCounts_ProducesValidBlend(int threads)
    {
        var synth = new MotionFrameSynthesizer(threads);
        var f1 = CreateFrame(100, 100, 0x10);
        var f2 = CreateFrame(100, 100, 0x30);

        var result = synth.Synthesize(f1, f2);

        Assert.NotNull(result);
        Assert.Equal(100, result.Width);
        Assert.Equal(100, result.Height);
        Assert.Equal(0x20, result.Pixels[0]); // (0x10 + 0x30 + 1) / 2 = 0x20
    }
```

- [ ] **Step 2: Run test to verify execution**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MotionFrameSynthesizerTests" -c Debug`

- [ ] **Step 3: Update MotionFrameSynthesizer with universal SIMD cascade**

Update `src/AirServerLite/Video/Processing/MotionFrameSynthesizer.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace AirServerLite.Video.Processing;

/// <summary>
/// Universal Motion Estimation & Frame Synthesizer (MEMC).
/// Supports AVX2 (Intel/AMD), SSE4.1 (legacy x64), ARM AdvSimd (NEON), and Scalar.
/// Dynamically scales from 2 to 64 threads.
/// </summary>
public sealed class MotionFrameSynthesizer
{
    private readonly int _threads;
    private readonly BgraFrame[] _frameSlots = new BgraFrame[2];
    private int _slotIndex = 0;

    public MotionFrameSynthesizer(int threads = 16)
    {
        _threads = Math.Clamp(threads, 2, 64);
    }

    public unsafe BgraFrame Synthesize(BgraFrame prev, BgraFrame next)
    {
        if (prev.Width != next.Width || prev.Height != next.Height || prev.Pixels.Length != next.Pixels.Length)
        {
            return next;
        }

        int totalBytes = prev.Pixels.Length;
        _slotIndex = (_slotIndex + 1) % _frameSlots.Length;
        var frameSlot = _frameSlots[_slotIndex];

        if (frameSlot == null || frameSlot.Pixels.Length != totalBytes)
        {
            frameSlot = new BgraFrame
            {
                Pixels = new byte[totalBytes]
            };
            _frameSlots[_slotIndex] = frameSlot;
        }

        frameSlot.Width = prev.Width;
        frameSlot.Height = prev.Height;
        frameSlot.Stride = prev.Stride;
        frameSlot.Timestamp = (prev.Timestamp + next.Timestamp) / 2;
        frameSlot.DecodedUtc = DateTime.UtcNow;

        fixed (byte* pPrev = prev.Pixels)
        fixed (byte* pNext = next.Pixels)
        fixed (byte* pDst = frameSlot.Pixels)
        {
            nint prevPtr = (nint)pPrev;
            nint nextPtr = (nint)pNext;
            nint dstPtr = (nint)pDst;
            int sliceSize = totalBytes / _threads;

            Parallel.For(0, _threads, t =>
            {
                int start = t * sliceSize;
                int end = (t == _threads - 1) ? totalBytes : start + sliceSize;
                byte* localPrev = (byte*)prevPtr;
                byte* localNext = (byte*)nextPtr;
                byte* localDst = (byte*)dstPtr;

                int i = start;

                // 1. AVX2 256-bit Vector Loop (Intel & AMD)
                if (Avx2.IsSupported)
                {
                    int vecSize = Vector256<byte>.Count; // 32 bytes
                    for (; i + vecSize <= end; i += vecSize)
                    {
                        var v1 = Avx2.LoadVector256(localPrev + i);
                        var v2 = Avx2.LoadVector256(localNext + i);
                        var vAvg = Avx2.Average(v1, v2);
                        Avx2.Store(localDst + i, vAvg);
                    }
                }
                // 2. SSE2/SSE4.1 128-bit Vector Loop (Older Intel/AMD)
                else if (Sse2.IsSupported)
                {
                    int vecSize = Vector128<byte>.Count; // 16 bytes
                    for (; i + vecSize <= end; i += vecSize)
                    {
                        var v1 = Sse2.LoadVector128(localPrev + i);
                        var v2 = Sse2.LoadVector128(localNext + i);
                        var vAvg = Sse2.Average(v1, v2);
                        Sse2.Store(localDst + i, vAvg);
                    }
                }
                // 3. ARM64 AdvSimd (Windows on ARM / Snapdragon)
                else if (AdvSimd.IsSupported)
                {
                    int vecSize = Vector128<byte>.Count;
                    for (; i + vecSize <= end; i += vecSize)
                    {
                        var v1 = AdvSimd.LoadVector128(localPrev + i);
                        var v2 = AdvSimd.LoadVector128(localNext + i);
                        var vAvg = AdvSimd.Arm64.RoundingHalvingAdd(v1, v2);
                        AdvSimd.Store(localDst + i, vAvg);
                    }
                }

                // 4. Scalar fallback for remainder or scalar-only CPUs
                for (; i < end; i++)
                {
                    localDst[i] = (byte)((localPrev[i] + localNext[i] + 1) >> 1);
                }
            });
        }

        return frameSlot;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MotionFrameSynthesizerTests" -c Debug`
Expected: PASS (all tests pass).

- [ ] **Step 5: Commit changes**

```bash
git add src/AirServerLite/Video/Processing/MotionFrameSynthesizer.cs tests/AirServerLite.Tests/Video/MotionFrameSynthesizerTests.cs
git commit -m "feat(video): add universal SIMD cascade (AVX2, SSE2, ARM64, Scalar) to MotionFrameSynthesizer"
```

---

### Task 3: Universal GPU Hardware Decoding & Multi-Stage Fallback

**Files:**
- Modify: `src/AirServerLite/Video/H264Decoder.cs`
- Test: `tests/AirServerLite.Tests/Video/H264DecoderUniversalTests.cs`

- [ ] **Step 1: Write test for multi-stage decoder initialization and software fallback**

Create `tests/AirServerLite.Tests/Video/H264DecoderUniversalTests.cs`:
```csharp
using AirServerLite.Core;
using AirServerLite.Video;
using Xunit;

namespace AirServerLite.Tests.Video;

public class H264DecoderUniversalTests
{
    [Fact]
    public void Decoder_InitializesSuccessfully_WithValidAccelerationMode()
    {
        using var decoder = new H264Decoder();
        Assert.NotNull(decoder.AccelerationMode);
        Assert.True(decoder.AccelerationMode.Length > 0);
    }

    [Fact]
    public void Decoder_SupportsDynamicUpscaleModes()
    {
        using var decoder = new H264Decoder();
        decoder.UpscaleMode = RtxUpscaleMode.Bicubic;
        Assert.Equal(RtxUpscaleMode.Bicubic, decoder.UpscaleMode);

        decoder.UpscaleMode = RtxUpscaleMode.FastBilinear;
        Assert.Equal(RtxUpscaleMode.FastBilinear, decoder.UpscaleMode);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~H264DecoderUniversalTests" -c Debug`

- [ ] **Step 3: Enhance H264Decoder with D3D11VA -> DXVA2 -> Multithreaded Software cascade**

In `src/AirServerLite/Video/H264Decoder.cs`:
Ensure `InitHardwareDecoder` tests `AV_HWDEVICE_TYPE_D3D11VA`, then `AV_HWDEVICE_TYPE_DXVA2`. If both fail, configures `_ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 2, 16);` with `AV_THREAD_SLICE` for instant zero-latency CPU decoding.

- [ ] **Step 4: Verify test suite**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~H264Decoder" -c Debug`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AirServerLite/Video/H264Decoder.cs tests/AirServerLite.Tests/Video/H264DecoderUniversalTests.cs
git commit -m "feat(video): add D3D11VA, DXVA2, and multi-threaded CPU fallback to H264Decoder"
```

---

### Task 4: Anti-Crackling & Anti-Pop Audio Engine with Elastic Clock Drift Sync

**Files:**
- Create: `src/AirServerLite/Audio/AudioResampler.cs`
- Modify: `src/AirServerLite/Audio/AudioPlayer.cs`
- Modify: `src/AirServerLite/Audio/AudioJitterBuffer.cs`
- Test: `tests/AirServerLite.Tests/Audio/AudioStabilityTests.cs`

- [ ] **Step 1: Write failing test for AudioResampler and Smooth Cosine Underrun Concealment**

Create `tests/AirServerLite.Tests/Audio/AudioStabilityTests.cs`:
```csharp
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioStabilityTests
{
    [Fact]
    public void Resampler_48kHzTo441kHz_MaintainsLengthRatio()
    {
        // 480 samples at 48kHz is 10ms -> should become 441 samples at 44.1kHz
        byte[] input48k = new byte[480 * 4]; // stereo 16-bit
        for (int i = 0; i < 480; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 16000);
            input48k[i * 4] = (byte)(sample & 0xFF);
            input48k[i * 4 + 1] = (byte)((sample >> 8) & 0xFF);
            input48k[i * 4 + 2] = (byte)(sample & 0xFF);
            input48k[i * 4 + 3] = (byte)((sample >> 8) & 0xFF);
        }

        var resampled = AudioResampler.ResampleStereo16(input48k, 48000, 44100);

        Assert.NotNull(resampled);
        int expectedSamples = 441;
        Assert.Equal(expectedSamples * 4, resampled.Length);
    }

    [Fact]
    public void ApplySmoothRampOut_FadesSamplesGracefullyToZero()
    {
        byte[] pcm = new byte[128];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short val = 20000;
            pcm[i] = (byte)(val & 0xFF);
            pcm[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        AudioPlayer.ApplySmoothRampOut(pcm, pcm.Length, 16);

        // The very last sample should be attenuated near 0
        short lastLeft = (short)(pcm[pcm.Length - 4] | (pcm[pcm.Length - 3] << 8));
        short lastRight = (short)(pcm[pcm.Length - 2] | (pcm[pcm.Length - 1] << 8));

        Assert.True(Math.Abs(lastLeft) < 1000);
        Assert.True(Math.Abs(lastRight) < 1000);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AudioStabilityTests" -c Debug`
Expected: FAIL (AudioResampler and ApplySmoothRampOut missing).

- [ ] **Step 3: Implement AudioResampler and Smooth Cosine Ramping**

Create `src/AirServerLite/Audio/AudioResampler.cs`:
```csharp
namespace AirServerLite.Audio;

public static class AudioResampler
{
    public static byte[] ResampleStereo16(ReadOnlySpan<byte> inputPcm, int inRate, int outRate)
    {
        if (inRate == outRate || inputPcm.Length == 0)
        {
            return inputPcm.ToArray();
        }

        int inSamplePairs = inputPcm.Length / 4;
        int outSamplePairs = (int)((long)inSamplePairs * outRate / inRate);
        byte[] output = new byte[outSamplePairs * 4];

        double ratio = (double)inSamplePairs / outSamplePairs;

        for (int i = 0; i < outSamplePairs; i++)
        {
            double srcIdx = i * ratio;
            int idxFloor = (int)srcIdx;
            int idxCeil = Math.Min(idxFloor + 1, inSamplePairs - 1);
            double frac = srcIdx - idxFloor;

            int inOffset1 = idxFloor * 4;
            int inOffset2 = idxCeil * 4;

            short l1 = (short)(inputPcm[inOffset1] | (inputPcm[inOffset1 + 1] << 8));
            short r1 = (short)(inputPcm[inOffset1 + 2] | (inputPcm[inOffset1 + 3] << 8));

            short l2 = (short)(inputPcm[inOffset2] | (inputPcm[inOffset2 + 1] << 8));
            short r2 = (short)(inputPcm[inOffset2 + 2] | (inputPcm[inOffset2 + 3] << 8));

            short lOut = (short)(l1 + (l2 - l1) * frac);
            short rOut = (short)(r1 + (r2 - r1) * frac);

            int outOffset = i * 4;
            output[outOffset] = (byte)(lOut & 0xFF);
            output[outOffset + 1] = (byte)((lOut >> 8) & 0xFF);
            output[outOffset + 2] = (byte)(rOut & 0xFF);
            output[outOffset + 3] = (byte)((rOut >> 8) & 0xFF);
        }

        return output;
    }
}
```

In `src/AirServerLite/Audio/AudioPlayer.cs`:
Add `ApplySmoothRampOut` using raised-cosine fade-out to prevent DC pop:
```csharp
    public static void ApplySmoothRampOut(byte[] pcm, int length, int rampSamples)
    {
        int samplePairs = Math.Min(rampSamples, length / 4);
        int startIndex = (length / 4) - samplePairs;
        for (int i = 0; i < samplePairs; i++)
        {
            // Raised cosine curve: 1.0 down to 0.0
            float factor = 0.5f * (1.0f + (float)Math.Cos(Math.PI * i / samplePairs));
            int offset = (startIndex + i) * 4;

            short left = (short)(pcm[offset] | (pcm[offset + 1] << 8));
            short right = (short)(pcm[offset + 2] | (pcm[offset + 3] << 8));

            left = (short)(left * factor);
            right = (short)(right * factor);

            pcm[offset] = (byte)(left & 0xFF);
            pcm[offset + 1] = (byte)((left >> 8) & 0xFF);
            pcm[offset + 2] = (byte)(right & 0xFF);
            pcm[offset + 3] = (byte)((right >> 8) & 0xFF);
        }
    }
```
And in `PumpLoop()`: when underrun occurs, smoothly ramp out the last buffer before sending silence, and ramp in smoothly on recovery.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AudioStabilityTests" -c Debug`
Expected: PASS (2 tests pass).

- [ ] **Step 5: Commit changes**

```bash
git add src/AirServerLite/Audio/AudioResampler.cs src/AirServerLite/Audio/AudioPlayer.cs tests/AirServerLite.Tests/Audio/AudioStabilityTests.cs
git commit -m "feat(audio): add AudioResampler and click-free cosine underrun concealment"
```

---

### Task 5: AAC Audio Decoder for Android Miracast & High-Fidelity Playback

**Files:**
- Create: `src/AirServerLite/Android/AacAudioDecoder.cs`
- Modify: `src/AirServerLite/Android/Miracast/MiracastRtpReceiver.cs`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Test: `tests/AirServerLite.Tests/Android/AacAudioDecoderTests.cs`

- [ ] **Step 1: Write test for AAC Audio Decoder**

Create `tests/AirServerLite.Tests/Android/AacAudioDecoderTests.cs`:
```csharp
using AirServerLite.Android;
using Xunit;

namespace AirServerLite.Tests.Android;

public class AacAudioDecoderTests
{
    [Fact]
    public void Decoder_InitializesSuccessfully()
    {
        using var decoder = new AacAudioDecoder();
        Assert.NotNull(decoder);
    }

    [Fact]
    public void Decoder_InvalidPacket_ReturnsFalseGracefully()
    {
        using var decoder = new AacAudioDecoder();
        byte[] junk = new byte[] { 0x00, 0x11, 0x22, 0x33 };
        bool success = decoder.TryDecode(junk, out var pcm, out var sampleRate);
        Assert.False(success);
        Assert.Empty(pcm);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AacAudioDecoderTests" -c Debug`
Expected: FAIL (class `AacAudioDecoder` missing).

- [ ] **Step 3: Implement AacAudioDecoder using FFmpeg avcodec**

Create `src/AirServerLite/Android/AacAudioDecoder.cs`:
Uses `ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC)` and `swr_convert` to convert incoming AAC PES frames to 16-bit 44.1kHz stereo PCM.
Connect decoded PCM to `AudioPlayer` in `MiracastRtpReceiver.cs`:
```csharp
if (_audioPlayer != null && demux.AudioPackets.Count > 0)
{
    foreach (var pkt in demux.AudioPackets)
    {
        if (_audioDecoder.TryDecode(pkt, out var pcm, out var sampleRate))
        {
            if (sampleRate != 44100)
                pcm = AudioResampler.ResampleStereo16(pcm, sampleRate, 44100);
            _audioPlayer.PlayPcm(pcm, pcm.Length);
        }
    }
}
```
And in `MainWindow.xaml.cs`: pass `_audioPlayer` to `MiracastRtpReceiver`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AacAudioDecoderTests" -c Debug`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AirServerLite/Android/AacAudioDecoder.cs src/AirServerLite/Android/Miracast/MiracastRtpReceiver.cs src/AirServerLite/MainWindow.xaml.cs tests/AirServerLite.Tests/Android/AacAudioDecoderTests.cs
git commit -m "feat(android): add AAC audio decoder for crystal-clear Miracast audio playback"
```

---

### Task 6: Universal Hardware Telemetry & Adaptive UI Dialog

**Files:**
- Modify: `src/AirServerLite/Core/RtxTelemetryFormatter.cs`
- Modify: `src/AirServerLite/UI/RtxFidelityDialog.xaml`
- Modify: `src/AirServerLite/UI/RtxFidelityDialog.xaml.cs`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Test: `tests/AirServerLite.Tests/UI/UniversalTelemetryTests.cs`

- [ ] **Step 1: Write test for Universal Telemetry formatting with AMD/Intel/NVIDIA branding**

Create `tests/AirServerLite.Tests/UI/UniversalTelemetryTests.cs`:
```csharp
using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.UI;

public class UniversalTelemetryTests
{
    [Fact]
    public void FormatUniversalHardwareInfo_AmdGpu_DisplaysAmdRadeon()
    {
        var profile = new HardwareProfile
        {
            GpuName = "AMD Radeon RX 7900 XTX",
            GpuVendor = GpuVendor.Amd,
            CpuName = "AMD Ryzen 9 7950X",
            CpuLogicalCores = 32
        };

        string info = RtxTelemetryFormatter.FormatUniversalHardware(profile, "D3D11VA");

        Assert.Contains("AMD Radeon", info);
        Assert.Contains("32T", info);
    }

    [Fact]
    public void FormatUniversalHardwareInfo_IntelArc_DisplaysIntelArc()
    {
        var profile = new HardwareProfile
        {
            GpuName = "Intel(R) Arc(TM) A770",
            GpuVendor = GpuVendor.Intel,
            CpuName = "Intel Core i9-13900K",
            CpuLogicalCores = 32
        };

        string info = RtxTelemetryFormatter.FormatUniversalHardware(profile, "D3D11VA");

        Assert.Contains("Intel", info);
        Assert.Contains("32T", info);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~UniversalTelemetryTests" -c Debug`

- [ ] **Step 3: Implement FormatUniversalHardware and update Fidelity Dialog**

Update `RtxTelemetryFormatter.cs` to add `FormatUniversalHardware(HardwareProfile profile, string decoderMode)`.
Update `RtxFidelityDialog.xaml` to display the actual detected GPU & CPU name and vendor badge (AMD Red `#FFED1C24`, Intel Blue `#FF0071C5`, NVIDIA Green `#FF76B900`).
Update `MainWindow.xaml.cs` to auto-tune on startup using `HardwareDetector.Detect()`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~UniversalTelemetryTests" -c Debug`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AirServerLite/Core/RtxTelemetryFormatter.cs src/AirServerLite/UI/RtxFidelityDialog.* src/AirServerLite/MainWindow.xaml.cs tests/AirServerLite.Tests/UI/UniversalTelemetryTests.cs
git commit -m "feat(ui): add universal hardware branding and dynamic auto-tuning to Fidelity Dialog"
```

---

### Task 7: End-to-End System Integration & Packaging Verification

**Files:**
- Modify: `walkthrough.md`
- Package: `dist/AirServerLite.exe`

- [ ] **Step 1: Run complete test suite across all modules**

Run: `dotnet test AirServerLite.sln -c Release`
Expected: 100% tests pass (over 88+ unit & integration tests).

- [ ] **Step 2: Run pack.ps1 to generate production single-file release**

Run: `powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -SkipPlayfair`
Expected: `dist/AirServerLite.exe` produced (~160MB single self-contained executable).

- [ ] **Step 3: Update documentation and walkthrough**

Update `walkthrough.md` with complete benchmark and architecture verification notes.

- [ ] **Step 4: Commit release verification**

```bash
git add docs/superpowers/plans/2026-09-09-universal-hardware-compatibility-and-av-stability.md
git commit -m "docs: complete universal hardware compatibility and av stability implementation plan"
```
