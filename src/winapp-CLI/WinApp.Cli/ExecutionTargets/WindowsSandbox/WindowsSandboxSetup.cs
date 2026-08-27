// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Brings a host to the point where Windows Sandbox can actually be used.</summary>
internal interface IWindowsSandboxSetup
{
    /// <summary>
    /// Returns once <c>wsb.exe</c> answers, performing whatever setup is still outstanding.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// Setup needs something only Windows or the user can supply: elevation, a restart, or a
    /// working Store connection.
    /// </exception>
    Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Performs Windows Sandbox prerequisite setup automatically, because <c>--sandbox</c> asked for it.
/// </summary>
/// <remarks>
/// <para>
/// Passing <c>--sandbox</c> is explicit consent to do everything feasible to make Windows Sandbox
/// usable in one command. So this enables the optional feature and initializes the Store-delivered
/// client without a second flag, a subcommand, or a prompt. Only the things winapp genuinely cannot
/// do are handed back: a denied or unavailable UAC elevation, a restart, an unsupported edition, and
/// a servicing or Store failure.
/// </para>
/// <para>
/// Setup is visible. Both phases can take minutes, and Windows shows its own update UI while the
/// client initializes — a window can appear and take focus, which winapp cannot prevent because it
/// belongs to the OS bootstrapper, not to winapp. Progress therefore goes to standard error as each
/// phase begins, leaving <c>--json</c> stdout untouched.
/// </para>
/// <para>
/// Every phase is resumable. Nothing is torn down on failure and nothing is relaunched while it is
/// already running, so a timed-out first attempt is continued by the next command rather than
/// restarted by it.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxSetup(
    IWindowsSandboxHostProbe probe,
    IWindowsFeatureEnabler featureEnabler,
    ITargetProgress? progress = null) : IWindowsSandboxSetup
{
    private readonly ITargetProgress _progress = progress ?? NullTargetProgress.Instance;

    /// <summary>How long to wait for the Store-delivered client to become usable.</summary>
    /// <remarks>
    /// Generous because the work behind it is a download over whatever connection the machine has,
    /// but still bounded: a command that never returns is worse than one that says what it was
    /// waiting for and that retrying resumes.
    /// </remarks>
    internal static readonly TimeSpan ClientInitializationTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Gap between readiness polls while the client initializes.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>How often to repeat the "still working" progress line.</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    /// <summary>Announced before enabling the optional feature, which raises a UAC prompt.</summary>
    internal const string EnablingFeatureMessage =
        "Enabling the Windows Sandbox feature (Windows will ask for permission)...";

    /// <summary>Announced before the Store-delivered client is initialized.</summary>
    internal const string InstallingClientMessage = "Installing/updating the Windows Sandbox client...";

    /// <summary>Repeated while that installation is still running.</summary>
    internal const string StillInstallingClientMessage =
        "Still installing/updating the Windows Sandbox client...";

    /// <summary>Delay seam, so timeout and cancellation are exercised without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clock seam, so the bound is exercised without real time passing.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Bootstrapper-launch seam.</summary>
    internal Action<string> LaunchClientBootstrapper { get; set; } = StartClientBootstrapper;

    /// <summary>Whether the OS client bootstrapper is already running.</summary>
    internal Func<bool> IsBootstrapperRunning { get; set; } = DefaultIsBootstrapperRunning;

    /// <inheritdoc/>
    public async Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

        if (facts.State is WindowsSandboxSetupState.Ready)
        {
            return facts;
        }

        if (facts.State is WindowsSandboxSetupState.NotWindows)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                "Windows Sandbox execution requires Windows.",
                userAction: "Run this command on a Windows 11 machine.");
        }

        if (facts.State is WindowsSandboxSetupState.FeaturePayloadMissing)
        {
            facts = await EnableFeatureAsync(facts, cancellationToken).ConfigureAwait(false);

            if (facts.State is WindowsSandboxSetupState.Ready)
            {
                return facts;
            }
        }

        return await InitializeClientAsync(facts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enables the optional feature, then re-measures rather than assuming it worked.</summary>
    private async Task<WindowsSandboxHostFacts> EnableFeatureAsync(
        WindowsSandboxHostFacts facts,
        CancellationToken cancellationToken)
    {
        _progress.Report(EnablingFeatureMessage);

        var result = await featureEnabler
            .EnableAsync(WindowsSandboxReadiness.FeatureName, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case FeatureEnableOutcome.ElevationUnavailable:
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupRequiresElevation,
                    "Windows Sandbox is not installed, and enabling it needs administrator permission "
                    + "that was declined or could not be requested.",
                    userAction:
                        "Run the command below from an elevated terminal, restart if Windows asks, then retry.",
                    context: Detail(facts, result.Detail),
                    nextCommand: new ExecutionTargetNextCommand
                    {
                        Command =
                            $"dism.exe /Online /Enable-Feature /FeatureName:{WindowsSandboxReadiness.FeatureName} "
                            + "/All /NoRestart",

                        // Enabling a Windows feature changes machine configuration and normally
                        // needs a restart, so running it stays the user's decision.
                        Advisory = true,
                    },
                    example: "winapp run . --sandbox");

            case FeatureEnableOutcome.RestartRequired:
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupRequiresRestart,
                    "The Windows Sandbox feature was enabled and Windows needs a restart to finish.",
                    userAction: "Restart Windows, then run the command again.",
                    context: Detail(facts, result.Detail),
                    example: "winapp run . --sandbox");

            case FeatureEnableOutcome.Failed:
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupFailed,
                    "Windows could not enable the Windows Sandbox feature.",
                    userAction:
                        "Check that this edition supports Windows Sandbox, that hardware virtualization is "
                        + "enabled in firmware, and that policy allows optional features, then retry.",
                    context: Detail(facts, result.Detail),
                    example: "winapp run . --sandbox");
        }

        // Enabled without a restart. Re-measured rather than assumed: the client still has to
        // initialize, and only a fresh probe can say whether it already has.
        return await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the Store-delivered client initialize, then waits for it to become usable.
    /// </summary>
    /// <remarks>
    /// Launching <c>%SystemRoot%\System32\WindowsSandbox.exe</c> is what triggers Windows to fetch
    /// and register the client; it is the same thing that happens when a user starts Windows Sandbox
    /// from the Start menu, and it shows the OS's own "Downloading and installing updates" UI while
    /// it runs. winapp does not wait for that process to exit — it can stay up as the Sandbox
    /// window itself — and instead waits for the outcome that matters: a <c>wsb.exe</c> that answers.
    /// </remarks>
    private async Task<WindowsSandboxHostFacts> InitializeClientAsync(
        WindowsSandboxHostFacts facts,
        CancellationToken cancellationToken)
    {
        _progress.Report(InstallingClientMessage);

        // Not relaunched when one is already up: repeating this on every retry would stack OS
        // bootstrapper processes and update windows on top of an installation that is progressing.
        if (!IsBootstrapperRunning())
        {
            try
            {
                LaunchClientBootstrapper(WindowsSandboxHostProbe.PayloadExecutablePath());
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                  or ExecutionTargetException)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupFailed,
                    "Windows Sandbox is installed but its client could not be started to finish setting up.",
                    userAction: "Start Windows Sandbox once from the Start menu, then retry.",
                    context: Detail(facts, ex.Message),
                    example: "winapp run . --sandbox",
                    innerException: ex);
            }
        }

        var deadline = UtcNow() + ClientInitializationTimeout;
        var nextProgress = UtcNow() + ProgressInterval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

            if (facts.State is WindowsSandboxSetupState.Ready)
            {
                return facts;
            }

            var now = UtcNow();

            if (now >= deadline)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.SetupIncomplete,
                    "The Windows Sandbox client is still being installed or updated.",
                    userAction:
                        "Windows may still be working in the background. Wait for it to finish, then run the "
                        + "command again — retrying continues the installation rather than restarting it.",
                    context: Detail(facts, facts.Detail),
                    example: "winapp run . --sandbox");
            }

            if (now >= nextProgress)
            {
                _progress.Report(StillInstallingClientMessage);
                nextProgress = now + ProgressInterval;
            }
        }
    }

    /// <summary>The observed state, for a failure a user has to act on.</summary>
    /// <remarks>
    /// Reports what was measured rather than what winapp concluded, so someone reading a timeout can
    /// see whether Windows said the package was servicing, offline, or simply absent.
    /// </remarks>
    private static Dictionary<string, string> Detail(WindowsSandboxHostFacts facts, string? detail)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["setupState"] = facts.State.ToString(),
            ["featurePayloadPresent"] = facts.FeaturePayloadPresent ? "true" : "false",
            ["packageRegistered"] = facts.PackageRegistered ? "true" : "false",
            ["aliasPresent"] = facts.AliasPresent ? "true" : "false",
            ["wsbVersion"] = facts.Version ?? "none",
        };

        if (!string.IsNullOrWhiteSpace(facts.PackageStatus))
        {
            context["packageStatus"] = facts.PackageStatus;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            context["detail"] = detail;
        }

        return context;
    }

    /// <summary>Starts the OS bootstrapper without waiting for it.</summary>
    /// <remarks>
    /// Absolute, trusted, and taken from the system directory, so nothing on PATH or in the current
    /// directory can stand in for it. Deliberately not a hidden window: this is the OS's own setup
    /// UI, and hiding it would leave the user watching a silent terminal while Windows waited on a
    /// dialog they could not see.
    /// </remarks>
    private static void StartClientBootstrapper(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            CreateNoWindow = false,
        };

        // This child outlives the command that started it, so it must not hold a duplicate of a
        // caller's captured stdout: a caller piping winapp would otherwise not see end of stream
        // until Windows Sandbox itself closed.
        using (Helpers.StandardHandleInheritance.Suppress())
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the Sandbox client.");
        }
    }

    /// <summary>Whether an OS Sandbox bootstrapper process is already running.</summary>
    private static bool DefaultIsBootstrapperRunning()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(WindowsSandboxReadiness.PayloadExecutableName);
            var running = Process.GetProcessesByName(name);

            foreach (var process in running)
            {
                process.Dispose();
            }

            return running.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                     or NotSupportedException)
        {
            // If the process list cannot be read, launching is still the safe answer: a duplicate
            // bootstrapper is recoverable, whereas never launching one wedges setup permanently.
            return false;
        }
    }
}
