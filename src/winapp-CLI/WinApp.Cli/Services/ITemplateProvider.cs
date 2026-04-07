// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// A language-specific template provider that can discover and create templates.
/// Each provider handles one language/toolchain (e.g., C# via dotnet new, C++ via embedded templates).
/// Register implementations as <c>ITemplateProvider</c> singletons in DI to make them available.
/// </summary>
internal interface ITemplateProvider
{
    /// <summary>
    /// Display name for the language/toolchain this provider handles (e.g., "C#", "C++").
    /// Used to group templates in the interactive UI.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Ensures the provider's templates are available and up-to-date.
    /// For example, a dotnet provider may run <c>dotnet new install</c>;
    /// a C++ provider may extract embedded resources.
    /// </summary>
    Task EnsureAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers all templates this provider offers.
    /// </summary>
    Task<IReadOnlyList<TemplateInfo>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a project or item from one of this provider's templates.
    /// </summary>
    /// <param name="shortName">Template short name.</param>
    /// <param name="name">Name for the created output.</param>
    /// <param name="outputDir">Output directory (for project templates). Null for item templates or to use current directory.</param>
    /// <param name="projectFile">Target project file (for item templates). Null for project templates.</param>
    /// <param name="parameters">Template-specific parameters as key-value pairs.</param>
    /// <param name="extraArgs">Additional raw arguments to pass through to the underlying tool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<(int ExitCode, string Output, string Error)> CreateAsync(
        string shortName,
        string name,
        DirectoryInfo? outputDir,
        FileInfo? projectFile,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyList<string>? extraArgs,
        CancellationToken cancellationToken = default);
}
