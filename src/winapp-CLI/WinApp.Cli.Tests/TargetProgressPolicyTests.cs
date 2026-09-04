// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// <c>--quiet</c> must silence target progress everywhere, and silence nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Preparing a target narrates itself: starting the Sandbox, staging the agent, provisioning
/// runtimes, deploying, registering, launching. That narration is the difference between a slow
/// command and an apparent hang, so it is on by default — and it is exactly what <c>--quiet</c>
/// exists to remove.
/// </para>
/// <para>
/// The decision lives in one place because it used to live in two. Progress is emitted from the
/// orchestrator and from <c>run</c>'s own target path, on different streams, and a flag that reached
/// only one of them produced a "quiet" run that still narrated half of itself.
/// </para>
/// </remarks>
[TestClass]
public class TargetProgressPolicyTests
{
    /// <summary>Registration resolves the enabled sink when <c>--quiet</c> was not given.</summary>
    [TestMethod]
    public void ConfigureServices_WithoutQuiet_ReportsProgress()
    {
        using var provider = new ServiceCollection().ConfigureServices().BuildServiceProvider();

        var progress = provider.GetRequiredService<ITargetProgress>();

        Assert.IsTrue(progress.IsEnabled, "Progress is on by default; a silent command looks like a hung one.");
        Assert.IsInstanceOfType<StandardErrorTargetProgress>(progress);
    }

    /// <summary>Registration resolves the silent sink when <c>--quiet</c> was given.</summary>
    [TestMethod]
    public void ConfigureServices_WithQuiet_ReportsNothing()
    {
        using var provider = new ServiceCollection().ConfigureServices(quiet: true).BuildServiceProvider();

        var progress = provider.GetRequiredService<ITargetProgress>();

        Assert.IsFalse(progress.IsEnabled);
        Assert.IsInstanceOfType<NullTargetProgress>(progress);
    }

    /// <summary>
    /// The silent sink answers <see cref="ITargetProgress.IsEnabled"/> consistently with what it
    /// writes.
    /// </summary>
    /// <remarks>
    /// A caller that renders its own progress elsewhere reads this property instead of writing
    /// through the sink, so a sink that claimed to be enabled and then discarded — or the reverse —
    /// would silence one surface and not the other.
    /// </remarks>
    [TestMethod]
    public void NullProgress_DiscardsAndSaysSo()
    {
        var written = new StringWriter();
        var enabled = new StandardErrorTargetProgress(() => written);

        enabled.Report("Starting Windows Sandbox...");
        Assert.AreEqual(enabled.IsEnabled, written.ToString().Length > 0);

        NullTargetProgress.Instance.Report("Starting Windows Sandbox...");
        Assert.IsFalse(NullTargetProgress.Instance.IsEnabled);
    }

    /// <summary>
    /// Non-quiet machine-readable runs keep their progress, on standard error.
    /// </summary>
    /// <remarks>
    /// <c>--json</c> is not <c>--quiet</c>. A caller that wants a parseable stdout still benefits
    /// from knowing a five-minute first run is progressing, and standard error is where that can be
    /// said without touching the document on stdout.
    /// </remarks>
    [TestMethod]
    public void Json_WithoutQuiet_StillReportsProgressOnStandardError()
    {
        using var provider = new ServiceCollection().ConfigureServices().BuildServiceProvider();
        var progress = provider.GetRequiredService<ITargetProgress>();

        Assert.IsTrue(
            progress.IsEnabled,
            "Machine-readable output does not imply silence; the two flags mean different things.");
    }

    /// <summary>Blank progress is never written, whatever the policy.</summary>
    [TestMethod]
    public void Progress_IgnoresBlankMessages()
    {
        var written = new StringWriter();
        var progress = new StandardErrorTargetProgress(() => written);

        progress.Report(string.Empty);
        progress.Report("   ");

        Assert.AreEqual(string.Empty, written.ToString());
    }

    /// <summary>
    /// <c>--quiet</c> silences progress without touching how failures are reported.
    /// </summary>
    /// <remarks>
    /// Failures never travel through <see cref="ITargetProgress"/> at all — each command reports
    /// them through its own error envelope — so there is no path by which quieting progress can
    /// quiet a reason. Pinned because the opposite is a common and costly mistake: a quiet run that
    /// fails silently is worse than a noisy one.
    /// </remarks>
    [TestMethod]
    public void Quiet_DoesNotSuppressFailureReporting()
    {
        var original = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);

            using var provider = new ServiceCollection().ConfigureServices(quiet: true).BuildServiceProvider();
            provider.GetRequiredService<ITargetProgress>().Report("Starting Windows Sandbox...");

            Assert.AreEqual(string.Empty, captured.ToString(), "Progress is silenced.");

            WinApp.Cli.Commands.TargetOutput.Fail(
                Spectre.Console.AnsiConsole.Console,
                json: false,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.Unsupported,
                    Message = "Windows Sandbox is not available on this machine.",
                    UserAction = "Enable the Windows Sandbox optional feature.",
                });

            StringAssert.Contains(
                captured.ToString(),
                "Windows Sandbox is not available on this machine.",
                "The reason a command failed is not progress, and --quiet must never remove it.");
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
