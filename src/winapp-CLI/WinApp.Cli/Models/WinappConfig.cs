// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal sealed class WinappConfig
{
    public List<PackagePin> Packages { get; set; } = new();

    // Optional JS/TS bindings; when set, restore runs the codegen step.
    public JsBindingsConfig? JsBindings { get; set; }

    // Whether to generate C++/WinRT projections (cppwinrt headers + headers/libs/runtimes
    // copy). Default true preserves the pre-existing behavior; only `winapp init` writes
    // `false` when the npm caller picks "JS only" so pure-Node projects skip ~130MB of
    // cppwinrt output. Yaml key: `cppProjections`.
    public bool CppProjections { get; set; } = true;

    public string? GetVersion(string name)
        => Packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Version;

    public void SetVersion(string name, string version)
    {
        var existing = Packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Packages.Add(new PackagePin { Name = name, Version = version });
        }
        else
        {
            existing.Version = version;
        }
    }
}
