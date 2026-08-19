// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets;

internal interface IExecutionTargetStateDirectoryProvider
{
    DirectoryInfo GetStateRoot();

    DirectoryInfo GetTargetDirectory(ExecutionTargetRef target);
}

internal sealed class ExecutionTargetStateDirectoryProvider : IExecutionTargetStateDirectoryProvider
{
    public DirectoryInfo GetStateRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return new DirectoryInfo(Path.Combine(localAppData, "Microsoft", "WinApp", "Targets"));
    }

    public DirectoryInfo GetTargetDirectory(ExecutionTargetRef target) =>
        new(Path.Combine(GetStateRoot().FullName, target.Id.Replace(':', '-')));
}
