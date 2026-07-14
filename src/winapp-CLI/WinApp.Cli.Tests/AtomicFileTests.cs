// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class AtomicFileTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AtomicFile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task WriteAllBytesAsync_WritesContentAndLeavesNoTempFiles()
    {
        var dest = Path.Combine(_tempDir, "out.bin");
        var bytes = Encoding.UTF8.GetBytes("hello atomic");

        await AtomicFile.WriteAllBytesAsync(dest, bytes, CancellationToken.None);

        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(dest));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.tmp").Length, "No leftover temp files must remain.");
    }

    [TestMethod]
    public void Copy_OverwritesExistingDestinationAtomically()
    {
        var source = Path.Combine(_tempDir, "src.bin");
        var dest = Path.Combine(_tempDir, "dst.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(dest, "old");

        AtomicFile.Copy(source, dest);

        Assert.AreEqual("new", File.ReadAllText(dest));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, "*.tmp").Length);
    }

    [TestMethod]
    public async Task WriteStagedAsync_DoesNotPublishUntilPublishCalled()
    {
        var dest = Path.Combine(_tempDir, "staged.bin");
        var bytes = Encoding.UTF8.GetBytes("staged content");

        var staged = await AtomicFile.WriteStagedAsync(dest, bytes, CancellationToken.None);

        Assert.IsTrue(File.Exists(staged), "The staged temp file must exist.");
        Assert.IsFalse(File.Exists(dest), "The destination must not exist before Publish is called.");
        Assert.AreNotEqual(dest, staged, "The staged path must differ from the final destination.");

        AtomicFile.Publish(staged, dest);

        Assert.IsTrue(File.Exists(dest), "After Publish, the destination must exist.");
        Assert.IsFalse(File.Exists(staged), "After Publish, the staged temp file must be gone (moved).");
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(dest));
    }

    [TestMethod]
    public async Task DiscardStaged_RemovesStagedFileAndLeavesDestinationAbsent()
    {
        var dest = Path.Combine(_tempDir, "discard.bin");
        var staged = await AtomicFile.WriteStagedAsync(dest, [1, 2, 3], CancellationToken.None);

        AtomicFile.DiscardStaged(staged);

        Assert.IsFalse(File.Exists(staged), "The staged temp file must be deleted.");
        Assert.IsFalse(File.Exists(dest), "The destination must never have been created.");
    }
}
