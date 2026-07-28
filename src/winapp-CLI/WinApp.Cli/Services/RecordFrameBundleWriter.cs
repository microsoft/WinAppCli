// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal interface IRecordFrameSink : IAsyncDisposable
{
    int SampleCount { get; }
    int ImageCount { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> bgra, RecordFrameSample sample, CancellationToken cancellationToken);
    Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion);
    Task AbortAsync();
}

internal sealed class RecordFrameBundleConfiguration
{
    public required string FinalDirectory { get; init; }
    public required string VideoPath { get; init; }
    public required string RecordingId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required RecordFrameRectManifest ContentRect { get; init; }
    public required RecordFrameRequestManifest Requested { get; init; }
    public required RecordFrameSourceManifest Source { get; init; }
    public required RecordFrameCropManifest Crop { get; init; }
    public required ILogger Logger { get; init; }
}

internal sealed class RecordFrameCompletion
{
    public required string Status { get; init; }
    public required string StopReason { get; init; }
    public required long ElapsedMs { get; init; }
    public required double AchievedFps { get; init; }
    public required double CadenceRatio { get; init; }
    public required string VideoStatus { get; init; }
    public required int VideoFrameCount { get; init; }
    public long? VideoFileSize { get; init; }
}

internal sealed class RecordFrameBundleWriter : IRecordFrameSink
{
    internal const int JpegQuality = 85;
    internal const long FramePipelineMemoryBudgetBytes = 256L * 1024 * 1024;
    private const int ReservedFrameBufferCount = 6;
    private const int MaximumQueuedFrames = 4;

    internal static Func<RecordFrameBundleConfiguration, IRecordFrameSink> s_create =
        configuration => new RecordFrameBundleWriter(configuration);
    internal static Func<byte[], int, int, byte[]> s_encodeJpeg = EncodeJpeg;

    private readonly RecordFrameBundleConfiguration _configuration;
    private readonly string _stagingDirectory;
    private readonly string _framesDirectory;
    private readonly string _indexPath;
    private readonly Channel<QueuedFrame> _channel;
    private readonly StreamWriter _indexWriter;
    private readonly Task _worker;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _sampleCount;
    private int _imageCount;
    private long _imageBytes;
    private bool _indexDisposed;
    private bool _published;
    private bool _aborted;

    public int SampleCount => Volatile.Read(ref _sampleCount);
    public int ImageCount => Volatile.Read(ref _imageCount);

    internal RecordFrameBundleWriter(RecordFrameBundleConfiguration configuration)
    {
        _configuration = configuration;
        var capacity = ComputeQueueCapacity(configuration.Width, configuration.Height);

        if (Path.Exists(configuration.FinalDirectory))
        {
            throw new IOException($"Frame artifact directory already exists: {configuration.FinalDirectory}");
        }

        var parent = Path.GetDirectoryName(configuration.FinalDirectory);
        if (string.IsNullOrEmpty(parent))
        {
            throw new IOException("Frame artifact directory must have a parent directory.");
        }

        Directory.CreateDirectory(parent);
        var leaf = Path.GetFileName(configuration.FinalDirectory);
        if (string.IsNullOrEmpty(leaf) || Path.IsPathRooted(leaf))
        {
            throw new IOException("Frame artifact directory must end with a valid directory name.");
        }
        var stagingName = $".{leaf}.{Guid.NewGuid():N}.staging";
        _stagingDirectory = Path.Join(parent, stagingName);
        _framesDirectory = Path.Join(_stagingDirectory, "frames");
        _indexPath = Path.Join(_stagingDirectory, "frames.ndjson");

        try
        {
            Directory.CreateDirectory(_framesDirectory);
            _indexWriter = new StreamWriter(
                new FileStream(_indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            _channel = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _worker = Task.Run(ProcessFramesAsync);
        }
        catch
        {
            DeleteStagingDirectory();
            throw;
        }
    }

    internal static int ComputeQueueCapacity(int width, int height)
    {
        var frameBytes = checked((long)width * height * 4);
        var capacity = (int)(FramePipelineMemoryBudgetBytes / Math.Max(1, frameBytes))
            - ReservedFrameBufferCount;
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"Frame artifacts at {width}x{height} exceed the 256 MiB pipeline memory budget. Lower --max-edge.");
        }

        return Math.Min(capacity, MaximumQueuedFrames);
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> bgra,
        RecordFrameSample sample,
        CancellationToken cancellationToken)
    {
        var expectedBytes = checked(_configuration.Width * _configuration.Height * 4);
        if (bgra.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"Frame buffer is {bgra.Length} bytes; expected {expectedBytes}.",
                nameof(bgra));
        }

        try
        {
            while (await _channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                // Clone only after capacity is available so backpressure does not retain
                // another full-frame buffer outside the bounded pipeline budget.
                var queuedFrame = new QueuedFrame(bgra.ToArray(), sample);
                if (_channel.Writer.TryWrite(queuedFrame))
                {
                    return;
                }
            }

            await _worker.ConfigureAwait(false);
            throw new ChannelClosedException();
        }
        catch (ChannelClosedException)
        {
            await _worker.ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion)
    {
        if (_published)
        {
            throw new InvalidOperationException("Frame artifact bundle has already been published.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Frame artifact bundle has already been aborted.");
        }

        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        await DisposeIndexWriterAsync().ConfigureAwait(false);

        var indexBytes = new FileInfo(_indexPath).Length;
        var manifest = new RecordFrameBundleManifest
        {
            Status = completion.Status,
            RecordingId = _configuration.RecordingId,
            StartedUtc = _configuration.StartedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            StopReason = completion.StopReason,
            Requested = _configuration.Requested,
            Timing = new RecordFrameTimingManifest
            {
                ElapsedMs = completion.ElapsedMs,
                SampleCount = SampleCount,
                ImageCount = ImageCount,
                RepeatedSampleCount = SampleCount - ImageCount,
                AchievedFps = completion.AchievedFps,
                CadenceRatio = completion.CadenceRatio,
            },
            Video = new RecordFrameVideoManifest
            {
                Path = _configuration.VideoPath,
                Status = completion.VideoStatus,
                FrameCount = completion.VideoFrameCount,
                FileSize = completion.VideoFileSize,
            },
            Frames = new RecordFrameImagesManifest
            {
                Width = _configuration.Width,
                Height = _configuration.Height,
                ContentRect = _configuration.ContentRect,
                TotalBytes = _imageBytes + indexBytes,
            },
            Source = _configuration.Source,
            Crop = _configuration.Crop,
        };

        var manifestPath = Path.Join(_stagingDirectory, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(
            manifest,
            UiJsonContext.Default.RecordFrameBundleManifest);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        await using (var manifestStream = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            useAsync: true))
        {
            await manifestStream.WriteAsync(
                manifestBytes,
                _lifetimeCts.Token).ConfigureAwait(false);
        }

        if (Path.Exists(_configuration.FinalDirectory))
        {
            throw new IOException($"Frame artifact directory appeared before publication: {_configuration.FinalDirectory}");
        }

        var totalBytes = Directory.EnumerateFiles(
            _stagingDirectory,
            "*",
            SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

        var result = new RecordFrameArtifactResult
        {
            Directory = _configuration.FinalDirectory,
            Manifest = Path.Join(_configuration.FinalDirectory, "manifest.json"),
            Index = Path.Join(_configuration.FinalDirectory, "frames.ndjson"),
            Samples = SampleCount,
            Images = ImageCount,
            RepeatedSamples = SampleCount - ImageCount,
            TotalBytes = totalBytes,
        };

        Directory.Move(_stagingDirectory, _configuration.FinalDirectory);
        _published = true;
        return result;
    }

    public async Task AbortAsync()
    {
        if (_published || _aborted)
        {
            return;
        }

        _aborted = true;
        _lifetimeCts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _configuration.Logger.LogDebug(ex, "Frame artifact worker stopped during cleanup");
            }
        }
        finally
        {
            try
            {
                try
                {
                    await DisposeIndexWriterAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                    and not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException)
                {
                    _configuration.Logger.LogDebug(ex, "Could not close the frame artifact index during cleanup");
                }
            }
            finally
            {
                try
                {
                    DeleteStagingDirectory();
                }
                finally
                {
                    _lifetimeCts.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_published && !_aborted)
        {
            await AbortAsync().ConfigureAwait(false);
            return;
        }

        if (_published)
        {
            _lifetimeCts.Dispose();
        }
    }

    private async Task ProcessFramesAsync()
    {
        byte[]? previousPixels = null;
        var currentImageIndex = -1;
        var currentFile = "";
        var currentHash = "";

        try
        {
            await foreach (var queued in _channel.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                var changed = previousPixels is null || !queued.Pixels.AsSpan().SequenceEqual(previousPixels);
                if (changed)
                {
                    currentImageIndex++;
                    var fileName = $"frame-{currentImageIndex:D6}-t{queued.Sample.ElapsedMs:D12}.jpg";
                    var absolutePath = Path.Join(_framesDirectory, fileName);
                    var jpeg = s_encodeJpeg(queued.Pixels, _configuration.Width, _configuration.Height);
                    await using (var imageStream = new FileStream(
                        absolutePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        useAsync: true))
                    {
                        await imageStream.WriteAsync(
                            jpeg,
                            _lifetimeCts.Token).ConfigureAwait(false);
                    }

                    currentFile = $"frames/{fileName}";
                    currentHash = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
                    Interlocked.Add(ref _imageBytes, jpeg.Length);
                    Interlocked.Increment(ref _imageCount);
                    previousPixels = queued.Pixels;
                }

                var entry = new RecordFrameIndexEntry
                {
                    SampleIndex = queued.Sample.SampleIndex,
                    ElapsedMs = queued.Sample.ElapsedMs,
                    MediaTimeMs = queued.Sample.MediaTimeMs,
                    ImageIndex = currentImageIndex,
                    File = currentFile,
                    Changed = changed,
                    Sha256 = currentHash,
                    SourceVersion = queued.Sample.SourceVersion,
                    SourceWidth = queued.Sample.SourceWidth,
                    SourceHeight = queued.Sample.SourceHeight,
                    ContentRect = queued.Sample.ContentRect,
                };
                var line = JsonSerializer.Serialize(
                    entry,
                    RecordFrameIndexJsonContext.Default.RecordFrameIndexEntry);
                await _indexWriter.WriteLineAsync(
                    line.AsMemory(),
                    _lifetimeCts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _sampleCount);
            }

            await _indexWriter.FlushAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            throw;
        }
    }

    private static byte[] EncodeJpeg(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new IOException("SkiaSharp could not encode the frame as JPEG.");
        return data.ToArray();
    }

    private async ValueTask DisposeIndexWriterAsync()
    {
        if (_indexDisposed)
        {
            return;
        }

        _indexDisposed = true;
        await _indexWriter.DisposeAsync().ConfigureAwait(false);
    }

    private void DeleteStagingDirectory()
    {
        if (!Directory.Exists(_stagingDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_stagingDirectory, recursive: true);
        }
        catch (IOException ex)
        {
            _configuration.Logger.LogWarning(
                ex,
                "Could not remove incomplete frame artifact staging directory {StagingDirectory}",
                _stagingDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _configuration.Logger.LogWarning(
                ex,
                "Could not remove incomplete frame artifact staging directory {StagingDirectory}",
                _stagingDirectory);
        }
    }

    private sealed record QueuedFrame(byte[] Pixels, RecordFrameSample Sample);
}

[JsonSerializable(typeof(RecordFrameIndexEntry))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RecordFrameIndexJsonContext : JsonSerializerContext;
