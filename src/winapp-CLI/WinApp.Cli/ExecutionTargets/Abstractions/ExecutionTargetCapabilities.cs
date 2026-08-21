// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// What a live guest reports it can do (spec §"Capabilities").
/// </summary>
/// <remarks>
/// Orchestration validates required capabilities before deployment or execution instead of
/// inferring them from the provider name, which is what lets a future backend reuse the same
/// orchestration unchanged.
/// <para>
/// Capability support is <em>not</em> current readiness. <see cref="SupportsRealInput"/> means the
/// guest can inject input in principle; whether input can be delivered right now is re-verified
/// immediately before each foreground-sensitive command, because a user can disconnect the
/// interactive client at any moment.
/// </para>
/// </remarks>
internal sealed class ExecutionTargetCapabilities
{
    /// <summary>Guest processor architecture, for example <c>x64</c> or <c>arm64</c>.</summary>
    public required string Architecture { get; init; }

    /// <summary>Whether the guest has an interactive desktop at all.</summary>
    public required bool SupportsInteractiveDesktop { get; init; }

    /// <summary>Whether real input injection (as opposed to UIA-only mutation) is possible.</summary>
    public required bool SupportsRealInput { get; init; }

    /// <summary>Whether Windows Graphics Capture based screenshot and recording are possible.</summary>
    public required bool SupportsScreenCapture { get; init; }

    /// <summary>Cooperative UI Turns protocol version the guest implements; 0 when unsupported.</summary>
    public required int CooperativeUiTurnsVersion { get; init; }

    /// <summary>
    /// Whether the guest exposes the fixed privileged setup helpers winapp needs, such as enabling
    /// developer mode. This is never arbitrary SYSTEM execution.
    /// </summary>
    public required bool SupportsInternalSystemSetup { get; init; }

    /// <summary>
    /// Whether guest state survives target teardown. False for Windows Sandbox, which is why
    /// deployments and runtimes must be reconciled after every new epoch.
    /// </summary>
    public required bool PersistentStorage { get; init; }
}

/// <summary>Source-generated serializer context for the capability payload.</summary>
[JsonSerializable(typeof(ExecutionTargetCapabilities))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ExecutionTargetCapabilitiesJsonContext : JsonSerializerContext
{
}
