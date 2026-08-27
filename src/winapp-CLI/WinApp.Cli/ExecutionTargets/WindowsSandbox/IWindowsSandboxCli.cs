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
    /// <summary>Whether a trusted <c>wsb.exe</c> has been resolved on this host.</summary>
    /// <remarks>
    /// Re-evaluated rather than cached for the life of the process, because prerequisite setup can
    /// make an alias appear part-way through a single command. A value fixed at first use would
    /// report the host as unusable for the rest of an invocation that had just finished making it
    /// usable.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Binds this client to the exact <c>wsb.exe</c> the readiness probe validated.
    /// </summary>
    /// <remarks>
    /// Setup already resolved and executed this path to prove it answers, so reusing it here avoids
    /// a second resolution that could pick a different file.
    /// </remarks>
    void UseExecutable(string executablePath);

    /// <summary>Lists the IDs of every running Sandbox, managed or not.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a Sandbox under the caller's own instance ID and returns the ID it reports.
    /// </summary>
    /// <remarks>
    /// The caller assigns the ID so that a start which fails <em>after</em> creating an instance can
    /// still be reconciled: without one, a later command could only guess which listed instance was
    /// the one it had just tried to create, and guessing from a list delta would attribute someone
    /// else's Sandbox to winapp.
    /// </remarks>
    Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken);

    /// <summary>Terminates the Sandbox with the given ID.</summary>
    Task StopAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns the guest's IPv4 address.</summary>
    Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the instance resolves to a usable guest, without changing anything about it.
    /// </summary>
    /// <remarks>
    /// A listed ID is not proof that a Sandbox can be used: an instance that is still coming up, or
    /// on its way out, is listed exactly like a healthy one. Resolving its address is the cheapest
    /// question whose answer requires the guest to actually be there, which is what makes it the
    /// gate before anything winapp does would change guest state.
    /// </remarks>
    Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken);

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
