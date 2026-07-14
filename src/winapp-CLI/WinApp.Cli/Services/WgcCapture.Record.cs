// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using D3D = Windows.Win32.Graphics.Direct3D11;
using D3DCommon = Windows.Win32.Graphics.Direct3D;

namespace WinApp.Cli.Services;

internal static partial class WgcCapture
{
    /// <summary>
    /// Opens a persistent Windows Graphics Capture session for a window and keeps
    /// the most recently arrived frame available on demand. Used by the recorder to
    /// sample frames at a fixed cadence without re-initializing D3D per frame.
    /// </summary>
    public static FrameGrabber StartGrabber(HWND hwnd, ILogger logger, int fps = 0)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        PInvoke.D3D11CreateDevice(
            pAdapter: null,
            D3DCommon.D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            Software: default,
            D3D.D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            pFeatureLevels: default,
            SDKVersion: D3D11_SDK_VERSION,
            out var device,
            out _,
            out var context).ThrowOnFailure();

        try
        {
            var winrtDevice = CreateDirect3DDevice(device);
            var item = CreateItemForWindow(hwnd);
            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                item.Size);
            var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            return new FrameGrabber(device, context, pool, session, item, logger, fps);
        }
        catch
        {
            (context as IDisposable)?.Dispose();
            (device as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Holds a live capture session and caches the latest CPU-readable frame. Thread-safe:
    /// frames arrive on WGC's free-threaded pool while the recorder samples via
    /// <see cref="TryGetLatest"/>.
    /// </summary>
    internal sealed class FrameGrabber : IDisposable
    {
        private readonly D3D.ID3D11Device _device;
        private readonly D3D.ID3D11DeviceContext _context;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly GraphicsCaptureItem _item;
        private readonly ILogger _logger;
        private readonly Lock _lock = new();
        private byte[]? _latestPixels;
        private int _latestWidth;
        private int _latestHeight;
        private bool _disposed;

        // Throttle: track when we last did the expensive GPU→CPU copy so arrivals faster than
        // the target FPS are discarded without copying. A residual TOCTOU race means at most
        // ~2 copies can occur in the same sampling interval (two threads both pass the check
        // before either updates _lastSampleMs), but this is harmless — the second copy simply
        // overwrites the cached frame with an identical (or slightly newer) one.
        private long _lastSampleMs;
        private readonly int _minIntervalMs; // 0 = no throttle

        internal FrameGrabber(
            D3D.ID3D11Device device,
            D3D.ID3D11DeviceContext context,
            Direct3D11CaptureFramePool pool,
            GraphicsCaptureSession session,
            GraphicsCaptureItem item,
            ILogger logger,
            int fps = 0)
        {
            _device = device;
            _context = context;
            _pool = pool;
            _session = session;
            _item = item;
            _logger = logger;
            // fps == 0 disables throttling (used when fps is unknown or caller handles timing).
            _minIntervalMs = fps > 0 ? 1000 / fps : 0;
            _lastSampleMs = 0; // treat as "very old" so the first frame is always captured
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            Direct3D11CaptureFrame? frame = null;
            try
            {
                frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                // Throttle BEFORE the expensive GPU→CPU readback: skip arrivals that are faster
                // than the target sampling interval. At 4K this can prevent gigabytes/sec of
                // unnecessary memory allocation and copy when the display refresh rate exceeds fps.
                if (_minIntervalMs > 0)
                {
                    var nowMs = Environment.TickCount64;
                    if (nowMs - Interlocked.Read(ref _lastSampleMs) < _minIntervalMs)
                    {
                        return; // too soon — discard without copying
                    }
                    Interlocked.Exchange(ref _lastSampleMs, nowMs);
                }

                var (pixels, width, height) = CopyFrame(_device, _context, frame);
                lock (_lock)
                {
                    _latestPixels = pixels;
                    _latestWidth = width;
                    _latestHeight = height;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WGC frame copy failed during recording");
            }
            finally
            {
                frame?.Dispose();
            }
        }

        /// <summary>Returns the most recently captured frame, or <see langword="null"/> if none has arrived yet.</summary>
        public (byte[] Pixels, int Width, int Height)? TryGetLatest()
        {
            lock (_lock)
            {
                if (_latestPixels is null)
                {
                    return null;
                }
                return (_latestPixels, _latestWidth, _latestHeight);
            }
        }

        /// <summary>Waits (up to <paramref name="timeout"/>) for the first frame to arrive.</summary>
        public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (TryGetLatest() is not null)
                {
                    return true;
                }
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
            return TryGetLatest() is not null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _pool.FrameArrived -= OnFrameArrived;
            _session.Dispose();
            _pool.Dispose();
            (_context as IDisposable)?.Dispose();
            (_device as IDisposable)?.Dispose();
        }
    }
}
