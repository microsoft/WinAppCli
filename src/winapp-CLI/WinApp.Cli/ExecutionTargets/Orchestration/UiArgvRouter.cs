// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>An output file a forwarded command will produce in the guest.</summary>
/// <param name="GuestRelativePath">Name inside the operation's guest staging folder.</param>
/// <param name="GuestFullPath">Absolute guest path handed to the guest command.</param>
/// <param name="HostDestination">Absolute host path the caller asked for.</param>
internal sealed record RoutedArtifact(string GuestRelativePath, string GuestFullPath, string HostDestination);

/// <summary>A UI command rewritten for the guest.</summary>
/// <param name="Arguments">Argument vector to hand to guest winapp.</param>
/// <param name="Artifact">The output file to fetch back, when the command declares one.</param>
internal sealed record RoutedUiCommand(List<string> Arguments, RoutedArtifact? Artifact);

/// <summary>
/// Rewrites a UI command line for execution in the guest (spec §"UI command routing").
/// </summary>
/// <remarks>
/// Only routing-specific arguments are touched: the <c>--sandbox</c> flag is removed, a
/// <c>sandbox:</c> application prefix is stripped, and an output path is redirected into guest
/// staging. Everything else is forwarded verbatim, so guest winapp parses and executes the ordinary
/// command — which is what keeps every verb's behaviour and output identical to running it locally.
/// <para>
/// Pure, and deliberately so: which tokens are rewritten is the only thing that can be wrong here,
/// and that is exactly what a test can pin without a Sandbox.
/// </para>
/// </remarks>
internal static class UiArgvRouter
{
    /// <summary>Prefix that routes a string application target into the guest.</summary>
    internal const string SandboxTargetPrefix = "sandbox:";

    private static readonly string[] SandboxFlagNames = ["--sandbox"];
    private static readonly string[] AppOptionNames = ["--app", "-a"];
    private static readonly string[] OutputOptionNames = ["--output", "-o"];

    /// <summary>Whether an application target opts into guest routing by prefix.</summary>
    public static bool IsSandboxTarget(string? app) =>
        app is not null && app.StartsWith(SandboxTargetPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the routing prefix from an application target.</summary>
    public static string StripSandboxPrefix(string app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return IsSandboxTarget(app) ? app[SandboxTargetPrefix.Length..] : app;
    }

    /// <summary>
    /// Rewrites <paramref name="arguments"/> for the guest.
    /// </summary>
    /// <param name="arguments">The command line as typed, including the <c>ui</c> verb.</param>
    /// <param name="guestArtifactDirectory">Absolute guest folder for this operation's outputs.</param>
    /// <param name="resolveHostPath">Resolves a caller-supplied output path against the host.</param>
    public static RoutedUiCommand Rewrite(
        IReadOnlyList<string> arguments,
        string guestArtifactDirectory,
        Func<string, string> resolveHostPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestArtifactDirectory);
        ArgumentNullException.ThrowIfNull(resolveHostPath);

        var rewritten = new List<string>(arguments.Count);
        RoutedArtifact? artifact = null;
        var forwardVerbatim = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var token = arguments[index];

            if (forwardVerbatim)
            {
                rewritten.Add(token);
                continue;
            }

            if (token == "--")
            {
                // Everything past a separator belongs to something else and is never rewritten.
                forwardVerbatim = true;
                rewritten.Add(token);
                continue;
            }

            var (name, inlineValue) = SplitOption(token);

            if (Matches(name, SandboxFlagNames))
            {
                // A bool option can also be written with a separate value token; dropping the flag
                // alone would leave that value stranded as a positional argument.
                if (inlineValue is null && index + 1 < arguments.Count && IsBooleanLiteral(arguments[index + 1]))
                {
                    index++;
                }

                continue;
            }

            if (Matches(name, AppOptionNames))
            {
                index += Rewrite(token, name, inlineValue, arguments, index, rewritten, StripSandboxPrefix);
                continue;
            }

            if (Matches(name, OutputOptionNames) && artifact is null)
            {
                index += Rewrite(token, name, inlineValue, arguments, index, rewritten, value =>
                {
                    artifact = CreateArtifact(value, guestArtifactDirectory, resolveHostPath);
                    return artifact.GuestFullPath;
                });
                continue;
            }

            rewritten.Add(token);
        }

        return new RoutedUiCommand(rewritten, artifact);
    }

    /// <summary>
    /// Emits an option with a transformed value, in whichever spelling the caller used.
    /// </summary>
    /// <returns>How many extra tokens were consumed.</returns>
    private static int Rewrite(
        string token,
        string name,
        string? inlineValue,
        IReadOnlyList<string> arguments,
        int index,
        List<string> rewritten,
        Func<string, string> transform)
    {
        if (inlineValue is not null)
        {
            // Preserve the delimiter the caller used rather than normalising it, so the forwarded
            // command stays as close to what was typed as the rewrite allows.
            var delimiter = token[name.Length];
            rewritten.Add($"{name}{delimiter}{transform(inlineValue)}");
            return 0;
        }

        if (index + 1 >= arguments.Count)
        {
            // A trailing option with no value: forwarded as-is so guest winapp produces its own
            // "missing value" error rather than this rewrite inventing one.
            rewritten.Add(token);
            return 0;
        }

        rewritten.Add(name);
        rewritten.Add(transform(arguments[index + 1]));
        return 1;
    }

    private static RoutedArtifact CreateArtifact(
        string requestedPath,
        string guestArtifactDirectory,
        Func<string, string> resolveHostPath)
    {
        var hostDestination = resolveHostPath(requestedPath);

        // Named from the requested file so guest-side diagnostics and any name the command derives
        // from its output path stay recognisable. The name is validated as a single safe segment,
        // because it is about to become part of a managed guest path.
        var name = Path.GetFileName(hostDestination);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                $"'{requestedPath}' does not name an output file.",
                userAction: "Give --output a file path, not a directory.");
        }

        return new RoutedArtifact(
            name,
            TargetPathSafety.CombineInsideRoot(guestArtifactDirectory, name),
            hostDestination);
    }

    /// <summary>Splits an option token into its name and, when present, its inline value.</summary>
    /// <remarks>
    /// The delimiter is honoured only when what precedes it is exactly a known option name.
    /// Otherwise <c>-o C:\out.png</c>'s value would be split at the drive colon.
    /// </remarks>
    private static (string Name, string? InlineValue) SplitOption(string token)
    {
        var delimiter = token.IndexOfAny(['=', ':']);

        if (delimiter <= 0)
        {
            return (token, null);
        }

        var name = token[..delimiter];

        return IsKnownOption(name)
            ? (name, token[(delimiter + 1)..])
            : (token, null);
    }

    private static bool IsKnownOption(string name) =>
        Matches(name, SandboxFlagNames) ||
        Matches(name, AppOptionNames) ||
        Matches(name, OutputOptionNames);

    private static bool Matches(string name, string[] candidates) =>
        candidates.Contains(name, StringComparer.Ordinal);

    private static bool IsBooleanLiteral(string token) =>
        string.Equals(token, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "false", StringComparison.OrdinalIgnoreCase);
}
