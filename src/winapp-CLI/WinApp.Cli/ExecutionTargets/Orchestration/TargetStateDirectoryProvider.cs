// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Resolves the on-disk state root for one execution target.</summary>
internal interface ITargetStateDirectoryProvider
{
    /// <summary>
    /// Returns the state root for <paramref name="target"/>, creating it when
    /// <paramref name="create"/> is true.
    /// </summary>
    DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true);
}

/// <summary>
/// Default provider rooted at <c>%LOCALAPPDATA%\Microsoft\WinApp\Targets</c> (spec §"Host
/// coordination and state").
/// </summary>
/// <remarks>
/// This deliberately differs from the repository's usual <c>%USERPROFILE%\.winapp</c> cache root:
/// the spec pins this location, and giving each target its own state root is what allows future
/// targets to mutate concurrently without sharing a lock or a state file.
/// <para>
/// The root can be redirected two ways. Tests pass <paramref name="rootOverride"/> directly, which
/// keeps them isolated under the assembly's method-level parallelism; CI and end-to-end runs set
/// <c>WINAPP_TARGET_STATE_ROOT</c>, which redirects the whole process. Neither path ever touches
/// real user state by accident.
/// </para>
/// </remarks>
/// <param name="rootOverride">
/// Explicit targets root. When null the environment variable, then <c>%LOCALAPPDATA%</c>, is used.
/// </param>
internal sealed class TargetStateDirectoryProvider(string? rootOverride = null) : ITargetStateDirectoryProvider
{
    /// <summary>Environment override for the state root.</summary>
    internal const string RootOverrideVariable = "WINAPP_TARGET_STATE_ROOT";

    /// <inheritdoc/>
    public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true)
    {
        ArgumentNullException.ThrowIfNull(target);

        var root = TargetPathSafety.CombineInsideRoot(GetTargetsRoot(), target.StateKey);
        var directory = new DirectoryInfo(root);
        if (create && !directory.Exists)
        {
            directory.Create();
            directory.Refresh();
        }

        return directory;
    }

    private string GetTargetsRoot()
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            return rootOverride;
        }

        var environmentRoot = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return environmentRoot;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return TargetPathSafety.CombineInsideRoot(localAppData, "Microsoft", "WinApp", "Targets");
    }
}
