using System.IO;
using System.Runtime.InteropServices;
using AirServerLite.Core;

namespace AirServerLite.AirPlay.Crypto;

public sealed class FairPlayUnavailableException : Exception
{
    public FairPlayUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// FairPlay SAP v3 - the handshake iOS performs at POST /fp-setup before it will send a
/// single byte of video.
///
/// This is the one part of an AirPlay receiver that cannot be written from a protocol
/// description. It is not an algorithm you can derive: it is a fixed set of response blobs
/// and key-derivation tables extracted from Apple firmware. Every open-source receiver
/// (RPiPlay, UxPlay, shairport-sync, playfair) carries the same reference C code.
///
/// Rather than transcribe byte tables - where a single wrong nibble produces a session that
/// dies with no error message - we P/Invoke the reference implementation directly.
/// native/build-playfair.ps1 clones UxPlay and compiles playfair.dll with whichever of GCC or
/// MSVC is installed.
///
/// Two messages make up the exchange; the native shim dispatches on request length:
///   msg1  16 bytes in  -> 142 bytes out
///   msg2 164 bytes in  ->  32 bytes out
/// Afterwards, fp_decrypt unwraps the 72-byte "ekey" from RTSP SETUP into the 16-byte AES key
/// that protects the mirror video stream.
/// </summary>
public sealed class FairPlay : IDisposable
{
    private const string Tag = "fairplay";
    private const string Library = "playfair";

    [DllImport(Library, EntryPoint = "fp_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeCreate();

    [DllImport(Library, EntryPoint = "fp_setup", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeSetup(IntPtr handle, byte[] request, int requestLen,
                                          byte[] response, int responseCapacity, out int responseLen);

    [DllImport(Library, EntryPoint = "fp_decrypt", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeDecrypt(IntPtr handle, byte[] ekey72, byte[] aesKey16);

    [DllImport(Library, EntryPoint = "fp_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeDestroy(IntPtr handle);

    private IntPtr _handle;
    private bool _disposed;

    public static bool IsAvailable { get; private set; }
    public static string? UnavailableReason { get; private set; }

    /// <summary>
    /// Probe once at startup so the UI can tell the user what to install, instead of the
    /// first mirroring attempt dying inside a DllNotFoundException on a background thread.
    /// </summary>
    public static void Probe()
    {
        try
        {
            var h = NativeCreate();
            if (h == IntPtr.Zero)
            {
                IsAvailable = false;
                UnavailableReason = "playfair.dll loaded but fp_create() returned NULL";
            }
            else
            {
                NativeDestroy(h);
                IsAvailable = true;
                UnavailableReason = null;
                Log.Info(Tag, "playfair.dll loaded - FairPlay handshake available");
            }
        }
        catch (DllNotFoundException)
        {
            IsAvailable = false;
            UnavailableReason =
                "playfair.dll not found. Screen mirroring cannot work without it. Build it with: " +
                "powershell -ExecutionPolicy Bypass -File native/build-playfair.ps1";
            Log.Error(Tag, UnavailableReason);
        }
        catch (EntryPointNotFoundException ex)
        {
            IsAvailable = false;
            UnavailableReason = "playfair.dll is missing an expected export: " + ex.Message;
            Log.Error(Tag, UnavailableReason);
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = "Failed to load playfair.dll: " + ex.Message;
            Log.Error(Tag, "FairPlay probe failed", ex);
        }
    }

    public FairPlay()
    {
        if (!IsAvailable)
            throw new FairPlayUnavailableException(UnavailableReason ?? "FairPlay backend unavailable");

        _handle = NativeCreate();
        if (_handle == IntPtr.Zero)
            throw new FairPlayUnavailableException("fp_create() returned NULL");
    }

    /// <summary>Handle one POST /fp-setup message. Returns the response body.</summary>
    public byte[] Setup(byte[] request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = new byte[256];
        var rc = NativeSetup(_handle, request, request.Length, buffer, buffer.Length, out var len);

        if (rc != 0 || len <= 0 || len > buffer.Length)
            throw new InvalidOperationException(
                $"fp_setup failed (rc={rc}, len={len}) for a {request.Length}-byte request");

        Log.Debug(Tag, $"fp-setup {request.Length}B -> {len}B");
        // Full, untruncated hex - needed to reproduce a captured handshake offline against a
        // standalone build of playfair.dll when diagnosing a decrypt that succeeds structurally
        // (rc=0) but yields a key that does not actually decode any real video.
        Log.Debug(Tag, $"fp-setup request full hex: {Convert.ToHexString(request)}");
        Log.Debug(Tag, $"fp-setup response full hex: {Convert.ToHexString(buffer.AsSpan(0, len))}");
        return buffer[..len];
    }

    /// <summary>
    /// Unwrap the FairPlay-encrypted AES key delivered as the "ekey" field of RTSP SETUP.
    /// </summary>
    public byte[] DecryptKey(byte[] ekey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ekey.Length != 72)
            throw new ArgumentException($"ekey must be exactly 72 bytes, got {ekey.Length}", nameof(ekey));

        var aesKey = new byte[16];
        var rc = NativeDecrypt(_handle, ekey, aesKey);
        if (rc != 0)
            throw new InvalidOperationException("fp_decrypt failed with rc=" + rc);

        Log.Debug(Tag, "ekey unwrapped to 16-byte AES key");
        Log.Debug(Tag, $"ekey full hex (72B): {Convert.ToHexString(ekey)}");
        Log.Debug(Tag, $"derived aesKey full hex (16B): {Convert.ToHexString(aesKey)}");
        return aesKey;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { NativeDestroy(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }
}
