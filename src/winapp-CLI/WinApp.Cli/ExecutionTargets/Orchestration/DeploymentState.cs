// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// What winapp registered in the guest for one deployment (spec §"Package ownership").
/// </summary>
/// <remarks>
/// Registration and unregistration act only on a package whose recorded location matches this
/// deployment. Matching by name alone would let winapp remove a package a user installed themselves
/// that happens to share an identity — which is exactly the "never adopts or removes an external
/// guest package" rule.
/// </remarks>
internal sealed record PackageOwnership
{
    /// <summary>Original package name, preserved rather than rewritten.</summary>
    public required string PackageName { get; init; }

    /// <summary>Original publisher, preserved rather than rewritten.</summary>
    public required string Publisher { get; init; }

    /// <summary>Effective package full name as registered in the guest.</summary>
    public required string PackageFullName { get; init; }

    /// <summary>Effective package family name.</summary>
    public required string PackageFamilyName { get; init; }

    /// <summary>Guest location the package was registered from.</summary>
    public required string RegisteredLocation { get; init; }

    /// <summary>Application user model ID used to launch it.</summary>
    public string? Aumid { get; init; }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the exact package this record owns.
    /// </summary>
    /// <remarks>
    /// Full name and registered location must both match. The full name alone would accept a
    /// different registration of the same version, and the location alone would accept a different
    /// package registered from a path this deployment happens to have used.
    /// </remarks>
    public bool Owns(string packageFullName, string registeredLocation) =>
        string.Equals(PackageFullName, packageFullName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(RegisteredLocation),
            Path.TrimEndingDirectorySeparator(registeredLocation),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Persisted state for one deployment inside one target generation
/// (spec §"Deployment model", §"Host coordination and state").
/// </summary>
/// <remarks>
/// Held separately from target ownership so a deployment write never has to rewrite — or risk
/// corrupting — the record that proves which Sandbox winapp owns.
/// </remarks>
internal sealed record DeploymentState
{
    /// <summary>Schema version. Migrations are monotonic; an unknown newer version fails closed.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Monotonic revision, incremented on every commit.</summary>
    public required long Revision { get; init; }

    /// <summary>Internal deployment identity this record belongs to.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>
    /// Target generation this state was written for. State from a previous generation describes a
    /// guest that no longer exists, so it is discarded rather than reconciled against.
    /// </summary>
    public required string TargetEpoch { get; init; }

    /// <summary>
    /// True while the guest layout is mid-change. A dirty deployment never launches and never
    /// reports healthy; the next run performs a complete desired-state reconciliation.
    /// </summary>
    public required bool Dirty { get; init; }

    /// <summary>Desired state the last reconciliation was working toward.</summary>
    public IReadOnlyList<DeploymentFile>? Desired { get; init; }

    /// <summary>Package this deployment registered, when it registered one.</summary>
    public PackageOwnership? Package { get; init; }

    /// <summary>Process ID of the launch this deployment tracks, valid only within the epoch.</summary>
    public int? ProcessId { get; init; }

    /// <summary>UTC ticks when that process started, so a reused process ID is detected.</summary>
    public long? ProcessStartTicksUtc { get; init; }

    /// <summary>UTC timestamp of the last commit, for diagnostics only.</summary>
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>Whether this state describes <paramref name="epoch"/>.</summary>
    public bool IsForEpoch(ExecutionTargetEpoch epoch) =>
        string.Equals(TargetEpoch, epoch.Value, StringComparison.Ordinal);
}

/// <summary>Source-generated serializer context for persisted deployment state.</summary>
[JsonSerializable(typeof(DeploymentState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DeploymentStateJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and atomically commits per-deployment state.</summary>
internal interface IDeploymentStateStore
{
    /// <summary>Reads a deployment's state, or null when none exists.</summary>
    /// <exception cref="ExecutionTargetException">The record is corrupt or from a newer schema.</exception>
    DeploymentState? Read(ExecutionTargetRef target, string deploymentId);

    /// <summary>Commits state under optimistic concurrency, returning the new revision.</summary>
    /// <exception cref="ExecutionTargetException">Another host process committed first.</exception>
    DeploymentState Commit(
        ExecutionTargetRef target,
        DeploymentState state,
        long expectedRevision);

    /// <summary>Removes a deployment's state. Succeeds when none exists.</summary>
    void Clear(ExecutionTargetRef target, string deploymentId);
}

/// <summary>
/// File-backed <see cref="IDeploymentStateStore"/> using atomic replace and monotonic revisions.
/// </summary>
/// <remarks>
/// Authoritative host state deliberately lives outside guest-writable staging: a guest that could
/// edit this could convince the host it owns a package it does not, or that a dirty deployment is
/// clean.
/// </remarks>
internal sealed class DeploymentStateStore(ITargetStateDirectoryProvider directoryProvider) : IDeploymentStateStore
{
    /// <summary>Schema version this build reads and writes.</summary>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>Folder holding per-deployment records inside the target state root.</summary>
    internal const string DeploymentsFolder = "deployments";

    /// <inheritdoc/>
    public DeploymentState? Read(ExecutionTargetRef target, string deploymentId)
    {
        var file = GetStateFile(target, deploymentId, create: false);
        if (!File.Exists(file))
        {
            return null;
        }

        DeploymentState? state;
        try
        {
            using var stream = File.OpenRead(file);
            state = JsonSerializer.Deserialize(stream, DeploymentStateJsonContext.Default.DeploymentState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Unreadable(file, ex);
        }

        if (state is null)
        {
            throw Unreadable(file, innerException: null);
        }

        if (state.SchemaVersion > CurrentSchemaVersion)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"Deployment state was written by a newer version of winapp (schema {state.SchemaVersion}, this build supports {CurrentSchemaVersion}).",
                userAction: "Update winapp to the newest version, then retry.",
                nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });
        }

        return state;
    }

    /// <inheritdoc/>
    public DeploymentState Commit(ExecutionTargetRef target, DeploymentState state, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(state);

        var current = Read(target, state.DeploymentId);
        var currentRevision = current?.Revision ?? 0;

        if (currentRevision != expectedRevision)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "This deployment's state changed while the command was running.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string>
                {
                    ["deploymentId"] = state.DeploymentId,
                    ["expectedRevision"] = expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["actualRevision"] = currentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var committed = state with
        {
            SchemaVersion = CurrentSchemaVersion,
            Revision = currentRevision + 1,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        var file = GetStateFile(target, state.DeploymentId, create: true);
        WriteAtomic(file, JsonSerializer.Serialize(committed, DeploymentStateJsonContext.Default.DeploymentState));
        return committed;
    }

    /// <inheritdoc/>
    public void Clear(ExecutionTargetRef target, string deploymentId)
    {
        var file = GetStateFile(target, deploymentId, create: false);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string GetStateFile(ExecutionTargetRef target, string deploymentId, bool create)
    {
        var root = directoryProvider.GetTargetRoot(target, create).FullName;
        var file = TargetPathSafety.CombineInsideRoot(root, DeploymentsFolder, $"{deploymentId}.json");

        if (create)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        }

        return file;
    }

    private static void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var temporary = TargetPathSafety.CombineInsideRoot(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary is harmless and must never mask the original error.
        }
    }

    private static ExecutionTargetException Unreadable(string file, Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            "Deployment state is unreadable, so winapp cannot tell what is deployed in the guest.",
            userAction: $"Delete '{file}', then retry to redeploy from scratch.",
            context: new Dictionary<string, string> { ["stateFile"] = file },
            innerException: innerException);
}
