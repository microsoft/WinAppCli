// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// The <c>run --sandbox</c> and <c>unregister --sandbox</c> orchestration, driven through the real
/// command channel into a real guest server with only the transport faked.
/// </summary>
/// <remarks>
/// Acceptance criterion 13 again: deployment, launch, ownership, and unregistration all run here
/// without Windows Sandbox. If any of it reached for a <c>wsb</c> command or a Sandbox path, these
/// tests could not run at all.
/// </remarks>
[TestClass]
public class SandboxRunTests
{
    private const string ManagedRoot = @"C:\WinAppGuest";

    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static readonly string[] FullOptionMatrixArguments =
    [
        "run", @"C:\WinApp\deployments\abc",
        "--output-appx-directory", @"C:\WinApp\deployments\abc-layout",
        "--no-launch", "--with-alias", "--debug-output", "--unregister-on-exit",
        "--detach", "--clean", "--json", "--args", "--flag value",
    ];

    private static readonly string[] MinimalRunArguments =
    [
        "run", @"C:\WinApp\deployments\abc", "--output-appx-directory", @"C:\WinApp\deployments\abc-layout",
    ];

    private static readonly string[] UnregisterArguments =
    [
        "unregister", "--manifest", @"C:\WinApp\deployments\abc-layout\appxmanifest.xml",
    ];

    private string _root = null!;
    private string _hostSource = null!;
    private string _guestManaged = null!;
    private string _stateRoot = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(SandboxRunTests));
        _hostSource = TestPaths.Under(_root, "host");
        _guestManaged = TestPaths.Under(_root, "guest");
        _stateRoot = TestPaths.Under(_root, "state");

        Directory.CreateDirectory(_hostSource);
        Directory.CreateDirectory(_guestManaged);
        Directory.CreateDirectory(_stateRoot);
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

    // ---- Guest command translation -------------------------------------------------

    [TestMethod]
    public void BuildRunArguments_ForwardsTheWholeOptionMatrix()
    {
        var arguments = GuestRunPlanner.BuildRunArguments(
            @"C:\WinApp\deployments\abc",
            @"C:\WinApp\deployments\abc-layout",
            new GuestRunOptions(
                NoLaunch: true,
                WithAlias: true,
                DebugOutput: true,
                UnregisterOnExit: true,
                Detach: true,
                Clean: true,
                Json: true,
                AppArguments: "--flag value"));

        // Every option is the guest's ordinary winapp run option, so its meaning cannot drift from
        // the local one.
        CollectionAssert.AreEqual(FullOptionMatrixArguments, arguments);
    }

    [TestMethod]
    public void BuildRunArguments_WithNoOptions_PassesOnlyTheTwoPaths()
    {
        var arguments = GuestRunPlanner.BuildRunArguments(
            @"C:\WinApp\deployments\abc", @"C:\WinApp\deployments\abc-layout", new GuestRunOptions());

        CollectionAssert.AreEqual(MinimalRunArguments, arguments);
    }

    [TestMethod]
    public void BuildUnregisterArguments_SelectsThePackageByItsRegisteredManifest()
    {
        var arguments = GuestRunPlanner.BuildUnregisterArguments(@"C:\WinApp\deployments\abc-layout", json: false);

        // Never by package name: the manifest inside the registered layout is what identifies it,
        // and the guest's own install-location check then proves the registration is rooted there.
        CollectionAssert.AreEqual(UnregisterArguments, arguments);
    }

    [TestMethod]
    public void EnsureSupportedForUnpackaged_DebugOutput_IsRefusedUpFront()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            GuestRunPlanner.EnsureSupportedForUnpackaged(new GuestRunOptions(DebugOutput: true)));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);
    }

    // ---- Guest path resolution -----------------------------------------------------

    [TestMethod]
    public void GuestPaths_ResolvesUnderTheRootTheGuestReported()
    {
        Assert.AreEqual(
            @"C:\WinAppGuest\deployments\abc",
            GuestPaths.Resolve(Capabilities(), GuestPaths.PayloadScope("abc")));
    }

    /// <summary>
    /// Nested, the layout would be enumerated by the next reconciliation, found absent from the
    /// host's desired state, and deleted — destroying what the previous run registered from.
    /// </summary>
    [TestMethod]
    public void GuestPaths_LayoutIsASiblingOfThePayloadNotAChildOfIt()
    {
        var payload = GuestPaths.Resolve(Capabilities(), GuestPaths.PayloadScope("abc"));
        var layout = GuestPaths.Resolve(Capabilities(), GuestPaths.LayoutScope("abc"));

        Assert.AreNotEqual(payload, layout);
        Assert.IsFalse(
            layout.StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The registration layout must not live inside the folder reconciliation owns");
    }

    [TestMethod]
    public void GuestPaths_WithoutAReportedRoot_RefusesRatherThanGuessing()
    {
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            GuestPaths.Resolve(Capabilities(managedRoot: null), GuestPaths.PayloadScope("abc")));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
    }

    // ---- Deployment ----------------------------------------------------------------

    [TestMethod]
    public async Task Deploy_PlacesTheLayoutWhereTheGuestRunWillBeToldToLookForIt()
    {
        await WriteHostFileAsync("appxmanifest.xml", "<Package/>");
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.AreEqual(@"C:\WinAppGuest\deployments\dep-1", deployment.PayloadPath);
        Assert.AreEqual(@"C:\WinAppGuest\deployments\dep-1-layout", deployment.LayoutPath);
        Assert.IsFalse(deployment.State.Dirty);

        Assert.AreEqual("binary", await File.ReadAllTextAsync(
            TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe"), TestContext.CancellationToken));
    }

    /// <summary>
    /// A recipe lists build outputs by absolute host path. Guest winapp would prefer it over the
    /// files actually present, resolve none of them, and register an empty layout.
    /// </summary>
    [TestMethod]
    public async Task Deploy_NeverTransfersAnAppxRecipe()
    {
        await WriteHostFileAsync("appxmanifest.xml", "<Package/>");
        await WriteHostFileAsync("app.exe", "binary");
        await WriteHostFileAsync("MyApp.build.appxrecipe", "<Project/>");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        Assert.IsFalse(File.Exists(TestPaths.Under(_guestManaged, "deployments", "dep-1", "MyApp.build.appxrecipe")));
        Assert.IsTrue(File.Exists(TestPaths.Under(_guestManaged, "deployments", "dep-1", "app.exe")));
    }

    [TestMethod]
    public async Task Deploy_Clean_DiscardsTheRegistrationLayoutTheLastRunProduced()
    {
        await WriteHostFileAsync("app.exe", "binary");

        var staleLayout = TestPaths.Under(_guestManaged, "deployments", "dep-1-layout");
        Directory.CreateDirectory(staleLayout);
        await File.WriteAllTextAsync(
            TestPaths.Under(staleLayout, "old.exe"), "previous-build", TestContext.CancellationToken);

        await using var harness = new Harness(_guestManaged, _stateRoot);

        await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: true, TestContext.CancellationToken);

        // Left in place, --clean would have registered the previous build's files.
        Assert.IsFalse(File.Exists(TestPaths.Under(staleLayout, "old.exe")));
    }

    // ---- Launch --------------------------------------------------------------------

    [TestMethod]
    public async Task Run_AsksTheGuestToRunItsOwnWinapp()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var run = harness.Runner.RunAsync(
            harness.Target,
            deployment.State,
            new GuestExecRequest
            {
                UseGuestWinapp = true,
                Arguments = GuestRunPlanner.BuildRunArguments(
                    deployment.PayloadPath, deployment.LayoutPath, new GuestRunOptions()),
            },
            new GuestExecCallbacks(),
            TestContext.CancellationToken);

        var process = await harness.Processes.WaitForNextAsync(TestContext.CancellationToken);

        // The host names the binary by intent, never by path: it does not know where the agent
        // installed itself, and a host-supplied path would make the agent's identity host-selectable.
        Assert.AreEqual(Harness.GuestWinappPath, process.Request.Executable);
        Assert.AreEqual("run", process.Request.Arguments[0]);

        process.Exit(7);

        // The guest application's exit code survives, distinct from an infrastructure failure.
        Assert.AreEqual(7, await run);
    }

    [TestMethod]
    public async Task Run_RecordsTheLaunchedProcessAgainstTheDeployment()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var run = harness.Runner.RunAsync(
            harness.Target,
            deployment.State,
            new GuestExecRequest { UseGuestWinapp = true, Arguments = ["run"] },
            new GuestExecCallbacks(),
            TestContext.CancellationToken);

        var process = await harness.Processes.WaitForNextAsync(TestContext.CancellationToken);
        process.Exit(0);
        await run;

        var state = harness.States.Read(ExecutionTargetRef.WindowsSandboxDefault, "dep-1");
        Assert.AreEqual(process.ProcessId, state!.ProcessId);
        Assert.AreEqual(process.StartTicksUtc, state.ProcessStartTicksUtc);
    }

    [TestMethod]
    public async Task Run_WhenTheAgentCannotLocateItsOwnBinary_RefusesRatherThanRunningSomethingElse()
    {
        await using var harness = new Harness(_guestManaged, _stateRoot, guestWinapp: null);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Target.Channel.ExecuteAsync(
                new GuestExecRequest { UseGuestWinapp = true, Arguments = ["run"] },
                callbacks: null,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);
        Assert.IsTrue(harness.Processes.Started.IsEmpty, "Nothing should have been started");
    }

    [TestMethod]
    public async Task Run_WithNeitherAnExecutableNorTheGuestWinappFlag_IsRefused()
    {
        await using var harness = new Harness(_guestManaged, _stateRoot);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            harness.Target.Channel.ExecuteAsync(
                new GuestExecRequest { Arguments = ["--info"] },
                callbacks: null,
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    // ---- Ownership -----------------------------------------------------------------

    [TestMethod]
    public async Task FindOwningDeployment_MatchesOnWhatWasActuallyRegistered()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        harness.Runner.CommitPackage(deployment.State, Ownership(deployment.LayoutPath));

        var found = harness.Runner.FindOwningDeployment(Epoch, "Contoso.MyApp", "CN=Contoso");
        Assert.AreEqual("dep-1", found!.DeploymentId);

        // A package winapp never deployed is not found, which is what keeps unregister from
        // removing something a user installed in the guest themselves.
        Assert.IsNull(harness.Runner.FindOwningDeployment(Epoch, "Other.App", "CN=Contoso"));
    }

    [TestMethod]
    public async Task FindOwningDeployment_IgnoresRecordsFromAPreviousGeneration()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        harness.Runner.CommitPackage(deployment.State, Ownership(deployment.LayoutPath));

        // State from a previous generation describes a guest that no longer exists; acting on it
        // would unregister inside a Sandbox that was never asked about.
        var other = ExecutionTargetEpoch.Create("sandbox-1", "nonce-b");
        Assert.IsNull(harness.Runner.FindOwningDeployment(other, "Contoso.MyApp", "CN=Contoso"));
    }

    [TestMethod]
    public async Task FindOwningDeployment_WhenTwoLiveDeploymentsShareAnIdentity_Refuses()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        foreach (var id in new[] { "dep-1", "dep-2" })
        {
            var deployment = await harness.Runner.DeployAsync(
                harness.Target, id, new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

            harness.Runner.CommitPackage(deployment.State, Ownership(deployment.LayoutPath));
        }

        // Picking one would unregister an application the user did not name.
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            harness.Runner.FindOwningDeployment(Epoch, "Contoso.MyApp", "CN=Contoso"));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    [TestMethod]
    public async Task ClearPackage_ForgetsTheRegistrationAfterItIsRemoved()
    {
        await WriteHostFileAsync("app.exe", "binary");

        await using var harness = new Harness(_guestManaged, _stateRoot);

        var deployment = await harness.Runner.DeployAsync(
            harness.Target, "dep-1", new DirectoryInfo(_hostSource), clean: false, TestContext.CancellationToken);

        var owned = harness.Runner.CommitPackage(deployment.State, Ownership(deployment.LayoutPath));
        harness.Runner.ClearPackage(owned);

        Assert.IsNull(harness.Runner.FindOwningDeployment(Epoch, "Contoso.MyApp", "CN=Contoso"));
    }

    // ---- Additive JSON -------------------------------------------------------------

    [TestMethod]
    public void AugmentGuestJson_AddsTheExecutionTargetWithoutDisturbingTheGuestPayload()
    {
        var guestPayload = """{"AUMID":"Contoso.MyApp_abc!App","ProcessId":4212}"""u8.ToArray();

        var augmented = RunCommand.Handler.TryAugmentGuestJson(
            guestPayload, TargetInfo(), agentProcessId: 900);

        using var document = JsonDocument.Parse(augmented!);
        var root = document.RootElement;

        Assert.AreEqual("Contoso.MyApp_abc!App", root.GetProperty("AUMID").GetString());
        Assert.AreEqual(4212u, root.GetProperty("ProcessId").GetUInt32());
        Assert.IsTrue(root.GetProperty("Sandbox").GetBoolean());
        Assert.AreEqual("sandbox", root.GetProperty("ProcessScope").GetString());

        // The guest's own process ID, not the agent's child: a UI command pointed at the launcher
        // would target the wrong process.
        Assert.AreEqual("sandbox:4212", root.GetProperty("AppTarget").GetString());

        var target = root.GetProperty("ExecutionTarget");
        Assert.AreEqual("windows-sandbox", target.GetProperty("Kind").GetString());
        Assert.AreEqual("windows-sandbox:default", target.GetProperty("Id").GetString());
        Assert.AreEqual("arm64", target.GetProperty("Architecture").GetString());
        Assert.AreEqual(Epoch.Value, target.GetProperty("Epoch").GetString());
    }

    [TestMethod]
    public void AugmentGuestJson_WithoutAProcess_OmitsTheCopyableTarget()
    {
        var guestPayload = """{"AUMID":"Contoso.MyApp_abc!App"}"""u8.ToArray();

        var augmented = RunCommand.Handler.TryAugmentGuestJson(
            guestPayload, TargetInfo(), agentProcessId: 0);

        using var document = JsonDocument.Parse(augmented!);
        Assert.IsFalse(document.RootElement.TryGetProperty("AppTarget", out _));
    }

    /// <summary>
    /// Losing an additive field is recoverable; handing a caller a re-encoded or truncated document
    /// is not.
    /// </summary>
    [TestMethod]
    public void AugmentGuestJson_UnparseablePayload_IsRelayedUnchanged()
    {
        Assert.IsNull(RunCommand.Handler.TryAugmentGuestJson(
            "not json at all"u8.ToArray(), TargetInfo(), agentProcessId: 4212));
    }

    [TestMethod]
    public void AugmentGuestJson_PayloadThatOverranTheCaptureBound_IsRelayedUnchanged()
    {
        var oversized = new byte[RunCommand.Handler.MaxCapturedJsonBytes];

        Assert.IsNull(RunCommand.Handler.TryAugmentGuestJson(oversized, TargetInfo(), agentProcessId: 0));
    }

    [TestMethod]
    public void DirectUnpackagedJsonResult_IsAlwaysAHostScopedEnvelope()
    {
        var result = RunCommand.Handler.CreateDirectGuestResult(
            architecture: "arm64",
            epoch: "epoch-1");

        Assert.IsNull(result.ProcessId);
        Assert.IsTrue(result.Sandbox);
        Assert.AreEqual("sandbox", result.ProcessScope);
        Assert.IsNull(result.AppTarget);
        Assert.AreEqual("arm64", result.ExecutionTarget!.Architecture);
        Assert.AreEqual("epoch-1", result.ExecutionTarget.Epoch);
    }

    private static ExecutionTargetInfo TargetInfo() => new()
    {
        Kind = ExecutionTargetRef.WindowsSandboxDefault.Kind,
        Id = ExecutionTargetRef.WindowsSandboxDefault.Id,
        Architecture = "arm64",
        Epoch = Epoch.Value,
    };

    private static PackageOwnership Ownership(string layoutPath) => new()
    {
        PackageName = "Contoso.MyApp",
        Publisher = "CN=Contoso",
        PackageFamilyName = "Contoso.MyApp_abc",
        RegisteredLocation = layoutPath,
    };

    private static ExecutionTargetCapabilities Capabilities(string? managedRoot = ManagedRoot) => new()
    {
        Architecture = "arm64",
        SupportsInteractiveDesktop = true,
        SupportsRealInput = true,
        SupportsScreenCapture = true,
        CooperativeUiTurnsVersion = 1,
        SupportsInternalSystemSetup = true,
        PersistentStorage = false,
        ManagedRoot = managedRoot,
    };

    private async Task WriteHostFileAsync(string relativePath, string contents)
    {
        var path = TestPaths.Under(_hostSource, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, TestContext.CancellationToken);
    }

    /// <summary>Host runner and guest server sharing one in-memory transport and a real file service.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>Stands in for the guest's own winapp binary.</summary>
        public const string GuestWinappPath = @"C:\WinAppGuest\agent\current\winapp.exe";

        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;
        private readonly GuestCommandChannel _channel;

        public Harness(string guestManagedRoot, string stateRoot, string? guestWinapp = GuestWinappPath)
        {
            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                Processes,
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                new GuestFileService(guestManagedRoot),
                guestWinapp);

            _serverTask = server.RunAsync(_cancellation.Token);

            _channel = new GuestCommandChannel(pair.Host, Epoch);
            _channel.Start();

            States = new DeploymentStateStore(new SandboxTestStateDirectoryProvider(stateRoot));
            Runner = new GuestApplicationRunner(new TargetDeploymentService(States));

            // The managed root is a guest-side value, so the harness reports a stable one rather
            // than the host temp folder the fake file service actually writes to.
            Target = new PreparedTarget(_channel, Epoch, Capabilities(), Reused: false);
        }

        public FakeGuestProcessHostFactory Processes { get; } = new();

        public DeploymentStateStore States { get; }

        public GuestApplicationRunner Runner { get; }

        public PreparedTarget Target { get; }

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

            await _channel.DisposeAsync();
            _cancellation.Dispose();
        }
    }

    /// <summary>A state directory provider rooted at a test-owned folder.</summary>
    private sealed class SandboxTestStateDirectoryProvider(string root) : ITargetStateDirectoryProvider
    {
        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create)
        {
            var directory = new DirectoryInfo(TestPaths.Under(root, target.Slug));

            if (create)
            {
                directory.Create();
            }

            return directory;
        }
    }
}
