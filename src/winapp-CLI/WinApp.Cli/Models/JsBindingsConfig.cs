// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

// User-facing configuration for JS/TS bindings generated from WinRT metadata.
// Materialized from the optional jsBindings: block in winapp.yaml.
internal sealed class JsBindingsConfig
{
    // Target language. Currently js (default) or py.
    public string Lang { get; set; } = "js";

    // Output directory, relative to the workspace root.
    public string Output { get; set; } = "bindings/winrt";

    // NuGet package IDs to scope binding generation to (empty = all).
    public List<string> Packages { get; set; } = new();

    // Individual classes to generate alongside the bulk pass.
    public List<JsBindingsExtraType> ExtraTypes { get; set; } = new();

    // Extra .winmd files to emit bindings for.
    public List<string> AdditionalWinmds { get; set; } = new();

    // Extra .winmd files loaded for type resolution only.
    public List<string> AdditionalRefs { get; set; } = new();

    // NuGet package IDs to drop entirely.
    public List<string> SkipPackages { get; set; } = new();

    // NuGet package IDs to load as --ref only.
    public List<string> RefOnlyPackages { get; set; } = new();

    // NuGet package IDs to force-emit, overriding skip / ref-only.
    public List<string> EmitPackages { get; set; } = new();
}

// One namespace + class-name list for selective generation.
internal sealed class JsBindingsExtraType
{
    public string Namespace { get; set; } = string.Empty;
    public List<string> Classes { get; set; } = new();
}
