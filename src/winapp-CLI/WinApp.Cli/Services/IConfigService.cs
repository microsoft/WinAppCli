// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IConfigService
{
    FileInfo ConfigPath { get; set; }
    bool Exists();
    WinappConfig Load();

    // Full save. Drops comments / unknown fields.
    void Save(WinappConfig cfg);

    // Splice only the jsBindings: block; preserves rest of yaml.
    void SaveJsBindingsOnly(WinappConfig cfg);
}
