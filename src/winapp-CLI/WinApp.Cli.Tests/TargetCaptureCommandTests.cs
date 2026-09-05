// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// The three <c>winapp target</c> verbs that report on, photograph, and film a target's own desktop.
/// </summary>
/// <remarks>
/// What is being checked throughout is the boundary: a target that draws no desktop on this machine
/// says so instead of capturing something else, a capture never foregrounds the window it captures,
/// and <c>--json</c> keeps stdout to one parsable document.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class TargetCaptureCommandTests
{
    private const nint DesktopHwnd = 0x1234;
    private const int DesktopProcessId = 7788;

    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup() => _root = TestPaths.TempRoot(nameof(TargetCaptureCommandTests));

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

    // ---- snapshot ------------------------------------------------------------------

    [TestMethod]
    public async Task Snapshot_ReportsReadinessTheDesktopWindowAndWhatIsOnIt()
    {
        await using var harness = new Harness(GuestWindows(Window(0x20, "Calculator", 800, 600)));
        var console = new TestConsole();

        var exitCode = await RunSnapshotAsync(harness, console, "sandbox");

        Assert.AreEqual(0, exitCode);

        var report = console.Output;
        StringAssert.Contains(report, "real input yes");
        StringAssert.Contains(report, $"HWND {DesktopHwnd}");
        StringAssert.Contains(report, "Calculator");
    }

    /// <summary>
    /// The whole point of a report is that it describes what was already there. A snapshot that
    /// prepared the target would create the Sandbox it then cheerfully reports as running, and the
    /// caller asking "is one up?" would be told yes because it asked.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_NeverPreparesTheTarget()
    {
        await using var harness = new Harness(GuestWindows());

        Assert.AreEqual(0, await RunSnapshotAsync(harness, new TestConsole(), "sandbox"));

        Assert.AreEqual(0, harness.Backend.EnsureCalls, "A snapshot must never create, connect, or repair.");
        Assert.AreEqual(1, harness.Backend.AttachCalls);
    }

    [TestMethod]
    public async Task Snapshot_NothingRunning_SaysSoAndSucceeds()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.Running = false;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox"));

        StringAssert.Contains(console.Output, "not running");
        StringAssert.Contains(console.Output, "winapp run . --on sandbox");
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    [TestMethod]
    public async Task Snapshot_NothingRunning_Json_ReportsItWithoutAnEpochOrCapabilities()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.Running = false;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsFalse(output.Running);
        Assert.IsFalse(output.Attached);
        Assert.IsNull(output.Capabilities);
        Assert.IsNull(output.ExecutionTarget.Epoch);
        Assert.IsFalse(output.Desktop.Rendered);
        Assert.AreEqual(0, output.Deployments.Length);
        Assert.IsNull(output.Windows);
    }

    /// <summary>
    /// A running instance whose agent is not answering is a state a caller needs reported, not
    /// repaired: repairing it is what <c>winapp run</c> does, and doing it here would replace the
    /// agent the caller was asking about.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_RunningButAgentSilent_ReportsWhatTheHostKnowsAndDoesNotRepair()
    {
        await using var harness = new Harness(GuestWindows());
        harness.Backend.AgentAnswers = false;
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsTrue(output.Running);
        Assert.IsFalse(output.Attached);
        Assert.IsNull(output.Capabilities);
        Assert.IsTrue(output.Desktop.Rendered, "The client window is a host-side fact, so it is still reported.");
        Assert.IsNull(output.Windows);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    [TestMethod]
    public async Task Snapshot_Json_IsOneDocumentDescribingTheTargetAndItsWindows()
    {
        await using var harness = new Harness(GuestWindows(
            Window(0x20, "Calculator", 800, 600),
            Window(0x21, "Settings", 400, 300, foreground: true)));
        var console = new TestConsole();

        var exitCode = await RunSnapshotAsync(harness, console, "sandbox", "--json");

        Assert.AreEqual(0, exitCode);

        var output = Deserialize(console.Output);

        Assert.AreEqual("sandbox", output.ExecutionTarget.Kind);
        Assert.AreEqual(Epoch.Value, output.ExecutionTarget.Epoch);
        Assert.IsTrue(output.Capabilities!.SupportsRealInput);
        Assert.IsTrue(output.Desktop.Rendered);
        Assert.AreEqual(DesktopHwnd, output.Desktop.WindowHandle);
        Assert.AreEqual(DesktopProcessId, output.Desktop.ProcessId);
        Assert.AreEqual(2, output.WindowCount);
        Assert.IsFalse(output.WindowsTruncated);

        // The window the user is actually looking at leads, because it is the one a caller acts on.
        Assert.AreEqual("Settings", output.Windows![0].Title);
    }

    [TestMethod]
    public async Task Snapshot_ManyGuestWindows_ReportsTheTotalAndSaysItListedFewer()
    {
        var windows = Enumerable.Range(0, TargetSnapshotCommand.MaxWindows + 10)
            .Select(index => Window(0x100 + index, $"Window {index}", 100 + index, 100))
            .ToArray();

        await using var harness = new Harness(GuestWindows(windows));
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.AreEqual(windows.Length, output.WindowCount);
        Assert.AreEqual(TargetSnapshotCommand.MaxWindows, output.Windows!.Length);
        Assert.IsTrue(output.WindowsTruncated);
    }

    /// <summary>
    /// The window list is the one part of a snapshot that needs a live desktop. Losing it must not
    /// cost the caller the readiness and deployment facts that explain why it is missing.
    /// </summary>
    [TestMethod]
    public async Task Snapshot_GuestCannotListItsWindows_StillReportsEverythingElse()
    {
        await using var harness = new Harness(stdout: "", exitCode: 1);
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsNull(output.Windows);
        Assert.IsTrue(output.Desktop.Rendered);
        Assert.IsTrue(output.Capabilities!.SupportsRealInput);
    }

    [TestMethod]
    public async Task Snapshot_TargetThatDrawsNoDesktopHere_SaysSoInsteadOfFailing()
    {
        await using var harness = new Harness(GuestWindows(), rendersDesktop: false);
        var console = new TestConsole();

        Assert.AreEqual(0, await RunSnapshotAsync(harness, console, "sandbox", "--json"));

        var output = Deserialize(console.Output);

        Assert.IsFalse(output.Desktop.Rendered);
        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, output.Desktop.Unavailable);
    }

    [TestMethod]
    public async Task Snapshot_UnknownTarget_IsRefusedBeforeTheTargetIsTouched()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();

        Assert.AreEqual(
            TargetOutput.InvalidCommandLineExitCode,
            await RunSnapshotAsync(harness, console, "vm"));

        Assert.AreEqual(string.Empty, console.Output);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    // ---- screenshot ----------------------------------------------------------------

    [TestMethod]
    public async Task Screenshot_WritesAPngAndReportsWhichTargetItCameFrom()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var capture = new FakeWindowCapture
        {
            CaptureWithoutActivationOverride = _ => (new byte[4 * 3 * 2], 3, 2),
        };
        var destination = TestPaths.Under(_root, "shots", "desktop.png");

        var exitCode = await RunScreenshotAsync(
            harness, console, capture, "sandbox", "-o", destination, "--json");

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(File.Exists(destination));

        var payload = JsonSerializer.Deserialize(
            console.Output.Trim(), UiJsonContext.Default.UiScreenshotResult)!;

        Assert.AreEqual(destination, payload.FilePath);
        Assert.AreEqual(3, payload.Width);
        Assert.AreEqual(2, payload.Height);
        Assert.AreEqual(DesktopHwnd, payload.Hwnd);
        Assert.AreEqual(DesktopProcessId, payload.ProcessId);
        Assert.AreEqual(Epoch.Value, payload.ExecutionTarget!.Epoch);
    }

    /// <summary>
    /// A managed client window is parked off-screen on purpose. The ordinary screenshot path
    /// recovers from a blank frame by foregrounding the window and trying again, which would drag
    /// the target's window back onto the user's screen mid-command.
    /// </summary>
    [TestMethod]
    public async Task Screenshot_CapturesTheDesktopWindowThroughTheNoActivationPathOnly()
    {
        await using var harness = new Harness(GuestWindows());
        var capture = new FakeWindowCapture();

        Assert.AreEqual(
            0,
            await RunScreenshotAsync(
                harness,
                new TestConsole(),
                capture,
                "sandbox",
                "-o",
                TestPaths.Under(_root, "desktop.png")));

        CollectionAssert.AreEqual(new[] { DesktopHwnd }, capture.CapturedWithoutActivation);
    }

    /// <summary>
    /// The command promises it takes no focus. When the only way left to get pixels would be to
    /// bring the window to the front, the honest outcome is to fail and say so.
    /// </summary>
    [TestMethod]
    public async Task Screenshot_WindowCannotBeCapturedWhereItSits_FailsInsteadOfForegroundingIt()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var destination = TestPaths.Under(_root, "desktop.png");
        var capture = new FakeWindowCapture { CaptureWithoutActivationOverride = _ => null };

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunScreenshotAsync(
            harness, console, capture, "sandbox", "-o", destination, "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output, "A failure must not put anything on stdout under --json.");
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.ArtifactFailed);
        StringAssert.Contains(stderr, "without bringing its window to the front");
        Assert.IsFalse(File.Exists(destination), "Nothing was captured, so nothing is published.");
    }

    [TestMethod]
    public async Task Screenshot_TargetThatDrawsNoDesktopHere_FailsAndPointsAtTheGuestSideVerb()
    {
        await using var harness = new Harness(GuestWindows(), rendersDesktop: false);
        var console = new TestConsole();
        var destination = TestPaths.Under(_root, "desktop.png");

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunScreenshotAsync(
            harness, console, new FakeWindowCapture(), "sandbox", "-o", destination, "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output, "A failure must not put anything on stdout under --json.");
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.Unsupported);
        StringAssert.Contains(stderr, "winapp ui screenshot");
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task Screenshot_UnknownTarget_IsRefusedBeforeAnythingIsCaptured()
    {
        await using var harness = new Harness(GuestWindows());
        var capture = new FakeWindowCapture();

        Assert.AreEqual(
            TargetOutput.InvalidCommandLineExitCode,
            await RunScreenshotAsync(
                harness, new TestConsole(), capture, "vm", "-o", TestPaths.Under(_root, "desktop.png")));

        Assert.AreEqual(0, capture.CapturedWithoutActivation.Count);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    // ---- record --------------------------------------------------------------------

    [TestMethod]
    public async Task Record_RecordsTheDesktopWindowThroughTheOrdinaryPipeline()
    {
        await using var harness = new Harness(GuestWindows());
        var console = new TestConsole();
        var recording = new FakeUiRecordingService();
        var destination = TestPaths.Under(_root, "desktop.mp4");

        var exitCode = await RunRecordAsync(
            harness, console, recording, "sandbox", "-o", destination, "--duration-sec", "1", "--json");

        Assert.AreEqual(0, exitCode);

        var payload = JsonSerializer.Deserialize(
            console.Output.Trim(), UiJsonContext.Default.UiRecordResult)!;

        Assert.AreEqual(destination, payload.Path);
        Assert.AreEqual("h264", payload.Codec);
        Assert.AreEqual(Epoch.Value, payload.ExecutionTarget!.Epoch);

        Assert.AreEqual(DesktopHwnd, recording.LastTarget!.WindowHandle);
        Assert.IsNull(recording.LastElementId);
        Assert.IsFalse(recording.LastRecordOptions!.CaptureScreen);
        Assert.AreEqual(1, recording.LastRecordOptions.DurationSec);
    }

    /// <summary>
    /// A guest connection is a scarce, single-occupant channel, and a recording can legitimately run
    /// for hours. Everything a recording needs is a host window handle read once up front, so the
    /// channel must be handed back before the long wait starts.
    /// </summary>
    [TestMethod]
    public async Task Record_ReleasesTheGuestChannelBeforeTheLongWaitBegins()
    {
        await using var harness = new Harness(GuestWindows());
        var recording = new FakeUiRecordingService();
        bool? connectedWhileRecording = null;
        recording.WhileRecording = () =>
            connectedWhileRecording = harness.Backend.LastHostTransport?.IsConnected;

        var exitCode = await RunRecordAsync(
            harness,
            new TestConsole(),
            recording,
            "sandbox",
            "-o",
            TestPaths.Under(_root, "desktop.mp4"),
            "--duration-sec",
            "1");

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(harness.Backend.LastHostTransport, "The target was prepared, so a channel was opened.");
        Assert.AreEqual(false, connectedWhileRecording, "The channel is still held while recording.");
    }

    [TestMethod]
    public async Task Record_TargetThatDrawsNoDesktopHere_FailsBeforeRecordingStarts()
    {
        await using var harness = new Harness(GuestWindows(), rendersDesktop: false);
        var console = new TestConsole();
        var recording = new FakeUiRecordingService();

        var (exitCode, stderr) = await CaptureStandardErrorAsync(() => RunRecordAsync(
            harness,
            console,
            recording,
            "sandbox",
            "-o",
            TestPaths.Under(_root, "desktop.mp4"),
            "--duration-sec",
            "1",
            "--json"));

        Assert.AreEqual(TargetOutput.TargetInfrastructureExitCode, exitCode);
        Assert.AreEqual(string.Empty, console.Output);
        StringAssert.Contains(stderr, ExecutionTargetErrorCodes.Unsupported);
        Assert.IsNull(recording.LastRecordOptions);
    }

    [TestMethod]
    public async Task Record_UnknownTarget_IsRefusedBeforeAnythingIsRecorded()
    {
        await using var harness = new Harness(GuestWindows());
        var recording = new FakeUiRecordingService();

        Assert.AreEqual(
            TargetOutput.InvalidCommandLineExitCode,
            await RunRecordAsync(
                harness,
                new TestConsole(),
                recording,
                "vm",
                "-o",
                TestPaths.Under(_root, "desktop.mp4"),
                "--duration-sec",
                "1"));

        Assert.IsNull(recording.LastRecordOptions);
        Assert.AreEqual(0, harness.Backend.EnsureCalls);
    }

    // ---- harness -------------------------------------------------------------------

    private Task<int> RunSnapshotAsync(Harness harness, IAnsiConsole console, params string[] arguments)
    {
        var command = new TargetSnapshotCommand();
        var handler = new TargetSnapshotCommand.Handler(
            harness.Orchestrator, new EmptyDeploymentStateStore(), console);

        return handler.InvokeAsync(Parse(command, arguments), TestContext.CancellationToken);
    }

    private Task<int> RunScreenshotAsync(
        Harness harness,
        IAnsiConsole console,
        IWindowCapture capture,
        params string[] arguments)
    {
        var command = new TargetScreenshotCommand();
        var handler = new TargetScreenshotCommand.Handler(
            harness.Orchestrator, capture, console, NullLogger<TargetScreenshotCommand>.Instance);

        return handler.InvokeAsync(Parse(command, arguments), TestContext.CancellationToken);
    }

    private Task<int> RunRecordAsync(
        Harness harness,
        IAnsiConsole console,
        IUiRecordingService recording,
        params string[] arguments)
    {
        var command = new TargetRecordCommand();
        var handler = new TargetRecordCommand.Handler(
            harness.Orchestrator,
            new FakeUiTargetResolver(),
            recording,
            console,
            NullLogger<UiRecordCommand>.Instance);

        return handler.InvokeAsync(Parse(command, arguments), TestContext.CancellationToken);
    }

    /// <summary>Parses against a root that carries the global options every verb reads.</summary>
    private static ParseResult Parse(Command command, params string[] arguments)
    {
        var root = new RootCommand();
        root.Options.Add(WinAppRootCommand.JsonOption);
        root.Options.Add(WinAppRootCommand.QuietOption);
        root.Subcommands.Add(command);

        return root.Parse([command.Name, .. arguments]);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with stderr captured, because failures are reported there.
    /// </summary>
    /// <remarks>
    /// Redirects the process-wide stream, which is why this class is <c>[DoNotParallelize]</c>: two
    /// tests redirecting at once would restore each other's writer and lose the output.
    /// </remarks>
    private static async Task<(int ExitCode, string StandardError)> CaptureStandardErrorAsync(Func<Task<int>> action)
    {
        var original = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            return (await action(), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static TargetSnapshotOutput Deserialize(string stdout) =>
        JsonSerializer.Deserialize(stdout.Trim(), TargetJsonContext.Default.TargetSnapshotOutput)!;

    private static WindowInfo Window(long hwnd, string title, int width, int height, bool foreground = false) =>
        new()
        {
            Hwnd = hwnd,
            ProcessId = 100,
            ProcessName = "app",
            Title = title,
            Width = width,
            Height = height,
            IsForeground = foreground,
        };

    private static string GuestWindows(params WindowInfo[] windows) =>
        JsonSerializer.Serialize(windows, UiJsonContext.Default.WindowInfoArray);

    /// <summary>A backend, guest agent, and orchestrator wired together over one in-memory transport.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
        private readonly List<Task> _servers = [];
        private readonly List<IDisposable> _leases = [];

        public Harness(string stdout, int exitCode = 0, bool rendersDesktop = true)
        {
            Backend = rendersDesktop
                ? new RenderingBackend(this, stdout, exitCode)
                : new FakeBackend(this, stdout, exitCode);

            Orchestrator = new ExecutionTargetOrchestrator(Backend, new FakeLock(this), new FakeLock(this));
        }
        public FakeBackend Backend { get; }

        public ExecutionTargetOrchestrator Orchestrator { get; }

        public CancellationToken ServerToken => _cancellation.Token;

        public void Track(Task server) => _servers.Add(server);

        public void Track(IDisposable lease) => _leases.Add(lease);

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            foreach (var server in _servers)
            {
                try
                {
                    await server;
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }

            foreach (var lease in _leases)
            {
                lease.Dispose();
            }

            _cancellation.Dispose();
        }
    }

    /// <summary>A target that runs commands but draws nothing on this machine.</summary>
    private class FakeBackend(Harness harness, string stdout, int exitCode)
        : IExecutionTargetBackend, IInspectableTarget
    {
        public ExecutionTargetRef Target => WindowsSandboxTarget.Default;

        /// <summary>How many times a command asked for a prepared, connected target.</summary>
        /// <remarks>
        /// The measure of "did this command change anything?": preparing is what creates, connects,
        /// and repairs. A snapshot that leaves this at zero cannot have started a Sandbox.
        /// </remarks>
        public int EnsureCalls { get; private set; }

        /// <summary>How many times a command asked what was already there.</summary>
        public int AttachCalls { get; private set; }

        /// <summary>Whether the managed target is running at all.</summary>
        public bool Running { get; set; } = true;

        /// <summary>Whether the running target's agent answers an inspect-only attach.</summary>
        public bool AgentAnswers { get; set; } = true;

        /// <summary>The host end of the last channel handed out, so a test can watch it close.</summary>
        public IGuestTransport? LastHostTransport { get; private set; }

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TargetSupportResult.Supported);

        public Task<TargetConnection> EnsureConnectedAsync(
            EnsureTargetOptions options,
            CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.FromResult(Connect());
        }

        public Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken)
        {
            AttachCalls++;

            if (!Running)
            {
                return Task.FromResult(TargetAttachment.NotRunning);
            }

            return Task.FromResult(AgentAnswers
                ? new TargetAttachment(true, Epoch, Connect())
                : new TargetAttachment(true, Epoch, null));
        }

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() =>
            new Dictionary<string, string> { ["sandboxId"] = "sandbox-1" };

        private TargetConnection Connect()
        {
            var pair = new LoopbackTransportPair();

            var server = new GuestCommandServer(
                pair.Guest,
                Epoch,
                new ScriptedGuestWinapp(stdout, exitCode),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", HasInputDesktop: true)),
                new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
                guestWinapp: @"C:\WinAppGuest\winapp.exe");

            harness.Track(server.RunAsync(harness.ServerToken));

            LastHostTransport = pair.Host;
            return new TargetConnection(Epoch, pair.Host, Reused: true);
        }
    }

    /// <summary>The same target, but one whose desktop this machine draws in a window.</summary>
    private sealed class RenderingBackend(Harness harness, string stdout, int exitCode)
        : FakeBackend(harness, stdout, exitCode), IHostRenderedTarget
    {
        public TargetDesktopSurface ResolveDesktopSurface() =>
            new(DesktopHwnd, DesktopProcessId, "WindowsSandboxRemoteSession", Adopted: false);
    }

    /// <summary>A guest winapp whose answer is scripted rather than run.</summary>
    private sealed class ScriptedGuestWinapp(string stdout, int exitCode) : IGuestProcessHostFactory
    {
        public IGuestProcessHost Start(
            GuestExecRequest request,
            Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput)
        {
            var host = new FakeGuestProcessHost(request, onOutput, processId: 4321);

            if (stdout.Length > 0)
            {
                host.Emit(GuestStreamId.StandardOutput, stdout);
            }

            host.Exit(exitCode);
            return host;
        }
    }

    /// <summary>A lock that always grants, standing in for both file-backed locks.</summary>
    private sealed class FakeLock(Harness harness) : ITargetMutationLock, ITargetConnectionLock
    {
        TargetMutationLease? ITargetMutationLock.TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            new(NewStream(), wasAbandoned: false);

        TargetConnectionLease? ITargetConnectionLock.TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            new(NewStream());

        private FileStream NewStream()
        {
            var stream = new FileStream(
                TestPaths.TempFile("target-capture-lock", ".lock"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            harness.Track(stream);
            return stream;
        }
    }

    /// <summary>A target with nothing deployed, so a snapshot reports capabilities and windows only.</summary>
    private sealed class EmptyDeploymentStateStore : IDeploymentStateStore
    {
        public DeploymentState? Read(ExecutionTargetRef target, string deploymentId) => null;

        public DeploymentState Commit(ExecutionTargetRef target, DeploymentState state, long expectedRevision) =>
            throw new NotSupportedException("A snapshot never writes deployment state.");

        public void Clear(ExecutionTargetRef target, string deploymentId) =>
            throw new NotSupportedException("A snapshot never clears deployment state.");

        public IReadOnlyList<DeploymentState> List(ExecutionTargetRef target) => [];
    }
}
