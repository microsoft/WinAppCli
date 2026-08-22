// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// UI argv rewriting and artifact copy-back: what `winapp ui ... --sandbox` forwards, and what
/// comes back.
/// </summary>
[TestClass]
public class SandboxUiRoutingTests
{
    private const string GuestArtifacts = @"C:\WinAppGuest\artifacts\op1";

    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static readonly string[] InspectForwarded = ["ui", "inspect", "-a", "MyApp", "--depth", "8", "--json"];
    private static readonly string[] InspectApp = ["ui", "inspect", "-a", "MyApp"];
    private static readonly string[] InspectWindow = ["ui", "inspect", "-w", "123456"];
    private static readonly string[] SendKeysAfterSeparator =
        ["ui", "send-keys", "-a", "MyApp", "--", "--sandbox", "sandbox:literal"];

    private static readonly string[][] SandboxFlagSpellings =
    [
        ["ui", "inspect", "--sandbox=true", "-a", "MyApp"],
        ["ui", "inspect", "--sandbox:true", "-a", "MyApp"],
        ["ui", "inspect", "--sandbox", "true", "-a", "MyApp"],
    ];

    private string _root = null!;
    private string _guestManaged = null!;
    private string _hostOutput = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(SandboxUiRoutingTests));
        _guestManaged = TestPaths.Under(_root, "guest");
        _hostOutput = TestPaths.Under(_root, "out");

        Directory.CreateDirectory(_guestManaged);
        Directory.CreateDirectory(_hostOutput);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // ---- Argv rewriting ------------------------------------------------------------

    [TestMethod]
    public void Rewrite_RemovesTheRoutingFlagAndForwardsEverythingElseVerbatim()
    {
        var routed = Rewrite(["ui", "inspect", "--sandbox", "-a", "MyApp", "--depth", "8", "--json"]);

        CollectionAssert.AreEqual(InspectForwarded, routed.Arguments);

        Assert.IsNull(routed.Artifact);
    }

    [TestMethod]
    public void Rewrite_RemovesTheFlagInEverySpellingItCanBeWritten()
    {
        foreach (var form in SandboxFlagSpellings)
        {
            var routed = Rewrite(form);

            // A stranded 'true' would arrive at the guest as a positional argument and be parsed as
            // a selector.
            CollectionAssert.AreEqual(InspectApp, routed.Arguments, string.Join(' ', form));
        }
    }

    [TestMethod]
    public void Rewrite_StripsTheApplicationTargetPrefixInEverySpelling()
    {
        Assert.AreEqual("MyApp", Rewrite(["ui", "inspect", "-a", "sandbox:MyApp"]).Arguments[3]);
        Assert.AreEqual("--app=MyApp", Rewrite(["ui", "inspect", "--app=sandbox:MyApp"]).Arguments[2]);
        Assert.AreEqual("-a:4212", Rewrite(["ui", "inspect", "-a:sandbox:4212"]).Arguments[2]);
    }

    [TestMethod]
    public void Rewrite_LeavesANumericWindowAlone()
    {
        // A handle carries no scope. Rewriting or inferring one would resolve a host window against
        // the guest, or the reverse.
        var routed = Rewrite(["ui", "inspect", "--sandbox", "-w", "123456"]);

        CollectionAssert.AreEqual(InspectWindow, routed.Arguments);
    }

    [TestMethod]
    public void Rewrite_RedirectsTheOutputPathIntoGuestStaging()
    {
        var destination = TestPaths.Under(_hostOutput, "result.png");
        var routed = Rewrite(["ui", "screenshot", "--sandbox", "-a", "MyApp", "-o", destination]);

        Assert.IsNotNull(routed.Artifact);
        Assert.AreEqual(destination, routed.Artifact.HostDestination);
        Assert.AreEqual("result.png", routed.Artifact.GuestRelativePath);
        Assert.AreEqual(TestPaths.Under(GuestArtifacts, "result.png"), routed.Artifact.GuestFullPath);
        Assert.AreEqual(routed.Artifact.GuestFullPath, routed.Arguments[^1]);
    }

    [TestMethod]
    public void RewriteOutputPaths_RewritesJsonEscapedWindowsPaths()
    {
        var artifact = new RoutedArtifact(
            "result.png",
            @"C:\WinApp\artifacts\op1\result.png",
            @"C:\host output\result.png");
        const string GuestJson =
            """{"filePath":"C:\\WinApp\\artifacts\\op1\\result.png","width":100}""";

        var rewritten = SandboxUiRouter.RewriteOutputPaths(GuestJson, artifact);

        Assert.AreEqual(
            """{"filePath":"C:\\host output\\result.png","width":100}""",
            rewritten);
    }

    /// <summary>
    /// A drive colon is not an option delimiter. Splitting on it would send the guest '-o C' and
    /// leave the rest as a positional.
    /// </summary>
    [TestMethod]
    public void Rewrite_DoesNotSplitAnOutputValueAtItsDriveColon()
    {
        var routed = Rewrite(["ui", "screenshot", "--sandbox", "-o", @"C:\out\result.png"]);

        Assert.IsNotNull(routed.Artifact);
        Assert.AreEqual(@"C:\out\result.png", routed.Artifact.HostDestination);
    }

    [TestMethod]
    public void Rewrite_NeverTouchesAnythingAfterASeparator()
    {
        var routed = Rewrite(["ui", "send-keys", "--sandbox", "-a", "MyApp", "--", "--sandbox", "sandbox:literal"]);

        CollectionAssert.AreEqual(SendKeysAfterSeparator, routed.Arguments);
    }

    [TestMethod]
    public void Rewrite_OutputPathThatNamesNoFile_IsRefused()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            Rewrite(["ui", "screenshot", "--sandbox", "-o", @"C:\"]));

        Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);
    }

    [TestMethod]
    public void IsSandboxTarget_RecognisesThePrefixWithoutCaseSensitivity()
    {
        Assert.IsTrue(UiArgvRouter.IsSandboxTarget("sandbox:MyApp"));
        Assert.IsTrue(UiArgvRouter.IsSandboxTarget("Sandbox:4212"));
        Assert.IsFalse(UiArgvRouter.IsSandboxTarget("MyApp"));
        Assert.IsFalse(UiArgvRouter.IsSandboxTarget(null));
    }

    // ---- Artifact copy-back --------------------------------------------------------

    [TestMethod]
    public async Task Publish_VerifiesTheFileThenPlacesItWhereTheCallerAskedFor()
    {
        await using var harness = new Harness(_guestManaged);

        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        await WriteGuestArtifactAsync(scope, "result.png", "image-bytes");

        var destination = TestPaths.Under(_hostOutput, "result.png");

        await TargetArtifactService.PublishAsync(
            harness.Channel, scope, Artifact("result.png", destination), TestContext.CancellationToken);

        Assert.AreEqual("image-bytes", await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Publish_WhenTheGuestProducedNothing_FailsAndLeavesTheDestinationAlone()
    {
        await using var harness = new Harness(_guestManaged);

        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var destination = TestPaths.Under(_hostOutput, "result.png");
        await File.WriteAllTextAsync(destination, "previous-result", TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetArtifactService.PublishAsync(
                harness.Channel, scope, Artifact("result.png", destination), TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);

        // Reporting a previous run's file as this run's result would be worse than reporting none.
        Assert.AreEqual("previous-result", await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
    }

    /// <summary>
    /// Nothing is published until the whole file has arrived and matched, so an interrupted
    /// transfer cannot appear as a shorter but plausible result.
    /// </summary>
    [TestMethod]
    public async Task Verify_ContentShorterThanDeclared_IsRejectedWithWhatArrived()
    {
        var path = TestPaths.Under(_hostOutput, "partial.mp4");
        await File.WriteAllTextAsync(path, "half", TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetArtifactService.VerifyAsync(
                path,
                new GuestFileInfo("result.mp4", Size: 1000, LastWriteUtcTicks: 0, Sha256: "irrelevant"),
                Artifact("result.mp4", path),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
        Assert.AreEqual("1000", failure.Error.Context!["expectedBytes"]);
        Assert.AreEqual("4", failure.Error.Context["receivedBytes"]);
        Assert.AreEqual("size", failure.Error.Context["phase"]);
    }

    [TestMethod]
    public async Task Verify_RightLengthButDifferentContent_IsRejected()
    {
        var path = TestPaths.Under(_hostOutput, "swapped.png");
        await File.WriteAllTextAsync(path, "abcd", TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetArtifactService.VerifyAsync(
                path,
                new GuestFileInfo("result.png", Size: 4, LastWriteUtcTicks: 0, Sha256: new string('0', 64)),
                Artifact("result.png", path),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransferInterrupted, failure.Error.Code);
        Assert.AreEqual("hash", failure.Error.Context!["phase"]);
    }

    [TestMethod]
    public async Task Publish_ThenRemove_LeavesNoGuestStagingBehind()
    {
        await using var harness = new Harness(_guestManaged);

        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        await WriteGuestArtifactAsync(scope, "result.png", "image-bytes");

        await TargetArtifactService.PublishAsync(
            harness.Channel,
            scope,
            Artifact("result.png", TestPaths.Under(_hostOutput, "result.png")),
            TestContext.CancellationToken);

        await TargetArtifactService.TryRemoveAsync(harness.Channel, scope, TestContext.CancellationToken);

        Assert.IsFalse(Directory.Exists(TestPaths.Under(_guestManaged, "artifacts", scope.Scope!)));
    }

    private static RoutedArtifact Artifact(string name, string destination) =>
        new(name, TestPaths.Under(GuestArtifacts, name), destination);

    private async Task<string> WriteGuestArtifactAsync(GuestPathScope scope, string name, string contents)
    {
        var directory = TestPaths.Under(_guestManaged, "artifacts", scope.Scope!);
        Directory.CreateDirectory(directory);

        var path = TestPaths.Under(directory, name);
        await File.WriteAllTextAsync(path, contents, TestContext.CancellationToken);
        return path;
    }

    private static RoutedUiCommand Rewrite(string[] arguments) =>
        UiArgvRouter.Rewrite(arguments, GuestArtifacts, Path.GetFullPath);

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
