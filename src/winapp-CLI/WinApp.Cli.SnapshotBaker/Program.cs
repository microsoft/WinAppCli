// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

// Regenerates the find-ui corpus committed under
// src/winapp-CLI/WinApp.Cli/Services/Controls/Data and embedded in the shipped CLI.
//
// Run by scripts/build-cli.ps1 on -Stable, and by the find-ui corpus drift workflow
// (which bakes to a throwaway directory purely to compare against what is committed).
// It is intentionally not reachable from the winapp CLI itself — see the csproj.
//
// Exit codes: 0 = complete bake, 1 = usage error or an incomplete/failed bake.
// A partial result is never written as if it were complete, so a non-zero exit means
// the committed corpus is untouched and safe to keep.

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("usage: WinApp.Cli.SnapshotBaker <output-directory>");
    Console.Error.WriteLine(
        "  e.g. dotnet run --project src/winapp-CLI/WinApp.Cli.SnapshotBaker -- " +
        "src/winapp-CLI/WinApp.Cli/Services/Controls/Data");
    return 1;
}

// Ctrl+C should abandon the bake rather than leave it half-written; BakeAsync writes
// each snapshot atomically and only writes the manifest after every source succeeds.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var failures = await SnapshotBaker
        .BakeAsync(args[0], Console.WriteLine, cancellation.Token)
        .ConfigureAwait(false);

    if (failures.Count > 0)
    {
        Console.Error.WriteLine(
            $"Bake incomplete — no fresh data fetched for: {string.Join(", ", failures)}. " +
            "The existing committed snapshot was left untouched.");
        return 1;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Bake cancelled.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Bake failed: {ex.Message}");
    return 1;
}
