// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="CreateExternalCatalogCommand"/>'s output-path resolution. A fake catalog
/// service records the resolved <see cref="FileInfo"/> so the command's ResolveOutputCatalogPath
/// branches (default name, existing directory, and extension-less path) are verified without
/// invoking the real makeappx-based catalog tooling.
/// </summary>
[TestClass]
public class CreateExternalCatalogCommandOutputPathTests : BaseCommandTests
{
    private FakeCodeIntegrityCatalogService _fakeCatalog = null!;
    private string _inputDir = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeCatalog = new FakeCodeIntegrityCatalogService();
        return services.AddSingleton<ICodeIntegrityCatalogService>(_fakeCatalog);
    }

    [TestInitialize]
    public void CreateInput()
    {
        _inputDir = Path.Combine(_tempDirectory.FullName, "input");
        Directory.CreateDirectory(_inputDir);
    }

    [TestMethod]
    public async Task Output_NotSpecified_UsesDefaultCatalogName()
    {
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_inputDir]);

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(_fakeCatalog.LastOutput);
        Assert.AreEqual("CodeIntegrityExternal.cat", _fakeCatalog.LastOutput!.Name);
    }

    [TestMethod]
    public async Task Output_ExistingDirectory_AppendsDefaultCatalogName()
    {
        var outDir = Path.Combine(_tempDirectory.FullName, "outdir");
        Directory.CreateDirectory(outDir);
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_inputDir, "--output", outDir]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("CodeIntegrityExternal.cat", _fakeCatalog.LastOutput!.Name);
        Assert.AreEqual(Path.GetFullPath(outDir), _fakeCatalog.LastOutput.Directory!.FullName);
    }

    [TestMethod]
    public async Task Output_PathWithoutExtension_GetsCatExtension()
    {
        var command = GetRequiredService<CreateExternalCatalogCommand>();
        var outNoExt = Path.Combine(_tempDirectory.FullName, "mycatalog");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_inputDir, "--output", outNoExt]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("mycatalog.cat", _fakeCatalog.LastOutput!.Name);
    }

    [TestMethod]
    public async Task InputFolders_SplitOnSemicolon_ForwardedToService()
    {
        var second = Path.Combine(_tempDirectory.FullName, "input2");
        Directory.CreateDirectory(second);
        var command = GetRequiredService<CreateExternalCatalogCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [$"{_inputDir};{second}", "--recursive", "--use-page-hashes"]);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(2, _fakeCatalog.LastDirectories!);
        Assert.IsTrue(_fakeCatalog.LastRecursive);
        Assert.IsTrue(_fakeCatalog.LastUsePageHashes);
    }

    private sealed class FakeCodeIntegrityCatalogService : ICodeIntegrityCatalogService
    {
        public FileInfo? LastOutput { get; private set; }
        public List<string>? LastDirectories { get; private set; }
        public bool LastRecursive { get; private set; }
        public bool LastUsePageHashes { get; private set; }

        public Task CreateExternalCatalogAsync(List<string> directories, bool recursive, bool usePageHashes, bool computeFlatHashes, IfExists ifExists, FileInfo output)
        {
            LastDirectories = directories;
            LastRecursive = recursive;
            LastUsePageHashes = usePageHashes;
            LastOutput = output;
            return Task.CompletedTask;
        }
    }
}
