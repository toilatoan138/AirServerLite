# Fix YouTube Playback, GPU Acceleration & Audio Stabilization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the YouTube "Đã xảy ra lỗi. Nhấn để thử lại" playback failure on iOS by eliminating audio pipeline underruns that trigger RTSP TEARDOWN, enable full GPU hardware-accelerated video rendering in WPF, and package the final application into `dist\AirServerLite.exe`.

**Architecture:** 
1. **Audio Stabilization:** Throttled hardware audio pump in `AudioPlayer` maintaining a steady 3-4 buffer in-flight driver queue clocked by `_hEvent`, eliminating the microsecond drain-and-pause loop that caused 80 underruns in 5 seconds and prompted iOS to tear down audio.
2. **Jitter Buffer Resilience:** Maintain `_primed = true` during steady-state playback without resetting on momentary empty reads, keeping the 65ms jitter cushion intact.
3. **GPU Hardware Acceleration:** Replace WPF's CPU-heavy `BitmapScalingMode="Fant"` with GPU-accelerated `Linear` hardware texture sampling, configure `ProcessRenderMode = RenderMode.Default`, and enable high-performance Direct3D tier 2 rendering.
4. **YouTube Playback Ease:** Ensure both Screen Mirroring and DIAL YouTube Cast (direct 1080p/4K 60fps web player) work reliably.
5. **Single-File Packaging:** Produce updated `dist\AirServerLite.exe` via `tools\pack.ps1`.

**Tech Stack:** .NET 8 (`net8.0-windows`), C# 12, WPF Direct3D 9/11 Hardware Acceleration, Win32 `waveOut` with 1ms timer precision, FFmpeg 9.0, PowerShell pack script.

---

### File Structure & Responsibilities

- `src/AirServerLite/Audio/AudioPlayer.cs` (Modify)
  - Throttle `PumpLoop`: maintain 3-4 buffers in sound card queue (`WHDR_INQUEUE`).
  - Wait on `_hEvent` when driver queue is full; replenish exactly 1 buffer per completion.
  - Fix underrun storm that caused iOS to TEARDOWN audio and break YouTube playback.
- `src/AirServerLite/Audio/AudioJitterBuffer.cs` (Modify)
  - Retain `_primed = true` during normal playback; only enter re-priming on sustained silence (>150ms).
- `src/AirServerLite/MainWindow.xaml` (Modify)
  - Change `RenderOptions.BitmapScalingMode="Fant"` to `"Linear"` for 100% GPU texture sampling.
  - Set `RenderOptions.EdgeMode="Aliased"` for instant GPU quad blitting.
- `src/AirServerLite/MainWindow.xaml.cs` (Modify)
  - Explicitly enable Direct3D hardware acceleration (`RenderOptions.ProcessRenderMode = RenderMode.Default`).
  - Verify and log GPU rendering tier (`RenderCapability.Tier >> 16`).
- `tests/AirServerLite.Tests/Audio/AudioJitterBufferTests.cs` (Modify)
  - Add unit test for steady-state priming preservation under momentary empty reads.
- `tests/AirServerLite.Tests/Audio/AudioPlayerTests.cs` (Modify)
  - Add unit test for queue depth throttling.
- `dist/AirServerLite.exe` (Pack Output)
  - Packaged single-file distributable.

---

## Tasks

### Task 1: Add Unit Tests for Steady-State Jitter Buffer & Queue Depth Throttling

**Files:**
- Modify: `tests/AirServerLite.Tests/Audio/AudioJitterBufferTests.cs`

- [ ] **Step 1: Write unit test verifying jitter buffer stays primed during momentary empty reads**

Add test to `tests/AirServerLite.Tests/Audio/AudioJitterBufferTests.cs`:

```csharp
    [Fact]
    public void JitterBuffer_SteadyState_DoesNotResetPrimedOnMomentaryEmpty()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 3);

        // Pre-buffer 3 frames
        buffer.Write(1, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(2, 1480, new byte[1920], 1920, isPooled: false);
        buffer.Write(3, 1960, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);

        // Read all 3 frames
        Assert.True(buffer.TryRead(out _));
        Assert.True(buffer.TryRead(out _));
        Assert.True(buffer.TryRead(out _));

        // Queue is now empty, but buffer was primed and should remain ready for incoming packets
        // without requiring another full pre-buffer pause
        Assert.False(buffer.TryRead(out _)); // Returns false (no frame right now)

        // Write the 4th frame (arriving 10ms later)
        buffer.Write(4, 2440, new byte[1920], 1920, isPooled: false);

        // Should be immediately readable without waiting for 3 new frames
        Assert.True(buffer.TryRead(out var frame4));
        Assert.Equal(4, frame4.SequenceNumber);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~DoesNotResetPrimedOnMomentaryEmpty --no-restore -v n`
Expected: FAIL (because current `TryRead` resets `_primed = false` when `_queue.Count == 0`).

---

### Task 2: Fix AudioJitterBuffer Steady-State Priming Logic

**Files:**
- Modify: `src/AirServerLite/Audio/AudioJitterBuffer.cs`

- [ ] **Step 1: Implement sustained silence detection instead of instant un-priming**

In `src/AirServerLite/Audio/AudioJitterBuffer.cs`:
Replace the instant `_primed = false` on empty read with a sustained starvation check (only unprime if empty for > 200ms of audio or on explicit reset):

```csharp
    private DateTime _lastReadTime = DateTime.UtcNow;
    private const int SustainedSilenceMs = 250;

    public bool TryRead(out AudioFrame frame)
    {
        lock (_lock)
        {
            if (_disposed || !_primed)
            {
                frame = default;
                return false;
            }

            if (_queue.Count == 0)
            {
                frame = default;
                // Only unprime if there has been genuine sustained silence (>250ms),
                // not a momentary 5-10ms WiFi packet arrival gap
                if ((DateTime.UtcNow - _lastReadTime).TotalMilliseconds > SustainedSilenceMs)
                {
                    _primed = false;
                    Log.Debug(Tag, "Sustained audio gap >250ms — pausing for re-priming");
                }
                return false;
            }

            frame = _queue.Dequeue();
            _recentSeqs.Remove((ushort)(frame.SequenceNumber - 32));
            _nextExpectedSeq = (ushort)(frame.SequenceNumber + 1);
            _lastReadTime = DateTime.UtcNow;
            Interlocked.Increment(ref _read);
            return true;
        }
    }
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~DoesNotResetPrimedOnMomentaryEmpty --no-restore -v n`
Expected: PASS.

---

### Task 3: Overhaul AudioPlayer.PumpLoop with Queue Depth Throttling (Hardware Clock Sync)

**Files:**
- Modify: `src/AirServerLite/Audio/AudioPlayer.cs`

- [ ] **Step 1: Limit in-flight sound driver buffers to TargetInFlight (3-4 buffers)**

In `src/AirServerLite/Audio/AudioPlayer.cs`:
Instead of draining up to 24 buffers immediately in a tight loop:
1. Define `TargetInFlightBuffers = 4;` (~45ms in driver queue).
2. In `PumpLoop`, count how many buffers are currently in the driver's queue (`WHDR_INQUEUE`).
3. If `inFlight >= TargetInFlightBuffers`, do NOT pull from the jitter buffer. Instead, call `WaitForSingleObject(_hEvent, 10)` to wait for the sound card to finish playing a buffer!
4. When `_hEvent` fires, exactly 1 buffer has finished. `PumpLoop` pulls exactly 1 frame from `AudioJitterBuffer` and submits it.
5. This locks the pump thread to the hardware audio clock, maintains the jitter buffer reserve, and reduces underruns to zero!

```csharp
    private const int TargetInFlightBuffers = 4; // ~45ms audio queued in waveOut driver

    private int CountInFlightBuffers()
    {
        int inFlight = 0;
        for (int i = 0; i < BufferCount; i++)
        {
            if ((_headers[i].dwFlags & WHDR_INQUEUE) != 0)
                inFlight++;
        }
        return inFlight;
    }
```

In `PumpLoop()`:
```csharp
                // Check how many buffers are currently in the sound card driver queue
                int inFlight;
                int chosen = -1;
                lock (_lock)
                {
                    if (_disposed || _hWaveOut == IntPtr.Zero) break;

                    inFlight = CountInFlightBuffers();

                    if (inFlight < TargetInFlightBuffers)
                    {
                        for (int i = 0; i < BufferCount; i++)
                        {
                            var candidate = (_nextBuffer + i) % BufferCount;
                            if ((_headers[candidate].dwFlags & WHDR_INQUEUE) == 0)
                            {
                                chosen = candidate;
                                break;
                            }
                        }

                        if (chosen < 0)
                        {
                            for (int i = 0; i < BufferCount; i++)
                            {
                                if ((_headers[i].dwFlags & WHDR_DONE) != 0)
                                {
                                    chosen = i;
                                    break;
                                }
                            }
                        }
                    }
                }

                // If driver queue has enough audio ahead, wait for hardware clock tick
                if (chosen < 0 || inFlight >= TargetInFlightBuffers)
                {
                    WaitForSingleObject(_hEvent, 10);
                    continue;
                }

                // Pull exactly 1 frame from jitter buffer to replenish
                if (jitter.TryRead(out var frame))
                {
                    try
                    {
                        if (_needsRampIn)
                        {
                            ApplyRampIn(frame.Data, frame.Length, RampSamples);
                            _needsRampIn = false;
                        }

                        SubmitBuffer(chosen, frame.Data, frame.Length);
                        Interlocked.Increment(ref _played);
                    }
                    finally
                    {
                        frame.Return();
                    }
                }
                else
                {
                    // Underrun — wait briefly for audio packet or sound event
                    _needsRampIn = true;
                    Interlocked.Increment(ref _underruns);
                    WaitForSingleObject(_hEvent, 5);
                }
```

- [ ] **Step 2: Run audio unit tests**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~Audio --no-restore -v n`
Expected: PASS (all tests pass).

---

### Task 4: GPU Hardware Acceleration & Video Render Optimization in WPF

**Files:**
- Modify: `src/AirServerLite/MainWindow.xaml`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`

- [ ] **Step 1: Switch VideoImage to GPU-accelerated Linear scaling and Aliased EdgeMode**

In `src/AirServerLite/MainWindow.xaml`:
Replace `RenderOptions.BitmapScalingMode="Fant"` with `"Linear"` (hardware bilinear texture filtering on the GPU).
Set `RenderOptions.EdgeMode="Aliased"` on `VideoImage`.

```xml
                <Image x:Name="VideoImage" Stretch="Uniform"
                       RenderOptions.BitmapScalingMode="Linear"
                       RenderOptions.EdgeMode="Aliased"
                       SnapsToDevicePixels="True"
                       UseLayoutRounding="True"/>
```

- [ ] **Step 2: Configure WPF Direct3D GPU ProcessRenderMode and Log GPU Tier**

In `src/AirServerLite/MainWindow.xaml.cs`:
In `OnLoaded`:
```csharp
    // Ensure full GPU hardware acceleration is active
    System.Windows.Interop.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
    int renderingTier = RenderCapability.Tier >> 16;
    Log.Info(LogTag, $"Direct3D GPU Hardware Rendering Tier: {renderingTier} (Tier 2 = Full Hardware Acceleration)");
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/AirServerLite/AirServerLite.csproj --no-restore`
Expected: 0 errors, 0 warnings.

---

### Task 5: Verify Full Test Suite & Package Single-File Distributable

**Files:**
- Output: `dist/AirServerLite.exe`

- [ ] **Step 1: Run complete unit test suite**

Run: `dotnet test tests/AirServerLite.Tests --no-restore -v n`
Expected: All 51+ tests pass.

- [ ] **Step 2: Stop any running AirServerLite process and run pack.ps1**

Run:
```powershell
Get-Process AirServerLite -ErrorAction SilentlyContinue | Stop-Process -Force
powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -SkipPlayfair
```
Expected:
Outputs `dist\AirServerLite.exe` (~160MB single file).

- [ ] **Step 3: Commit all changes to git**

```bash
git add src/AirServerLite/Audio/ src/AirServerLite/MainWindow.xaml src/AirServerLite/MainWindow.xaml.cs tests/AirServerLite.Tests/Audio/ docs/superpowers/plans/2026-09-09-fix-youtube-playback-gpu-acceleration.md
git commit -m "fix(audio/gpu): throttle waveOut pump, stabilize jitter priming, enable GPU bilinear scaling, and pack single-file exe"
```

---

## Verification Plan

### Automated Tests
- `dotnet test tests/AirServerLite.Tests --no-restore -v n`
- Verify jitter buffer does not unprime on momentary empty reads.
- Verify audio player pump operates cleanly without throwing.

### Manual Verification
1. Launch `dist\AirServerLite.exe`.
2. Verify log output confirms:
   `Direct3D GPU Hardware Rendering Tier: 2 (Full Hardware Acceleration)`
   `Audio hardware pump thread started at Highest priority`
3. Mirror iPhone (iOS 18) screen to `AirServer-LITE`.
4. Open the YouTube app on iPhone and tap the "Toàn bộ 18 HỆ trong POKEMON" video (or any other video).
5. Verify:
   - Video plays immediately without "Đã xảy ra lỗi. Nhấn để thử lại".
   - Audio plays continuously with 0 underruns (no `TEARDOWN audio=True`).
   - Video runs at full 60 fps smoothly with GPU acceleration.
