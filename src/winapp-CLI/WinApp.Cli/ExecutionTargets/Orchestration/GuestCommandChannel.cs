// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Callbacks for one running guest operation.</summary>
/// <param name="OnStarted">Invoked once the guest reports the process ID.</param>
/// <param name="OnStandardOutput">Invoked for each stdout chunk, in order.</param>
/// <param name="OnStandardError">Invoked for each stderr chunk, in order.</param>
internal sealed record GuestExecCallbacks(
    Action<int>? OnStarted = null,
    Action<ReadOnlyMemory<byte>>? OnStandardOutput = null,
    Action<ReadOnlyMemory<byte>>? OnStandardError = null);

/// <summary>Outcome of a completed guest operation.</summary>
/// <param name="ExitCode">The guest process's exit code.</param>
/// <param name="ProcessId">The guest process ID, valid only within the current target epoch.</param>
internal sealed record GuestExecResult(int ExitCode, int ProcessId);

/// <summary>
/// The single target-neutral command channel over an <see cref="IGuestTransport"/>
/// (spec §"Transport and command channel").
/// </summary>
/// <remarks>
/// Everything meaningful lives here rather than in a backend: capability negotiation, structured
/// execution, stream forwarding, cancellation, and epoch fencing. Because it depends only on
/// <see cref="IGuestTransport"/>, the whole contract is exercised against a fake transport with no
/// Windows Sandbox involved — which is what proves orchestration does not depend on WSB APIs.
/// <para>
/// One receive pump owns the transport's read side and dispatches to per-operation state. Frames
/// for an unknown operation are dropped rather than treated as fatal, because a late frame from a
/// cancelled operation is normal and must not tear down the channel.
/// </para>
/// </remarks>
internal sealed class GuestCommandChannel : IAsyncDisposable
{
    private readonly IGuestTransport _transport;
    private readonly string _targetEpoch;
    private readonly ConcurrentDictionary<Guid, OperationState> _operations = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _receivePump;
    private ExecutionTargetErrorInfo? _fatalError;

    /// <summary>Creates a channel over <paramref name="transport"/> fenced to <paramref name="targetEpoch"/>.</summary>
    public GuestCommandChannel(IGuestTransport transport, ExecutionTargetEpoch targetEpoch)
    {
        _transport = transport;
        _targetEpoch = targetEpoch.Value;
    }

    /// <summary>Starts the receive pump. Must be called before any operation.</summary>
    public void Start() => _receivePump ??= Task.Run(() => ReceiveLoopAsync(_shutdown.Token));

    /// <summary>Asks the guest to describe what it supports.</summary>
    /// <remarks>
    /// Commands validate required capabilities before deployment or execution rather than inferring
    /// them from the provider name, so a future backend reuses this unchanged.
    /// </remarks>
    public async Task<ExecutionTargetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.CapabilitiesRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                },
                cancellationToken).ConfigureAwait(false);

            var message = await state.Capabilities.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return message;
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>
    /// Runs one guest process, streaming its output, and returns its exit code.
    /// </summary>
    /// <remarks>
    /// Cancellation asks the guest to terminate the process gracefully first; the guest enforces its
    /// own timeout before killing the process tree, so a well-behaved child can still flush and exit
    /// cleanly. The exit code returned is the guest application's, kept distinguishable from the
    /// infrastructure failures reported as <see cref="ExecutionTargetException"/>.
    /// </remarks>
    public async Task<GuestExecResult> ExecuteAsync(
        GuestExecRequest request,
        GuestExecCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);
        state.Callbacks = callbacks;

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ExecRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Exec = request,
                },
                cancellationToken).ConfigureAwait(false);

            var exitCode = await state.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GuestExecResult(exitCode, state.ProcessId);
        }
        catch (OperationCanceledException)
        {
            // Ask the guest to stop before surfacing cancellation. This is done here rather than
            // from a CancellationToken registration because registrations fire last-in-first-out:
            // WaitAsync's own registration would run first, and unwinding this method would dispose
            // ours before it ever ran, silently leaving the guest process running.
            await RequestCancelAsync(operationId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>Sends a chunk of standard input to a running operation.</summary>
    public async Task SendStandardInputAsync(
        Guid operationId,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        // Split so one write can never exceed the frame limit and stall the channel.
        var remaining = data;
        while (!remaining.IsEmpty)
        {
            var take = Math.Min(remaining.Length, GuestPayloadCodec.MaxStreamChunkSize);
            var payload = GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardInput, remaining.Span[..take]);
            await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
            remaining = remaining[take..];
        }
    }

    /// <summary>Signals that no more standard input will arrive for an operation.</summary>
    public Task CloseStandardInputAsync(Guid operationId, CancellationToken cancellationToken) =>
        SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.StdinClosed,
                OperationId = operationId.ToString(),
                TargetEpoch = _targetEpoch,
            },
            cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            // Idempotent: disposing twice must not throw on the already-disposed shutdown source.
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_receivePump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: shutdown cancels the pump.
            }
        }

        FailPendingOperations(_fatalError ?? new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.TransportFailed,
            Message = "The connection to the guest was closed.",
        });

        _shutdown.Dispose();
        _sendLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private OperationState Register(Guid operationId)
    {
        var state = new OperationState();
        _operations[operationId] = state;
        return state;
    }

    /// <summary>
    /// Best-effort request for the guest to terminate an operation gracefully.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed: cancellation is usually why the channel is going away, and a dead
    /// channel must not turn cancellation into a different, more confusing error.
    /// </remarks>
    private async Task RequestCancelAsync(Guid operationId)
    {
        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.CancelRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // The channel is already gone; the guest process dies with it.
        }
        catch (ObjectDisposedException)
        {
            // The channel was disposed concurrently with cancellation.
        }
    }

    private Task SendAsync(GuestMessage message, CancellationToken cancellationToken) =>
        SendRawAsync(GuestPayloadCodec.EncodeJson(message), cancellationToken);

    private async Task SendRawAsync(byte[] payload, CancellationToken cancellationToken)
    {
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

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    // The guest closed cleanly. Any operation still waiting will never complete.
                    FailPendingOperations(new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.Terminated,
                        Message = "The guest closed the connection before the operation completed.",
                        UserAction = "Retry the command.",
                    });
                    return;
                }

                Dispatch(frame.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (ExecutionTargetException ex)
        {
            _fatalError = ex.Error;
            FailPendingOperations(ex.Error);
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> frame)
    {
        if (!GuestPayloadCodec.TryGetKind(frame.Span, out var kind))
        {
            return;
        }

        if (kind == GuestPayloadKind.Stream)
        {
            DispatchStream(frame);
            return;
        }

        var message = GuestPayloadCodec.TryDecodeJson(frame.Span);
        if (message?.OperationId is null || !Guid.TryParse(message.OperationId, out var operationId))
        {
            return;
        }

        // A frame for an operation we no longer track is normal after cancellation, so it is
        // dropped rather than treated as a protocol violation.
        if (!_operations.TryGetValue(operationId, out var state))
        {
            return;
        }

        switch (message.Type)
        {
            case GuestMessageTypes.CapabilitiesResponse when message.Capabilities is { } capabilities:
                state.Capabilities.TrySetResult(capabilities);
                break;

            case GuestMessageTypes.ExecStarted when message.ProcessId is { } processId:
                state.ProcessId = processId;
                state.Callbacks?.OnStarted?.Invoke(processId);
                break;

            case GuestMessageTypes.ExecCompleted when message.ExitCode is { } exitCode:
                state.Completion.TrySetResult(exitCode);
                break;

            case GuestMessageTypes.OperationFailed when message.Error is { } error:
                Fail(state, error);
                break;

            default:
                break;
        }
    }

    private void DispatchStream(ReadOnlyMemory<byte> frame)
    {
        if (!GuestPayloadCodec.TryDecodeStream(frame, out var operationId, out var stream, out var data))
        {
            return;
        }

        if (!_operations.TryGetValue(operationId, out var state))
        {
            return;
        }

        switch (stream)
        {
            case GuestStreamId.StandardOutput:
                state.Callbacks?.OnStandardOutput?.Invoke(data);
                break;

            case GuestStreamId.StandardError:
                state.Callbacks?.OnStandardError?.Invoke(data);
                break;

            default:
                // Standard input only flows host to guest; a guest sending it is ignored.
                break;
        }
    }

    private void FailPendingOperations(ExecutionTargetErrorInfo error)
    {
        foreach (var state in _operations.Values)
        {
            Fail(state, error);
        }
    }

    private static void Fail(OperationState state, ExecutionTargetErrorInfo error)
    {
        var exception = new ExecutionTargetException(error);
        state.Completion.TrySetException(exception);
        state.Capabilities.TrySetException(exception);
    }

    /// <summary>Per-operation state owned by the receive pump.</summary>
    private sealed class OperationState
    {
        public TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ExecutionTargetCapabilities> Capabilities { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GuestExecCallbacks? Callbacks { get; set; }

        public int ProcessId { get; set; }
    }
}
