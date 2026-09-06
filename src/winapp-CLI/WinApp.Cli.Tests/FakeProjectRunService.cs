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
    public List<ProjectPreparationOperation> PreparationOperations { get; } = [];

    public ProjectPreparationOutcome? PreparationOutcome { get; set; }

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
        => Task.FromResult(SdkError);

    public Task<ProjectPreparationOutcome> PrepareAndResolveAsync(
       FileInfo csproj,
       ProjectRunOptions options,
       ProjectPreparationOperation operation,
       CancellationToken cancellationToken)
    {
        BuildAndResolveCalls.Add(csproj);
        BuildOptions.Add(options);
       PreparationOperations.Add(operation);
        if (BuildThrows != null)
        {
            throw BuildThrows;
        }

       if (PreparationOutcome is not null)
       {
           return Task.FromResult(PreparationOutcome);
       }

       var build = BuildOutcome
           ?? throw new InvalidOperationException("FakeProjectRunService.BuildOutcome was not configured.");
       return Task.FromResult(new ProjectPreparationOutcome(
           build.Resolution,
           build.ExitCode,
           Executed: !options.DryRun,
           Ready: build.Resolution is not null));
    }

    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
       FileInfo csproj,
       ProjectRunOptions options,
       CancellationToken cancellationToken)
    {
       var outcome = await PrepareAndResolveAsync(
           csproj,
           options,
           ProjectPreparationOperation.Build,
           cancellationToken);
       return new ProjectBuildOutcome(outcome.Resolution, outcome.ExitCode);
    }

    public Task<bool> IsDefinitivelyUnpackagedAsync(FileInfo csproj, ProjectRunOptions options, CancellationToken cancellationToken)
    {
        IsDefinitivelyUnpackagedCalls.Add(csproj);
        return Task.FromResult(DefinitivelyUnpackaged);
    }
}
