// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// The barrier that makes per-operation job containment race-free.
/// </summary>
/// <remarks>
/// <see cref="Process.Start(ProcessStartInfo)"/> cannot create a process that is already a job
/// member, and assigning one afterwards leaves a window in which it can spawn descendants outside
/// the job. Those descendants then survive a cancellation of their own operation, which the
/// specification requires to terminate the whole process tree.
/// <para>
/// Rather than reimplementing process creation to pass <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, the
/// agent starts <em>this</em> host instead of the requested command. It does nothing but wait on an
/// event, so it provably cannot spawn anything before the agent has assigned it to the job. Once
/// released it starts the real command as its own child, which Windows places in the job at
/// creation because its parent is already a member. There is no window at any point.
/// </para>
/// <para>
/// Standard streams are inherited rather than redirected again, so the requested command writes
/// directly into the pipes the agent created. The extra process costs one handle and no copying,
/// and it does not change what the host observes: the agent already tracks an intermediate — guest
/// <c>winapp</c> — rather than the application itself, and an application's own process ID still
/// comes from that command's output.
/// </para>
/// </remarks>
internal static class GuestOperationHost
{
    /// <summary>Hidden flag that runs winapp as the containment barrier.</summary>
    public const string OperationHostOption = "--operation-host";

    /// <summary>Hidden option naming the event the agent signals once the job is assigned.</summary>
    public const string ReadyEventOption = "--ready-event";

    /// <summary>How long the barrier waits to be released before giving up.</summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Exit code used when the barrier itself could not do its job.</summary>
    /// <remarks>
    /// Distinct from anything the requested command is likely to return, so a containment failure
    /// is not mistaken for an application result.
    /// </remarks>
    internal const int BarrierFailedExitCode = 64;

    /// <summary>Builds the arguments that run winapp as the barrier for one request.</summary>
    public static List<string> BuildArguments(string readyEventName, GuestExecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = new List<string>
        {
            GuestAgentCommandNames.Verb,
            OperationHostOption,
            ReadyEventOption,
            readyEventName,

            // Everything after the separator is the requested command, still as discrete values, so
            // argument boundaries survive this extra hop exactly as they survive the transport.
            "--",
            request.Executable,
        };

        arguments.AddRange(request.Arguments);
        return arguments;
    }

    /// <summary>Waits to be released, then runs the requested command as its own child.</summary>
    /// <returns>The requested command's exit code.</returns>
    public static async Task<int> RunAsync(
        string readyEventName,
        IReadOnlyList<string> command,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readyEventName);
        ArgumentNullException.ThrowIfNull(command);

        if (command.Count == 0)
        {
            return BarrierFailedExitCode;
        }

        if (!await WaitForReleaseAsync(readyEventName, cancellationToken).ConfigureAwait(false))
        {
            // Never released, so this process may not be a job member. Starting the command now
            // would create exactly the unconstrained process this barrier exists to prevent.
            return BarrierFailedExitCode;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory ?? string.Empty,

            // Deliberately no redirection: the child inherits this process's standard handles,
            // which are the agent's pipes. Redirecting again would add a second copy of every byte
            // and a second place for ordering to go wrong.
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return BarrierFailedExitCode;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"The guest could not start '{command[0]}': {ex.Message}");
            return BarrierFailedExitCode;
        }
    }

    /// <summary>Waits for the agent to signal that this process is inside the job.</summary>
    private static async Task<bool> WaitForReleaseAsync(string readyEventName, CancellationToken cancellationToken)
    {
        try
        {
            using var released = EventWaitHandle.OpenExisting(readyEventName);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReleaseTimeout);

            await released.WaitOneAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The agent died between starting this process and creating the event.
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Awaits a <see cref="WaitHandle"/> without blocking a thread-pool thread.</summary>
internal static class WaitHandleExtensions
{
    /// <summary>Completes when <paramref name="handle"/> is signalled or the token fires.</summary>
    public static async Task WaitOneAsync(this WaitHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            static (state, timedOut) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            System.Threading.Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);

        await using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            completion);

        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(waitObject: null);
        }
    }
}
