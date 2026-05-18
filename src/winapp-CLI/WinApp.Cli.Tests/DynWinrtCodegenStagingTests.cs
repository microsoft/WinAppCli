// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// split of the historical DynWinrtCodegenServiceTests.
// Scope: RunWithStagingAsync — staging/swap failure-safety contract.
[TestClass]
public class DynWinrtCodegenStagingTests
{
    public TestContext TestContext { get; set; } = null!;

    private DirectoryInfo _temp = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"DynWinrtCodegenStagingTests_{Guid.NewGuid():N}"));
        _temp.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    [TestMethod]
    public async Task RunWithStagingAsync_Success_SwapsStagingIntoOutputDir()
    {
        var outputDir = new DirectoryInfo(Path.Combine(_temp.FullName, "out"));

        await DynWinrtCodegenService.RunWithStagingAsync(outputDir, stagingDir =>
        {
            File.WriteAllText(Path.Combine(stagingDir.FullName, "Foo.js"), "// stub");
            File.WriteAllText(Path.Combine(stagingDir.FullName, "Bar.js"), "// stub");
            return Task.CompletedTask;
        });

        outputDir.Refresh();
        Assert.IsTrue(outputDir.Exists, "Output dir must exist after success");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Foo.js")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Bar.js")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, DynWinrtCodegenService.ManagedMarkerFileName)),
            "Marker must be present so subsequent runs are allowed to wipe.");

        var leftovers = outputDir.Parent!
            .EnumerateDirectories($"{outputDir.Name}.staging.*")
            .ToList();
        Assert.AreEqual(0, leftovers.Count,
            $"Staging dirs must be cleaned up; found: {string.Join(", ", leftovers.Select(d => d.Name))}");
    }

    [TestMethod]
    public async Task RunWithStagingAsync_Failure_PreservesOldOutputAndCleansStaging()
    {
        var outputDir = new DirectoryInfo(Path.Combine(_temp.FullName, "out"));
        outputDir.Create();

        File.WriteAllText(Path.Combine(outputDir.FullName, "PrevBinding.js"), "// previous output");
        File.WriteAllText(Path.Combine(outputDir.FullName, DynWinrtCodegenService.ManagedMarkerFileName),
            "# managed");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DynWinrtCodegenService.RunWithStagingAsync(outputDir, stagingDir =>
            {
                File.WriteAllText(Path.Combine(stagingDir.FullName, "Half.js"), "// half-written");
                throw new InvalidOperationException("simulated codegen crash");
            }));

        outputDir.Refresh();
        Assert.IsTrue(outputDir.Exists, "Previous output dir must be preserved on failure.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "PrevBinding.js")),
            "Previous bindings must survive a failed regeneration — this is the whole point of staging.");
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Half.js")),
            "Half-written staging file must NOT bleed into the output dir.");

        var leftovers = outputDir.Parent!
            .EnumerateDirectories($"{outputDir.Name}.staging.*")
            .ToList();
        Assert.AreEqual(0, leftovers.Count,
            $"Staging dirs must be cleaned up after failure; found: {string.Join(", ", leftovers.Select(d => d.Name))}");
    }

    [TestMethod]
    public async Task RunWithStagingAsync_PreservesContentsOnSuccess()
    {
        var outputDir = new DirectoryInfo(Path.Combine(_temp.FullName, "out"));

        await DynWinrtCodegenService.RunWithStagingAsync(outputDir, stagingDir =>
        {
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(stagingDir.FullName, $"F{i}.js"), $"// {i}");
            }
            Directory.CreateDirectory(Path.Combine(stagingDir.FullName, "sub"));
            File.WriteAllText(Path.Combine(stagingDir.FullName, "sub", "deep.js"), "// nested");
            return Task.CompletedTask;
        });

        outputDir.Refresh();
        var jsFiles = outputDir.EnumerateFiles("*.js").Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.AreEqual(10, jsFiles.Count, "All 10 .js files from staging must land in output dir.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "sub", "deep.js")),
            "Nested files in staging must survive the swap.");
    }

    [TestMethod]
    public async Task RunWithStagingAsync_OldOutputWithoutMarker_Throws_StagingCleaned()
    {
        var outputDir = new DirectoryInfo(Path.Combine(_temp.FullName, "out"));
        outputDir.Create();
        File.WriteAllText(Path.Combine(outputDir.FullName, "user-handwritten.js"), "important");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DynWinrtCodegenService.RunWithStagingAsync(outputDir, stagingDir =>
            {
                File.WriteAllText(Path.Combine(stagingDir.FullName, "Generated.js"), "// new");
                return Task.CompletedTask;
            }));

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "user-handwritten.js")),
            "Non-managed user file must survive — WipeOutputDirSafely refused.");

        var leftovers = outputDir.Parent!
            .EnumerateDirectories($"{outputDir.Name}.staging.*")
            .ToList();
        Assert.AreEqual(0, leftovers.Count,
            $"Staging dirs must be cleaned up even when swap fails; found: {string.Join(", ", leftovers.Select(d => d.Name))}");
    }

    // Failure during the swap step — backup restore succeeds; everything cleaned up.
    [TestMethod]
    public async Task RunWithStagingAsync_SwapStepFailure_RestoresOldOutputAndCleansStaging()
    {
        var outputDir = new DirectoryInfo(Path.Combine(_temp.FullName, "out"));
        outputDir.Create();
        File.WriteAllText(Path.Combine(outputDir.FullName, "Prev.js"), "// prev");
        File.WriteAllText(Path.Combine(outputDir.FullName, DynWinrtCodegenService.ManagedMarkerFileName),
            "# managed");

        var lockPath = Path.Combine(outputDir.FullName, "lock-file");
        using (var blocker = new FileStream(lockPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await DynWinrtCodegenService.RunWithStagingAsync(outputDir, stagingDir =>
                {
                    File.WriteAllText(Path.Combine(stagingDir.FullName, "New.js"), "// new");
                    return Task.CompletedTask;
                }));
        }

        var stagingLeftovers = outputDir.Parent!
            .EnumerateDirectories($"{outputDir.Name}.staging.*")
            .ToList();
        Assert.AreEqual(0, stagingLeftovers.Count,
            "Staging must be cleaned up after a swap-step failure.");

        var backupLeftovers = outputDir.Parent!
            .EnumerateDirectories($"{outputDir.Name}.backup.*")
            .ToList();
        Assert.AreEqual(0, backupLeftovers.Count,
            "Backup must be cleaned up after a swap-step failure.");

        outputDir.Refresh();
        Assert.IsTrue(outputDir.Exists, "Old output dir must still exist.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Prev.js")),
            "Previous bindings must be preserved — that's the whole point of staging.");
    }

    // the catch block in RunWithStagingAsync preserves the
    // backup directory on disk when the restore Move also fails, and
    // surfaces the preserved path in the thrown IOException so the user
    // can recover manually.
    //
    // This branch cannot be exercised deterministically without
    // file-system hooks: triggering it would require both the
    // staging→outputDir Move AND the backup→outputDir restore Move to
    // fail in sequence within the same call, which is essentially
    // impossible to inject from outside the function. Coverage is
    // delegated to code review of the catch block — the behavior is
    // mechanically verified there. See review #4 M1.
}
