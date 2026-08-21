// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// The guest half of the command channel: receives host operations and runs them
/// (spec §"Guest winapp agent mode").
/// </summary>
/// <remarks>
/// This is the mirror of <c>GuestCommandChannel</c> and, like it, depends only on
/// <see cref="IGuestTransport"/>. Both halves can therefore be run against each other over an
/// in-memory transport, which is what makes the whole protocol — dispatch, streaming, cancellation,
/// fencing, failure envelopes — testable without a Sandbox.
/// <para>
/// The agent implements no application semantics. Every operation becomes an ordinary guest winapp
/// child process, which is precisely what keeps guest behaviour identical to local behaviour instead
/// of a second implementation that drifts.
/// </para>
/// </remarks>
internal sealed class GuestCommandServer : IAsyncDisposable
{
    private readonly IGuestTransport _transport;
    private readonly string _targetEpoch;
    private readonly IGuestProcessHostFactory _processes;
    private readonly IGuestSessionProbe _sessionProbe;
    private readonly GuestAgentIdentity _identity;
    private readonly GuestFileService? _files;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RunningOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, GuestFileWrite> _writes = new();
    private bool _disposed;

    /// <summary>Creates a server bound to one connection and one target generation.</summary>
    public GuestCommandServer(
        IGuestTransport transport,
        ExecutionTargetEpoch targetEpoch,
        IGuestProcessHostFactory processes,
        IGuestSessionProbe sessionProbe,
        GuestAgentIdentity identity,
        GuestFileService? files = null)
    {
        _transport = transport;
        _targetEpoch = targetEpoch.Value;
        _processes = processes;
        _sessionProbe = sessionProbe;
        _identity = identity;
        _files = files;
    }

    /// <summary>How long a cancelled child gets to exit before its job is terminated.</summary>
    public TimeSpan GracefulStopTimeout { get; init; } = GuestProcessHost.DefaultGracefulStopTimeout;

    /// <summary>
    /// Serves operations until the host disconnects or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                await DispatchAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            await StopAllOperationsAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllOperationsAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (!GuestPayloadCodec.TryGetKind(frame.Span, out var kind))
        {
            return;
        }

        if (kind == GuestPayloadKind.Stream)
        {
            await DispatchStreamAsync(frame, cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = GuestPayloadCodec.TryDecodeJson(frame.Span);
        if (message?.OperationId is null || !Guid.TryParse(message.OperationId, out var operationId))
        {
            return;
        }

        // Fence every request on the generation the host believes it is talking to. A request built
        // against a previous Sandbox must not be applied to this one, whatever it asks for.
        if (!IsCurrentEpoch(message.TargetEpoch))
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.OperationFailed,
                    OperationId = message.OperationId,
                    TargetEpoch = _targetEpoch,
                    Error = new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.TargetStale,
                        Message = "The request was built for a different Windows Sandbox generation.",
                        UserAction = "Retry the command so it targets the current Sandbox.",
                    },
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (message.Type)
        {
            case GuestMessageTypes.CapabilitiesRequest:
                await SendCapabilitiesAsync(message.OperationId, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.ExecRequest when message.Exec is { } exec:
                StartOperation(operationId, exec);
                break;

            case GuestMessageTypes.StdinClosed:
                if (_operations.TryGetValue(operationId, out var forClose))
                {
                    forClose.Host.CloseStandardInput();
                }
                else if (_writes.TryRemove(operationId, out var completedWrite))
                {
                    // End of a file transfer: verify and publish, or report exactly how far it got.
                    await CompleteWriteAsync(operationId, completedWrite, cancellationToken).ConfigureAwait(false);
                }

                break;

            case GuestMessageTypes.CancelRequest:
                if (_operations.TryGetValue(operationId, out var forCancel))
                {
                    await forCancel.CancelAsync().ConfigureAwait(false);
                }

                break;

            case GuestMessageTypes.ListFilesRequest when message.Scope is { } listScope:
                await HandleListAsync(operationId, listScope, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.PutFileRequest when message.Scope is { } putScope && message.File is { } file:
                BeginWrite(operationId, putScope, file);
                break;

            case GuestMessageTypes.GetFileRequest when message.Scope is { } getScope && message.Paths is [var path]:
                await HandleGetAsync(operationId, getScope, path, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.DeleteFilesRequest when message.Scope is { } deleteScope && message.Paths is { } paths:
                await HandleDeleteAsync(operationId, deleteScope, paths, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.RemoveScopeRequest when message.Scope is { } removeScope:
                await HandleRemoveScopeAsync(operationId, removeScope, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // An unknown or malformed message is ignored rather than fatal: the host is
                // authenticated, so this is a version skew, and one unusable message must not take
                // down operations that are working.
                break;
        }
    }

    private async Task DispatchStreamAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (!GuestPayloadCodec.TryDecodeStream(frame, out var operationId, out var stream, out var data))
        {
            return;
        }

        if (stream != GuestStreamId.StandardInput)
        {
            // Output only ever flows guest to host; a host sending it is ignored.
            return;
        }

        if (_writes.TryGetValue(operationId, out var write))
        {
            try
            {
                await write.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex)
            {
                _writes.TryRemove(operationId, out _);

                // Same ordering as the completion path: discard the partial file before reporting,
                // so the failure the host observes is already true on disk.
                await write.DisposeAsync().ConfigureAwait(false);
                await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
            }

            return;
        }

        if (_operations.TryGetValue(operationId, out var operation))
        {
            await operation.Host.WriteStandardInputAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsCurrentEpoch(string? epoch) =>
        string.IsNullOrEmpty(epoch) || string.Equals(epoch, _targetEpoch, StringComparison.Ordinal);

    private async Task SendCapabilitiesAsync(string operationId, CancellationToken cancellationToken)
    {
        var session = _sessionProbe.Probe();
        var readiness = GuestAgentReadiness.Evaluate(session);

        await SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.CapabilitiesResponse,
                OperationId = operationId,
                TargetEpoch = _targetEpoch,
                Capabilities = new ExecutionTargetCapabilities
                {
                    Architecture = _identity.Architecture,

                    // Capability, not readiness: the guest can do these in principle. Whether input
                    // can be delivered right now is re-verified immediately before each
                    // foreground-sensitive command, because the client can be closed at any moment.
                    SupportsInteractiveDesktop = GuestAgentReadiness.SupportsReadOnlyAutomation(session),
                    SupportsRealInput = readiness == GuestReadinessFailure.None,
                    SupportsScreenCapture = readiness == GuestReadinessFailure.None,
                    CooperativeUiTurnsVersion = GuestOwnerContext.CooperativeUiTurnsVersion,
                    SupportsInternalSystemSetup = true,

                    // Windows Sandbox discards everything on teardown, so deployments and runtimes
                    // must be reconciled after every new epoch.
                    PersistentStorage = false,
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the actual contents of a managed guest location.</summary>
    private async Task HandleListAsync(Guid operationId, GuestPathScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var files = await RequireFiles().ListAsync(scope, cancellationToken).ConfigureAwait(false);

            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ListFilesResponse,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Files = files,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a destination for an incoming file; content follows as stream frames.</summary>
    private void BeginWrite(Guid operationId, GuestPathScope scope, GuestFileInfo file)
    {
        try
        {
            _writes[operationId] = RequireFiles().BeginWrite(scope, file);
        }
        catch (ExecutionTargetException ex)
        {
            _ = SendFailureAsync(operationId, ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = SendFailureAsync(operationId, FileFailure(ex));
        }
    }

    /// <summary>Verifies and publishes a completed transfer.</summary>
    /// <remarks>
    /// Cleanup happens before the outcome is reported, not in a trailing <c>finally</c>. Reporting
    /// first would let a host that retries immediately race the temporary file it was told did not
    /// survive — and would make "no partial file is left behind" true only eventually.
    /// </remarks>
    private async Task CompleteWriteAsync(Guid operationId, GuestFileWrite write, CancellationToken cancellationToken)
    {
        ExecutionTargetErrorInfo? failure = null;

        try
        {
            await write.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            failure = ex.Error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = FileFailure(ex);
        }

        await write.DisposeAsync().ConfigureAwait(false);

        if (failure is null)
        {
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendFailureAsync(operationId, failure).ConfigureAwait(false);
    }

    /// <summary>Streams one managed guest file back to the host.</summary>
    private async Task HandleGetAsync(
        Guid operationId,
        GuestPathScope scope,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = RequireFiles().OpenRead(scope, relativePath);
            var buffer = new byte[GuestPayloadCodec.MaxStreamChunkSize];

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var payload = GuestPayloadCodec.EncodeStream(
                    operationId,
                    GuestStreamId.StandardOutput,
                    buffer.AsSpan(0, read));

                await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Removes paths a reconciliation determined should no longer exist.</summary>
    private async Task HandleDeleteAsync(
        Guid operationId,
        GuestPathScope scope,
        List<string> relativePaths,
        CancellationToken cancellationToken)
    {
        try
        {
            RequireFiles().Delete(scope, relativePaths);
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Discards an entire managed scope, for an explicit clean reinstall.</summary>
    private async Task HandleRemoveScopeAsync(
        Guid operationId,
        GuestPathScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            RequireFiles().RemoveScope(scope);
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    private Task SendFileCompletedAsync(Guid operationId, CancellationToken cancellationToken) =>        SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.FileCompleted,
                OperationId = operationId.ToString(),
                TargetEpoch = _targetEpoch,
            },
            cancellationToken);

    /// <summary>The file service, or a clear failure when this agent was built without one.</summary>
    private GuestFileService RequireFiles() =>
        _files ?? throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "This guest agent was not configured with managed storage.",
            userAction: "Retry the command.");

    private static ExecutionTargetErrorInfo FileFailure(Exception ex) => new()
    {
        Code = ExecutionTargetErrorCodes.TransferInterrupted,
        Message = $"A guest file operation failed: {ex.Message}",
        UserAction = "Retry the command.",
    };

    private void StartOperation(Guid operationId, GuestExecRequest request)
    {
        RunningOperation operation;

        try
        {
            var host = _processes.Start(
                request,
                (stream, data) => _ = ForwardOutputAsync(operationId, stream, data));

            operation = new RunningOperation(host, GracefulStopTimeout);
        }
        catch (ExecutionTargetException ex)
        {
            _ = SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.OperationFailed,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Error = ex.Error,
                },
                CancellationToken.None);
            return;
        }

        _operations[operationId] = operation;
        operation.Completion = Task.Run(() => RunOperationAsync(operationId, operation));
    }

    private async Task RunOperationAsync(Guid operationId, RunningOperation operation)
    {
        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ExecStarted,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    ProcessId = operation.Host.ProcessId,
                    ProcessStartTicksUtc = operation.Host.StartTicksUtc,
                },
                CancellationToken.None).ConfigureAwait(false);

            var exitCode = await operation.Host.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ExecCompleted,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    ExitCode = exitCode,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.TransportFailed,
                    Message = "The guest lost track of a running process.",
                    UserAction = "Retry the command.",
                }).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
            await operation.Host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendFailureAsync(Guid operationId, ExecutionTargetErrorInfo error)
    {
        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.OperationFailed,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Error = error,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // The connection is already gone; the host will observe the closed channel instead.
        }
    }

    /// <summary>Forwards one output chunk, splitting it to fit the frame limit.</summary>
    private async Task ForwardOutputAsync(Guid operationId, GuestStreamId stream, ReadOnlyMemory<byte> data)
    {
        var remaining = data;

        try
        {
            while (!remaining.IsEmpty)
            {
                var take = Math.Min(remaining.Length, GuestPayloadCodec.MaxStreamChunkSize);
                var payload = GuestPayloadCodec.EncodeStream(operationId, stream, remaining.Span[..take]);
                await SendRawAsync(payload, CancellationToken.None).ConfigureAwait(false);
                remaining = remaining[take..];
            }
        }
        catch (ExecutionTargetException)
        {
            // The host went away mid-stream. The operation's own completion path reports it.
        }
        catch (ObjectDisposedException)
        {
            // The server shut down while output was still draining.
        }
    }

    private Task SendAsync(GuestMessage message, CancellationToken cancellationToken) =>
        SendRawAsync(GuestPayloadCodec.EncodeJson(message), cancellationToken);

    private async Task SendRawAsync(byte[] payload, CancellationToken cancellationToken)
    {
        // One lock over the whole send keeps frames — and therefore the sequence numbers the AEAD
        // nonce is derived from — strictly ordered even with several operations streaming at once.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.SendFrameAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Stops every running operation, so nothing outlives the connection that asked for it.</summary>
    private async Task StopAllOperationsAsync()
    {
        foreach (var (id, operation) in _operations.ToArray())
        {
            _operations.TryRemove(id, out _);

            try
            {
                await operation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The process already died with the connection.
            }
        }

        // An unfinished transfer is discarded rather than published. A partially received file that
        // survived would be indistinguishable from a legitimate one on the next hash comparison.
        foreach (var (id, write) in _writes.ToArray())
        {
            _writes.TryRemove(id, out _);
            await write.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One in-flight operation and its child process.</summary>
    private sealed class RunningOperation(IGuestProcessHost host, TimeSpan gracefulTimeout)
    {
        /// <summary>The child process running this operation.</summary>
        public IGuestProcessHost Host { get; } = host;

        /// <summary>Task that completes when the operation has fully reported its outcome.</summary>
        public Task? Completion { get; set; }

        /// <summary>
        /// Requests graceful termination, then terminates the process tree after the timeout.
        /// </summary>
        public Task<int> CancelAsync() => Host.StopAsync(gracefulTimeout, CancellationToken.None);
    }
}
