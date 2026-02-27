// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface ICodeIntegrityCatalogService
{
    Task CreateExternalCatalogAsync(List<string> directories, bool recursive, bool usePageHashes, bool computeFlatHashes, IfExists ifExists, FileInfo output);
}
