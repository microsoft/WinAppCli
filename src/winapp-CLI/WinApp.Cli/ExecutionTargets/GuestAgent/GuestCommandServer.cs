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
    private readonly string? _guestWinapp;
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
        GuestFileService? files = null,
        string? guestWinapp = null)
    {
        _transport = transport;
        _targetEpoch = targetEpoch.Value;
        _processes = processes;
        _sessionProbe = sessionProbe;
        _identity = identity;
        _files = files;
        _guestWinapp = guestWinapp;
    }

    /// <summary>How long a cancelled child gets to exit before its job is terminated.</summary>
    public TimeSpan GracefulStopTimeout { get; init; } = GuestProcessHost.DefaultGracefulStopTimeout;

    /// <summary>Most operations and transfers one connection may have in flight at once.</summary>
    /// <remarks>
    /// A bound, not a queue: an admitted connection that asks for more gets an immediate, actionable
    /// refusal rather than an unbounded set of child processes and open file handles. It is per
    /// connection because operation identity is per connection, so one host's traffic cannot consume
    /// another's budget.
    /// </remarks>
    public int MaxConcurrentOperations { get; init; } = DefaultMaxConcurrentOperations;

    /// <summary>Default value of <see cref="MaxConcurrentOperations"/>.</summary>
    internal const int DefaultMaxConcurrentOperations = 32;

    /// <summary>
    /// How long shutdown waits for operations to finish reporting before closing the connection.
    /// </summary>
    /// <remarks>
    /// Generous enough that an operation stopping normally always completes within it, and finite so
    /// that one whose final frames are blocked on an unresponsive peer cannot hold the agent open.
    /// </remarks>
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When set, every request on this connection is answered with this error instead of served.
    /// </summary>
    /// <remarks>
    /// This is how the agent refuses a connection past its channel bound. Refusing here rather than
    /// by dropping the socket is deliberate: the refusal is only produced after the secure handshake
    /// has authenticated the peer, and it reaches the host as an ordinary failure envelope with a
    /// user action, instead of as a connection reset the host can only report as a transport error.
    /// </remarks>
    public ExecutionTargetErrorInfo? AdmissionRefusal { get; init; }

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

        // Refused connections never reach the dispatch table. The check sits after the epoch fence
        // so a stale request still gets the more specific answer, and before every handler so a
        // refused peer cannot start a process, open a file, or learn capabilities.
        if (AdmissionRefusal is { } refusal)
        {
            await SendFailureAsync(operationId, refusal).ConfigureAwait(false);
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
                else if (_writes.TryRemove(operationId, out var abandonedWrite))
                {
                    // A cancelled upload must release its handle and discard the partial file here.
                    // Leaving it would keep the destination locked, so an immediate retry of the
                    // same transfer would fail on a file the caller believes was abandoned.
                    await abandonedWrite.DisposeAsync().ConfigureAwait(false);
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

                    // Reported rather than assumed by the host, so the guest layout stays the
                    // guest's business and target-neutral orchestration never encodes it.
                    ManagedRoot = _files?.ManagedRoot,
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
        if (IsAtOperationLimit())
        {
            _ = SendFailureAsync(operationId, OperationLimitReached());
            return;
        }

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

    /// <summary>Whether this connection already owns as much in-flight work as it may.</summary>
    private bool IsAtOperationLimit() => _operations.Count + _writes.Count >= MaxConcurrentOperations;

    private ExecutionTargetErrorInfo OperationLimitReached() => new()
    {
        Code = ExecutionTargetErrorCodes.AgentBusy,
        Message =
            $"This connection to the Windows Sandbox agent already has {MaxConcurrentOperations} operations in flight.",
        UserAction = "Wait for one of this command's operations to finish, then retry.",
    };

    private void StartOperation(Guid operationId, GuestExecRequest request)
    {
        RunningOperation operation;

        if (IsAtOperationLimit())
        {
            _ = SendFailureAsync(operationId, OperationLimitReached());
            return;
        }

        // Readiness is re-verified here, immediately before the process starts, rather than taken
        // from the capability handshake. The user can close the Sandbox window at any moment, which
        // silently removes real input and screen capture while leaving UI Automation working -- so
        // a command that would inject input must be refused now, not admitted on the strength of a
        // check that was true when the channel opened.
        if (request.RequiresRealInput)
        {
            var readiness = GuestAgentReadiness.Evaluate(_sessionProbe.Probe());

            if (readiness != GuestReadinessFailure.None)
            {
                _ = SendFailureAsync(operationId, GuestAgentReadiness.Describe(readiness));
                return;
            }
        }

        if (!TryResolveExecutable(request, out var resolved, out var resolutionError))
        {
            _ = SendFailureAsync(operationId, resolutionError);
            return;
        }

        try
        {
            var outputSends = new ConcurrentQueue<Task>();
            var host = _processes.Start(
                resolved,
                (stream, data) => outputSends.Enqueue(ForwardOutputAsync(operationId, stream, data)));

            operation = new RunningOperation(host, outputSends, GracefulStopTimeout, request.Detach);
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
        _ = Task.Run(() => RunOperationAsync(operationId, operation));
    }

    /// <summary>
    /// Decides which binary an operation actually starts.
    /// </summary>
    /// <remarks>
    /// The guest's own winapp is named by a flag rather than a path, so a host cannot point "guest
    /// winapp" at some other binary and have the agent run it with the agent's own privileges and
    /// forwarded owner context. An empty executable is refused outright rather than handed to
    /// process creation to reject with an OS error the caller cannot act on.
    /// </remarks>
    private bool TryResolveExecutable(
        GuestExecRequest request,
        out GuestExecRequest resolved,
        out ExecutionTargetErrorInfo error)
    {
        error = null!;

        if (request.UseGuestWinapp)
        {
            if (string.IsNullOrWhiteSpace(_guestWinapp))
            {
                resolved = request;
                error = new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.AgentIncompatible,
                    Message = "The guest agent cannot locate its own winapp binary.",
                    UserAction = "Restart Windows Sandbox so winapp reinstalls the guest agent.",
                };
                return false;
            }

            resolved = new GuestExecRequest
            {
                Executable = _guestWinapp,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                Environment = request.Environment,
                RequiresRealInput = request.RequiresRealInput,
            };
            return true;
        }

        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            resolved = request;
            error = new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.TargetAmbiguous,
                Message = "The request did not name an executable to run inside the guest.",
                UserAction = "Put the executable and its arguments after '--'.",
            };
            return false;
        }

        resolved = request;
        return true;
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

            if (operation.Detach)
            {
                await SendAsync(
                    new GuestMessage
                    {
                        Type = GuestMessageTypes.ExecCompleted,
                        OperationId = operationId.ToString(),
                        TargetEpoch = _targetEpoch,
                        ExitCode = 0,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }

            var exitCode = await operation.Host.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            // GuestProcessHost has drained the child pipes at this point, so no more tasks can be
            // enqueued. Wait for every encoded frame to cross the transport before completion makes
            // the host remove the operation; otherwise a fast process loses its trailing output.
            await Task.WhenAll(operation.OutputSends.ToArray()).ConfigureAwait(false);

            if (!operation.Detach)
            {
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
        catch (Exception ex)
        {
            // Total by construction. Nothing awaits this task, so an escaping exception would become
            // an unobserved fault and leave the requesting host waiting for a result that can no
            // longer arrive. It is reported to that host and to diagnostics instead.
            System.Diagnostics.Trace.TraceWarning("A guest operation ended unexpectedly: {0}", ex.Message);

            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.TransportFailed,
                    Message = "The guest agent failed while running this operation.",
                    UserAction = "Retry the command.",
                }).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
            await operation.Host.DisposeAsync().ConfigureAwait(false);
            operation.MarkFinished();
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

    /// <summary>
    /// Stops every running operation, so nothing outlives the connection that asked for it.
    /// </summary>
    /// <remarks>
    /// Cancellation is requested for all of them first and awaited afterwards, so operations stop in
    /// parallel rather than one graceful timeout after another.
    /// <para>
    /// Completion is awaited rather than merely requested. Returning as soon as a stop was asked for
    /// would leave child processes and their handles being disposed on background tasks after the
    /// agent believed it had shut down — which is exactly the kind of "mostly cleaned up" that
    /// leaves a Sandbox holding files the next deployment has to replace.
    /// </para>
    /// <para>
    /// The wait is bounded because an operation's final frames are sent with no cancellation: a peer
    /// that stopped reading can block that send until its socket buffer drains, which may be never.
    /// On timeout this returns anyway, and disposing the transport immediately afterwards is what
    /// unblocks it. Bounded rather than unbounded is the difference between an agent that always
    /// shuts down and one that a single stalled host can wedge.
    /// </para>
    /// </remarks>
    private async Task StopAllOperationsAsync()
    {
        var stopping = new List<Task>();

        foreach (var (id, operation) in _operations.ToArray())
        {
            if (operation.Detach)
            {
                continue;
            }

            _operations.TryRemove(id, out _);
            stopping.Add(StopOperationAsync(operation));
        }

        if (stopping.Count > 0)
        {
            try
            {
                await Task.WhenAll(stopping).WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "A guest operation did not finish reporting within {0}; closing the connection anyway.",
                    ShutdownDrainTimeout);
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

    /// <summary>Stops one operation and waits for it to finish reporting and releasing its child.</summary>
    private static async Task StopOperationAsync(RunningOperation operation)
    {
        try
        {
            // Bounded by the host's own graceful timeout, after which it terminates the job.
            await operation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The process already died with the connection.
        }

        await operation.Completion.ConfigureAwait(false);
    }
    /// <summary>One in-flight operation and its child process.</summary>
    private sealed class RunningOperation(
        IGuestProcessHost host,
        ConcurrentQueue<Task> outputSends,
        TimeSpan gracefulTimeout,
        bool detach)
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The child process running this operation.</summary>
        public IGuestProcessHost Host { get; } = host;

        /// <summary>Output frames that must be sent before completion is published.</summary>
        public ConcurrentQueue<Task> OutputSends { get; } = outputSends;

        /// <summary>Whether this process remains owned by the agent after its host channel closes.</summary>
        public bool Detach { get; } = detach;

        /// <summary>
        /// Completes when the operation has fully reported its outcome and released its child.
        /// </summary>
        /// <remarks>
        /// Created with the operation rather than assigned once its task is scheduled, so a shutdown
        /// that races the very first moment of an operation still has something to wait on.
        /// </remarks>
        public Task Completion => _finished.Task;

        /// <summary>Marks this operation as fully finished.</summary>
        public void MarkFinished() => _finished.TrySetResult();

        /// <summary>
        /// Requests graceful termination, then terminates the process tree after the timeout.
        /// </summary>
        public Task<int> CancelAsync() => Host.StopAsync(gracefulTimeout, CancellationToken.None);
    }
}
