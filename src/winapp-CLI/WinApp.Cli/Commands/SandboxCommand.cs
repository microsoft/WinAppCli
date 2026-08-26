// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

/// <summary>
/// The <c>winapp sandbox</c> namespace: generic escape hatches for agents and scripts
/// (spec §"Generic command execution", §"File copy").
/// </summary>
/// <remarks>
/// Deliberately limited to <c>exec</c> and <c>cp</c>. Lifecycle, images, snapshots, ports,
/// providers, shells, and package-manager commands are not added — Sandbox lifecycle stays with the
/// Windows Sandbox CLI, and every additional verb here would be a second way to do something winapp
/// already does through its own commands.
/// </remarks>
internal class SandboxCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Run commands and copy files inside the managed Windows Sandbox";

    /// <summary>Composes the namespace.</summary>
    public SandboxCommand(SandboxExecCommand execCommand, SandboxCopyCommand copyCommand)
        : base(
            "sandbox",
            "Run commands and copy files inside the Windows Sandbox winapp manages. " +
            "Use these to prepare dependencies or diagnose an application that 'winapp run --sandbox' cannot resolve on its own.")
    {
        Subcommands.Add(execCommand);
        Subcommands.Add(copyCommand);
    }
}

/// <summary>
/// Runs one command inside the managed Sandbox as the interactive user
/// (spec §"Generic command execution").
/// </summary>
/// <remarks>
/// Everything after <c>--</c> is passed through as a structured argument array, never an
/// interpolated command line, so quoting and spacing survive exactly and nothing can be
/// reinterpreted as an extra argument.
/// <para>
/// Arguments, paths, and stream contents are excluded from telemetry entirely.
/// </para>
/// </remarks>
internal class SandboxExecCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Run a command inside the managed Windows Sandbox";

    /// <summary>Working directory inside the guest.</summary>
    public static Option<string?> WorkingDirectoryOption { get; } = new("--cwd")
    {
        Description = "Working directory inside the Sandbox.",
    };

    /// <summary>Executable and arguments, taken verbatim after <c>--</c>.</summary>
    public static Argument<string[]> CommandArgument { get; } = new("command")
    {
        Description = "Executable and arguments to run inside the Sandbox, after '--'.",
        Arity = ArgumentArity.OneOrMore,
    };

    /// <summary>Creates the command.</summary>
    public SandboxExecCommand()
        : base(
            "exec",
            "Run a command inside the Windows Sandbox winapp manages, as the interactive Sandbox user. " +
            "Streams stdin, stdout, and stderr, and returns the guest process's exit code. " +
            "Does not provide a full terminal, so interactive console applications may see redirected pipes.")
    {
        Options.Add(WorkingDirectoryOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Arguments.Add(CommandArgument);
    }

    /// <summary>Forwards the command to the guest and relays its streams.</summary>
    public class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var command = parseResult.GetValue(CommandArgument) ?? [];
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (command.Length == 0)
            {
                return SandboxOutput.Fail(
                    console,
                    json,
                    ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.TargetAmbiguous,
                        "No command was given to run inside the Sandbox.",
                        userAction: "Put the executable and its arguments after '--'.",
                        example: "winapp sandbox exec -- dotnet --info").Error);
            }

            try
            {
                // Read-only from the target's point of view: running a command does not change
                // deployment or package state, so it must not block a deployment or wait for one.
                await using var target = await orchestrator
                    .PrepareAsync(PrepareTargetOptions.ReadOnly, cancellationToken)
                    .ConfigureAwait(false);

                var request = new GuestExecRequest
                {
                    Executable = command[0],
                    Arguments = [.. command.Skip(1)],
                    WorkingDirectory = parseResult.GetValue(WorkingDirectoryOption),

                    // Forwarded so a script run through exec can invoke guest 'winapp ui' commands
                    // without losing its workflow ownership.
                    Environment = GuestOwnerContext.WithOwner(
                        environment: null,
                        GuestOwnerContext.ResolveGuestToken(
                            ExecutionTargetRef.WindowsSandboxDefault.Id,
                            target.Epoch.Value)),
                };

                var result = await target.Channel.ExecuteAsync(
                    request,
                    new GuestExecCallbacks(
                        // Documented as streaming stdin as well as stdout and stderr. Started from
                        // the operation ID so input a caller piped in before winapp began is not
                        // sent for an operation the guest has not heard of.
                        OnOperationId: GuestStandardInputPump.Attach(target.Channel, cancellationToken),
                        OnStandardOutput: data => WriteRaw(Console.OpenStandardOutput(), data),
                        OnStandardError: data => WriteRaw(Console.OpenStandardError(), data)),
                    cancellationToken).ConfigureAwait(false);

                // The guest application's own exit code, kept distinguishable from the
                // infrastructure failures reported through the error envelope.
                return result.ExitCode;
            }
            catch (ExecutionTargetException ex)
            {
                return SandboxOutput.Fail(console, json, ex.Error);
            }
        }

        private static void WriteRaw(Stream stream, ReadOnlyMemory<byte> data)
        {
            // Written as bytes rather than decoded text: guest output can be binary or split
            // mid-character at a chunk boundary, and decoding per chunk would corrupt both.
            stream.Write(data.Span);
            stream.Flush();
        }
    }
}

/// <summary>Copies files and directories between the host and the managed Sandbox.</summary>
internal class SandboxCopyCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Copy files between the host and the managed Windows Sandbox";

    /// <summary>Where to copy from.</summary>
    public static Argument<string> SourceArgument { get; } = new("source")
    {
        Description = "Source path. Prefix with 'sandbox:' to copy out of the Sandbox.",
    };

    /// <summary>Where to copy to.</summary>
    public static Argument<string> DestinationArgument { get; } = new("destination")
    {
        Description = "Destination path. Prefix with 'sandbox:' to copy into the Sandbox.",
    };

    /// <summary>Creates the command.</summary>
    public SandboxCopyCommand()
        : base(
            "cp",
            "Copy files or directories between the host and the Windows Sandbox winapp manages. " +
            "Exactly one path must be prefixed with 'sandbox:'. Directory structure and useful timestamps are preserved, " +
            "unchanged files are skipped, and changed files are replaced atomically.")
    {
        Arguments.Add(SourceArgument);
        Arguments.Add(DestinationArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>Parses the endpoints and performs the copy.</summary>
    public class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            try
            {
                var request = SandboxCopyParser.Parse(
                    parseResult.GetValue(SourceArgument)!,
                    parseResult.GetValue(DestinationArgument)!);

                // Copying changes guest storage, so it takes the mutation lock -- otherwise a copy
                // could interleave with a deployment writing the same managed roots.
                await using var target = await orchestrator
                    .PrepareAsync(PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false },
                        cancellationToken)
                    .ConfigureAwait(false);

                var result = await SandboxCopyService
                    .CopyAsync(target.Channel, request, cancellationToken)
                    .ConfigureAwait(false);

                // Guest paths resolve under a managed root, so the effective destination is stated
                // rather than left for the user to infer from a path they did not type.
                var resolved = request.Direction == SandboxCopyDirection.ToGuest
                    ? SandboxCopyService.DescribeGuestPath(
                        SandboxCopyService.NormalizeGuestRelative(request.GuestPath))
                    : null;

                if (json)
                {
                    console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(
                        new SandboxCopyOutput
                        {
                            Transferred = result.Transferred,
                            Skipped = result.Skipped,
                            Bytes = result.Bytes,
                            GuestPath = resolved,
                        },
                        SandboxJsonContext.Default.SandboxCopyOutput));
                }
                else if (resolved is not null)
                {
                    console.MarkupLineInterpolated(
                        $"Copied {result.Transferred} file(s), skipped {result.Skipped} unchanged, to {resolved} in the Sandbox.");
                }
                else
                {
                    console.MarkupLineInterpolated(
                        $"Copied {result.Transferred} file(s), skipped {result.Skipped} unchanged.");
                }

                return 0;
            }
            catch (ExecutionTargetException ex)
            {
                return SandboxOutput.Fail(console, json, ex.Error);
            }
        }
    }
}

/// <summary>Machine-readable result of a copy.</summary>
internal sealed class SandboxCopyOutput
{
    /// <summary>Files whose content moved.</summary>
    public int Transferred { get; init; }

    /// <summary>Files already identical at the destination.</summary>
    public int Skipped { get; init; }

    /// <summary>Total bytes transferred.</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Where the copy actually landed, fully qualified in the guest.
    /// </summary>
    /// <remarks>
    /// Reported so the effective location is never implicit: it is exactly the path a following
    /// <c>sandbox exec --cwd</c> should use. Null for a copy out to the host, where the caller
    /// already named the destination.
    /// </remarks>
    public string? GuestPath { get; init; }
}

/// <summary>Error envelope emitted by the <c>sandbox</c> commands.</summary>
internal sealed class SandboxErrorOutput
{
    /// <summary>The structured failure.</summary>
    public required ExecutionTargetErrorInfo Error { get; init; }
}

/// <summary>Source-generated serializer context for <c>sandbox</c> command output.</summary>
[JsonSerializable(typeof(SandboxCopyOutput))]
[JsonSerializable(typeof(SandboxErrorOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SandboxJsonContext : JsonSerializerContext
{
}

/// <summary>Shared failure reporting for the <c>sandbox</c> commands.</summary>
internal static class SandboxOutput
{
    /// <summary>Reports an infrastructure failure and returns the process exit code.</summary>
    /// <remarks>
    /// Always on stderr, in both modes. Under <c>--json</c> stdout carries the command's result and
    /// nothing else, so a caller can parse it without stripping diagnostics first.
    /// </remarks>
    public static int Fail(IAnsiConsole console, bool json, ExecutionTargetErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(error);

        if (json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(
                new SandboxErrorOutput { Error = error },
                SandboxJsonContext.Default.SandboxErrorOutput));
        }
        else
        {
            Console.Error.WriteLine(error.Message);

            if (error.UserAction is { } action)
            {
                Console.Error.WriteLine(action);
            }

            if (error.NextCommand is { } next)
            {
                // Advisory commands are shown, never run: only the user knows whether the side
                // effect is acceptable.
                Console.Error.WriteLine(next.Advisory ? $"You may want to run: {next.Command}" : $"Try: {next.Command}");
            }
        }

        // Distinct from any plausible guest application exit code, so a caller can tell "winapp
        // could not run your command" from "your command failed".
        return SandboxInfrastructureExitCode;
    }

    /// <summary>Exit code used for execution-target infrastructure failures.</summary>
    internal const int SandboxInfrastructureExitCode = 70;
}
