// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake project-run service that returns canned input classifications and build outcomes so the
/// <see cref="WinApp.Cli.Commands.RunCommand"/> project-mode routing can be tested without invoking
/// the real .NET SDK.
/// </summary>
internal sealed class FakeProjectRunService : IProjectRunService
{
    /// <summary>Overrides the input classification. When null, a .csproj file maps to project mode and a directory to folder mode.</summary>
    public RunInputResolution? InputResolutionOverride { get; set; }

    /// <summary>When set, <see cref="ResolveInputAsync"/> throws this (simulates the multi-csproj ambiguity error).</summary>
    public ProjectRunException? ResolveInputThrows { get; set; }

    /// <summary>Returned from <see cref="CheckSdkAsync"/>. Null = capable SDK.</summary>
    public string? SdkError { get; set; }

    /// <summary>Returned from <see cref="BuildAndResolveAsync"/> when no exception is configured.</summary>
    public ProjectBuildOutcome? BuildOutcome { get; set; }

    /// <summary>When set, <see cref="BuildAndResolveAsync"/> throws it (simulates a guardrail violation).</summary>
    public ProjectRunException? BuildThrows { get; set; }

    /// <summary>Returned from <see cref="IsDefinitivelyUnpackagedAsync"/>. Default false = indeterminate/packaged (fall through to the post-build gate).</summary>
    public bool DefinitivelyUnpackaged { get; set; }

    public List<FileSystemInfo> ResolveInputCalls { get; } = [];
    public List<string?> ResolveInputSelectors { get; } = [];
    public List<ProjectClassificationInputs?> ResolveInputClassificationInputs { get; } = [];
    public List<FileInfo> BuildAndResolveCalls { get; } = [];
    public List<ProjectRunOptions> BuildOptions { get; } = [];
    public int CheckSdkCallCount { get; private set; }

    /// <summary>Records each <see cref="IsDefinitivelyUnpackagedAsync"/> invocation (for asserting the pre-flight probe fired or was skipped).</summary>
    public List<FileInfo> IsDefinitivelyUnpackagedCalls { get; } = [];

    public Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken, string? projectSelector = null, ProjectClassificationInputs? classificationInputs = null)
    {
        ResolveInputCalls.Add(input);
        ResolveInputSelectors.Add(projectSelector);
        ResolveInputClassificationInputs.Add(classificationInputs);
        if (ResolveInputThrows != null)
        {
            throw ResolveInputThrows;
        }

        if (InputResolutionOverride != null)
        {
            return Task.FromResult(InputResolutionOverride);
        }

        if (input is FileInfo file && string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new RunInputResolution(WinAppRunMode.Project, file, file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory())));
        }

        var dir = input as DirectoryInfo ?? new DirectoryInfo(input.FullName);
        return Task.FromResult(new RunInputResolution(WinAppRunMode.Folder, null, dir));
    }

    public Task<string?> CheckSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        CheckSdkCallCount++;
        return Task.FromResult(SdkError);
    }

    public Task<ProjectBuildOutcome> BuildAndResolveAsync(FileInfo csproj, ProjectRunOptions options, CancellationToken cancellationToken)
    {
        BuildAndResolveCalls.Add(csproj);
        BuildOptions.Add(options);
        if (BuildThrows != null)
        {
            throw BuildThrows;
        }

        return Task.FromResult(BuildOutcome
            ?? throw new InvalidOperationException("FakeProjectRunService.BuildOutcome was not configured."));
    }

    public Task<bool> IsDefinitivelyUnpackagedAsync(FileInfo csproj, ProjectRunOptions options, CancellationToken cancellationToken)
    {
        IsDefinitivelyUnpackagedCalls.Add(csproj);
        return Task.FromResult(DefinitivelyUnpackaged);
    }
}
