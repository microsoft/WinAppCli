// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Test double for <see cref="ITemplateCacheReader"/>. Returns whatever <c>templatecache.json</c>
/// documents a test scripts, so template-metadata driven behavior (target-framework resolution) can be
/// exercised hermetically without touching the machine's real template engine cache. Defaults to an
/// empty set, which drives the command's heuristic fallback path.
/// </summary>
internal sealed class FakeTemplateCacheReader : ITemplateCacheReader
{
    public List<string> Documents { get; } = [];

    public IReadOnlyList<string> ReadTemplateCacheDocuments() => Documents;
}
