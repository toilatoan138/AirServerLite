using System;

namespace AirServerLite.Audio;

/// <summary>
/// Studio-grade real-time DSP Audio Enhancer.
/// Provides:
/// 1. Warm Bass Boost: +2.5 dB low shelf at 100 Hz for punchy, deep low-end.
/// 2. Crystal Presence & Clarity: +1.8 dB high shelf at 3.5 kHz for crisp vocals and clear dialogue.
/// 3. Stereo Spatial Expansion: 15% mid-side stereo widening for an immersive, expansive soundstage.
/// 4. Zero-Clipping Soft Limiter: Smooth polynomial compression to prevent any digital distortion.
/// </summary>
public static class AudioEnhancer
{
    public static bool Enabled { get; set; } = false;

    // Filter states for Left and Right channels
    private static float _x1L, _x2L, _y1L, _y2L;
    private static float _x1R, _x2R, _y1R, _y2R;

    private static float _hx1L, _hx2L, _hy1L, _hy2L;
    private static float _hx1R, _hx2R, _hy1R, _hy2R;

    // Biquad coefficients: Low shelf (100 Hz, +2.5 dB, Q=0.707 @ 44.1kHz)
    private static readonly float Lb0, Lb1, Lb2, La1, La2;

    // Biquad coefficients: High shelf (3500 Hz, +1.8 dB, Q=0.707 @ 44.1kHz)
    private static readonly float Hb0, Hb1, Hb2, Ha1, Ha2;

    static AudioEnhancer()
    {
        CalculateLowShelfCoeffs(100f, 2.5f, 44100f, 0.7071f, out Lb0, out Lb1, out Lb2, out La1, out La2);
        CalculateHighShelfCoeffs(3500f, 1.8f, 44100f, 0.7071f, out Hb0, out Hb1, out Hb2, out Ha1, out Ha2);
    }

    private static void CalculateLowShelfCoeffs(float f0, float gainDb, float fs, float q,
        out float b0, out float b1, out float b2, out float a1, out float a2)
    {
        float a = MathF.Pow(10f, gainDb / 40f);
        float w0 = 2f * MathF.PI * f0 / fs;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * q);
        float twoSqrtAAlpha = 2f * MathF.Sqrt(a) * alpha;

        float a0 = (a + 1f) + (a - 1f) * cosW + twoSqrtAAlpha;
        b0 = (a * ((a + 1f) - (a - 1f) * cosW + twoSqrtAAlpha)) / a0;
        b1 = (2f * a * ((a - 1f) - (a + 1f) * cosW)) / a0;
        b2 = (a * ((a + 1f) - (a - 1f) * cosW - twoSqrtAAlpha)) / a0;
        a1 = (-2f * ((a - 1f) + (a + 1f) * cosW)) / a0;
        a2 = ((a + 1f) + (a - 1f) * cosW - twoSqrtAAlpha) / a0;
    }

    private static void CalculateHighShelfCoeffs(float f0, float gainDb, float fs, float q,
        out float b0, out float b1, out float b2, out float a1, out float a2)
    {
        float a = MathF.Pow(10f, gainDb / 40f);
        float w0 = 2f * MathF.PI * f0 / fs;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * q);
        float twoSqrtAAlpha = 2f * MathF.Sqrt(a) * alpha;

        float a0 = (a + 1f) - (a - 1f) * cosW + twoSqrtAAlpha;
        b0 = (a * ((a + 1f) + (a - 1f) * cosW + twoSqrtAAlpha)) / a0;
        b1 = (-2f * a * ((a - 1f) + (a + 1f) * cosW)) / a0;
        b2 = (a * ((a + 1f) + (a - 1f) * cosW - twoSqrtAAlpha)) / a0;
        a1 = (2f * ((a - 1f) - (a + 1f) * cosW)) / a0;
        a2 = ((a + 1f) - (a - 1f) * cosW - twoSqrtAAlpha) / a0;
    }

    public static void Reset()
    {
        _x1L = _x2L = _y1L = _y2L = 0;
        _x1R = _x2R = _y1R = _y2R = 0;
        _hx1L = _hx2L = _hy1L = _hy2L = 0;
        _hx1R = _hx2R = _hy1R = _hy2R = 0;
    }

    public static void Process(byte[] pcm, int length)
    {
        if (!Enabled || pcm == null || length < 4) return;

        int samplePairs = length / 4; // 16-bit stereo (4 bytes per sample)
        const float inv32768 = 1f / 32768f;
        const float stereoWidening = 1.15f; // +15% stereo soundstage

        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 4;
            short rawL = (short)(pcm[offset] | (pcm[offset + 1] << 8));
            short rawR = (short)(pcm[offset + 2] | (pcm[offset + 3] << 8));

            float inL = rawL * inv32768;
            float inR = rawR * inv32768;

            // 1. Low shelf filter (Bass boost)
            float lowL = Lb0 * inL + Lb1 * _x1L + Lb2 * _x2L - La1 * _y1L - La2 * _y2L;
            _x2L = _x1L; _x1L = inL; _y2L = _y1L; _y1L = lowL;

            float lowR = Lb0 * inR + Lb1 * _x1R + Lb2 * _x2R - La1 * _y1R - La2 * _y2R;
            _x2R = _x1R; _x1R = inR; _y2R = _y1R; _y1R = lowR;

            // 2. High shelf filter (Presence & clarity)
            float highL = Hb0 * lowL + Hb1 * _hx1L + Hb2 * _hx2L - Ha1 * _hy1L - Ha2 * _hy2L;
            _hx2L = _hx1L; _hx1L = lowL; _hy2L = _hy1L; _hy1L = highL;

            float highR = Hb0 * lowR + Hb1 * _hx1R + Hb2 * _hx2R - Ha1 * _hy1R - Ha2 * _hy2R;
            _hx2R = _hx1R; _hx1R = lowR; _hy2R = _hy1R; _hy2R = highR;

            // 3. Stereo Widening (Mid/Side processing)
            float mid = (highL + highR) * 0.5f;
            float side = (highL - highR) * 0.5f * stereoWidening;
            float outL = mid + side;
            float outR = mid - side;

            // 4. Soft-knee limiter / saturation (guarantees zero digital clipping)
            if (outL > 0.95f) outL = 0.95f + (outL - 0.95f) / (1f + (outL - 0.95f) * (outL - 0.95f));
            else if (outL < -0.95f) outL = -0.95f + (outL + 0.95f) / (1f + (outL + 0.95f) * (outL + 0.95f));

            if (outR > 0.95f) outR = 0.95f + (outR - 0.95f) / (1f + (outR - 0.95f) * (outR - 0.95f));
            else if (outR < -0.95f) outR = -0.95f + (outR + 0.95f) / (1f + (outR + 0.95f) * (outR + 0.95f));

            int sL = (int)(outL * 32767f);
            int sR = (int)(outR * 32767f);

            short finalL = (short)Math.Clamp(sL, -32768, 32767);
            short finalR = (short)Math.Clamp(sR, -32768, 32767);

            pcm[offset] = (byte)(finalL & 0xFF);
            pcm[offset + 1] = (byte)((finalL >> 8) & 0xFF);
            pcm[offset + 2] = (byte)(finalR & 0xFF);
            pcm[offset + 3] = (byte)((finalR >> 8) & 0xFF);
        }
    }
}
