// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace WinApp.Cli.Services;

// Source-generated ([GeneratedComInterface]) COM objects are ComWrappers RCWs, not classic
// RCWs, so Marshal.ReleaseComObject throws for them. The ComObject wrapper implements IDisposable,
// whose Dispose() deterministically releases the underlying IUnknown — that is the AOT-safe way to
// drop a COM reference. This local helper centralizes that pattern.

/// <summary>
/// Encodes a sequence of BGRA (RGB32) frames to an H.264 MP4 file using the
/// Windows Media Foundation <c>IMFSinkWriter</c>. Frames are written
/// incrementally so the full sequence never has to be held in memory.
/// </summary>
/// <remarks>
/// Input frames are treated as top-down RGB32 (BGRA byte order, matching the
/// pixel layout produced by <see cref="WgcCapture"/> and GDI captures). The
/// sink writer inserts the color-conversion + H.264 encoder MFTs automatically.
/// </remarks>
internal sealed unsafe class Mp4SinkWriterEncoder : IDisposable
{
    // MF_VERSION for Windows 7+ (MF_SDK_VERSION 0x0002, MF_API_VERSION 0x0070).
    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_FULL = 0;
    private const uint MFVideoInterlace_Progressive = 2;

    private readonly IMFSinkWriter _writer;
    private readonly uint _streamIndex;
    private readonly uint _frameBytes;
    private readonly string _path;
    private bool _mfStarted;
    private bool _finalized;
    private bool _disposed;

    public int Width { get; }

    public int Height { get; }

    public Mp4SinkWriterEncoder(string path, int width, int height, int fps, uint bitrate)
    {
        Width = width;
        Height = height;
        _path = path;
        _frameBytes = checked((uint)(width * height * 4));

        PInvoke.MFStartup(MF_VERSION, MFSTARTUP_FULL).ThrowOnFailure();
        _mfStarted = true;

        IMFMediaType? outType = null;
        IMFMediaType? inType = null;
        try
        {
            PInvoke.MFCreateSinkWriterFromURL(path, null, null, out _writer).ThrowOnFailure();

            // Output (encoded) media type: H.264.
            PInvoke.MFCreateMediaType(out outType).ThrowOnFailure();
            outType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            outType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
            outType.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, bitrate);
            outType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            outType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackU64((uint)width, (uint)height));
            outType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackU64((uint)fps, 1));
            outType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackU64(1, 1));
            _writer.AddStream(outType, out _streamIndex);

            // Input (uncompressed) media type: RGB32, top-down (positive stride).
            PInvoke.MFCreateMediaType(out inType).ThrowOnFailure();
            inType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            inType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            inType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            inType.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(width * 4));
            inType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackU64((uint)width, (uint)height));
            inType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackU64((uint)fps, 1));
            inType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackU64(1, 1));
            _writer.SetInputMediaType(_streamIndex, inType, null);

            _writer.BeginWriting();
        }
        catch
        {
            // Constructor failed after MFStartup — release any partial writer and undo the
            // MFStartup so we don't leak a global Media Foundation reference, then rethrow.
            ReleaseCom(_writer);
            try
            {
                PInvoke.MFShutdown();
            }
            catch
            {
                // Best-effort shutdown.
            }
            _mfStarted = false;
            throw;
        }
        finally
        {
            // The one-time media-type descriptors are consumed by AddStream/SetInputMediaType;
            // release these ComWrappers RCWs deterministically rather than waiting on the GC.
            ReleaseCom(outType);
            ReleaseCom(inType);
        }
    }

    /// <summary>
    /// Writes a single top-down BGRA frame. <paramref name="bgra"/> must contain
    /// exactly Width*Height*4 bytes.
    /// </summary>
    public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns)
    {
        if (bgra.Length < _frameBytes)
        {
            throw new ArgumentException($"Frame buffer is {bgra.Length} bytes; expected {_frameBytes}.", nameof(bgra));
        }

        PInvoke.MFCreateMemoryBuffer(_frameBytes, out var buffer).ThrowOnFailure();
        try
        {
            buffer.Lock(out var dest, out _, out _);
            try
            {
                bgra[..(int)_frameBytes].CopyTo(new Span<byte>(dest, (int)_frameBytes));
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength(_frameBytes);

            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(sampleTimeHns);
                sample.SetSampleDuration(sampleDurationHns);
                _writer.WriteSample(_streamIndex, sample);
            }
            finally
            {
                // Release the per-frame sample so its native MF buffers don't accumulate
                // across thousands of frames.
                ReleaseCom(sample);
            }
        }
        finally
        {
            ReleaseCom(buffer);
        }
    }

    /// <summary>Finalizes the MP4 container. Safe to call once; a no-op afterwards.</summary>
    public void Complete()
    {
        if (_finalized)
        {
            return;
        }
        _writer.Finalize();
        _finalized = true;
    }

    private static ulong PackU64(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>Deterministically releases a source-generated COM RCW (ComWrappers-based).</summary>
    private static void ReleaseCom(object? comObject)
    {
        if (comObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // If the encoder is being torn down without a successful Complete() (e.g. an exception
        // during capture), the on-disk .mp4 is truncated/unfinalized. Release the writer and remove
        // the partial file so a corrupt recording is never left behind as if it succeeded.
        var leftPartial = !_finalized;
        ReleaseCom(_writer);
        if (leftPartial)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch
            {
                // Best-effort cleanup of the partial file.
            }
        }

        if (_mfStarted)
        {
            try
            {
                PInvoke.MFShutdown();
            }
            catch
            {
                // Best-effort shutdown.
            }
            _mfStarted = false;
        }
    }
}
