// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

/// <summary>
/// The single place a <c>winapp ui</c> command is diverted into the guest
/// (spec §"UI command routing").
/// </summary>
/// <remarks>
/// Interception happens before any local UI service runs, rather than inside each verb. Twenty-odd
/// handlers each remembering to check a flag is twenty-odd chances for one of them to perform UI
/// Automation, window discovery, capture, or input injection on the host desktop when the user asked
/// for the Sandbox — which is the one thing <c>--sandbox</c> exists to prevent.
/// <para>
/// Guest winapp parses and executes the ordinary command. The host rewrites only routing-specific
/// arguments, relays the standard streams and exit code, brings any declared output file back, and
/// rewrites the guest path in the result so the caller sees the path they asked for.
/// </para>
/// </remarks>
internal sealed class SandboxUiRouter(
    ExecutionTargetOrchestrator orchestrator,
    IAnsiConsole console)
{
    /// <summary>
    /// Whether this invocation must run in the guest.
    /// </summary>
    /// <remarks>
    /// Decided from the parsed result, not by scanning tokens: the parser already knows which
    /// spelling of the flag was used and which value the application target resolved to.
    /// </remarks>
    public static bool ShouldRoute(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (!IsUiCommand(parseResult.CommandResult.Command))
        {
            return false;
        }

        if (parseResult.GetValue(SharedUiOptions.SandboxOption))
        {
            return true;
        }

        // A string application target may opt in by prefix, so `-a sandbox:MyApp` routes without
        // a separate flag. Numeric --window deliberately does not: a handle carries no scope, and
        // guessing one would resolve a host window against the guest or the reverse.
        return parseResult.CommandResult.Command.Options.Contains(SharedUiOptions.AppOption) &&
            UiArgvRouter.IsSandboxTarget(parseResult.GetValue(SharedUiOptions.AppOption));
    }

    /// <summary>Runs the command in the guest and returns its exit code.</summary>
    public async Task<int> RouteAsync(
        IReadOnlyList<string> arguments,
        bool isJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var operationScope = TargetArtifactService.ScopeFor(Guid.NewGuid());

        // Ctrl+C has to reach the guest process, not just end this one. Cancelling the operation
        // asks the guest to stop the command gracefully and only then terminate its tree, which is
        // what lets a recording finalize its file instead of being killed mid-write.
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            interrupt.Cancel();
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            return await RouteCoreAsync(arguments, isJson, operationScope, interrupt.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private async Task<int> RouteCoreAsync(
        IReadOnlyList<string> arguments,
        bool isJson,
        GuestPathScope operationScope,
        CancellationToken cancellationToken)
    {
        try
        {
            // Real input and screen capture need a connected client; inspection does not. The
            // distinction is not made here because the guest re-verifies readiness immediately
            // before a command that injects input, and only the guest can know which those are.
            await using var target = await orchestrator
                .PrepareAsync(PrepareTargetOptions.Interactive, cancellationToken)
                .ConfigureAwait(false);

            var routed = UiArgvRouter.Rewrite(
                arguments,
                GuestPaths.Resolve(target.Capabilities, operationScope),
                Path.GetFullPath);

            var owner = GuestOwnerContext.WithOwner(
                environment: null,
                GuestOwnerContext.ResolveGuestToken(
                    ExecutionTargetRef.WindowsSandboxDefault.Id, target.Epoch.Value));

            // Buffered only when the guest path would otherwise appear in the result. Streaming is
            // the default so a long-running verb still shows progress as it happens.
            using var buffered = routed.Artifact is null ? null : new MemoryStream();

            var result = await target.Channel.ExecuteAsync(
                new GuestExecRequest
                {
                    UseGuestWinapp = true,
                    Arguments = routed.Arguments,
                    Environment = owner,

                    // The guest decides per verb whether input is actually injected; declaring it
                    // here makes the guest re-probe readiness before starting any of them.
                    RequiresRealInput = true,
                },
                new GuestExecCallbacks(
                    OnOperationId: id => _ = PumpStandardInputAsync(target.Channel, id, cancellationToken),
                    OnStandardOutput: data =>
                    {
                        if (buffered is not null)
                        {
                            buffered.Write(data.Span);
                            return;
                        }

                        WriteRaw(Console.OpenStandardOutput(), data);
                    },
                    OnStandardError: data => WriteRaw(Console.OpenStandardError(), data)),
                cancellationToken).ConfigureAwait(false);

            if (routed.Artifact is { } artifact)
            {
                await PublishArtifactAsync(
                    target.Channel, operationScope, artifact, buffered!, result.ExitCode, cancellationToken)
                    .ConfigureAwait(false);
            }

            return result.ExitCode;
        }
        catch (ExecutionTargetException ex)
        {
            return SandboxOutput.Fail(console, isJson, ex.Error);
        }
    }

    /// <summary>
    /// Publishes the declared output, then emits the guest's result with its path corrected.
    /// </summary>
    /// <remarks>
    /// Ordering matters: the result is emitted only after the file is at the requested path, so a
    /// caller that parses the output and immediately opens the file never loses that race. A guest
    /// command that failed publishes nothing — there is no output to copy, and reporting a stale
    /// file from a previous run as this run's result would be worse than reporting nothing.
    /// </remarks>
    private async Task PublishArtifactAsync(
        GuestCommandChannel channel,
        GuestPathScope scope,
        RoutedArtifact artifact,
        MemoryStream buffered,
        int exitCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (exitCode == 0)
            {
                await TargetArtifactService
                    .PublishAsync(channel, scope, artifact, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Written whatever happened, because it is the guest's own result and suppressing it
            // would hide why the command failed.
            EmitWithHostPaths(buffered, artifact);

            await TargetArtifactService.TryRemoveAsync(channel, scope, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the guest staging path in the guest's output with the path the caller asked for.
    /// </summary>
    /// <remarks>
    /// A literal replacement rather than a JSON-aware edit, deliberately: the guest path was
    /// injected by this router, so it is a unique string that appears exactly where the guest
    /// reported the file — in a JSON field, in a human-readable line, or both. Rewriting the text
    /// keeps every verb's output shape untouched, and works for the verbs that print rather than
    /// serialize.
    /// </remarks>
    private void EmitWithHostPaths(MemoryStream buffered, RoutedArtifact artifact)
    {
        var bytes = buffered.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        string text;

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not text at all. Relayed byte-for-byte rather than mangled by a rewrite that assumed
            // it was.
            WriteRaw(Console.OpenStandardOutput(), bytes);
            return;
        }

        var rewritten = text
            .Replace(artifact.GuestFullPath, artifact.HostDestination, StringComparison.OrdinalIgnoreCase)
            .Replace(
                artifact.GuestFullPath.Replace('\\', '/'),
                artifact.HostDestination,
                StringComparison.OrdinalIgnoreCase);

        console.Profile.Out.Writer.Write(rewritten);
        console.Profile.Out.Writer.Flush();
    }

    /// <summary>Forwards this process's standard input to the guest command.</summary>
    /// <remarks>
    /// Started only once the channel has named the operation, because input sent before the request
    /// is on the wire belongs to an operation the guest has not heard of and is dropped. Recording
    /// with no fixed duration ends on a newline or EOF, so this is what makes that verb usable
    /// through the router at all.
    /// </remarks>
    private static async Task PumpStandardInputAsync(
        GuestCommandChannel channel,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var input = Console.OpenStandardInput();
            var buffer = new byte[8 * 1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await channel.SendStandardInputAsync(
                    operationId, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await channel.CloseStandardInputAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ExecutionTargetException)
        {
            // The command owns the outcome; a closed stdin or a finished operation is not a failure
            // of the command itself.
        }
    }

    private static bool IsUiCommand(Command? command)
    {
        while (command is not null)
        {
            if (command.Name == "ui")
            {
                return true;
            }

            command = command.Parents.OfType<Command>().FirstOrDefault();
        }

        return false;
    }

    private static void WriteRaw(Stream stream, ReadOnlyMemory<byte> data)
    {
        stream.Write(data.Span);
        stream.Flush();
    }
}
