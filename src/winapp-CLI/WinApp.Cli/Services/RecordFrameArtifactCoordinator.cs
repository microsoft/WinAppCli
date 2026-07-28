// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

internal sealed class RecordFrameArtifactCoordinator : IAsyncDisposable
{
    private readonly ILogger _logger;
    private IRecordFrameSink? _sink;

    private RecordFrameArtifactCoordinator(
        ILogger logger,
        IRecordFrameSink? sink,
        Exception? failure)
    {
        _logger = logger;
        _sink = sink;
        Failure = failure;
    }

    public Exception? Failure { get; private set; }

    public int SamplesAccepted { get; private set; }

    public int ImageCount => _sink?.ImageCount ?? 0;

    public static RecordFrameArtifactCoordinator Create(RecordFrameBundleConfiguration configuration)
    {
        try
        {
            return new RecordFrameArtifactCoordinator(
                configuration.Logger,
                RecordFrameBundleWriter.s_create(configuration),
                failure: null);
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            configuration.Logger.LogError(ex, "Could not initialize frame artifact output");
            return new RecordFrameArtifactCoordinator(configuration.Logger, sink: null, ex);
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> processedFrame,
        RecordFrameSample sample)
    {
        if (_sink is null)
        {
            return;
        }

        try
        {
            // Once a processed sample is ready, bounded backpressure must not discard it on
            // graceful cancellation. The capture loop observes cancellation before acquiring
            // the next sample.
            await _sink.WriteAsync(processedFrame, sample, CancellationToken.None).ConfigureAwait(false);
            SamplesAccepted++;
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure ??= ex;
            _logger.LogError(ex, "Frame artifact output failed; continuing MP4 recording");
            await AbortAndDisposeAsync("Frame artifact cleanup also failed").ConfigureAwait(false);
        }
    }

    public async Task<RecordFrameArtifactResult?> CompleteAfterVideoFailureAsync(
        RecordFrameCompletion completion)
    {
        if (_sink is null)
        {
            return null;
        }

        if (SamplesAccepted == 0)
        {
            await AbortAndDisposeAsync("Empty frame artifact cleanup failed").ConfigureAwait(false);
            return null;
        }

        try
        {
            var result = await _sink.CompleteAsync(completion).ConfigureAwait(false);
            await DisposeSinkAsync().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            Failure ??= ex;
            _logger.LogError(ex, "Could not preserve partial frame artifacts after MP4 failure");
            await AbortAndDisposeAsync("Partial frame artifact cleanup also failed").ConfigureAwait(false);
            return null;
        }
    }

    public async Task<RecordFrameArtifactResult?> CompleteAfterVideoSuccessAsync(
        RecordFrameCompletion completion)
    {
        if (_sink is null)
        {
            return null;
        }

        try
        {
            var result = await _sink.CompleteAsync(completion).ConfigureAwait(false);
            await DisposeSinkAsync().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure ??= ex;
            _logger.LogError(ex, "Could not finalize frame artifact output");
            await AbortAndDisposeAsync("Frame artifact cleanup also failed").ConfigureAwait(false);
            return null;
        }
    }

    public Task AbortAsync()
        => AbortAndDisposeAsync("Frame artifact cleanup failed");

    public async ValueTask DisposeAsync()
    {
        await DisposeSinkAsync().ConfigureAwait(false);
    }

    private async Task AbortAndDisposeAsync(string cleanupMessage)
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.AbortAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
        {
            Failure = Failure is null ? ex : new AggregateException(Failure, ex);
            _logger.LogDebug(ex, "Frame artifact cleanup failed during {CleanupStage}", cleanupMessage);
        }
        finally
        {
            await DisposeSinkAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DisposeSinkAsync()
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        _sink = null;
        await sink.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsRecoverableFrameOutputFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or ExternalException
            or OperationCanceledException;
}
