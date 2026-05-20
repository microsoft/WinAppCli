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

    // Enable JS/TS bindings generation in Step 5.5 of setup. Populated by the
    // npm-caller prompt in WorkspaceSetupService; no CLI flag exposes this.
    public bool AddJsBindings { get; set; }

    // Skip cppwinrt headers/libs/runtimes/projection generation. Populated by the
    // npm-caller prompt when the user picks "JS only" so pure-Node projects don't
    // pay the ~130MB / ~20s C++ projection cost.
    public bool SkipCppProjections { get; set; }
}
