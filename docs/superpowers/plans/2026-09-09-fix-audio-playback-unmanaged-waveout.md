# Fix Audio Playback via Unmanaged WaveOut Buffers & Ensure Smooth YouTube Mirroring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the audio stall where YouTube video mirrors smoothly on screen but produces no sound after 4 frames due to C# managed `WaveHdr` marshaling freezing the pump loop, and ensure continuous, crystal-clear 60 FPS video and stutter-free audio for YouTube.

**Architecture:** Allocate `WAVEHDR` headers and PCM audio buffers directly in unmanaged memory via `Marshal.AllocHGlobal`, passing unmanaged pointers (`IntPtr`) to `waveOutPrepareHeader` and `waveOutWrite`. The Windows sound driver directly and asynchronously updates `dwFlags` (`WHDR_DONE` / `WHDR_INQUEUE`) in unmanaged memory and triggers `CALLBACK_EVENT`, allowing `CountInFlightBuffers()` to accurately decrement and stream audio continuously without stalling.

**Tech Stack:** C# .NET 8, Win32 Multimedia API (`winmm.dll`, `waveOut*`, `timeBeginPeriod`), `kernel32.dll` (`WaitForSingleObject`), xUnit.

---

### Task 1: Add Unit Tests Verifying Unmanaged WaveOut Multi-Buffer Playback

**Files:**
- Modify: `tests/AirServerLite.Tests/Audio/AudioPlayerTests.cs`

- [ ] **Step 1: Write tests for unmanaged multi-buffer playback and sustained feed**

Add tests ensuring `AudioPlayer` can receive 50+ frames sequentially through `PlayPcm` and `AttachJitterBuffer` without blocking or freezing after 4 buffers.

```csharp
    [Fact]
    public void AudioPlayer_PlayPcm_MultipleBuffers_DoesNotFreeze()
    {
        using var player = new AudioPlayer(44100, 2, 16);
        byte[] pcm = new byte[1920]; // 10.88ms frame

        // Writing 20 consecutive buffers must not block or throw
        for (int i = 0; i < 20; i++)
        {
            player.PlayPcm(pcm, pcm.Length);
        }

        Assert.True(player.Volume > 0);
    }

    [Fact]
    public void AudioPlayer_AttachJitterBuffer_SustainedPlayback_PlaysContinuously()
    {
        using var player = new AudioPlayer(44100, 2, 16);
        var jitter = new AudioJitterBuffer(preBufferFrames: 4);
        player.AttachJitterBuffer(jitter);

        // Feed 15 frames into jitter buffer
        for (ushort i = 0; i < 15; i++)
        {
            byte[] frame = new byte[1920];
            jitter.Write(i, (uint)(i * 480), frame, frame.Length, isPooled: false);
        }

        // Allow pump thread to process
        Thread.Sleep(80);

        // Verify player is alive and did not freeze at 4
        Assert.True(player.PlayedCount > 4, $"Expected played > 4, but got {player.PlayedCount}");
    }
```

- [ ] **Step 2: Run test to verify failure with existing implementation**

Run: `dotnet test tests/AirServerLite.Tests --filter "AudioPlayer_AttachJitterBuffer_SustainedPlayback_PlaysContinuously"`
Expected: FAIL (stalls or plays <= 4 because `inFlight` count never drops in current implementation).

---

### Task 2: Implement Unmanaged `WAVEHDR` Allocation and Pointer-Based P/Invoke in `AudioPlayer.cs`

**Files:**
- Modify: `src/AirServerLite/Audio/AudioPlayer.cs`

- [ ] **Step 1: Update Win32 P/Invoke signatures and allocate headers in unmanaged memory**

1. Change `waveOutPrepareHeader`, `waveOutUnprepareHeader`, and `waveOutWrite` to accept `IntPtr lpWaveHdr` instead of `ref WaveHdr`.
2. Replace `WaveHdr[] _headers` with `IntPtr[] _headerPtrs = new IntPtr[BufferCount]`.
3. In constructor, allocate each header with `Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>())` and zero memory.
4. Calculate `FlagsOffset = Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags)).ToInt32()`.
5. Expose `public long PlayedCount => Interlocked.Read(ref _played);` for diagnostics and testing.

- [ ] **Step 2: Update `CountInFlightBuffers()` and `SubmitBuffer` to read/write unmanaged memory**

1. `CountInFlightBuffers()`:
```csharp
private int CountInFlightBuffers()
{
    int inFlight = 0;
    for (int i = 0; i < BufferCount; i++)
    {
        if (_headerPtrs[i] != IntPtr.Zero)
        {
            uint flags = (uint)Marshal.ReadInt32(_headerPtrs[i], FlagsOffset);
            if ((flags & WHDR_INQUEUE) != 0)
                inFlight++;
        }
    }
    return inFlight;
}
```

2. `SubmitBuffer(int index, byte[] pcm, int length)`:
```csharp
private void SubmitBuffer(int index, byte[] pcm, int length)
{
    lock (_lock)
    {
        if (_disposed || _hWaveOut == IntPtr.Zero) return;

        var headerPtr = _headerPtrs[index];
        var dataPtr = _buffers[index];
        if (headerPtr == IntPtr.Zero || dataPtr == IntPtr.Zero) return;

        _nextBuffer = (index + 1) % BufferCount;

        uint flags = (uint)Marshal.ReadInt32(headerPtr, FlagsOffset);
        if ((flags & WHDR_PREPARED) != 0)
        {
            waveOutUnprepareHeader(_hWaveOut, headerPtr, (uint)Marshal.SizeOf<WaveHdr>());
        }

        var copyLen = Math.Min(length, BufferSize);
        Marshal.Copy(pcm, 0, dataPtr, copyLen);

        Marshal.WriteIntPtr(headerPtr, 0, dataPtr); // lpData
        Marshal.WriteInt32(headerPtr, BufferLengthOffset, copyLen); // dwBufferLength
        Marshal.WriteInt32(headerPtr, FlagsOffset, 0); // dwFlags = 0

        var prepRc = waveOutPrepareHeader(_hWaveOut, headerPtr, (uint)Marshal.SizeOf<WaveHdr>());
        if (prepRc != 0) return;

        waveOutWrite(_hWaveOut, headerPtr, (uint)Marshal.SizeOf<WaveHdr>());
    }
}
```

3. Update `Dispose()` to safely unprepare and free all `_headerPtrs` and `_buffers`.

- [ ] **Step 3: Run unit tests to verify they pass**

Run: `dotnet test tests/AirServerLite.Tests`
Expected: PASS (all 53+ tests passing).

- [ ] **Step 4: Commit changes**

```bash
git add src/AirServerLite/Audio/AudioPlayer.cs tests/AirServerLite.Tests/Audio/AudioPlayerTests.cs
git commit -m "fix(audio): unmanaged WAVEHDR pointers for reliable asynchronous waveOut driver tracking"
```

---

### Task 3: Package Executable and Perform End-to-End Verification

**Files:**
- Execute: `tools/pack.ps1 -SkipPlayfair`
- Output: `dist/AirServerLite.exe`

- [ ] **Step 1: Execute package script**

Run: `powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -SkipPlayfair`
Expected: Generates `dist/AirServerLite.exe` successfully.

- [ ] **Step 2: Verify binary size and launch sanity check**

Ensure `dist/AirServerLite.exe` exists, timestamp is current, and size is ~160 MB.

- [ ] **Step 3: Update walkthrough artifact with user instructions**
