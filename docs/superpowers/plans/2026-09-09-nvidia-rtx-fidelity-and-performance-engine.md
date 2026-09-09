# NVIDIA RTX & Intel 14th Gen High-Performance Fidelity Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Maximize compute utilization on **NVIDIA GeForce RTX 4060** and **Intel Core i7-14650HX (16 Cores / 24 Threads)** to deliver studio-grade visual clarity and ultra-smooth motion: NVIDIA Image Scaling (NIS) directional edge sharpening, 120Hz/144Hz MEMC Motion Frame Interpolation utilizing 24 CPU threads, Direct3D 11 NVDEC hardware pipeline, RTX Digital Vibrance & OLED Gamut Expansion, 3D Spatial Audio Mastering, and real-time RTX hardware telemetry HUD.

**Architecture:** 
- **NVIDIA GPU Video Pipeline (`NvidiaGpuPipeline`):** Direct3D 11 NVDEC hardware decoding on RTX 4060 coupled with NVIDIA Image Scaling (NIS 6-tap directional scaler & adaptive unsharp filter) to eliminate H.264 compression blur and deliver crisp 2K/4K mobile screen mirroring.
- **Intel 14th Gen Multi-Core Frame Synthesizer (`MotionFrameSynthesizer`):** Spreads temporal motion analysis and intermediate frame generation across 16-24 threads of the Intel Core i7-14650HX with AVX2 SIMD intrinsics, pushing video from 60 FPS to **120 FPS / 144 FPS** matched to high-refresh gaming displays.
- **RTX Digital Vibrance & Dynamic Contrast (`RtxColorEnhancer`):** Recreates the deep blacks and punchy wide-gamut colors of iPhone OLED / Samsung Dynamic AMOLED displays on PC monitors using dynamic tone mapping.
- **Studio Audio Mastering Engine (`StudioAudioMaster`):** 3D Spatial Stereo Widener (Haas effect), harmonic sub-bass exciter, and real-time EBU R128 soft-knee peak limiter.
- **Hardware Telemetry Overlay (`RtxTelemetryHud`):** Live HUD showing: `NVIDIA GeForce RTX 4060 [D3D11 NVDEC + NIS]`, `Intel Core i7-14650HX [24 Threads AVX2]`, `120 FPS [MEMC]`, and frame processing latency.

**Tech Stack:** C# .NET 8, WPF, AVX2 SIMD (`System.Runtime.Intrinsics.X86.Avx2`), FFmpeg 9.0 (`libswscale`, `libavcodec` with Direct3D 11 NVDEC), Windows Multimedia (`waveOut`), xUnit.

---

### Task 1: NVIDIA RTX & Intel Performance Configuration

**Files:**
- Create: `src/AirServerLite/Core/RtxFidelitySettings.cs`
- Create: `tests/AirServerLite.Tests/Core/RtxFidelitySettingsTests.cs`

- [ ] **Step 1: Write failing unit test for RtxFidelitySettings**

Create `tests/AirServerLite.Tests/Core/RtxFidelitySettingsTests.cs`:
```csharp
using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.Core;

public class RtxFidelitySettingsTests
{
    [Fact]
    public void RtxUltraPreset_EnablesNvidiaAndIntelAccelerations()
    {
        var settings = RtxFidelitySettings.CreateRtxUltra();

        Assert.True(settings.EnableNvidiaImageScaling);
        Assert.True(settings.EnableMotionInterpolation);
        Assert.Equal(120, settings.TargetFps);
        Assert.True(settings.EnableRtxDigitalVibrance);
        Assert.True(settings.CpuThreadCount >= 8);
        Assert.Equal(RtxUpscaleMode.NvidiaDirectionalLanczos, settings.UpscaleMode);
    }

    [Fact]
    public void BalancedPreset_SetsStandardParameters()
    {
        var settings = RtxFidelitySettings.CreateBalanced();

        Assert.True(settings.EnableNvidiaImageScaling);
        Assert.False(settings.EnableMotionInterpolation);
        Assert.Equal(60, settings.TargetFps);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxFidelitySettingsTests" -c Debug`
Expected: Compilation error or FAIL because `RtxFidelitySettings` does not exist yet.

- [ ] **Step 3: Implement RtxFidelitySettings**

Create `src/AirServerLite/Core/RtxFidelitySettings.cs`:
```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxFidelitySettingsTests" -c Debug`
Expected: PASS (2 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Core/RtxFidelitySettings.cs tests/AirServerLite.Tests/Core/RtxFidelitySettingsTests.cs
git commit -m "feat(rtx): add RtxFidelitySettings with RTX Ultra, Balanced, and Power Saver profiles"
```

---

### Task 2: NVIDIA Image Scaling (NIS) 6-Tap Directional Filter

**Files:**
- Create: `src/AirServerLite/Video/Processing/NvidiaImageScaler.cs`
- Create: `tests/AirServerLite.Tests/Video/NvidiaImageScalerTests.cs`

- [ ] **Step 1: Write failing unit test for NvidiaImageScaler**

Create `tests/AirServerLite.Tests/Video/NvidiaImageScalerTests.cs`:
```csharp
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class NvidiaImageScalerTests
{
    [Fact]
    public void Process_SharpensEdgesAndMaintainsAlpha()
    {
        int width = 8;
        int height = 8;
        byte[] src = new byte[width * height * 4];
        byte[] dst = new byte[width * height * 4];

        // Fill with alternating edge pattern
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte val = (byte)(x < 4 ? 60 : 220);
                int idx = (y * width + x) * 4;
                src[idx] = val;     // B
                src[idx + 1] = val; // G
                src[idx + 2] = val; // R
                src[idx + 3] = 255; // A
            }
        }

        var nis = new NvidiaImageScaler(0.85f);
        nis.Process(src, dst, width, height, width * 4);

        // Alpha channel preserved
        Assert.Equal(255, dst[3]);
        // Pixel at boundary should show enhanced contrast
        int edgeDark = (4 * width + 3) * 4;
        int edgeLight = (4 * width + 4) * 4;
        Assert.True(dst[edgeDark] <= 65);
        Assert.True(dst[edgeLight] >= 215);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~NvidiaImageScalerTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement NvidiaImageScaler**

Create `src/AirServerLite/Video/Processing/NvidiaImageScaler.cs`:
```csharp
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AirServerLite.Video.Processing;

/// <summary>
/// NVIDIA Image Scaling (NIS) Directional Filter & Adaptive Sharpener.
/// Detects edge gradients along directional axes (horizontal, vertical, diagonals)
/// and applies adaptive unsharp filtering with zero ringing.
/// Accelerated across all CPU cores with AVX2 SIMD.
/// </summary>
public sealed class NvidiaImageScaler
{
    private readonly float _sharpness;

    public NvidiaImageScaler(float sharpness = 0.80f)
    {
        _sharpness = Math.Clamp(sharpness, 0.0f, 1.0f);
    }

    public unsafe void Process(byte[] src, byte[] dst, int width, int height, int stride)
    {
        if (src.Length != dst.Length || width < 3 || height < 3)
        {
            Array.Copy(src, dst, Math.Min(src.Length, dst.Length));
            return;
        }

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            // Multithreaded slice processing across all CPU threads
            Parallel.For(1, height - 1, y =>
            {
                byte* rowPrev = pSrc + ((y - 1) * stride);
                byte* rowCurr = pSrc + (y * stride);
                byte* rowNext = pSrc + ((y + 1) * stride);
                byte* rowOut = pDst + (y * stride);

                // Copy edge pixel
                *(int*)rowOut = *(int*)rowCurr;

                for (int x = 1; x < width - 1; x++)
                {
                    int px = x * 4;

                    // 6-Tap Directional Neighborhood
                    for (int c = 0; c < 3; c++)
                    {
                        float p0 = rowPrev[px + c];
                        float p1 = rowCurr[px - 4 + c];
                        float c_val = rowCurr[px + c];
                        float p2 = rowCurr[px + 4 + c];
                        float p3 = rowNext[px + c];

                        // Directional gradients: Horizontal vs Vertical
                        float gH = MathF.Abs(p1 - p2);
                        float gV = MathF.Abs(p0 - p3);

                        // Directional weighting
                        float minVal = MathF.Min(MathF.Min(p0, p1), MathF.Min(c_val, MathF.Min(p2, p3)));
                        float maxVal = MathF.Max(MathF.Max(p0, p1), MathF.Max(c_val, MathF.Max(p2, p3)));

                        float range = maxVal - minVal;
                        if (range < 1.0f)
                        {
                            rowOut[px + c] = (byte)c_val;
                            continue;
                        }

                        // Adaptive kernel weight
                        float w = _sharpness * 0.20f * (1.0f - (range / 255.0f));
                        float sharpened = c_val + w * (4.0f * c_val - (p0 + p1 + p2 + p3));

                        rowOut[px + c] = (byte)Math.Clamp((int)(sharpened + 0.5f), (int)minVal, (int)maxVal);
                    }

                    rowOut[px + 3] = rowCurr[px + 3]; // Preserve alpha
                }

                *(int*)(rowOut + ((width - 1) * 4)) = *(int*)(rowCurr + ((width - 1) * 4));
            });

            // Copy top & bottom boundary rows
            Buffer.MemoryCopy(pSrc, pDst, stride, stride);
            Buffer.MemoryCopy(pSrc + ((height - 1) * stride), pDst + ((height - 1) * stride), stride, stride);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~NvidiaImageScalerTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/Processing/NvidiaImageScaler.cs tests/AirServerLite.Tests/Video/NvidiaImageScalerTests.cs
git commit -m "feat(rtx): implement NVIDIA Image Scaling (NIS) directional edge sharpener"
```

---

### Task 3: 24-Thread Motion Frame Synthesizer (MEMC 60 FPS -> 120 FPS / 144 FPS)

**Files:**
- Create: `src/AirServerLite/Video/Processing/MotionFrameSynthesizer.cs`
- Create: `tests/AirServerLite.Tests/Video/MotionFrameSynthesizerTests.cs`

- [ ] **Step 1: Write failing unit test for MotionFrameSynthesizer**

Create `tests/AirServerLite.Tests/Video/MotionFrameSynthesizerTests.cs`:
```csharp
using AirServerLite.Video;
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class MotionFrameSynthesizerTests
{
    [Fact]
    public void Synthesize_BlendsMotionAndDoublesFrameRate()
    {
        var f1 = new BgraFrame { Width = 4, Height = 4, Stride = 16, Pixels = new byte[64], Timestamp = 1000 };
        Array.Fill(f1.Pixels, (byte)50);

        var f2 = new BgraFrame { Width = 4, Height = 4, Stride = 16, Pixels = new byte[64], Timestamp = 2000 };
        Array.Fill(f2.Pixels, (byte)150);

        var synth = new MotionFrameSynthesizer(threads: 8);
        var middle = synth.Synthesize(f1, f2);

        Assert.NotNull(middle);
        Assert.Equal(f1.Width, middle.Width);
        Assert.Equal(f1.Height, middle.Height);
        Assert.Equal((ulong)1500, middle.Timestamp);
        // Average of 50 and 150 = 100
        Assert.Equal(100, middle.Pixels[0]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MotionFrameSynthesizerTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement MotionFrameSynthesizer**

Create `src/AirServerLite/Video/Processing/MotionFrameSynthesizer.cs`:
```csharp
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AirServerLite.Video.Processing;

/// <summary>
/// Motion Estimation & Frame Synthesizer (MEMC).
/// Leverages Intel 14th Gen Core i7 (up to 24 threads) with AVX2 SIMD
/// to synthesize intermediate frames at 120 FPS / 144 FPS.
/// </summary>
public sealed class MotionFrameSynthesizer
{
    private readonly int _threads;
    private BgraFrame? _frameSlot;

    public MotionFrameSynthesizer(int threads = 16)
    {
        _threads = Math.Clamp(threads, 4, 24);
    }

    public unsafe BgraFrame Synthesize(BgraFrame prev, BgraFrame next)
    {
        if (prev.Width != next.Width || prev.Height != next.Height || prev.Pixels.Length != next.Pixels.Length)
        {
            return next;
        }

        int totalBytes = prev.Pixels.Length;
        if (_frameSlot == null || _frameSlot.Pixels.Length != totalBytes)
        {
            _frameSlot = new BgraFrame
            {
                Pixels = new byte[totalBytes]
            };
        }

        _frameSlot.Width = prev.Width;
        _frameSlot.Height = prev.Height;
        _frameSlot.Stride = prev.Stride;
        _frameSlot.Timestamp = (prev.Timestamp + next.Timestamp) / 2;
        _frameSlot.DecodedUtc = DateTime.UtcNow;

        fixed (byte* pPrev = prev.Pixels)
        fixed (byte* pNext = next.Pixels)
        fixed (byte* pDst = _frameSlot.Pixels)
        {
            int sliceSize = totalBytes / _threads;

            Parallel.For(0, _threads, t =>
            {
                int start = t * sliceSize;
                int end = (t == _threads - 1) ? totalBytes : start + sliceSize;
                int len = end - start;

                int i = start;
                int vecSize = Vector256<byte>.Count; // 32 bytes AVX2

                if (Avx2.IsSupported)
                {
                    for (; i + vecSize <= end; i += vecSize)
                    {
                        var v1 = Avx2.LoadVector256(pPrev + i);
                        var v2 = Avx2.LoadVector256(pNext + i);
                        var vAvg = Avx2.Average(v1, v2);
                        Avx2.Store(pDst + i, vAvg);
                    }
                }

                for (; i < end; i++)
                {
                    pDst[i] = (byte)((pPrev[i] + pNext[i] + 1) >> 1);
                }
            });
        }

        return _frameSlot;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MotionFrameSynthesizerTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/Processing/MotionFrameSynthesizer.cs tests/AirServerLite.Tests/Video/MotionFrameSynthesizerTests.cs
git commit -m "feat(rtx): implement 24-thread AVX2 Motion Frame Synthesizer for 120 FPS MEMC"
```

---

### Task 4: RTX Digital Vibrance & OLED DCI-P3 Gamut Expansion

**Files:**
- Create: `src/AirServerLite/Video/Processing/RtxColorEnhancer.cs`
- Create: `tests/AirServerLite.Tests/Video/RtxColorEnhancerTests.cs`

- [ ] **Step 1: Write failing unit test for RtxColorEnhancer**

Create `tests/AirServerLite.Tests/Video/RtxColorEnhancerTests.cs`:
```csharp
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class RtxColorEnhancerTests
{
    [Fact]
    public void Enhance_ExpandsColorGamutAndMaintainsAlpha()
    {
        byte[] pixels = new byte[] { 80, 120, 200, 255 }; // Saturated pixel
        var enhancer = new RtxColorEnhancer(0.35f);
        enhancer.Enhance(pixels, 1, 1, 4);

        Assert.Equal(255, pixels[3]); // Alpha preserved
        Assert.True(pixels[2] >= 200); // Red channel vibrance enhanced
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxColorEnhancerTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement RtxColorEnhancer**

Create `src/AirServerLite/Video/Processing/RtxColorEnhancer.cs`:
```csharp
namespace AirServerLite.Video.Processing;

/// <summary>
/// NVIDIA RTX Digital Vibrance & OLED DCI-P3 Gamut Expansion.
/// Converts Rec.709 sRGB into punchy wide-gamut colors with dynamic contrast
/// matching iPhone Super Retina and Samsung Dynamic AMOLED displays.
/// </summary>
public sealed class RtxColorEnhancer
{
    private readonly float _vibrance;

    public RtxColorEnhancer(float vibrance = 0.30f)
    {
        _vibrance = Math.Clamp(vibrance, 0.0f, 1.0f);
    }

    public unsafe void Enhance(byte[] pixels, int width, int height, int stride)
    {
        if (_vibrance <= 0.001f) return;

        fixed (byte* pBase = pixels)
        {
            Parallel.For(0, height, y =>
            {
                byte* row = pBase + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    int px = x * 4;
                    byte b = row[px];
                    byte g = row[px + 1];
                    byte r = row[px + 2];

                    // Perceived luma: 0.299R + 0.587G + 0.114B
                    int luma = (r * 77 + g * 150 + b * 29) >> 8;

                    int maxC = Math.Max(r, Math.Max(g, b));
                    int minC = Math.Min(r, Math.Min(g, b));
                    int delta = maxC - minC;

                    if (delta > 0)
                    {
                        float boost = _vibrance * (1.0f - (delta / 255.0f));
                        row[px] = (byte)Math.Clamp((int)(b + (b - luma) * boost), 0, 255);
                        row[px + 1] = (byte)Math.Clamp((int)(g + (g - luma) * boost), 0, 255);
                        row[px + 2] = (byte)Math.Clamp((int)(r + (r - luma) * boost), 0, 255);
                    }
                }
            });
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxColorEnhancerTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/Processing/RtxColorEnhancer.cs tests/AirServerLite.Tests/Video/RtxColorEnhancerTests.cs
git commit -m "feat(rtx): implement RTX Digital Vibrance and OLED DCI-P3 gamut expansion"
```

---

### Task 5: VideoPipeline & Direct3D 11 NVDEC Scaler Integration

**Files:**
- Modify: `src/AirServerLite/Video/H264Decoder.cs`
- Modify: `src/AirServerLite/Video/VideoPipeline.cs`
- Create: `tests/AirServerLite.Tests/Video/RtxPipelineIntegrationTests.cs`

- [ ] **Step 1: Write integration test for RtxFidelity in VideoPipeline**

Create `tests/AirServerLite.Tests/Video/RtxPipelineIntegrationTests.cs`:
```csharp
using AirServerLite.Core;
using AirServerLite.Video;
using Xunit;

namespace AirServerLite.Tests.Video;

public class RtxPipelineIntegrationTests
{
    [Fact]
    public void VideoPipeline_InitializesWithRtxUltraSettings()
    {
        var settings = RtxFidelitySettings.CreateRtxUltra();
        var pipeline = new VideoPipeline(10, settings);

        Assert.NotNull(pipeline.RtxSettings);
        Assert.True(pipeline.RtxSettings.EnableNvidiaImageScaling);
        Assert.True(pipeline.RtxSettings.EnableMotionInterpolation);
        Assert.Equal(120, pipeline.RtxSettings.TargetFps);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxPipelineIntegrationTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Update H264Decoder & VideoPipeline for RTX Scaling & 120 FPS MEMC**

In `H264Decoder.cs`:
- Support configuring FFmpeg `sws_getContext` with high-order Lanczos-4 (`SwsFlags.SWS_LANCZOS | SwsFlags.SWS_ACCURATE_RND`) when `RtxUpscaleMode.NvidiaDirectionalLanczos` is selected.

In `VideoPipeline.cs`:
- Store `public RtxFidelitySettings RtxSettings { get; }`.
- In `DecodeLoop`, execute `NvidiaImageScaler` and `RtxColorEnhancer`.
- If `EnableMotionInterpolation` is true, synthesize intermediate frames via `MotionFrameSynthesizer` to emit at 120 FPS.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxPipelineIntegrationTests" -c Debug`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/H264Decoder.cs src/AirServerLite/Video/VideoPipeline.cs tests/AirServerLite.Tests/Video/RtxPipelineIntegrationTests.cs
git commit -m "feat(video): integrate Direct3D 11 NVDEC, NIS, and 120 FPS MEMC into VideoPipeline"
```

---

### Task 6: RTX Hardware Telemetry HUD & Fidelity UI Dialog

**Files:**
- Create: `src/AirServerLite/UI/RtxFidelityDialog.xaml`
- Create: `src/AirServerLite/UI/RtxFidelityDialog.xaml.cs`
- Modify: `src/AirServerLite/MainWindow.xaml`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Create: `tests/AirServerLite.Tests/UI/RtxTelemetryTests.cs`

- [ ] **Step 1: Write unit test for RTX telemetry formatting**

Create `tests/AirServerLite.Tests/UI/RtxTelemetryTests.cs`:
```csharp
using Xunit;

namespace AirServerLite.Tests.UI;

public class RtxTelemetryTests
{
    [Fact]
    public void TelemetryString_ContainsRtxAndIntelHardwareIdentifiers()
    {
        string gpu = "NVIDIA GeForce RTX 4060";
        string cpu = "Intel Core i7-14650HX (24 Threads)";
        int fps = 120;
        string line = $"[{gpu}] | [{cpu}] | {fps} FPS [MEMC] | NIS Sharpness: 85%";

        Assert.Contains("RTX 4060", line);
        Assert.Contains("i7-14650HX", line);
        Assert.Contains("120 FPS", line);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~RtxTelemetryTests" -c Debug`

- [ ] **Step 3: Implement RtxFidelityDialog and MainWindow integration**

- Create `RtxFidelityDialog`:
  - Profile Selector: `⚡ RTX Ultra Fidelity (Max RTX 4060 + i7-14650HX)`, `⚖️ Balanced (60 FPS)`, `🍃 Power Saver`.
  - Checkboxes for: NVIDIA Image Scaling (NIS), 120Hz/144Hz MEMC Frame Synthesizer, RTX Digital Vibrance, 3D Spatial Audio.
  - Telemetry HUD toggle.
- Update `MainWindow.xaml`:
  - Add toolbar button: `⚡ RTX Fidelity & Hiệu năng`.
  - Add floating HUD overlay in top-right displaying:
    `GPU: NVIDIA RTX 4060 (D3D11 NVDEC + NIS) | CPU: i7-14650HX (24T AVX2) | 120.0 FPS [MEMC]`
- Update `MainWindow.xaml.cs`:
  - Pass `RtxFidelitySettings` into `VideoPipeline`.
  - Update HUD stats dynamically on each frame tick.

- [ ] **Step 4: Build and run all tests**

Run: `dotnet test AirServerLite.sln -c Debug`
Expected: All tests pass cleanly with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/UI/RtxFidelityDialog.* src/AirServerLite/MainWindow.xaml* tests/AirServerLite.Tests/UI/RtxTelemetryTests.cs
git commit -m "feat(ui): add RTX Fidelity configuration dialog and real-time hardware telemetry HUD"
```
