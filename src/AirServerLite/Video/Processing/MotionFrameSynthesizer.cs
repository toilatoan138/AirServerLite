using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AirServerLite.Video.Processing;

/// <summary>
/// Motion Estimation &amp; Frame Synthesizer (MEMC).
/// Blends two consecutive frames into an intermediate one so a 60 fps source can be
/// presented at 120 fps, using AVX2 across the CPU's threads.
///
/// The synthesizer owns no buffers. It writes into a destination the caller supplies and
/// keeps nothing between calls - an earlier version handed back its own internal slots,
/// which the pipeline then adopted as its decode target, so the decoder and the UI could
/// end up on the same memory. Taking the destination as a parameter makes that impossible.
/// </summary>
public sealed class MotionFrameSynthesizer
{
    private readonly int _threads;

    public MotionFrameSynthesizer(int threads = 16)
    {
        _threads = Math.Clamp(threads, 1, Environment.ProcessorCount);
    }

    /// <summary>
    /// Fills <paramref name="dst"/> with the midpoint of <paramref name="prev"/> and
    /// <paramref name="next"/>. Returns false - writing nothing - when the two frames do not
    /// describe the same surface, which happens for one frame after a resolution change.
    /// The caller must then present only the real frame.
    /// </summary>
    public unsafe bool Synthesize(BgraFrame prev, BgraFrame next, BgraFrame dst)
    {
        if (prev.Width != next.Width || prev.Height != next.Height ||
            prev.Stride != next.Stride || prev.Pixels.Length != next.Pixels.Length ||
            prev.Pixels.Length == 0)
        {
            return false;
        }

        if (ReferenceEquals(dst, prev) || ReferenceEquals(dst, next))
            return false;

        int totalBytes = prev.Pixels.Length;
        if (dst.Pixels.Length != totalBytes) dst.Pixels = new byte[totalBytes];

        dst.Width = prev.Width;
        dst.Height = prev.Height;
        dst.Stride = prev.Stride;
        dst.Timestamp = prev.Timestamp + ((next.Timestamp - prev.Timestamp) / 2);
        dst.DecodedUtc = DateTime.UtcNow;

        fixed (byte* pPrev = prev.Pixels)
        fixed (byte* pNext = next.Pixels)
        fixed (byte* pDst = dst.Pixels)
        {
            nint prevPtr = (nint)pPrev;
            nint nextPtr = (nint)pNext;
            nint dstPtr = (nint)pDst;

            // Round the slice up, not down: with a floor the last thread inherited every
            // leftover byte, which on a 2K frame is a visible tail of extra work.
            int slices = Math.Max(1, _threads);
            int sliceSize = (totalBytes + slices - 1) / slices;

            Parallel.For(0, slices, t =>
            {
                int start = t * sliceSize;
                if (start >= totalBytes) return;
                int end = Math.Min(start + sliceSize, totalBytes);

                byte* localPrev = (byte*)prevPtr;
                byte* localNext = (byte*)nextPtr;
                byte* localDst = (byte*)dstPtr;

                int i = start;
                int vecSize = Vector256<byte>.Count; // 32 bytes AVX2

                if (Avx2.IsSupported)
                {
                    for (; i + vecSize <= end; i += vecSize)
                    {
                        var v1 = Avx2.LoadVector256(localPrev + i);
                        var v2 = Avx2.LoadVector256(localNext + i);
                        var vAvg = Avx2.Average(v1, v2);
                        Avx2.Store(localDst + i, vAvg);
                    }
                }

                for (; i < end; i++)
                {
                    localDst[i] = (byte)((localPrev[i] + localNext[i] + 1) >> 1);
                }
            });
        }

        return true;
    }
}
