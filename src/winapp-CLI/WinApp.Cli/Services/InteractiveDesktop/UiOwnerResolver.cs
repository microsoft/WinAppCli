// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// One resolved logical UI workflow owner. Only <see cref="Key"/> is ever persisted; the raw
/// <c>WINAPP_UI_OWNER_ID</c> never leaves this process.
/// </summary>
/// <param name="Kind">How the owner was resolved.</param>
/// <param name="Key">Lowercase hex SHA-256 of the domain-separated owner payload.</param>
/// <param name="ParentPid">Immediate parent PID, when known. Local diagnostics only.</param>
/// <param name="ParentStartTicksUtc">The parent's start ticks, when known.</param>
internal sealed record UiOwnerIdentity(
    UiOwnerKind Kind,
    string Key,
    int? ParentPid,
    long? ParentStartTicksUtc);

/// <summary>Resolves the logical workflow owner for the current command (spec §5).</summary>
internal interface IUiOwnerResolver
{
    /// <summary>
    /// Resolves the owner, preferring <c>WINAPP_UI_OWNER_ID</c>, then the immediate parent process,
    /// then a unique anonymous one-command owner.
    /// </summary>
    /// <exception cref="UiCoordinationException">
    /// <c>WINAPP_UI_OWNER_ID</c> is present but invalid. Thrown before any UI side effect.
    /// </exception>
    UiOwnerIdentity Resolve();
}

/// <inheritdoc cref="IUiOwnerResolver"/>
internal sealed class UiOwnerResolver(IProcessInspector processInspector) : IUiOwnerResolver
{
    /// <summary>Environment variable naming one logical UI workflow — not an agent and not an app.</summary>
    internal const string OwnerIdVariable = "WINAPP_UI_OWNER_ID";

    /// <summary>
    /// Maximum accepted length in UTF-16 code units (<c>string.Length</c>). The value is an opaque
    /// grouping token, so a bound keeps a pathological value from bloating every state write.
    /// </summary>
    internal const int MaxOwnerIdLength = 256;

    private const string ExplicitDomain = "winapp-ui-owner-v1\0";
    private const string ParentDomain = "winapp-ui-parent-v1\0";
    private const string AnonymousDomain = "winapp-ui-anonymous-v1\0";

    public UiOwnerIdentity Resolve()
    {
        var raw = Environment.GetEnvironmentVariable(OwnerIdVariable);
        if (raw is not null)
        {
            return ResolveExplicit(raw);
        }

        var parentPid = processInspector.TryGetParentProcessId();
        if (parentPid is { } pid)
        {
            var parentStart = processInspector.TryGetProcessStartTicksUtc(pid);
            if (parentStart is { } startTicks)
            {
                return new UiOwnerIdentity(UiOwnerKind.Parent, ComputeParentKey(pid, startTicks), pid, startTicks);
            }
        }

        // Spec §5.3: parent inspection failed, so this command gets a unique owner of its own. It queues
        // normally but receives no idle grace, because there is no shell to issue a follow-up command.
        return new UiOwnerIdentity(UiOwnerKind.Anonymous, ComputeAnonymousKey(), null, null);
    }

    private static UiOwnerIdentity ResolveExplicit(string raw)
    {
        // An explicitly-set-but-blank value is a scripting mistake (an unset variable expanded to ""),
        // not a request for an empty owner. Failing here is far cheaper than silently merging every
        // workflow that made the same mistake into one shared owner.
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.InvalidOwnerId,
                $"{OwnerIdVariable} is set but empty or whitespace.",
                $"Set {OwnerIdVariable} to a non-empty value that identifies one logical UI workflow, for example a GUID, or unset it to use the parent process identity.");
        }

        if (raw.Length > MaxOwnerIdLength)
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.InvalidOwnerId,
                $"{OwnerIdVariable} is longer than {MaxOwnerIdLength} characters.",
                $"Set {OwnerIdVariable} to a short opaque value such as a GUID.");
        }

        return new UiOwnerIdentity(UiOwnerKind.Explicit, ComputeExplicitKey(raw), null, null);
    }

    /// <summary>
    /// <c>SHA-256("winapp-ui-owner-v1\0" + raw value)</c>. Hashing means a workflow id that happens to
    /// contain a path, ticket number or user name never reaches disk, and the domain prefix keeps an
    /// explicit id from ever colliding with a parent-derived one.
    /// </summary>
    internal static string ComputeExplicitKey(string rawOwnerId)
        => Hash(Encoding.UTF8.GetBytes(ExplicitDomain + rawOwnerId));

    /// <summary>
    /// <c>SHA-256("winapp-ui-parent-v1\0" + pid + "\0" + parentStartUtcTicks)</c> (spec §5.2). Including
    /// the start time means a recycled PID does not inherit the previous shell's turn.
    /// </summary>
    internal static string ComputeParentKey(int parentPid, long parentStartTicksUtc)
        => Hash(Encoding.UTF8.GetBytes(
            ParentDomain
            + parentPid.ToString(CultureInfo.InvariantCulture)
            + "\0"
            + parentStartTicksUtc.ToString(CultureInfo.InvariantCulture)));

    private static string ComputeAnonymousKey()
        => Hash(Encoding.UTF8.GetBytes(AnonymousDomain + Guid.NewGuid().ToString("N")));

    private static string Hash(byte[] payload)
        => Convert.ToHexStringLower(SHA256.HashData(payload));
}
