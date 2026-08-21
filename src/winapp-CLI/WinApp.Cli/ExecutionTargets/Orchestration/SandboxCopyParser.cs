// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Which side of a copy an endpoint names.</summary>
internal enum SandboxCopyDirection
{
    /// <summary>Host to guest.</summary>
    ToGuest,

    /// <summary>Guest to host.</summary>
    FromGuest,
}

/// <summary>A parsed <c>winapp sandbox cp</c> invocation.</summary>
/// <param name="Direction">Which way files move.</param>
/// <param name="HostPath">The host endpoint, always fully qualified.</param>
/// <param name="GuestPath">The guest endpoint, with the <c>sandbox:</c> prefix removed.</param>
internal sealed record SandboxCopyRequest(
    SandboxCopyDirection Direction,
    string HostPath,
    string GuestPath);

/// <summary>
/// Parses the endpoints of <c>winapp sandbox cp</c> (spec §"File copy").
/// </summary>
/// <remarks>
/// Exactly one endpoint must carry the <c>sandbox:</c> prefix. Requiring exactly one — rather than
/// inferring a direction from which path happens to exist — means the command can never guess wrong
/// about which side is being overwritten.
/// </remarks>
internal static class SandboxCopyParser
{
    /// <summary>Prefix that marks an endpoint as living inside the execution target.</summary>
    public const string GuestPrefix = "sandbox:";

    /// <summary>Parses source and destination into a directed copy.</summary>
    /// <exception cref="ExecutionTargetException">
    /// Neither or both endpoints named the guest.
    /// </exception>
    public static SandboxCopyRequest Parse(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var sourceIsGuest = HasGuestPrefix(source);
        var destinationIsGuest = HasGuestPrefix(destination);

        if (sourceIsGuest == destinationIsGuest)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                sourceIsGuest
                    ? "Both paths refer to the Sandbox, so there is nothing to copy between."
                    : "Neither path refers to the Sandbox.",
                userAction: $"Prefix exactly one path with '{GuestPrefix}'.",
                example: @"winapp sandbox cp .\setup.ps1 sandbox:C:\Setup\setup.ps1",
                context: new Dictionary<string, string>
                {
                    ["source"] = source,
                    ["destination"] = destination,
                });
        }

        return sourceIsGuest
            ? new SandboxCopyRequest(
                SandboxCopyDirection.FromGuest,
                Path.GetFullPath(destination),
                StripPrefix(source))
            : new SandboxCopyRequest(
                SandboxCopyDirection.ToGuest,
                Path.GetFullPath(source),
                StripPrefix(destination));
    }

    /// <summary>Whether an endpoint names the guest.</summary>
    public static bool HasGuestPrefix(string value) =>
        value.StartsWith(GuestPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the guest marker, leaving the guest-side path.</summary>
    /// <exception cref="ExecutionTargetException">Nothing followed the prefix.</exception>
    public static string StripPrefix(string value)
    {
        var path = value[GuestPrefix.Length..].Trim();

        if (path.Length == 0)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"'{value}' does not name a path inside the Sandbox.",
                userAction: "Include a path after the prefix.",
                example: @"winapp sandbox cp .\setup.ps1 sandbox:C:\Setup\setup.ps1");
        }

        return path;
    }
}

/// <summary>
/// Application target strings that opt into execution-target routing
/// (spec §"UI automation": <c>-a sandbox:MyApp</c>).
/// </summary>
/// <remarks>
/// Separate from copy parsing despite sharing the prefix, because the two mean different things: on
/// a path it selects a filesystem, on an application target it selects where the command runs. The
/// prefix is removed before the value reaches the guest, so guest-side target resolution is
/// unchanged.
/// </remarks>
internal static class SandboxAppTarget
{
    /// <summary>Whether an <c>--app</c> value opts into execution-target routing.</summary>
    public static bool IsRouted(string? app) =>
        app is not null && SandboxCopyParser.HasGuestPrefix(app);

    /// <summary>Returns the value the guest should see, with any routing prefix removed.</summary>
    public static string? Unwrap(string? app) =>
        IsRouted(app) ? SandboxCopyParser.StripPrefix(app!) : app;
}
