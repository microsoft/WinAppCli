// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the one verified pull both ways out of a guest share: a command's routed artifact and
/// an explicit <c>winapp target pull</c>.
/// </summary>
/// <remarks>
/// The two callers keep different timestamp rules and different "nothing to copy" errors, so those
/// are asserted per caller. Everything else — verification, the atomic publish, cleanup, and how a
/// failed publish is reported — is shared, and a failure that escaped unmapped would surface to the
/// user as a raw <c>IOException</c> that reads like a winapp defect.
/// </remarks>
[TestClass]
public class GuestFilePullTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-pull", "nonce-pull");

    private string _guestManaged = null!;
    private string _hostOutput = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _guestManaged = TestPaths.TempRoot(nameof(GuestFilePullTests));
        _hostOutput = TestPaths.TempRoot("pull-host-output");

        Directory.CreateDirectory(_guestManaged);
        Directory.CreateDirectory(_hostOutput);
    }

    [TestCleanup]
    public void Cleanup()
    {
        TryDeleteDirectory(_guestManaged);
        TryDeleteDirectory(_hostOutput);
    }

    /// <summary>
    /// A pulled file keeps the guest's last-write time, so a repeated pull can tell what changed.
    /// </summary>
    [TestMethod]
    public async Task Pull_CopiesContentOutAndKeepsTheGuestTimestamp()
    {
        await using var harness = new Harness(_guestManaged);

        var guestFile = await WriteGuestWorkFileAsync("Results\\log.txt", "guest-content");
        var guestWrite = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(guestFile, guestWrite);

        var destination = TestPaths.Under(_hostOutput, "pulled");

        // An existing directory destination preserves the guest's structure beneath it; a single
        // file pulled to a path that is not a directory would keep that exact name instead.
        Directory.CreateDirectory(destination);

        var result = await TargetFileTransferService.CopyAsync(
            harness.Channel,
            new TargetTransferRequest(TargetTransferDirection.FromTarget, destination, "Results"),
            TestContext.CancellationToken);

        Assert.AreEqual(1, result.Transferred);

        var landed = TestPaths.Under(destination, "log.txt");
        Assert.AreEqual("guest-content", await File.ReadAllTextAsync(landed, TestContext.CancellationToken));
        Assert.AreEqual(guestWrite, File.GetLastWriteTimeUtc(landed));
    }

    /// <summary>
    /// A command's output is timestamped when it was produced here, so the guest's last-write time
    /// is deliberately not carried over.
    /// </summary>
    [TestMethod]
    public async Task PublishArtifact_DoesNotCarryTheGuestTimestamp()
    {
        await using var harness = new Harness(_guestManaged);

        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var guestFile = await WriteGuestArtifactAsync(scope, "result.png", "image-bytes");
        File.SetLastWriteTimeUtc(guestFile, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var destination = TestPaths.Under(_hostOutput, "result.png");
        var before = DateTime.UtcNow.AddSeconds(-5);

        await TargetArtifactService.PublishAsync(
            harness.Channel,
            scope,
            new RoutedArtifact("result.png", @"C:\artifacts\result.png", destination),
            TestContext.CancellationToken);

        Assert.IsGreaterThan(before, File.GetLastWriteTimeUtc(destination));
    }

    /// <summary>
    /// A file that arrived intact stays published even when its timestamp cannot be applied.
    /// </summary>
    /// <remarks>
    /// The tick count comes off the wire, so an unrepresentable value is guest-supplied input. The
    /// content has already been verified and renamed into place by then, so reporting an interrupted
    /// transfer would tell the caller its previous file survived when it has already been replaced.
    /// </remarks>
    [TestMethod]
    public async Task Pull_WithAnUnrepresentableGuestTimestamp_StillPublishesTheVerifiedContent()
    {
        await using var harness = new Harness(_guestManaged);

        const string Content = "guest-content";
        await WriteGuestWorkFileAsync("Results\\log.txt", Content);

        var destination = TestPaths.Under(_hostOutput, "stamped.txt");

        var declared = new GuestFileInfo(
            "Results\\log.txt",
            System.Text.Encoding.UTF8.GetByteCount(Content),
            LastWriteUtcTicks: long.MaxValue,
            Sha256: Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Content))).ToLowerInvariant());

        await GuestFilePull.ReceiveAsync(
            harness.Channel,
            new GuestPathScope(GuestRootNames.Work, Scope: null),
            declared,
            destination,
            applyGuestTimestamp: true,
            TestContext.CancellationToken);

        Assert.AreEqual(Content, await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
    }

    /// <summary>
    /// A destination another process holds open cannot be replaced. That must be reported as the
    /// interrupted transfer it is, not as a raw IO failure that reads like a winapp defect.
    /// </summary>
    [TestMethod]
    public async Task Pull_WhenTheDestinationIsLocked_ReportsAnInterruptedTransfer()
    {
        await using var harness = new Harness(_guestManaged);

        await WriteGuestWorkFileAsync("Results\\log.txt", "guest-content");

        var destination = TestPaths.Under(_hostOutput, "locked");
        Directory.CreateDirectory(destination);

        var landed = TestPaths.Under(destination, "log.txt");
        await File.WriteAllTextAsync(landed, "previous", TestContext.CancellationToken);

        await using (new FileStream(landed, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
                TargetFileTransferService.CopyAsync(
                    harness.Channel,
                    new TargetTransferRequest(TargetTransferDirection.FromTarget, destination, "Results"),
                    TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
            Assert.AreEqual("transfer", failure.Error.Context!["phase"]);
        }

        // What was already there is still exactly what it was.
        Assert.AreEqual("previous", await File.ReadAllTextAsync(landed, TestContext.CancellationToken));

        // And the temporary the failed publish wrote is gone rather than left beside it.
        Assert.IsEmpty(Directory.GetFiles(destination, "*.part"));
    }

    /// <summary>The artifact caller reports a locked destination the same way.</summary>
    [TestMethod]
    public async Task PublishArtifact_WhenTheDestinationIsLocked_ReportsAnInterruptedTransfer()
    {
        await using var harness = new Harness(_guestManaged);

        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        await WriteGuestArtifactAsync(scope, "result.png", "image-bytes");

        var destination = TestPaths.Under(_hostOutput, "result.png");
        await File.WriteAllTextAsync(destination, "previous-result", TestContext.CancellationToken);

        await using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
                TargetArtifactService.PublishAsync(
                    harness.Channel,
                    scope,
                    new RoutedArtifact("result.png", @"C:\artifacts\result.png", destination),
                    TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
            Assert.AreEqual("transfer", failure.Error.Context!["phase"]);
        }

        Assert.AreEqual("previous-result", await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
        Assert.IsEmpty(Directory.GetFiles(_hostOutput, "*.part"));
    }

    /// <summary>
    /// Each caller keeps its own wording for "there is nothing to copy", because one is a command
    /// that promised an output and the other is a path the user named.
    /// </summary>
    [TestMethod]
    public async Task NothingToCopy_KeepsEachCallersOwnFailure()
    {
        await using var harness = new Harness(_guestManaged);

        var missingArtifact = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetArtifactService.PublishAsync(
                harness.Channel,
                TargetArtifactService.ScopeFor(Guid.NewGuid()),
                new RoutedArtifact("result.png", @"C:\artifacts\result.png", TestPaths.Under(_hostOutput, "x.png")),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, missingArtifact.Error.Code);
        StringAssert.Contains(missingArtifact.Error.Message, "produced no", StringComparison.Ordinal);

        var missingPath = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetFileTransferService.CopyAsync(
                harness.Channel,
                new TargetTransferRequest(TargetTransferDirection.FromTarget, _hostOutput, "NotThere"),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, missingPath.Error.Code);
        StringAssert.Contains(missingPath.Error.Message, "Nothing at", StringComparison.Ordinal);
    }

    private async Task<string> WriteGuestWorkFileAsync(string relativePath, string contents)
    {
        var path = TestPaths.Under(_guestManaged, "work", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, TestContext.CancellationToken);
        return path;
    }

    private async Task<string> WriteGuestArtifactAsync(GuestPathScope scope, string name, string contents)
    {
        var directory = TestPaths.Under(_guestManaged, "artifacts", scope.Scope!);
        Directory.CreateDirectory(directory);

        var path = TestPaths.Under(directory, name);
        await File.WriteAllTextAsync(path, contents, TestContext.CancellationToken);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>Host channel and guest server over one in-memory transport, with a real file service.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;

        public Harness(string guestManagedRoot)
        {
            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                new FakeGuestProcessHostFactory(),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                new GuestFileService(guestManagedRoot));

            _serverTask = server.RunAsync(_cancellation.Token);

            Channel = new GuestCommandChannel(pair.Host, Epoch);
            Channel.Start();
        }

        public GuestCommandChannel Channel { get; }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            await Channel.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
