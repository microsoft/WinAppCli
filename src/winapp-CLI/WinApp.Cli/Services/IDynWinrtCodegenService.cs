// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Generates JS / TS / Python WinRT bindings via @microsoft/dynwinrt-codegen.
internal interface IDynWinrtCodegenService
{
    // One bulk pass + one pass per extraTypes entry.
    Task<DirectoryInfo> RunAsync(
        JsBindingsConfig config,
        IReadOnlyList<FileInfo> winmds,
        FileInfo? windowsSdkWinmd,
        DirectoryInfo workspaceDir,
        DirectoryInfo winappDir,
        TaskContext taskContext,
        IReadOnlyList<FileInfo>? userAdditionalWinmds = null,
        IReadOnlyList<FileInfo>? userAdditionalRefs = null,
        CancellationToken cancellationToken = default);
}
