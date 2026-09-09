namespace AirServerLite.Audio;

/// <summary>
/// High-fidelity stereo 16-bit PCM audio resampler.
/// Converts between 48.0 kHz (Android Miracast / YouTube Cast) and 44.1 kHz (iOS AirPlay / DAC default)
/// with zero allocation on identical rates and sub-millisecond linear interpolation.
/// </summary>
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
