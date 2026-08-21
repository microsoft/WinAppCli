// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// Provider-neutral reference to one Windows execution target (spec §"Target reference").
/// </summary>
/// <remarks>
/// V1 exposes no public target selector; <c>--sandbox</c> resolves to
/// <see cref="WindowsSandboxDefault"/>. A future selector may resolve values such as
/// <c>hyperv:winui-test</c> without changing anything above the target backend, so nothing
/// outside <c>ExecutionTargets/WindowsSandbox</c> may branch on <see cref="Kind"/> to reach
/// Windows Sandbox APIs.
/// </remarks>
/// <param name="Kind">Target family, for example <c>windows-sandbox</c>.</param>
/// <param name="Id">Stable target identity within that family.</param>
internal sealed record ExecutionTargetRef(string Kind, string Id)
{
    /// <summary>The only target kind implemented in v1.</summary>
    public const string WindowsSandboxKind = "windows-sandbox";

    /// <summary>The single managed Windows Sandbox target that <c>--sandbox</c> resolves to.</summary>
    public static ExecutionTargetRef WindowsSandboxDefault { get; } =
        new(WindowsSandboxKind, "windows-sandbox:default");

    /// <summary>
    /// Filesystem-safe slug for this target's state root and named lock. <c>windows-sandbox:default</c>
    /// becomes <c>windows-sandbox-default</c>, matching the path the spec pins in
    /// §"Host coordination and state".
    /// </summary>
    public string Slug => Sanitize(Id);

    /// <summary>
    /// Replaces every character that is not an ASCII letter, digit, or underscore with a dash so the
    /// value is safe as both a directory name and a kernel object name. Runs of invalid characters
    /// collapse into a single dash, and a trailing dash is trimmed, so the slug stays readable.
    /// </summary>
    private static string Sanitize(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_')
            {
                buffer.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
                continue;
            }

            if (!lastWasDash && buffer.Length > 0)
            {
                buffer.Append('-');
                lastWasDash = true;
            }
        }

        if (buffer.Length > 0 && buffer[^1] == '-')
        {
            buffer.Length--;
        }

        return buffer.Length == 0 ? "target" : buffer.ToString();
    }
}

/// <summary>
/// Opaque generation identity for one live target instance (spec §"Output and JSON":
/// <c>executionTarget.epoch</c>).
/// </summary>
/// <remarks>
/// Guest process IDs and window handles are only meaningful within the epoch that produced them.
/// Every request, result, and state commit carries the epoch so a value captured before the target
/// was recreated is rejected instead of silently resolving against a different guest.
/// </remarks>
/// <param name="Value">Opaque token. Callers must treat it as unstructured.</param>
internal readonly record struct ExecutionTargetEpoch(string Value)
{
    /// <summary>Sentinel meaning "no target instance has been observed yet".</summary>
    public static ExecutionTargetEpoch None => new(string.Empty);

    /// <summary>True when this epoch does not identify a target instance.</summary>
    public bool IsNone => string.IsNullOrEmpty(Value);

    /// <summary>
    /// Builds an epoch from a provider instance identity and that instance's random boot nonce.
    /// Including the nonce means a provider that reuses instance IDs still produces a new epoch for
    /// a new boot, which is what makes stale-handle rejection reliable.
    /// </summary>
    public static ExecutionTargetEpoch Create(string instanceId, string bootNonce) =>
        new($"{instanceId}:{bootNonce}");

    /// <inheritdoc/>
    public override string ToString() => Value;
}
