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
    public static FrameGrabber StartGrabber(HWND hwnd, ILogger logger)
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

            return new FrameGrabber(device, context, pool, session, item, logger);
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

        internal FrameGrabber(
            D3D.ID3D11Device device,
            D3D.ID3D11DeviceContext context,
            Direct3D11CaptureFramePool pool,
            GraphicsCaptureSession session,
            GraphicsCaptureItem item,
            ILogger logger)
        {
            _device = device;
            _context = context;
            _pool = pool;
            _session = session;
            _item = item;
            _logger = logger;
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
