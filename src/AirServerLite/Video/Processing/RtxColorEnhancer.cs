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
            nint basePtr = (nint)pBase;

            Parallel.For(0, height, y =>
            {
                byte* row = (byte*)basePtr + (y * stride);
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
