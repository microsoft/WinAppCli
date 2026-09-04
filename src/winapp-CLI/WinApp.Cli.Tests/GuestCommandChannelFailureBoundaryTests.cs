// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// A failure inside the receive pump must fail the operation that caused it, never hang the command.
/// </summary>
/// <remarks>
/// <para>
/// One pump owns the transport's read side and dispatches every reply. It also runs each operation's
/// callbacks, and those callbacks do real I/O: <c>GetFileAsync</c>'s writes each chunk straight to
/// the caller's destination stream, and <c>target exec</c>'s writes to this process's standard
/// output. A full disk, a revoked handle, or a closed pipe therefore throws <em>on the pump</em>.
/// </para>
/// <para>
/// If that escaped, the channel would lose its only reader while operations were still waiting for
/// replies, and every one of them — including the one that caused it — would wait forever. What the
/// user sees is <c>winapp target pull</c> printing nothing and never returning, with no error to
/// search for. These tests pin the two properties that prevent it: the failure is reported, and it
/// is bounded to the operation that caused it.
/// </para>
/// </remarks>
[TestClass]
public class GuestCommandChannelFailureBoundaryTests : IDisposable
{
    private const string Epoch = "instance-1:nonce-1";

    /// <summary>The longest a bounded failure may take before it is really a hang.</summary>
    /// <remarks>
    /// Every wait in this class is bounded, because the regression being guarded against is exactly
    /// an operation that never completes. Unbounded, a reintroduced hang would stall the whole test
    /// run instead of failing one test, and the reason would be lost with it.
    /// </remarks>
    private static readonly TimeSpan BoundedFailure = TimeSpan.FromSeconds(30);

    private FakeGuestTransport _transport = null!;
    private GuestCommandChannel _channel = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _transport = new FakeGuestTransport();
        _channel = new GuestCommandChannel(_transport, new ExecutionTargetEpoch(Epoch));
        _channel.Start();
    }

    [TestCleanup]
    public void ResetTransport() => _transport.Break();

    private Task<GuestMessage> NextRequestAsync() => NextRequestAsync(_transport);

    private async Task<GuestMessage> NextRequestAsync(FakeGuestTransport transport)
    {
        var frame = await transport.PeerInbox
            .ReadAsync(TestContext.CancellationTokenSource.Token)
            .AsTask()
            .WaitAsync(BoundedFailure, TestContext.CancellationTokenSource.Token);

        var message = GuestPayloadCodec.TryDecodeJson(frame.Span);
        Assert.IsNotNull(message, "Expected a control message.");
        return message;
    }

    /// <summary>Asserts the operation fails within the bound, and returns why.</summary>
    private async Task<ExecutionTargetException> AssertFailsBoundedAsync(Task pending) =>
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => pending.WaitAsync(BoundedFailure, TestContext.CancellationTokenSource.Token));

    private void PeerReply(GuestMessage message) => _transport.PeerSend(GuestPayloadCodec.EncodeJson(message));

    private static GuestExecRequest SampleRequest => new()
    {
        Executable = "winapp.exe",
        Arguments = ["--version"],
    };

    /// <summary>
    /// A destination that cannot be written fails the transfer instead of hanging it.
    /// </summary>
    /// <remarks>
    /// This is the realistic case: <c>winapp target pull</c> writes into a file on this machine, and
    /// that write can fail at any chunk. Reported as a transfer that stopped rather than a channel
    /// that broke, because the two lead to different recovery — free some space and retry, versus
    /// the target itself being unusable.
    /// </remarks>
    [TestMethod]
    public async Task GetFile_WhenTheDestinationThrows_FailsTheTransferRatherThanHanging()
    {
        using var destination = new ThrowingStream();

        var pending = _channel.GetFileAsync(
            GuestPaths.LayoutScope("dep-1"),
            "app.exe",
            destination,
            TestContext.CancellationTokenSource.Token);

        var sent = await NextRequestAsync();
        Assert.AreEqual(GuestMessageTypes.GetFileRequest, sent.Type);

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(sent.OperationId!), GuestStreamId.StandardOutput, "content"u8));

        var failure = await AssertFailsBoundedAsync(pending);

        Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
        StringAssert.Contains(
            failure.Error.Message,
            ThrowingStream.Reason,
            "The reported message must name what actually went wrong, not a generic channel failure.");
        Assert.AreEqual(
            nameof(IOException),
            failure.Error.Context!["cause"],
            "The original failure type is kept so a caller can tell a disk problem from a protocol one.");
        Assert.IsInstanceOfType<IOException>(
            failure.InnerException,
            "The original exception is preserved for logs and diagnostics.");
    }

    /// <summary>
    /// One operation's broken destination must not take down operations that share the channel.
    /// </summary>
    /// <remarks>
    /// The cleanup a caller runs after a failed transfer — deleting the partial file it left in the
    /// guest, for example — goes over this same channel. If the pump had died with the transfer,
    /// that cleanup would hang too, and the failure would leave the target dirty as well as the
    /// command stuck.
    /// </remarks>
    [TestMethod]
    public async Task GetFile_WhenTheDestinationThrows_LeavesOtherOperationsWorking()
    {
        using var destination = new ThrowingStream();

        var doomed = _channel.GetFileAsync(
            GuestPaths.LayoutScope("dep-1"),
            "app.exe",
            destination,
            TestContext.CancellationTokenSource.Token);

        var transfer = await NextRequestAsync();

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(transfer.OperationId!), GuestStreamId.StandardOutput, "content"u8));

        await AssertFailsBoundedAsync(doomed);

        // The cleanup an interrupted transfer would run next.
        var cleanup = _channel.DeleteFilesAsync(
            GuestPaths.LayoutScope("dep-1"), ["app.exe"], TestContext.CancellationTokenSource.Token);

        var deletion = await NextRequestAsync();
        Assert.AreEqual(GuestMessageTypes.DeleteFilesRequest, deletion.Type);

        PeerReply(new GuestMessage { Type = GuestMessageTypes.FileCompleted, OperationId = deletion.OperationId });

        await cleanup.WaitAsync(BoundedFailure, TestContext.CancellationTokenSource.Token);
    }

    /// <summary>A caller's stdout callback that throws fails only that execution.</summary>
    /// <remarks>
    /// <c>target exec</c> relays guest output to this process's standard output, so a closed or
    /// broken pipe surfaces here — the same shape of failure as a broken destination, on the path
    /// every relayed command takes.
    /// </remarks>
    [TestMethod]
    public async Task Execute_WhenTheStandardOutputCallbackThrows_FailsTheExecution()
    {
        var pending = _channel.ExecuteAsync(
            SampleRequest,
            new GuestExecCallbacks(OnStandardOutput: _ => throw new IOException(ThrowingStream.Reason)),
            TestContext.CancellationTokenSource.Token);

        var sent = await NextRequestAsync();

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(sent.OperationId!), GuestStreamId.StandardOutput, "hello"u8));

        var failure = await AssertFailsBoundedAsync(pending);

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "standard output");
    }

    /// <summary>A caller's stderr callback that throws fails only that execution.</summary>
    [TestMethod]
    public async Task Execute_WhenTheStandardErrorCallbackThrows_FailsTheExecution()
    {
        var pending = _channel.ExecuteAsync(
            SampleRequest,
            new GuestExecCallbacks(OnStandardError: _ => throw new IOException(ThrowingStream.Reason)),
            TestContext.CancellationTokenSource.Token);

        var sent = await NextRequestAsync();

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(sent.OperationId!), GuestStreamId.StandardError, "warning"u8));

        var failure = await AssertFailsBoundedAsync(pending);

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "standard error");
    }

    /// <summary>
    /// A pump that dies for an unforeseen reason still fails everything waiting on it.
    /// </summary>
    /// <remarks>
    /// The per-operation boundary covers the failures that can be attributed to one operation. This
    /// covers everything else — a decoding fault, an unexpected transport exception — where there is
    /// no single operation to blame. Without it those simply stop the reader, and every operation
    /// waits forever on replies that can no longer arrive.
    /// </remarks>
    [TestMethod]
    public async Task ReceiveLoop_WhenTheTransportFailsUnexpectedly_FailsEveryPendingOperation()
    {
        await using var transport = new FaultingTransport(new InvalidOperationException("the reader broke"));
        await using var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(Epoch));

        var first = channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);
        var second = channel.GetCapabilitiesAsync(TestContext.CancellationTokenSource.Token);

        // Started only now, so both operations are registered before the pump reads and faults.
        channel.Start();
        transport.ReleaseReader();

        var failure = await AssertFailsBoundedAsync(first);
        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "the reader broke", "The original cause must survive.");
        Assert.AreEqual(nameof(InvalidOperationException), failure.Error.Context!["cause"]);

        await AssertFailsBoundedAsync(second);
    }

    /// <summary>
    /// A structured failure raised by the transport is reported as-is, not reclassified.
    /// </summary>
    /// <remarks>
    /// Authentication and protocol failures already carry the right code and guidance. Folding them
    /// into a generic pump fault would replace an accurate diagnosis with a vague one.
    /// </remarks>
    [TestMethod]
    public async Task ReceiveLoop_WhenTheTransportRaisesAStructuredFailure_PreservesIt()
    {
        await using var transport = new FaultingTransport(ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.AgentIncompatible,
            "The guest agent speaks a different protocol version.",
            userAction: "Update winapp in the guest."));

        await using var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(Epoch));

        var pending = channel.ExecuteAsync(SampleRequest, callbacks: null, TestContext.CancellationTokenSource.Token);

        channel.Start();
        transport.ReleaseReader();

        var failure = await AssertFailsBoundedAsync(pending);

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
        Assert.AreEqual("Update winapp in the guest.", failure.Error.UserAction);
    }

    /// <summary>
    /// A callback failure asks the guest to stop, rather than leaving its process running.
    /// </summary>
    /// <remarks>
    /// The realistic trigger is a pipeline that stops reading: <c>winapp target exec &lt;target&gt;
    /// -- &lt;long-running command&gt;</c> piped into something that exits early makes the relay
    /// callback throw on a closed pipe. The operation is then dropped on this side, but the guest
    /// child is still running — and the guest agent is reused across commands, so it would outlive
    /// the command that started it. The cancel request is what closes that gap; it is best effort,
    /// so a channel that is already gone costs nothing.
    /// </remarks>
    [TestMethod]
    public async Task Execute_WhenACallbackFails_AsksTheGuestToStopTheProcess()
    {
        var pending = _channel.ExecuteAsync(
            SampleRequest,
            new GuestExecCallbacks(OnStandardOutput: _ => throw new IOException(ThrowingStream.Reason)),
            TestContext.CancellationTokenSource.Token);

        var sent = await NextRequestAsync();

        _transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(sent.OperationId!), GuestStreamId.StandardOutput, "hello"u8));

        await AssertFailsBoundedAsync(pending);

        var cancel = await NextRequestAsync();

        Assert.AreEqual(GuestMessageTypes.CancelRequest, cancel.Type);
        Assert.AreEqual(
            sent.OperationId,
            cancel.OperationId,
            "The stop must name the operation that failed, not some other one.");
    }

    /// <summary>
    /// Disposing after a callback failure still tears the channel down cleanly.
    /// </summary>
    /// <remarks>
    /// Disposal awaits the pump. A pump left faulted would rethrow there, skipping the transport
    /// disposal that follows and leaking the connection — so an unrelated failure would also leak a
    /// socket every time it happened.
    /// </remarks>
    [TestMethod]
    public async Task Dispose_AfterACallbackFailure_CompletesAndClosesTheTransport()
    {
        var transport = new FakeGuestTransport();
        var channel = new GuestCommandChannel(transport, new ExecutionTargetEpoch(Epoch));
        channel.Start();

        var pending = channel.ExecuteAsync(
            SampleRequest,
            new GuestExecCallbacks(OnStandardOutput: _ => throw new IOException(ThrowingStream.Reason)),
            TestContext.CancellationTokenSource.Token);

        var sent = await NextRequestAsync(transport);

        transport.PeerSend(GuestPayloadCodec.EncodeStream(
            Guid.Parse(sent.OperationId!), GuestStreamId.StandardOutput, "hello"u8));

        await AssertFailsBoundedAsync(pending);

        await channel.DisposeAsync().AsTask().WaitAsync(BoundedFailure, TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(transport.IsConnected, "Disposal must have closed the transport.");
    }

    /// <summary>
    /// Disposes the channel and its transport, in that order.
    /// </summary>
    /// <remarks>
    /// The channel owns the transport's read side, so it goes first: disposing the transport out
    /// from under a live pump would turn an orderly shutdown into the very fault these tests are
    /// about. Done here rather than in <c>[TestCleanup]</c> so both disposables have one owner.
    /// </remarks>
    public void Dispose()
    {
        _channel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _transport?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>A destination that fails on the first write, as a full disk would.</summary>
    private sealed class ThrowingStream : Stream
    {
        public const string Reason = "There is not enough space on the disk.";

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new IOException(Reason);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException(Reason);

        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException(Reason);
    }

    /// <summary>A transport whose read side fails once the test releases it.</summary>
    /// <remarks>
    /// Held until released so the test can register its operations first: a pump that faulted before
    /// they existed would have nothing to fail, and the test would pass without proving anything.
    /// </remarks>
    private sealed class FaultingTransport(Exception failure) : IGuestTransport
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConnected { get; private set; } = true;

        public void ReleaseReader() => _released.TrySetResult();

        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw failure;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _released.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
