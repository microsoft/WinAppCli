// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Composite service that aggregates all registered <see cref="ITemplateProvider"/> instances
/// to provide a unified view of templates across all languages/toolchains.
/// </summary>
internal interface ITemplateService
{
    /// <summary>
    /// Ensures all registered template providers are available and up-to-date.
    /// </summary>
    Task EnsureAllProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers all templates from all registered providers.
    /// </summary>
    Task<IReadOnlyList<TemplateInfo>> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets only project templates from all providers.
    /// </summary>
    Task<IReadOnlyList<TemplateInfo>> GetProjectTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets only item templates from all providers.
    /// </summary>
    Task<IReadOnlyList<TemplateInfo>> GetItemTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a project or item from a template, routing to the correct provider.
    /// </summary>
    Task<(int ExitCode, string Output, string Error)> CreateFromTemplateAsync(
        string shortName,
        string name,
        DirectoryInfo? outputDir,
        FileInfo? projectFile,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyList<string>? extraArgs,
        CancellationToken cancellationToken = default);
}
