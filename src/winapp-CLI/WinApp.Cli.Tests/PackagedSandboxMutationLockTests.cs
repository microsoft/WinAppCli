// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Proves the SBX-009 packaged-registration mutation-lock gap is closed: a launching
/// <c>run --sandbox</c> registers the package (a real guest package mutation) inside its own
/// locked call before releasing the lease, so two concurrent runs against the same deployment
/// never have their registrations overlap, even though the application launch that follows is
/// deliberately unlocked and may run indefinitely.
/// </summary>
/// <remarks>
/// Driven through <see cref="RunCommand.Handler.ExecuteRunPipelineAsync"/> -- the same internal
/// entry point <c>winapp run --sandbox</c> reaches after CLI parsing and project/folder
/// resolution -- so the exercised sequence is the production
/// <c>PrepareAsync -&gt; DeployAsync -&gt; RegisterPackageAsync (locked) -&gt;
/// ReleaseMutationLease -&gt; launch (unlocked)</c> pipeline, built on the real, file-backed
/// <see cref="TargetMutationLock"/> and the real wire protocol
/// (<see cref="GuestCommandChannel"/>/<see cref="GuestCommandServer"/>,
/// <see cref="GuestRunPlanner.BuildRunArguments"/>). Only the guest OS process each request would
/// start is scripted -- the same boundary <c>SandboxRunTests</c> already accepts as
/// production-equivalent, since a real Windows Sandbox is unavailable in this environment.
/// <para>
/// Each harness gets its own connection lock (a separate, unrelated pre-existing constraint that
/// serializes the agent's single channel across every command, mutating or not) so this suite
/// isolates the one thing under test: the mutation lease. Both harnesses share the same mutation
/// lock and deployment state store, because those are what must serialize two commands against
/// the same target/deployment.
/// </para>
/// </remarks>
[TestClass]
public class PackagedSandboxMutationLockTests : BaseCommandTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private string _root = null!;
    private string _hostFolder = null!;
    private FileInfo _manifest = null!;
    private DirectoryInfo _layout = null!;

    private ITargetMutationLock _mutationLock = null!;
    private IDeploymentStateStore _deploymentStateStore = null!;

    /// <summary>
    /// Raises the process-wide thread-pool floor once for this class.
    /// </summary>
    /// <remarks>
    /// This suite deliberately exercises real, thread-blocking synchronization
    /// (<see cref="TargetMutationLock.TryAcquire"/> polls with <see cref="Thread.Sleep(int)"/>, and
    /// each harness's guest server occupies a worker while running) with two concurrent invocations
    /// per test. Under the full suite's own parallel test load, the default thread pool's slow,
    /// starvation-triggered growth can make an otherwise sub-second test take many minutes to
    /// unblock. Raising the floor once is the standard mitigation and is process-wide, so it can
    /// only help the rest of the suite's own concurrency, never hurt it.
    /// </remarks>
    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 64), Math.Max(completionPortThreads, 64));
    }

    [TestInitialize]
    public void SetupSandboxState()
    {
        _root = TestPaths.TempRoot(nameof(PackagedSandboxMutationLockTests));
        var stateRoot = TestPaths.Under(_root, "state");
        _hostFolder = TestPaths.Under(_root, "host");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(_hostFolder);

        _manifest = new FileInfo(TestPaths.Under(_hostFolder, "appxmanifest.xml"));
        File.WriteAllText(_manifest.FullName, RunCommandTests.TestManifestContent);

        // No runtime dependency markers, so RuntimeRequirementDiscovery reports empty
        // requirements and TargetRuntimeService.EnsureAsync returns immediately without ever
        // needing the mutation lease itself -- only registration and launch are under test here.
        _layout = new DirectoryInfo(TestPaths.Under(_hostFolder, "AppX"));
        _layout.Create();

        var directories = new TargetStateDirectoryProvider(stateRoot);
        _mutationLock = new TargetMutationLock(directories);
        _deploymentStateStore = new DeploymentStateStore(directories);
    }

    [TestCleanup]
    public void CleanupSandboxState()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over a leftover temp directory.
        }
    }

    /// <summary>
    /// Two concurrent, launching <c>run --sandbox --clean</c> invocations against the same
    /// deployment: registration never overlaps, and the second run's registration proceeds as
    /// soon as the first's is done -- even though the first run's application is still "running".
    /// </summary>
    [TestMethod]
    public async Task ConcurrentPackagedSandboxRuns_SameDeployment_SerializeRegistrationNotLaunch()
    {
        var ct = TestContext.CancellationToken;

        await using var harnessA = CreateHarness("a");
        await using var harnessB = CreateHarness("b");

        var taskA = RunAsync(harnessA, noLaunch: false, clean: true, ct);

        var aRegister = await harnessA.Processes.WaitForNextAsync(ct);
        Assert.IsTrue(IsRegisterOnly(aRegister), "A's first guest exec must be the register-only phase.");
        Assert.IsTrue(aRegister.Request.Arguments.Contains("--clean"), "A's registration call must carry --clean.");

        var taskB = RunAsync(harnessB, noLaunch: false, clean: true, ct);

        // B must be blocked behind A's still-open registration lease: no guest process starts for
        // B within a bounded window while A holds it. A bounded wait proving mutual exclusion is
        // the same pattern TargetMutationLockTests already uses (TryAcquire_WhileHeld_TimesOut) --
        // it asserts an invariant, not a guess about when registration finishes.
        Assert.IsFalse(
            await TryWaitForNextAsync(harnessB, TimeSpan.FromMilliseconds(300), ct),
            "B must not start any guest process while A's registration lease is held.");

        // A's registration completes and releases its mutation lease.
        aRegister.Exit(0);

        var aLaunch = await harnessA.Processes.WaitForNextAsync(ct);
        Assert.IsFalse(IsRegisterOnly(aLaunch), "A's second guest exec must be the (unlocked) launch.");
        Assert.IsTrue(
            IsGuestLaunchVerb(aLaunch),
            "The unlocked launch call must be the structurally mutation-incapable guest-launch verb, never the general (registration-capable) run.");
        Assert.IsFalse(aLaunch.Request.Arguments.Contains("--clean"), "The launch call must never re-apply --clean.");

        // With A's registration done and its own app now "running" (aLaunch deliberately left
        // open), B's registration must be able to proceed -- proving the lease does not span the
        // launch/wait that follows registration.
        var bRegister = await harnessB.Processes.WaitForNextAsync(ct);
        Assert.IsTrue(IsRegisterOnly(bRegister), "B's first guest exec must be the register-only phase.");

        bRegister.Exit(0);

        var bLaunch = await harnessB.Processes.WaitForNextAsync(ct);
        Assert.IsFalse(IsRegisterOnly(bLaunch));
        Assert.IsTrue(IsGuestLaunchVerb(bLaunch), "B's unlocked launch call must also be the guest-launch verb.");

        // Only now let both "applications" finish.
        aLaunch.Exit(0);
        bLaunch.Exit(0);

        Assert.AreEqual(0, await taskA);
        Assert.AreEqual(0, await taskB);
    }

    /// <summary>
    /// <c>--no-launch</c> never splits into two guest calls -- there is no launch phase to protect
    /// the lease from -- so the single register-only call is the whole, already-locked operation.
    /// </summary>
    [TestMethod]
    public async Task NoLaunchPackagedSandboxRun_IsOneRegisterOnlyCall_AndReleasesAfterIt()
    {
        var ct = TestContext.CancellationToken;

        await using var harness = CreateHarness("single");

        var task = RunAsync(harness, noLaunch: true, clean: false, ct);

        var register = await harness.Processes.WaitForNextAsync(ct);
        Assert.IsTrue(IsRegisterOnly(register), "The one guest exec for --no-launch must carry --no-launch.");

        register.Exit(0);

        Assert.AreEqual(0, await task);

        // Nothing else started: --no-launch performs no separate launch call.
        Assert.AreEqual(0, harness.Processes.Started.Count);
    }

    /// <summary>
    /// A registration failure never reaches the launch phase, and still releases the lease --
    /// proving the failure/cancellation path is not a leak, and exercising it through a second,
    /// otherwise-blocked run that becomes unblocked the moment the failed lease is released.
    /// </summary>
    [TestMethod]
    public async Task RegistrationFailure_NeverLaunches_AndReleasesTheLease()
    {
        var ct = TestContext.CancellationToken;

        await using var harnessA = CreateHarness("a");
        await using var harnessB = CreateHarness("b");

        var taskA = RunAsync(harnessA, noLaunch: false, clean: false, ct);

        var aRegister = await harnessA.Processes.WaitForNextAsync(ct);
        Assert.IsTrue(IsRegisterOnly(aRegister));

        var taskB = RunAsync(harnessB, noLaunch: false, clean: false, ct);

        Assert.IsFalse(
            await TryWaitForNextAsync(harnessB, TimeSpan.FromMilliseconds(300), ct),
            "B must not start while A's registration lease is held, win or lose.");

        // A's registration itself fails.
        aRegister.Exit(1);

        Assert.AreNotEqual(0, await taskA, "A registration failure must surface as a non-zero exit.");
        Assert.AreEqual(0, harnessA.Processes.Started.Count, "A must never reach the launch phase.");

        // B's registration must now be able to proceed: a failed registration still released the
        // lease rather than leaking it.
        var bRegister = await harnessB.Processes.WaitForNextAsync(ct);
        Assert.IsTrue(IsRegisterOnly(bRegister));

        bRegister.Exit(0);
        var bLaunch = await harnessB.Processes.WaitForNextAsync(ct);
        bLaunch.Exit(0);

        Assert.AreEqual(0, await taskB);
    }

    /// <summary>
    /// <c>--with-alias</c> is a launching, packaged combination too, so it must also split: the
    /// register-only call never carries alias/debug/detach/unregister flags, and the launch call
    /// is what carries the caller's actual option matrix.
    /// </summary>
    [TestMethod]
    public async Task WithAliasPackagedSandboxRun_SplitsRegistrationFromLaunch()
    {
        var ct = TestContext.CancellationToken;

        await using var harness = CreateHarness("alias");

        var task = RunAsync(harness, noLaunch: false, clean: false, ct, withAlias: true);

        var register = await harness.Processes.WaitForNextAsync(ct);
        Assert.IsTrue(IsRegisterOnly(register));
        Assert.IsFalse(register.Request.Arguments.Contains("--with-alias"), "Registration must never carry --with-alias.");

        register.Exit(0);

        var launch = await harness.Processes.WaitForNextAsync(ct);
        Assert.IsFalse(IsRegisterOnly(launch));
        Assert.IsTrue(IsGuestLaunchVerb(launch), "The unlocked launch call must be the guest-launch verb, not the general run.");
        Assert.IsTrue(launch.Request.Arguments.Contains("--with-alias"), "The launch call must carry the caller's --with-alias.");

        launch.Exit(0);

        Assert.AreEqual(0, await task);
    }

    private static bool IsRegisterOnly(FakeGuestProcessHost host) =>
        host.Request.Arguments.Contains("--no-launch");

    /// <summary>
    /// True when a guest exec request is the hidden guest-launch verb -- the structurally
    /// mutation-incapable verify-and-launch call -- rather than the general, registration-capable
    /// <c>run</c>.
    /// </summary>
    private static bool IsGuestLaunchVerb(FakeGuestProcessHost host) =>
        host.Request.Arguments is [WinApp.Cli.ExecutionTargets.Orchestration.GuestLaunchPlanner.Verb, ..];

    /// <summary>
    /// Starts one simulated <c>run --sandbox</c> invocation on its own thread-pool work item.
    /// </summary>
    /// <remarks>
    /// <see cref="ExecutionTargetOrchestrator.PrepareAsync"/> acquires the mutation lock through a
    /// synchronous, thread-blocking poll (<see cref="TargetMutationLock.TryAcquire"/>), and nothing
    /// before it in this call chain performs a real asynchronous yield (the fake backend's
    /// <c>EnsureConnectedAsync</c> returns an already-completed task). Calling this inline would
    /// therefore block the *caller's* thread for as long as the lock is contended, rather than
    /// returning a pending <see cref="Task{TResult}"/> the test can interleave with -- exactly the
    /// kind of accidental synchronous blocking a real winapp process never exhibits, because each
    /// invocation is its own OS process. <see cref="Task.Run(Func{Task})"/> restores that here.
    /// </remarks>
    private static Task<int> RunAsync(
        RunHarness harness,
        bool noLaunch,
        bool clean,
        CancellationToken cancellationToken,
        bool withAlias = false) =>
        Task.Run(
            () => harness.Handler.ExecuteRunPipelineAsync(
                new DirectoryInfo(harness.HostFolder ?? throw new InvalidOperationException()),
                harness.Manifest,
                harness.Layout,
                appArgs: null,
                noLaunch,
                withAlias,
                debugOutput: false,
                unregisterOnExit: false,
                detach: false,
                clean,
                useSymbols: false,
                executable: null,
                isJson: false,
                runtimeArch: null,
                projectFile: null,
                framework: null,
                noRestore: false,
                sandbox: true,
                cancellationToken),
            cancellationToken);

    /// <summary>Waits up to <paramref name="timeout"/> for a host to start, without throwing.</summary>
    private static async Task<bool> TryWaitForNextAsync(
        RunHarness harness, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await harness.Processes.WaitForNextAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private RunHarness CreateHarness(string name) => new(
        TestPaths.Under(_root, $"guest-{name}"),
        TestPaths.Under(_root, $"connection-{name}"),
        _mutationLock,
        _deploymentStateStore,
        _hostFolder,
        _manifest,
        _layout,
        GetRequiredService<IStatusService>(),
        GetRequiredService<ICurrentDirectoryProvider>(),
        GetRequiredService<IPackageRegistrationService>(),
        GetRequiredService<IDebugOutputService>(),
        GetRequiredService<IProjectRunService>(),
        GetRequiredService<Microsoft.Extensions.Logging.ILogger<RunCommand>>());

    /// <summary>
    /// One simulated <c>winapp run --sandbox</c> caller: a real <see cref="RunCommand.Handler"/>
    /// wired to a real <see cref="ExecutionTargetOrchestrator"/> and a real, in-process guest
    /// server, whose scripted guest process is the only faked boundary.
    /// </summary>
    private sealed class RunHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly Task _serverTask;

        public RunHarness(
            string guestManagedRoot,
            string connectionStateRoot,
            ITargetMutationLock mutationLock,
            IDeploymentStateStore deploymentStateStore,
            string hostFolder,
            FileInfo manifest,
            DirectoryInfo layout,
            IStatusService statusService,
            ICurrentDirectoryProvider currentDirectoryProvider,
            IPackageRegistrationService packageRegistrationService,
            IDebugOutputService debugOutputService,
            IProjectRunService projectRunService,
            Microsoft.Extensions.Logging.ILogger<RunCommand> logger)
        {
            HostFolder = hostFolder;
            Manifest = manifest;
            Layout = layout;

            Processes = new FakeGuestProcessHostFactory();

            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                Processes,
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", HasInputDesktop: true)),
                new GuestAgentIdentity("1.0.0", "hash", "x64", 1, 1),
                new GuestFileService(guestManagedRoot),
                guestWinapp: Path.Join(guestManagedRoot, "agent", "current", "winapp.exe"));

            _serverTask = server.RunAsync(_cancellation.Token);

            var backend = new SingleConnectionFakeBackend(pair.Host, Epoch);

            // Deliberately its own connection lock: the agent's real single-channel constraint is
            // orthogonal to the mutation lease this suite exists to verify, and sharing it would
            // force every run to fully serialize end to end, masking the very overlap this test
            // needs to be able to observe.
            var orchestrator = new ExecutionTargetOrchestrator(
                backend,
                mutationLock,
                new TargetConnectionLock(new TargetStateDirectoryProvider(connectionStateRoot)));

            var runner = new GuestApplicationRunner(new TargetDeploymentService(deploymentStateStore));

            var runtimeService = new TargetRuntimeService(
                new RuntimeProvisionStateStore(new TargetStateDirectoryProvider(
                    Path.Join(connectionStateRoot, "runtime-unused"))),
                new UnusedRuntimePayloadResolver(),
                new UnusedRuntimeFrameworkResolver());

            var identity = new MsixIdentityResult("SbxMutationLockTestPackage", "CN=SbxMutationLockTests", "App");

            Handler = new RunCommand.Handler(
                new FakeMsixService { FakeIdentityResult = identity },
                new FakeAppLauncherService(),
                packageRegistrationService,
                debugOutputService,
                currentDirectoryProvider,
                new Spectre.Console.Testing.TestConsole(),
                statusService,
                projectRunService,
                orchestrator,
                runner,
                runtimeService,
                logger);
        }

        public string HostFolder { get; }

        public FileInfo Manifest { get; }

        public DirectoryInfo Layout { get; }

        public FakeGuestProcessHostFactory Processes { get; }

        public RunCommand.Handler Handler { get; }

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

            _cancellation.Dispose();
        }
    }

    /// <summary>A backend that reports one connection, to one scripted guest server, always.</summary>
    private sealed class SingleConnectionFakeBackend(IGuestTransport transport, ExecutionTargetEpoch epoch)
        : IExecutionTargetBackend
    {
        public ExecutionTargetRef Target => ExecutionTargetRef.WindowsSandboxDefault;

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TargetSupportResult.Supported);

        public Task<TargetConnection> EnsureConnectedAsync(
            EnsureTargetOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new TargetConnection(epoch, transport, Reused: false));

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() =>
            new Dictionary<string, string> { ["sandboxId"] = "sandbox-1" };
    }

    /// <summary>Never called: the test layout carries no runtime dependency markers.</summary>
    private sealed class UnusedRuntimePayloadResolver : IRuntimePayloadResolver
    {
        public Task<IReadOnlyList<ResolvedRuntimePackage>> ResolveAsync(
            RuntimeRequirements requirements,
            DirectoryInfo projectRoot,
            TaskContext taskContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected to be called: the test layout declares no runtime requirements.");
    }

    /// <summary>Never called: the test layout carries no runtime dependency markers.</summary>
    private sealed class UnusedRuntimeFrameworkResolver : IRuntimeFrameworkResolver
    {
        public Task<RuntimeFrameworkPayload?> ResolveAsync(
            RuntimeFrameworkRequirement requirement,
            DirectoryInfo projectRoot,
            TaskContext taskContext,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected to be called: the test layout declares no runtime requirements.");
    }
}
