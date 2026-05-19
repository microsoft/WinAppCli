// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Parameters for workspace setup operations
internal class WorkspaceSetupOptions
{
    public required DirectoryInfo BaseDirectory { get; set; }
    public required DirectoryInfo ConfigDir { get; set; }
    public SdkInstallMode? SdkInstallMode { get; set; }
    public bool IgnoreConfig { get; set; }
    public bool NoGitignore { get; set; }
    public bool UseDefaults { get; set; }
    public bool RequireExistingConfig { get; set; }
    public bool ForceLatestBuildTools { get; set; }
    public bool ConfigOnly { get; set; }

    // Enable JS/TS bindings generation in Step 5.5 of setup.
    public bool AddJsBindings { get; set; }

    // CLI override for jsBindings.output.
    public string? JsBindingsOutputOverride { get; set; }

    // CLI override for jsBindings.lang.
    public string? JsBindingsLangOverride { get; set; }

    // Preset names from JsBindingsPresets — unioned into jsBindings.packages.
    public IReadOnlyList<string>? JsBindingsPresets { get; set; }
}

// Params for AddJsBindingsAsync.
internal class AddJsBindingsOptions
{
    public required DirectoryInfo BaseDirectory { get; set; }
    public required DirectoryInfo ConfigDir { get; set; }

    // CLI override for jsBindings.output.
    public string? Output { get; set; }

    // Preset names from JsBindingsPresets.
    public IReadOnlyList<string>? Presets { get; set; }

    // Patch an existing jsBindings: block without prompting.
    public bool Force { get; set; }

    // Preserve an existing jsBindings: block and exit 0 without prompting.
    // Mutually exclusive with Force.
    public bool UseDefaults { get; set; }
}

// Params for the read-only `node jsbindings generate` flow.
internal class GenerateJsBindingsOptions
{
    public required DirectoryInfo BaseDirectory { get; set; }
    public required DirectoryInfo ConfigDir { get; set; }
}
