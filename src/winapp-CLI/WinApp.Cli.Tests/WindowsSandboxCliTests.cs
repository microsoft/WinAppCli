// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>Records what was launched so the resolved executable path can be asserted.</summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    public List<ProcessRunRequest> Requests { get; } = [];

    public ProcessRunResult Result { get; set; } = new(0, "{}", string.Empty);

    public Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

/// <summary>
/// Regression tests for how winapp invokes <c>wsb.exe</c>.
/// </summary>
/// <remarks>
/// Both behaviours here were found by independent review of PR #779 and reproduced empirically.
/// </remarks>
[TestClass]
public class WindowsSandboxCliTests
{
    private RecordingProcessRunner _runner = null!;
    private WindowsSandboxCli _cli = null!;

    [TestInitialize]
    public void Setup()
    {
        _runner = new RecordingProcessRunner();
        _cli = new WindowsSandboxCli(_runner);
    }

    [TestMethod]
    public void ResolveExecutable_ReturnsAFullyQualifiedPath()
    {
        var resolved = WindowsSandboxCli.ResolveExecutable();

        if (resolved is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        Assert.IsTrue(Path.IsPathFullyQualified(resolved), "wsb.exe must be resolved to an absolute path.");
        Assert.IsTrue(File.Exists(resolved));
    }

    [TestMethod]
    public void ResolveExecutable_IgnoresRelativePathEntriesSoTheCurrentDirectoryCannotWin()
    {
        // Regression: launching by bare name lets CreateProcess search the application and current
        // directories before PATH, and a relative PATH entry resolves against the current directory
        // too. Either route lets a wsb.exe dropped into a repository a developer happens to be
        // sitting in take over Sandbox control.
        var decoyDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"wsb-decoy-{Guid.NewGuid():N}"));
        decoyDirectory.Create();

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalCurrent = Directory.GetCurrentDirectory();

        try
        {
            File.WriteAllText(Path.Combine(decoyDirectory.FullName, WindowsSandboxCli.ExecutableName), "decoy");
            Directory.SetCurrentDirectory(decoyDirectory.FullName);

            // A relative entry that would resolve to the decoy through the current directory.
            Environment.SetEnvironmentVariable("PATH", ".");

            Assert.IsNull(
                WindowsSandboxCli.ResolveExecutable(),
                "A relative PATH entry must never resolve wsb.exe.");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrent);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            decoyDirectory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Commands_LaunchTheResolvedAbsolutePathNotABareName()
    {
        if (WindowsSandboxCli.ResolveExecutable() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        _runner.Result = new ProcessRunResult(0, """{ "WindowsSandboxEnvironments": [] }""", string.Empty);

        await _cli.ListAsync(TestContext.CancellationTokenSource.Token);

        var launched = _runner.Requests.Single().FileName;
        Assert.IsTrue(
            Path.IsPathFullyQualified(launched),
            $"Expected an absolute path, but winapp launched '{launched}'.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WsbDiagnosticOnStderr_IsAnInfrastructureFailureNotAGuestExitCode()
    {
        // Regression: wsb exec never relays the guest's stdout or stderr, so anything on stderr is
        // wsb's own diagnostic and means the command was never dispatched. Returning that exit code
        // as a guest result lets an infrastructure failure impersonate an application outcome --
        // reproduced by passing a nonexistent Sandbox ID, which yields an HRESULT-like code.
        if (WindowsSandboxCli.ResolveExecutable() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        _runner.Result = new ProcessRunResult(
            unchecked((int)0x80070490),
            string.Empty,
            "Element not found. (0x80070490)");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _cli.ExecuteAsync("missing-id", "cmd /c exit 0", null, asSystem: false, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        Assert.AreEqual("missing-id", failure.Error.Context!["sandboxId"]);
    }

    [TestMethod]
    public async Task ExecuteAsync_CleanDispatch_ReturnsTheGuestExitCode()
    {
        if (WindowsSandboxCli.ResolveExecutable() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        // No wsb diagnostic means the command really was dispatched, so its exit code is the
        // guest's and must survive intact.
        _runner.Result = new ProcessRunResult(42, string.Empty, string.Empty);

        var exitCode = await _cli.ExecuteAsync(
            "sandbox-1", "cmd /c exit 42", null, asSystem: false, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(42, exitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_PassesRunAsAndPreservesArgumentsAsSeparateValues()
    {
        if (WindowsSandboxCli.ResolveExecutable() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        await _cli.ExecuteAsync("sandbox-1", "cmd /c exit 0", @"C:\Work", asSystem: true, TestContext.CancellationTokenSource.Token);

        var arguments = _runner.Requests.Single().Arguments;

        // Values travel as their own list entries, so a path can never be smuggled in as an option.
        CollectionAssert.Contains(arguments.ToList(), "System");
        CollectionAssert.Contains(arguments.ToList(), @"C:\Work");
        CollectionAssert.Contains(arguments.ToList(), "cmd /c exit 0");
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}
