# YouTube Streaming & Audio Optimization Design

- **Date:** 2026-09-08
- **Topic:** YouTube AirPlay Cast, Screen Mirroring Audio, and Cinema Fullscreen Mode
- **Status:** Approved by User

---

## 1. Background & Problem Statement

AirServerLite currently supports AirPlay Screen Mirroring (H.264 video on stream type 110). However, when users try to play and present YouTube videos from iOS devices on their PC, they face three critical limitations:

1. **No Audio Output:** Audio packets on stream type 96 (AAC-ELD / ALAC) are accepted and drained without decoding (`DrainUdpAsync`), resulting in complete silence during video playback.
2. **Missing AirPlay Media Handling (`POST /play`):** When tapping the AirPlay/Cast icon in the YouTube app or Safari, iOS sends HTTP/RTSP requests (`POST /play`) containing HLS master playlists. AirServerLite currently lacks handlers for these media endpoints, causing iOS to freeze, disconnect, or show a black screen.
3. **Fixed Portrait Aspect Ratio & No Fullscreen:** The app interface is currently constrained to a 560x880 portrait layout. When watching 16:9 landscape YouTube videos, the video appears as a tiny stripe with large black letterboxes, and there is no true borderless Fullscreen (Cinema) mode.

---

## 2. Goals & Non-Goals

### Goals
- **Audio Subsystem:** Implement real-time reception, AES-CBC decryption, FFmpeg decoding (AAC-ELD/ALAC to PCM 16-bit 44.1kHz), and low-latency audio output (WASAPI / winmm `WaveOut`) with volume control and mute.
- **Cinema Display & Fullscreen Mode:** Provide seamless Fullscreen toggling (F11, Alt+Enter, double click), auto-orientation handling for landscape video (16:9), auto-hiding HUD overlay with volume and playback controls, and maintain accurate WDA input mapping in Fullscreen.
- **AirPlay Media Engine (`POST /play`):** Handle AirPlay media streaming endpoints (`POST /play`, `GET /playback-info`, `POST /rate`, `POST /scrub`, `POST /stop`), parse binary plists, and coordinate playback state with iOS devices.
- **Quick Play Direct Link:** Provide an integrated utility to paste or launch YouTube video links directly on PC with playback controls.

### Non-Goals
- FairPlay DRM hardware-decrypted video from paid YouTube Movies/Rentals (which requires Apple-certified FairPlay DRM hardware decoders). Standard YouTube videos and HLS streams are fully supported.
- Bluetooth HID wireless peripheral emulation (ruled out due to Windows platform limitations as detailed in README).

---

## 3. System Architecture & Components

```
                            [ iPhone / iOS ]
                                   │
         ┌─────────────────────────┼─────────────────────────┐
         │ (AirPlay Mirroring)     │ (AirPlay Media)         │ (Audio Stream)
         ▼                         ▼                         ▼
   TCP Stream 110            HTTP POST /play           UDP Stream 96
(MirrorStreamReceiver)      (AirPlaySession)        (AudioStreamReceiver)
         │                         │                         │
         │ (H.264 Annex-B)         │ (HLS / Media URL)       │ (AES-CBC Encrypted)
         ▼                         ▼                         ▼
    VideoPipeline            MediaController            AudioCipher
         │                         │                         │
   H264Decoder (FFmpeg)            │                   AudioDecoder (FFmpeg)
         │                         │                         │
   WriteableBitmap                 │                   AudioPlayer (WASAPI/WinMM)
         │                         │                         │
         └─────────────────┬───────┴─────────────────────────┘
                           ▼
                    MainWindow (WPF)
            - Auto-orientation (16:9 / 9:16)
            - Fullscreen Cinema Mode (F11 / Double-Click)
            - Auto-hiding HUD & Volume Controls
            - CoordinateMapper (Input Touch/Click in Fullscreen)
```

### 3.1 Audio Subsystem (`src/AirServerLite/Audio/`)

1. **`AudioStreamReceiver.cs`**:
   - Binds ephemeral UDP port for audio data and control.
   - Listens for RTP packets (payload type 96):
     - Bytes 0..11: RTP header (sequence number, timestamp).
     - Bytes 12..end: Encrypted audio payload.
   - Handles packet ordering and discards initial no-data markers (`0x00 0x68 0x34 0x00`).
2. **`AudioCipher.cs`**:
   - Decrypts RTP payload using AES-128-CBC.
   - Uses the session AES key derived during FairPlay / pair-verify.
   - Resets CBC IV to zero for each audio packet per AirPlay RAOP specification.
3. **`AudioDecoder.cs`**:
   - Uses FFmpeg (`AV_CODEC_ID_AAC` / `AAC_LATM` / `AV_CODEC_ID_ALAC` via `libavcodec`).
   - Converts decoded audio frames to standard PCM 16-bit, stereo, 44,100 Hz.
4. **`AudioPlayer.cs`**:
   - Low-latency circular buffer output using Windows `waveOut` or WASAPI.
   - Latency target: ~20-30 ms buffer to prevent stutter while keeping audio and video synchronized.
   - Volume adjustment and mute support.

### 3.2 Display & Cinema Fullscreen Mode

1. **Auto-Orientation Detection**:
   - Detected in `H264Decoder` and `VideoPipeline` when frame dimensions transition between portrait (`w < h`) and landscape (`w > h`).
   - `MainWindow` dynamically adjusts the layout without stretching or distortion.
2. **True Borderless Fullscreen (F11 / Alt+Enter / Double Click)**:
   - Toggles window style between normal bordered mode and borderless fullscreen covering all screen real estate.
   - Toolbar and status bars collapse to maximize video area.
   - Auto-hiding Overlay HUD: Appears on mouse movement near the bottom edge with Fullscreen toggle, Volume slider, Mute, and connection status; fades after 2 seconds of inactivity.
3. **Touch/Pointer Coordinate Re-mapping**:
   - `CoordinateMapper.cs` adapts to fullscreen viewport dimensions so WDA clicks and swipes map accurately to iOS device coordinates.

### 3.3 AirPlay Media Protocol Engine (`src/AirServerLite/AirPlay/`)

1. **Endpoint Handlers in `AirPlaySession.cs`**:
   - `POST /play`: Extracts `Content-Location`, `Start-Position-Seconds`, `uuid`, `clientProcName` (`"YouTube"`). Returns `200 OK`.
   - `GET /playback-info`: Returns XML property list containing `duration`, `position`, `rate`, `readyToPlay: true`, `playbackLikelyToKeepUp: true`.
   - `POST /rate?value=X.X`: Handles Play (1.0) and Pause (0.0).
   - `POST /scrub?position=X.X`: Handles seeking to target position.
   - `POST /stop`: Handles media stop and returns receiver to idle/mirroring state.
2. **Quick Play Direct YouTube Helper**:
   - UI dialog / input on toolbar to paste a YouTube link or video ID to stream directly with native controls.

---

## 4. Error Handling & Edge Cases

- **Audio Buffer Underflow / Overflow:** If network jitter causes packet delays, the audio player drops stale buffers rather than accumulating permanent lag.
- **Orientation Flipping:** Fast orientation changes (e.g. rotating phone repeatedly) trigger clean reallocation of `WriteableBitmap` and SWS scale context without memory leaks.
- **Simultaneous Media & Mirroring:** If an iOS device switches from screen mirroring to an AirPlay media URL, `AirPlaySession` cleanly transitions the active renderer without crashing the RTSP connection.

---

## 5. Verification Plan

1. **Compilation & Build:** Verify clean compilation with `dotnet build AirServerLite.sln -c Debug` and `-c Release`.
2. **Audio Unit Tests:** Test `AudioCipher` decrypting known vectors and `AudioDecoder` parsing sample AAC-ELD frames.
3. **Fullscreen & Orientation Tests:** Verify F11, Escape, Alt+Enter, double-click fullscreen transitions, HUD auto-hide, and letterbox aspect ratios.
4. **AirPlay Protocol Validation:** Verify RTSP/HTTP mock responses for `POST /play`, `GET /playback-info`, `POST /rate`, and `POST /stop`.
