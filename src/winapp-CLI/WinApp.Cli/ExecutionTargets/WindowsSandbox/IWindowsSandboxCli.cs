// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// Typed wrapper over the <c>wsb.exe</c> command line.
/// </summary>
/// <remarks>
/// This is the only seam through which winapp touches Windows Sandbox. Keeping it behind an
/// interface is what lets the lifecycle rules — singleton ownership, unmanaged refusal, teardown
/// waiting, epoch recovery — be exercised deterministically without starting a real Sandbox, and it
/// keeps <c>wsb</c> knowledge out of every layer above the backend.
/// </remarks>
internal interface IWindowsSandboxCli
{
    /// <summary>Whether <c>wsb.exe</c> is present on this host.</summary>
    bool IsAvailable { get; }

    /// <summary>Lists the IDs of every running Sandbox, managed or not.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Starts a Sandbox and returns its ID.</summary>
    Task<string> StartAsync(string? configuration, CancellationToken cancellationToken);

    /// <summary>Terminates the Sandbox with the given ID.</summary>
    Task StopAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns the guest's IPv4 address.</summary>
    Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken);

    /// <summary>Shares a host folder into the guest.</summary>
    Task ShareFolderAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken);

    /// <summary>Starts the interactive remote session for the Sandbox.</summary>
    Task ConnectAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one fixed bootstrap command in the guest and returns its exit code.
    /// </summary>
    /// <remarks>
    /// <c>wsb exec</c> takes the command as a single string and relays only an exit code, never the
    /// guest's stdout or stderr. That is why it is used solely for fixed bootstrap operations: real
    /// work needs argument fidelity and streaming, which only the authenticated channel provides.
    /// The bootstrap writes its own output to the managed bootstrap folder so failures still surface
    /// real guest diagnostics.
    /// </remarks>
    Task<int> ExecuteAsync(
        string id,
        string command,
        string? workingDirectory,
        bool asSystem,
        CancellationToken cancellationToken);

    /// <summary>
    /// Launches the fixed persistent guest-agent bootstrap command without waiting for it to exit.
    /// </summary>
    /// <remarks>
    /// This is separate from <see cref="ExecuteAsync"/> because a healthy agent intentionally remains
    /// running. The agent heartbeat, not the lifetime of <c>wsb exec</c>, proves successful dispatch.
    /// </remarks>
    Task LaunchAgentAsync(string id, string command, CancellationToken cancellationToken);
}

/// <summary>One entry of <c>wsb list --raw</c>.</summary>
internal sealed class WsbEnvironment
{
    /// <summary>The Sandbox instance ID.</summary>
    public string? Id { get; init; }
}

/// <summary>Payload of <c>wsb list --raw</c> and <c>wsb start --raw</c>.</summary>
internal sealed class WsbEnvironmentList
{
    /// <summary>Running environments, or the one just created.</summary>
    public List<WsbEnvironment>? WindowsSandboxEnvironments { get; init; }

    /// <summary>Some verbs report a bare ID at the root instead of a list.</summary>
    public string? Id { get; init; }
}

/// <summary>One entry of <c>wsb ip --raw</c>.</summary>
internal sealed class WsbNetwork
{
    /// <summary>The guest's IPv4 address.</summary>
    public string? IpV4Address { get; init; }
}

/// <summary>Payload of <c>wsb ip --raw</c>.</summary>
internal sealed class WsbNetworkList
{
    /// <summary>Guest networks.</summary>
    public List<WsbNetwork>? Networks { get; init; }
}

/// <summary>
/// Source-generated serializer context for <c>wsb --raw</c> payloads.
/// </summary>
/// <remarks>
/// <c>wsb</c> emits PascalCase property names, so this context deliberately does not apply the
/// camelCase policy used for winapp's own output.
/// </remarks>
[JsonSerializable(typeof(WsbEnvironmentList))]
[JsonSerializable(typeof(WsbNetworkList))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class WindowsSandboxCliJsonContext : JsonSerializerContext
{
}
