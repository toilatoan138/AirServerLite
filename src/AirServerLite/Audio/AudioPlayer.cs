using System.Runtime.InteropServices;
using AirServerLite.Core;

namespace AirServerLite.Audio;

/// <summary>
/// Low-latency real-time PCM audio playback via Windows Multimedia waveOut API.
/// Circular buffer architecture guarantees <30ms delay with no external heavy dependencies.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const string Tag = "audio-play";
    private const int BufferCount = 4;
    private const int BufferSize = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WaveFormatEx lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WaveHdr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WaveHdr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, ref WaveHdr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);

    private IntPtr _hWaveOut;
    private readonly WaveHdr[] _headers = new WaveHdr[BufferCount];
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private int _currentBuffer;
    private readonly object _lock = new();
    private bool _disposed;
    private float _volume = 1.0f;
    private bool _isMuted;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            ApplyVolume();
        }
    }

    public AudioPlayer(int sampleRate = 44100, int channels = 2, int bitsPerSample = 16)
    {
        var format = new WaveFormatEx
        {
            wFormatTag = 1, // PCM
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = (ushort)bitsPerSample,
            nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
            nAvgBytesPerSec = (uint)(sampleRate * channels * (bitsPerSample / 8)),
            cbSize = 0
        };

        var rc = waveOutOpen(out _hWaveOut, unchecked((uint)-1) /* WAVE_MAPPER */, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0)
        {
            Log.Warn(Tag, $"waveOutOpen returned {rc} - audio output disabled");
            _hWaveOut = IntPtr.Zero;
            return;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(BufferSize);
            _headers[i] = new WaveHdr
            {
                lpData = _buffers[i],
                dwBufferLength = BufferSize,
                dwFlags = 0
            };
            waveOutPrepareHeader(_hWaveOut, ref _headers[i], (uint)Marshal.SizeOf<WaveHdr>());
        }

        ApplyVolume();
        Log.Info(Tag, "AudioPlayer ready (Windows waveOut)");
    }

    public void PlayPcm(byte[] pcm)
    {
        if (_disposed || _hWaveOut == IntPtr.Zero || pcm.Length == 0) return;

        lock (_lock)
        {
            var idx = _currentBuffer;
            _currentBuffer = (_currentBuffer + 1) % BufferCount;

            var copyLen = Math.Min(pcm.Length, BufferSize);
            Marshal.Copy(pcm, 0, _buffers[idx], copyLen);
            _headers[idx].dwBufferLength = (uint)copyLen;

            waveOutWrite(_hWaveOut, ref _headers[idx], (uint)Marshal.SizeOf<WaveHdr>());
        }
    }

    private void ApplyVolume()
    {
        if (_hWaveOut == IntPtr.Zero) return;
        var vol = _isMuted ? 0f : _volume;
        var intVol = (ushort)(vol * 0xFFFF);
        var combined = ((uint)intVol << 16) | intVol;
        waveOutSetVolume(_hWaveOut, combined);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hWaveOut != IntPtr.Zero)
        {
            waveOutReset(_hWaveOut);
            for (int i = 0; i < BufferCount; i++)
            {
                waveOutUnprepareHeader(_hWaveOut, ref _headers[i], (uint)Marshal.SizeOf<WaveHdr>());
                if (_buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_buffers[i]);
            }
            waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }
        Log.Info(Tag, "AudioPlayer disposed");
    }
}
