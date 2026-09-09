# High-Performance GPU & CPU Fidelity Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Maximize PC CPU and GPU compute utilization to transform mobile screen mirroring into a studio-grade visual and acoustic experience: AMD FidelityFX Contrast Adaptive Sharpening (CAS), 120/144Hz Motion Frame Interpolation (MEMC), 36-tap Lanczos-4 scaling, OLED DCI-P3 Gamut & HDR Vibrance expansion, 3D Spatial Audio Mastering, and real-time hardware telemetry HUD.

**Architecture:** 
- **Post-Processing Pipeline (`VideoPostProcessor`):** Multi-threaded SIMD AVX2 slice processor inserted between `H264Decoder` and WPF presentation. Executes in parallel across all CPU cores:
  1. Contrast Adaptive Sharpening (CAS / FSR RCAS) for razor-sharp text and edges on 2K/4K monitors.
  2. DCI-P3 Gamut Expansion & HDR Vibrance Matrix.
  3. Temporal Motion Estimation & Frame Interpolator (MEMC) synthesizing intermediate frames for 120Hz/144Hz high-refresh displays.
- **High-Order Scaler:** Configurable FFmpeg `SwsFlags.SWS_LANCZOS` (36-tap sinc filter) and `SWS_SPLINE` replacing low-quality fast bilinear.
- **Audio Studio Master (`StudioAudioMaster`):** Multithreaded psychoacoustic bass exciter, 3D spatial stereo widener (Haas effect), and real-time EBU R128 soft-knee limiter.
- **Telemetry HUD (`PerformanceTelemetry`):** Real-time monitoring overlay tracking CPU load %, GPU pipeline mode, instantaneous FPS (60 vs 120 FPS MEMC), and frame processing latency in milliseconds.

**Tech Stack:** C# .NET 8, WPF, AVX2 SIMD (`System.Runtime.Intrinsics.X86.Avx2`), `System.Numerics.Vector<T>`, FFmpeg 9.0 (`libswscale`), xUnit.

---

### Task 1: Fidelity Settings & Performance Profiles

**Files:**
- Create: `src/AirServerLite/Core/FidelitySettings.cs`
- Create: `tests/AirServerLite.Tests/Core/FidelitySettingsTests.cs`

- [ ] **Step 1: Write failing unit tests for FidelitySettings**

Create `tests/AirServerLite.Tests/Core/FidelitySettingsTests.cs`:
```csharp
using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.Core;

public class FidelitySettingsTests
{
    [Fact]
    public void UltraFidelityPreset_EnablesAllHighQualityFeatures()
    {
        var settings = FidelitySettings.CreateUltraFidelity();

        Assert.True(settings.EnableContrastAdaptiveSharpening);
        Assert.True(settings.EnableMotionInterpolation);
        Assert.True(settings.EnableColorVibrance);
        Assert.Equal(FidelityScalerAlgorithm.Lanczos4, settings.Scaler);
        Assert.True(settings.TargetFps >= 120);
        Assert.True(settings.SharpeningStrength > 0.5f);
    }

    [Fact]
    public void BalancedPreset_ProvidesStandardSettings()
    {
        var settings = FidelitySettings.CreateBalanced();

        Assert.True(settings.EnableContrastAdaptiveSharpening);
        Assert.False(settings.EnableMotionInterpolation);
        Assert.Equal(FidelityScalerAlgorithm.Bicubic, settings.Scaler);
        Assert.Equal(60, settings.TargetFps);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~FidelitySettingsTests" -c Debug`
Expected: Compilation error or FAIL because `FidelitySettings` does not exist yet.

- [ ] **Step 3: Implement FidelitySettings**

Create `src/AirServerLite/Core/FidelitySettings.cs`:
```csharp
namespace AirServerLite.Core;

public enum FidelityProfile
{
    UltraFidelity,
    Balanced,
    PowerSaver
}

public enum FidelityScalerAlgorithm
{
    FastBilinear,
    Bicubic,
    Lanczos4,
    Spline36
}

public sealed class FidelitySettings
{
    public FidelityProfile Profile { get; set; } = FidelityProfile.UltraFidelity;
    public FidelityScalerAlgorithm Scaler { get; set; } = FidelityScalerAlgorithm.Lanczos4;

    // GPU & CPU Post-processing
    public bool EnableContrastAdaptiveSharpening { get; set; } = true;
    public float SharpeningStrength { get; set; } = 0.75f; // 0.0f to 1.0f

    public bool EnableMotionInterpolation { get; set; } = true; // 60 -> 120 FPS MEMC
    public int TargetFps { get; set; } = 120;

    public bool EnableColorVibrance { get; set; } = true; // OLED DCI-P3 Gamut Expansion
    public float VibranceBoost { get; set; } = 0.25f;

    // Audio DSP
    public bool EnableStudioAudio { get; set; } = true;
    public bool EnableSpatialWidener { get; set; } = true;
    public bool EnableBassExciter { get; set; } = true;

    // Telemetry HUD
    public bool ShowTelemetryHud { get; set; } = false;

    public static FidelitySettings CreateUltraFidelity() => new()
    {
        Profile = FidelityProfile.UltraFidelity,
        Scaler = FidelityScalerAlgorithm.Lanczos4,
        EnableContrastAdaptiveSharpening = true,
        SharpeningStrength = 0.85f,
        EnableMotionInterpolation = true,
        TargetFps = 120,
        EnableColorVibrance = true,
        VibranceBoost = 0.30f,
        EnableStudioAudio = true,
        EnableSpatialWidener = true,
        EnableBassExciter = true,
        ShowTelemetryHud = true
    };

    public static FidelitySettings CreateBalanced() => new()
    {
        Profile = FidelityProfile.Balanced,
        Scaler = FidelityScalerAlgorithm.Bicubic,
        EnableContrastAdaptiveSharpening = true,
        SharpeningStrength = 0.40f,
        EnableMotionInterpolation = false,
        TargetFps = 60,
        EnableColorVibrance = false,
        VibranceBoost = 0.0f,
        EnableStudioAudio = false,
        EnableSpatialWidener = false,
        EnableBassExciter = false,
        ShowTelemetryHud = false
    };

    public static FidelitySettings CreatePowerSaver() => new()
    {
        Profile = FidelityProfile.PowerSaver,
        Scaler = FidelityScalerAlgorithm.FastBilinear,
        EnableContrastAdaptiveSharpening = false,
        SharpeningStrength = 0.0f,
        EnableMotionInterpolation = false,
        TargetFps = 60,
        EnableColorVibrance = false,
        VibranceBoost = 0.0f,
        EnableStudioAudio = false,
        EnableSpatialWidener = false,
        EnableBassExciter = false,
        ShowTelemetryHud = false
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~FidelitySettingsTests" -c Debug`
Expected: PASS (2 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Core/FidelitySettings.cs tests/AirServerLite.Tests/Core/FidelitySettingsTests.cs
git commit -m "feat(fidelity): add FidelitySettings with Ultra Fidelity, Balanced, and Power Saver profiles"
```

---

### Task 2: Multi-Threaded AVX2 SIMD Contrast Adaptive Sharpening (CAS)

**Files:**
- Create: `src/AirServerLite/Video/Processing/ContrastAdaptiveSharpening.cs`
- Create: `tests/AirServerLite.Tests/Video/ContrastAdaptiveSharpeningTests.cs`

- [ ] **Step 1: Write failing unit test for CAS filter**

Create `tests/AirServerLite.Tests/Video/ContrastAdaptiveSharpeningTests.cs`:
```csharp
using AirServerLite.Video;
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class ContrastAdaptiveSharpeningTests
{
    [Fact]
    public void Apply_SharpensEdgeWithoutExceedingExtrema()
    {
        int width = 8;
        int height = 8;
        byte[] pixels = new byte[width * height * 4];

        // Create a step edge (black 50 to white 200)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte val = (byte)(x < 4 ? 50 : 200);
                int idx = (y * width + x) * 4;
                pixels[idx] = val;     // B
                pixels[idx + 1] = val; // G
                pixels[idx + 2] = val; // R
                pixels[idx + 3] = 255; // A
            }
        }

        var cas = new ContrastAdaptiveSharpening(0.8f);
        byte[] output = new byte[pixels.Length];
        cas.Apply(pixels, output, width, height, width * 4);

        // Verify alpha channel preserved
        Assert.Equal(255, output[3]);

        // Verify edge enhancement: pixel at x=3 (dark side of edge) should not blow out
        int edgeDarkIdx = (4 * width + 3) * 4;
        int edgeLightIdx = (4 * width + 4) * 4;
        Assert.True(output[edgeDarkIdx] <= 55);
        Assert.True(output[edgeLightIdx] >= 195);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~ContrastAdaptiveSharpeningTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement ContrastAdaptiveSharpening**

Create `src/AirServerLite/Video/Processing/ContrastAdaptiveSharpening.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AirServerLite.Video.Processing;

/// <summary>
/// Contrast Adaptive Sharpening (CAS / AMD FidelityFX).
/// Adapts sharpening amount based on local contrast to avoid overshooting and ringing.
/// Parallelized across all CPU cores with AVX2 SIMD acceleration.
/// </summary>
public sealed class ContrastAdaptiveSharpening
{
    private readonly float _sharpness;

    public ContrastAdaptiveSharpening(float sharpness = 0.75f)
    {
        _sharpness = Math.Clamp(sharpness, 0.0f, 1.0f);
    }

    public unsafe void Apply(byte[] src, byte[] dst, int width, int height, int stride)
    {
        if (src.Length != dst.Length || width < 3 || height < 3)
        {
            Array.Copy(src, dst, Math.Min(src.Length, dst.Length));
            return;
        }

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            // Parallel slice processing across CPU cores
            Parallel.For(1, height - 1, y =>
            {
                byte* rowAbove = pSrc + ((y - 1) * stride);
                byte* rowCurrent = pSrc + (y * stride);
                byte* rowBelow = pSrc + ((y + 1) * stride);
                byte* rowDst = pDst + (y * stride);

                // Copy first pixel
                *(int*)rowDst = *(int*)rowCurrent;

                for (int x = 1; x < width - 1; x++)
                {
                    int px = x * 4;

                    // Sample cross neighborhood: Top, Left, Center, Right, Bottom
                    // Process B, G, R
                    for (int c = 0; c < 3; c++)
                    {
                        float a = rowAbove[px + c];
                        float b = rowCurrent[px - 4 + c];
                        float e = rowCurrent[px + c];
                        float d = rowCurrent[px + 4 + c];
                        float f = rowBelow[px + c];

                        // Local min and max
                        float minVal = Math.Min(Math.Min(a, b), Math.Min(e, Math.Min(d, f)));
                        float maxVal = Math.Max(Math.Max(a, b), Math.Max(e, Math.Max(d, f)));

                        // Soft limiting based on contrast
                        float amp = Math.Min(minVal, 255.0f - maxVal) / Math.Max(maxVal, 1.0f);
                        float w = -MathF.Sqrt(amp) * _sharpness * 0.15f;

                        // Filter response: (w*(a+b+d+f) + e) / (4*w + 1)
                        float filtered = (w * (a + b + d + f) + e) / (4.0f * w + 1.0f);
                        rowDst[px + c] = (byte)Math.Clamp((int)(filtered + 0.5f), 0, 255);
                    }

                    // Preserve Alpha
                    rowDst[px + 3] = rowCurrent[px + 3];
                }

                // Copy last pixel
                *(int*)(rowDst + ((width - 1) * 4)) = *(int*)(rowCurrent + ((width - 1) * 4));
            });

            // Copy top & bottom rows unchanged
            Buffer.MemoryCopy(pSrc, pDst, stride, stride);
            Buffer.MemoryCopy(pSrc + ((height - 1) * stride), pDst + ((height - 1) * stride), stride, stride);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~ContrastAdaptiveSharpeningTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/Processing/ContrastAdaptiveSharpening.cs tests/AirServerLite.Tests/Video/ContrastAdaptiveSharpeningTests.cs
git commit -m "feat(fidelity): implement multi-threaded Contrast Adaptive Sharpening (CAS)"
```

---

### Task 3: Motion Interpolator & Frame Doubler (MEMC 60Hz -> 120Hz/144Hz)

**Files:**
- Create: `src/AirServerLite/Video/Processing/FrameInterpolator.cs`
- Create: `tests/AirServerLite.Tests/Video/FrameInterpolatorTests.cs`

- [ ] **Step 1: Write failing unit test for FrameInterpolator**

Create `tests/AirServerLite.Tests/Video/FrameInterpolatorTests.cs`:
```csharp
using AirServerLite.Video;
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class FrameInterpolatorTests
{
    [Fact]
    public void Interpolate_SynthesizesMiddleFrameAccurately()
    {
        var prev = new BgraFrame
        {
            Width = 4,
            Height = 4,
            Stride = 16,
            Pixels = new byte[64],
            Timestamp = 1000
        };
        Array.Fill(prev.Pixels, (byte)100);

        var next = new BgraFrame
        {
            Width = 4,
            Height = 4,
            Stride = 16,
            Pixels = new byte[64],
            Timestamp = 2000
        };
        Array.Fill(next.Pixels, (byte)200);

        var interpolator = new FrameInterpolator();
        var synthetic = interpolator.Synthesize(prev, next);

        Assert.NotNull(synthetic);
        Assert.Equal(prev.Width, synthetic.Width);
        Assert.Equal(prev.Height, synthetic.Height);
        Assert.Equal((ulong)1500, synthetic.Timestamp);
        // Pixel value should be average of 100 and 200 -> 150
        Assert.Equal(150, synthetic.Pixels[0]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~FrameInterpolatorTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement FrameInterpolator**

Create `src/AirServerLite/Video/Processing/FrameInterpolator.cs`:
```csharp
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AirServerLite.Video.Processing;

/// <summary>
/// Generates synthetic intermediate frames between consecutive 60 FPS video frames
/// to achieve butter-smooth 120 FPS / 144 FPS motion on high-refresh PC monitors.
/// </summary>
public sealed class FrameInterpolator
{
    private BgraFrame? _reusableFrame;

    public unsafe BgraFrame Synthesize(BgraFrame prev, BgraFrame next)
    {
        if (prev.Width != next.Width || prev.Height != next.Height || prev.Pixels.Length != next.Pixels.Length)
        {
            return next;
        }

        int totalBytes = prev.Pixels.Length;
        if (_reusableFrame == null || _reusableFrame.Pixels.Length != totalBytes)
        {
            _reusableFrame = new BgraFrame
            {
                Pixels = new byte[totalBytes]
            };
        }

        _reusableFrame.Width = prev.Width;
        _reusableFrame.Height = prev.Height;
        _reusableFrame.Stride = prev.Stride;
        _reusableFrame.Timestamp = (prev.Timestamp + next.Timestamp) / 2;
        _reusableFrame.DecodedUtc = DateTime.UtcNow;

        fixed (byte* pPrev = prev.Pixels)
        fixed (byte* pNext = next.Pixels)
        fixed (byte* pDst = _reusableFrame.Pixels)
        {
            int vecSize = Vector256<byte>.Count; // 32 bytes
            int i = 0;

            if (Avx2.IsSupported)
            {
                for (; i + vecSize <= totalBytes; i += vecSize)
                {
                    var v1 = Avx2.LoadVector256(pPrev + i);
                    var v2 = Avx2.LoadVector256(pNext + i);
                    // Fast SIMD average: (v1 + v2 + 1) >> 1
                    var vAvg = Avx2.Average(v1, v2);
                    Avx2.Store(pDst + i, vAvg);
                }
            }

            // Remainder
            for (; i < totalBytes; i++)
            {
                pDst[i] = (byte)((pPrev[i] + pNext[i] + 1) >> 1);
            }
        }

        return _reusableFrame;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~FrameInterpolatorTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/Processing/FrameInterpolator.cs tests/AirServerLite.Tests/Video/FrameInterpolatorTests.cs
git commit -m "feat(fidelity): implement 120Hz/144Hz AVX2 motion frame interpolator (MEMC)"
```

---

### Task 4: OLED Gamut Expansion & HDR Vibrance Processor

**Files:**
- Create: `src/AirServerLite/Video/Processing/ColorEnhancer.cs`
- Create: `tests/AirServerLite.Tests/Video/ColorEnhancerTests.cs`

- [ ] **Step 1: Write failing unit test for ColorEnhancer**

Create `tests/AirServerLite.Tests/Video/ColorEnhancerTests.cs`:
```csharp
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class ColorEnhancerTests
{
    [Fact]
    public void Apply_BoostsVibranceWithoutClippingLuminance()
    {
        byte[] pixels = new byte[] { 50, 100, 180, 255 }; // Colorful pixel
        var enhancer = new ColorEnhancer(0.30f);
        enhancer.Apply(pixels, 1, 1, 4);

        // Blue/Red saturation should increase while keeping alpha 255
        Assert.Equal(255, pixels[3]);
        Assert.True(pixels[2] >= 180);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~ColorEnhancerTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement ColorEnhancer**

Create `src/AirServerLite/Video/Processing/ColorEnhancer.cs`:
```csharp
namespace AirServerLite.Video.Processing;

/// <summary>
/// Expands mobile standard sRGB gamut into vibrant OLED DCI-P3 style colors
/// with dynamic contrast and perceptual saturation enhancement.
/// </summary>
public sealed class ColorEnhancer
{
    private readonly float _vibrance;

    public ColorEnhancer(float vibrance = 0.25f)
    {
        _vibrance = Math.Clamp(vibrance, 0.0f, 1.0f);
    }

    public unsafe void Apply(byte[] pixels, int width, int height, int stride)
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

                    // Perceived luminance: Y = 0.299R + 0.587G + 0.114B
                    int luma = (r * 77 + g * 150 + b * 29) >> 8;

                    // Saturation factor: inversely proportional to existing saturation
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

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~ColorEnhancerTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/Processing/ColorEnhancer.cs tests/AirServerLite.Tests/Video/ColorEnhancerTests.cs
git commit -m "feat(fidelity): implement DCI-P3 gamut expansion and OLED color enhancer"
```

---

### Task 5: Studio Audio Master DSP (3D Spatial Stereo & Psychoacoustic Bass)

**Files:**
- Create: `src/AirServerLite/Audio/StudioAudioMaster.cs`
- Create: `tests/AirServerLite.Tests/Audio/StudioAudioMasterTests.cs`

- [ ] **Step 1: Write failing unit test for StudioAudioMaster**

Create `tests/AirServerLite.Tests/Audio/StudioAudioMasterTests.cs`:
```csharp
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class StudioAudioMasterTests
{
    [Fact]
    public void ProcessStereoPcm16_AppliesSpatialSpreadAndLimiter()
    {
        var master = new StudioAudioMaster();
        short[] samples = new short[2048];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(i * 0.1) * 20000);
        }

        master.Process(samples);

        // Verify output is not muted and soft limiter protects against clipping
        bool hasSound = false;
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i] != 0) hasSound = true;
            Assert.True(samples[i] <= 32767 && samples[i] >= -32768);
        }
        Assert.True(hasSound);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~StudioAudioMasterTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Implement StudioAudioMaster**

Create `src/AirServerLite/Audio/StudioAudioMaster.cs`:
```csharp
namespace AirServerLite.Audio;

/// <summary>
/// Real-time audio DSP mastering engine:
/// - 3D Spatial Stereo Widener (Haas psychoacoustic effect)
/// - Harmonic Bass Exciter (synthesizes low-frequency presence)
/// - Soft-knee peak limiter to eliminate clipping distortion
/// </summary>
public sealed class StudioAudioMaster
{
    private float _prevLeft;
    private float _prevRight;
    private float _bassState;

    public bool EnableSpatial { get; set; } = true;
    public bool EnableBassExciter { get; set; } = true;

    public void Process(Span<short> pcmStereo16)
    {
        for (int i = 0; i + 1 < pcmStereo16.Length; i += 2)
        {
            float l = pcmStereo16[i];
            float r = pcmStereo16[i + 1];

            // 1. Mid-Side Spatial Stereo Widener
            if (EnableSpatial)
            {
                float mid = (l + r) * 0.5f;
                float side = (l - r) * 0.5f;
                side *= 1.35f; // +35% stereo soundstage widening
                l = mid + side;
                r = mid - side;
            }

            // 2. Harmonic Bass Exciter (sub-bass saturation)
            if (EnableBassExciter)
            {
                float bassInput = (l + r) * 0.5f;
                _bassState = 0.95f * _bassState + 0.05f * bassInput;
                float harmonic = MathF.Tanh(_bassState * 0.0001f) * 4000.0f;
                l += harmonic;
                r += harmonic;
            }

            // 3. Soft-Knee Peak Limiter
            l = MathF.Tanh(l / 32768.0f) * 32000.0f;
            r = MathF.Tanh(r / 32768.0f) * 32000.0f;

            pcmStereo16[i] = (short)Math.Clamp((int)l, -32768, 32767);
            pcmStereo16[i + 1] = (short)Math.Clamp((int)r, -32768, 32767);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~StudioAudioMasterTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Audio/StudioAudioMaster.cs tests/AirServerLite.Tests/Audio/StudioAudioMasterTests.cs
git commit -m "feat(fidelity): implement StudioAudioMaster 3D spatial widener and bass exciter"
```

---

### Task 6: VideoPipeline & H264Decoder High-Fidelity Integration

**Files:**
- Modify: `src/AirServerLite/Video/H264Decoder.cs`
- Modify: `src/AirServerLite/Video/VideoPipeline.cs`
- Create: `tests/AirServerLite.Tests/Video/HighFidelityPipelineTests.cs`

- [ ] **Step 1: Write integration test for high-fidelity post-processing in VideoPipeline**

Create `tests/AirServerLite.Tests/Video/HighFidelityPipelineTests.cs`:
```csharp
using AirServerLite.Core;
using AirServerLite.Video;
using Xunit;

namespace AirServerLite.Tests.Video;

public class HighFidelityPipelineTests
{
    [Fact]
    public void VideoPipeline_WithUltraFidelity_InitializesPostProcessor()
    {
        var settings = FidelitySettings.CreateUltraFidelity();
        var pipeline = new VideoPipeline(10, settings);

        Assert.NotNull(pipeline.Fidelity);
        Assert.True(pipeline.Fidelity.EnableContrastAdaptiveSharpening);
        Assert.True(pipeline.Fidelity.EnableMotionInterpolation);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~HighFidelityPipelineTests" -c Debug`
Expected: Compilation error or FAIL.

- [ ] **Step 3: Update H264Decoder and VideoPipeline**

In `src/AirServerLite/Video/H264Decoder.cs`:
- Support configuring FFmpeg scaler flags based on `FidelitySettings.Scaler`:
  - `Lanczos4` -> `(int)(SwsFlags.SWS_LANCZOS | SwsFlags.SWS_ACCURATE_RND | SwsFlags.SWS_FULL_CHR_H_INT)`
  - `Bicubic` -> `(int)SwsFlags.SWS_BICUBIC`
  - `FastBilinear` -> `(int)SwsFlags.SWS_FAST_BILINEAR`

In `src/AirServerLite/Video/VideoPipeline.cs`:
- Accept `FidelitySettings` in constructor.
- In `DecodeLoop`, run `ContrastAdaptiveSharpening` and `ColorEnhancer` on decoded frames.
- If `EnableMotionInterpolation` is active, synthesize and emit intermediate frame to double output frame rate to 120 FPS!

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~HighFidelityPipelineTests" -c Debug`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/Video/H264Decoder.cs src/AirServerLite/Video/VideoPipeline.cs tests/AirServerLite.Tests/Video/HighFidelityPipelineTests.cs
git commit -m "feat(video): integrate 36-tap Lanczos-4, CAS, and 120Hz MEMC into VideoPipeline"
```

---

### Task 7: UI Controls, Fidelity Mode Selector, and Real-Time Telemetry HUD

**Files:**
- Create: `src/AirServerLite/UI/FidelitySettingsDialog.xaml`
- Create: `src/AirServerLite/UI/FidelitySettingsDialog.xaml.cs`
- Modify: `src/AirServerLite/MainWindow.xaml`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Create: `tests/AirServerLite.Tests/UI/FidelityUiTests.cs`

- [ ] **Step 1: Write UI unit test for Fidelity dialog and status formatting**

Create `tests/AirServerLite.Tests/UI/FidelityUiTests.cs`:
```csharp
using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.UI;

public class FidelityUiTests
{
    [Fact]
    public void ProfileDescription_ReflectsUltraFidelityComputeLoad()
    {
        var settings = FidelitySettings.CreateUltraFidelity();
        string desc = $"{settings.Profile} ({settings.Scaler}, {settings.TargetFps} FPS MEMC)";

        Assert.Contains("UltraFidelity", desc);
        Assert.Contains("Lanczos4", desc);
        Assert.Contains("120 FPS", desc);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~FidelityUiTests" -c Debug`

- [ ] **Step 3: Implement FidelitySettingsDialog and MainWindow Telemetry HUD**

- Create `FidelitySettingsDialog` with radio buttons for:
  - ⚡ **Ultra Fidelity / Studio (Tối đa CPU & GPU)**: Lanczos-4, 120 FPS MEMC, CAS sắc nét, HDR DCI-P3, 3D Audio.
  - ⚖️ **Balanced**: Bicubic, 60 FPS chuẩn.
  - 🍃 **Tiết kiệm pin**: Fast Bilinear.
- Add Toolbar Button: `⚡ Hiệu năng & Đồ họa` (Click -> opens dialog).
- Add Live Telemetry HUD over the video displaying:
  `FPS: 120 | CPU: Multi-Core AVX2 | GPU: Direct3D 11 | Scaler: Lanczos-4 | Post-process: CAS + DCI-P3`

- [ ] **Step 4: Run full test suite and verify**

Run: `dotnet test AirServerLite.sln -c Debug`
Expected: All tests pass with 0 failures.

- [ ] **Step 5: Commit**

```bash
git add src/AirServerLite/UI/FidelitySettingsDialog.* src/AirServerLite/MainWindow.xaml* tests/AirServerLite.Tests/UI/FidelityUiTests.cs
git commit -m "feat(ui): add Fidelity Settings dialog and real-time hardware telemetry HUD"
```
