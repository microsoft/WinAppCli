// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets;

internal sealed record ExecutionTargetRef
{
    public const string WindowsSandboxKind = "windows-sandbox";
    public const string WindowsSandboxDefaultId = "windows-sandbox:default";

    public static ExecutionTargetRef WindowsSandboxDefault { get; } =
        new(WindowsSandboxKind, WindowsSandboxDefaultId);

    public string Kind { get; }

    public string Id { get; }

    public ExecutionTargetRef(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!IsValidSegment(kind))
        {
            throw new ArgumentException("Execution target kinds may contain only lowercase letters, digits, and hyphens.", nameof(kind));
        }

        if (!id.StartsWith(kind + ":", StringComparison.Ordinal) ||
            !IsValidSegment(id[(kind.Length + 1)..]))
        {
            throw new ArgumentException("Execution target IDs must use the form '<kind>:<name>' with lowercase letters, digits, and hyphens.", nameof(id));
        }

        Kind = kind;
        Id = id;
    }

    private static bool IsValidSegment(string value) =>
        value.Length > 0 &&
        value.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

internal readonly record struct ExecutionTargetEpoch
{
    public string Value { get; }

    public ExecutionTargetEpoch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 32 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Execution target epochs must be 32 hexadecimal characters.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public static ExecutionTargetEpoch Create() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}

internal sealed record ExecutionTargetRequirements(
    bool Running = true,
    bool InteractiveDesktop = false);

internal sealed record ExecutionTargetCapabilities(
    bool Running,
    bool InteractiveDesktop,
    bool GuestExecution,
    bool RealInput,
    bool ScreenCapture,
    bool PersistentStorage)
{
    public static ExecutionTargetCapabilities Stopped { get; } =
        new(false, false, false, false, false, false);

    public bool Satisfies(ExecutionTargetRequirements requirements) =>
        (!requirements.Running || Running) &&
        (!requirements.InteractiveDesktop || InteractiveDesktop);
}

internal enum ExecutionTargetStatus
{
    Unavailable,
    Stopped,
    Running,
    Unmanaged,
}

internal enum ExecutionTargetDiagnosticCode
{
    UnsupportedHost,
    BackendMismatch,
    CapabilityUnavailable,
    WindowsSandboxCliMissing,
    WindowsSandboxCliIncompatible,
    WindowsSandboxListFailed,
    WindowsSandboxStartFailed,
    WindowsSandboxRollbackFailed,
    WindowsSandboxUnmanagedInstance,
    WindowsSandboxStateUnavailable,
}

internal sealed record ExecutionTargetDiagnostic(
    ExecutionTargetDiagnosticCode Code,
    string Message,
    string? RecoveryCommand = null);

internal sealed record ExecutionTargetProbeResult(
    bool IsSupported,
    IReadOnlyList<ExecutionTargetDiagnostic> Diagnostics)
{
    public static ExecutionTargetProbeResult Supported { get; } = new(true, []);
}

internal sealed record ExecutionTargetStatusResult(
    ExecutionTargetRef Target,
    ExecutionTargetStatus Status,
    string? ProviderInstanceId,
    ExecutionTargetEpoch? Epoch,
    ExecutionTargetCapabilities Capabilities,
    IReadOnlyList<ExecutionTargetDiagnostic> Diagnostics);

internal sealed record ExecutionTargetInstance(
    ExecutionTargetRef Target,
    string ProviderInstanceId,
    ExecutionTargetEpoch Epoch,
    ExecutionTargetCapabilities Capabilities);

internal sealed record ExecutionTargetEnsureResult(
    ExecutionTargetInstance? Instance,
    IReadOnlyList<ExecutionTargetDiagnostic> Diagnostics)
{
    public bool Succeeded => Instance is not null && Diagnostics.Count == 0;

    public static ExecutionTargetEnsureResult Failure(params ExecutionTargetDiagnostic[] diagnostics) =>
        new(null, diagnostics);
}
