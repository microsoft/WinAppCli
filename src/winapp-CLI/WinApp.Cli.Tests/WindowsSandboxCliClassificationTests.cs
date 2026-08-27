// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for how <c>wsb.exe</c> failures are classified and how the executable is bound.
/// </summary>
/// <remarks>
/// Two <c>wsb</c> HRESULTs mean something specific enough that reporting them as a generic start
/// failure sends the user somewhere useless, and a third behaviour — availability latched for the
/// life of the process — made a host that had just been set up look permanently unusable.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class WindowsSandboxCliClassificationTests
{
    private RecordingProcessRunner _runner = null!;
    private WindowsSandboxCli _cli = null!;

    [TestInitialize]
    public void Setup()
    {
        _runner = new RecordingProcessRunner();
        _cli = new WindowsSandboxCli(_runner);

        // Bound explicitly so these tests never depend on whether the machine running them happens
        // to have Windows Sandbox installed.
        _cli.UseExecutable(Path.Join(Environment.SystemDirectory, "wsb.exe"));
    }

    [TestMethod]
    public void UseExecutable_RefusesARelativePath()
    {
        // A relative path resolves against the current directory, which is exactly the hijack that
        // absolute resolution exists to prevent.
        Assert.ThrowsExactly<ArgumentException>(() => new WindowsSandboxCli(_runner).UseExecutable(@"tools\wsb.exe"));
    }

    [TestMethod]
    public void UseExecutable_MakesTheClientAvailableWithoutRe_resolving()
    {
        // Availability must be able to become true inside a single command, once setup has finished.
        // A Lazy<T> latched at first use reported "not installed" for the rest of the process.
        var cli = new WindowsSandboxCli(_runner);
        cli.UseExecutable(@"C:\Windows\System32\wsb.exe");

        Assert.IsTrue(cli.IsAvailable);
    }

    [TestMethod]
    public async Task Start_PassesTheCallerAssignedId()
    {
        _runner.Result = new ProcessRunResult(0, """{"Id":"caller-assigned"}""", string.Empty);

        var id = await _cli.StartAsync("caller-assigned", configuration: null, TestContext.CancellationToken);

        Assert.AreEqual("caller-assigned", id);

        var arguments = _runner.Requests.Single().Arguments.ToList();
        var idIndex = arguments.IndexOf("--id");

        Assert.IsGreaterThanOrEqualTo(0, idIndex, "wsb start must be given the ID winapp assigned.");
        Assert.AreEqual("caller-assigned", arguments[idIndex + 1]);
    }

    [TestMethod]
    public async Task Start_ThatReportsNoId_SaysWhichIdItAskedFor()
    {
        // The caller needs the requested ID in the failure so it can reconcile that exact instance
        // rather than guess from a list.
        _runner.Result = new ProcessRunResult(0, "{}", string.Empty);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _cli.StartAsync("caller-assigned", configuration: null, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);
        Assert.AreEqual("caller-assigned", failure.Error.Context!["requestedId"]);
    }

    [TestMethod]
    public async Task Start_ThatFailsWithAnHResult_CarriesItInContext()
    {
        _runner.Result = new ProcessRunResult(1, string.Empty, "Sandbox failed to start. (0x80070002)");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _cli.StartAsync("caller-assigned", configuration: null, TestContext.CancellationToken));

        Assert.AreEqual("0x80070002", failure.Error.Context![WsbHResult.ContextKey]);
        Assert.AreEqual("start", failure.Error.Context["wsbVerb"]);
    }

    [TestMethod]
    public async Task IsResolvable_IsFalseRatherThanThrowingWhenTheGuestDoesNotAnswer()
    {
        _runner.Result = new ProcessRunResult(0, """{"Networks":[]}""", string.Empty);

        Assert.IsFalse(await _cli.IsResolvableAsync("some-id", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task IsResolvable_IsTrueWhenTheGuestReportsAnAddress()
    {
        _runner.Result = new ProcessRunResult(0, """{"Networks":[{"IpV4Address":"172.27.0.2"}]}""", string.Empty);

        Assert.IsTrue(await _cli.IsResolvableAsync("some-id", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UnavailableClient_DoesNotTellTheUserToEnableAnOptionalFeature()
    {
        // Regression: this message used to offer Enable-WindowsOptionalFeature unconditionally,
        // which is wrong on a host whose feature is already enabled. The setup runner decides why a
        // host is not ready; this failure must not contradict it with a guess.
        var cli = new WindowsSandboxCli(new RecordingProcessRunner())
        {
            // Nothing bound and nothing resolvable, which is what a host mid-setup looks like.
        };

        var probeResolved = WindowsSandboxHostProbe.ResolveTrustedAlias();
        if (probeResolved is not null)
        {
            Assert.Inconclusive("wsb.exe is installed on this machine, so the unavailable path cannot be reached.");
            return;
        }

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => cli.ListAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);
        Assert.IsNull(failure.Error.NextCommand, "A guess about why the host is not ready must not be offered here.");
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

/// <summary>Tests for <see cref="WsbHResult"/>: recognising the two statuses that change behaviour.</summary>
[TestClass]
public class WsbHResultTests
{
    [TestMethod]
    public void ExitCode_ThatIsItselfAnHResult_IsUsedDirectly()
    {
        var result = new ProcessRunResult(unchecked((int)0x800401F6), string.Empty, string.Empty);

        Assert.AreEqual(WsbHResult.AppSingleUse, WsbHResult.Extract(result));
    }

    [TestMethod]
    public void StandardError_CarryingAnHResult_IsRecognised()
    {
        var result = new ProcessRunResult(1, string.Empty, "The system cannot find the file specified. (0x80070002)");

        Assert.AreEqual(WsbHResult.FileNotFound, WsbHResult.Extract(result));
    }

    [TestMethod]
    public void StandardOutput_IsScannedWhenStandardErrorIsSilent()
    {
        var result = new ProcessRunResult(1, "Error 0x800401F6 occurred.", string.Empty);

        Assert.AreEqual(WsbHResult.AppSingleUse, WsbHResult.Extract(result));
    }

    [TestMethod]
    public void AnOrdinaryHexNumber_IsNotMistakenForAStatusCode()
    {
        // Only a value with the failure bit set is a status. A plain eight-digit hexadecimal number
        // in a message -- an identifier, an offset -- must not be classified as one, because doing so
        // would route an unrelated failure into singleton-reuse or partial-start recovery.
        var result = new ProcessRunResult(1, string.Empty, "Instance 0x00ABCDEF was not found.");

        Assert.IsNull(WsbHResult.Extract(result));
    }

    [TestMethod]
    public void ATruncatedHexValue_IsIgnored()
    {
        var result = new ProcessRunResult(1, string.Empty, "code 0x8007");

        Assert.IsNull(WsbHResult.Extract(result));
    }

    [TestMethod]
    public void NoHResultAnywhere_IsNull()
    {
        Assert.IsNull(WsbHResult.Extract(new ProcessRunResult(1, "no status here", "nor here")));
    }

    [TestMethod]
    public void Format_MatchesTheShapeWsbPrints()
    {
        Assert.AreEqual("0x80070002", WsbHResult.Format(WsbHResult.FileNotFound));
        Assert.AreEqual("0x800401F6", WsbHResult.Format(WsbHResult.AppSingleUse));
    }
}
