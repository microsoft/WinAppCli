// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Composite template service that aggregates all registered <see cref="ITemplateProvider"/> instances.
/// Provides a unified view of templates across all languages (C#, C++, etc.).
/// To add a new language, register a new <see cref="ITemplateProvider"/> implementation in DI.
/// </summary>
internal class TemplateService(IEnumerable<ITemplateProvider> providers) : ITemplateService
{
    private IReadOnlyList<TemplateInfo>? _cachedTemplates;

    /// <inheritdoc />
    public async Task EnsureAllProvidersAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            await provider.EnsureAvailableAsync(cancellationToken);
        }
        _cachedTemplates = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateInfo>> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedTemplates is not null)
        {
            return _cachedTemplates;
        }

        var allTemplates = new List<TemplateInfo>();
        foreach (var provider in providers)
        {
            var templates = await provider.GetTemplatesAsync(cancellationToken);
            allTemplates.AddRange(templates);
        }

        _cachedTemplates = allTemplates;
        return allTemplates;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateInfo>> GetProjectTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAvailableTemplatesAsync(cancellationToken);
        return all.Where(t => t.Type == TemplateType.Project).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateInfo>> GetItemTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAvailableTemplatesAsync(cancellationToken);
        return all.Where(t => t.Type == TemplateType.Item).ToList();
    }

    /// <inheritdoc />
    public async Task<(int ExitCode, string Output, string Error)> CreateFromTemplateAsync(
        string shortName,
        string name,
        DirectoryInfo? outputDir,
        FileInfo? projectFile,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyList<string>? extraArgs,
        CancellationToken cancellationToken = default)
    {
        // Build a shortName → provider lookup from cached templates
        var allTemplates = await GetAvailableTemplatesAsync(cancellationToken);
        var targetTemplate = allTemplates.FirstOrDefault(t =>
            string.Equals(t.ShortName, shortName, StringComparison.OrdinalIgnoreCase));

        if (targetTemplate is null)
        {
            return (1, string.Empty, $"No template provider found for template '{shortName}'.");
        }

        // Find the provider that owns this template's language
        foreach (var provider in providers)
        {
            if (string.Equals(provider.Language, targetTemplate.Language, StringComparison.Ordinal))
            {
                return await provider.CreateAsync(shortName, name, outputDir, projectFile, parameters, extraArgs, cancellationToken);
            }
        }

        return (1, string.Empty, $"No template provider found for template '{shortName}'.");
    }
}
