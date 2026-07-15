// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="PriService"/>. MakePri.exe is never invoked for real; instead a fake
/// <see cref="IBuildToolsService"/> stands in and (for the config/dump flows) writes the XML output
/// files that the real tool would produce so the service's XML manipulation and parsing can be exercised.
/// </summary>
[TestClass]
public class PriServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _packageDir = null!;
    private FakeBuildToolsService _buildTools = null!;
    private PriService _service = null!;
    private TaskContext _taskContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"PriSvcTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _packageDir = _tempDir.CreateSubdirectory("package");
        _buildTools = new FakeBuildToolsService();
        _service = new PriService(_buildTools);

        var task = new GroupableTask("test", null);
        _taskContext = new TaskContext(task, null, new TestConsole(), NullLogger<PriServiceTests>.Instance, new Lock());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private const string BasePriConfig = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<resources targetOsVersion=""10.0.0"" majorVersion=""1"">
  <index root=""\"" startIndexAt=""\"">
    <default>
      <qualifier name=""Language"" value=""en-US"" />
    </default>
    <indexer-config type=""folder"" qualifierDelimiter=""."" />
    <indexer-config type=""PRI"" />
  </index>
</resources>";

    #region CreatePriConfigAsync

    [TestMethod]
    public async Task CreatePriConfigAsync_WritesResfiles_FilteringToImageExtensions()
    {
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
            return ("", "");
        };

        var candidates = new[]
        {
            "Assets\\Logo.png",
            "Assets\\Splash.jpg",
            "readme.txt",       // filtered out (not an image extension)
            "app.dll",          // filtered out
            "Assets\\Icon.ico",
        };

        await _service.CreatePriConfigAsync(_packageDir, _taskContext, candidates);

        var resfiles = await File.ReadAllTextAsync(Path.Combine(_packageDir.FullName, "pri.resfiles"));
        StringAssert.Contains(resfiles, "Assets\\Logo.png");
        StringAssert.Contains(resfiles, "Assets\\Splash.jpg");
        StringAssert.Contains(resfiles, "Assets\\Icon.ico");
        Assert.IsFalse(resfiles.Contains("readme.txt", StringComparison.Ordinal));
        Assert.IsFalse(resfiles.Contains("app.dll", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_InvokesMakePriCreateConfig()
    {
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
            return ("", "");
        };

        await _service.CreatePriConfigAsync(_packageDir, _taskContext, ["a.png"], language: "fr-FR", platformVersion: "10.0.1");

        Assert.AreEqual(1, _buildTools.Invocations.Count);
        var (tool, args) = _buildTools.Invocations[0];
        Assert.AreEqual("makepri.exe", tool);
        StringAssert.Contains(args, "createconfig");
        StringAssert.Contains(args, "lang-fr-FR");
        StringAssert.Contains(args, "/pv 10.0.1");
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_InjectsResfilesIndexerAndFolderQualifierAttributes()
    {
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
            return ("", "");
        };

        var configPath = await _service.CreatePriConfigAsync(_packageDir, _taskContext, ["Logo.png"]);

        var xml = await File.ReadAllTextAsync(configPath.FullName);
        // A resfiles indexer-config must be appended.
        StringAssert.Contains(xml, "type=\"resfiles\"");
        // startIndexAt must be rewritten to the relative resfiles path.
        StringAssert.Contains(xml, ".\\pri.resfiles");
        // Folder indexer must be augmented with both qualifier flags (created since they were absent).
        StringAssert.Contains(xml, "foldernameAsQualifier=\"true\"");
        StringAssert.Contains(xml, "filenameAsQualifier=\"true\"");
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_UpdatesExistingFolderQualifierAttributes()
    {
        // Folder indexer already declares the attributes (with false) — service should flip them to true.
        const string configWithFolderAttrs = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<resources targetOsVersion=""10.0.0"" majorVersion=""1"">
  <index root=""\"" startIndexAt=""\"">
    <indexer-config type=""folder"" foldernameAsQualifier=""false"" filenameAsQualifier=""false"" qualifierDelimiter=""."" />
  </index>
</resources>";
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), configWithFolderAttrs);
            return ("", "");
        };

        var configPath = await _service.CreatePriConfigAsync(_packageDir, _taskContext, ["Logo.png"]);

        var xml = await File.ReadAllTextAsync(configPath.FullName);
        StringAssert.Contains(xml, "foldernameAsQualifier=\"true\"");
        StringAssert.Contains(xml, "filenameAsQualifier=\"true\"");
        Assert.IsFalse(xml.Contains("=\"false\"", StringComparison.Ordinal), "Existing false flags should have been flipped to true.");
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_MissingPackageDir_ThrowsDirectoryNotFound()
    {
        var missing = new DirectoryInfo(Path.Combine(_tempDir.FullName, "missing"));

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            _service.CreatePriConfigAsync(missing, _taskContext, ["a.png"]));
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_NullCandidates_ThrowsArgumentNull()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            _service.CreatePriConfigAsync(_packageDir, _taskContext, null!));
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_ToolFailure_WrappedInInvalidOperationException()
    {
        _buildTools.Handler = (_, _) => throw new InvalidOperationException("makepri boom");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.CreatePriConfigAsync(_packageDir, _taskContext, ["a.png"]));
        StringAssert.Contains(ex.Message, "Failed to create PRI configuration");
    }

    #endregion

    #region GeneratePriFileAsync

    [TestMethod]
    public async Task GeneratePriFileAsync_ParsesResourceFilesFromOutput()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) =>
            ("Some header line\r\nResource File: resources.pri\r\nResource File: fr-FR\\resources.pri\r\nTrailing", "");

        var result = await _service.GeneratePriFileAsync(_packageDir, _taskContext);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Any(f => f.Name == "resources.pri"));
        Assert.IsTrue(result.Any(f => f.FullName.EndsWith(Path.Combine("fr-FR", "resources.pri"), StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_StripsNullChars_AndIsCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) => ("resource file: a.pri\0\r\n\0RESOURCE FILE: b.pri", "");

        var result = await _service.GeneratePriFileAsync(_packageDir, _taskContext);

        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_NoResourceLines_ReturnsEmptyList()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) => ("Nothing relevant here", "");

        var result = await _service.GeneratePriFileAsync(_packageDir, _taskContext);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_MissingPackageDir_ThrowsDirectoryNotFound()
    {
        var missing = new DirectoryInfo(Path.Combine(_tempDir.FullName, "missing"));

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            _service.GeneratePriFileAsync(missing, _taskContext));
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_MissingConfig_ThrowsFileNotFound()
    {
        // No priconfig.xml written.
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _service.GeneratePriFileAsync(_packageDir, _taskContext));
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_ToolFailure_WrappedInInvalidOperationException()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) => throw new InvalidOperationException("makepri new boom");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.GeneratePriFileAsync(_packageDir, _taskContext));
        StringAssert.Contains(ex.Message, "Failed to generate PRI file");
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_RespectsExplicitConfigAndOutputPaths()
    {
        var customConfig = new FileInfo(Path.Combine(_packageDir.FullName, "custom.xml"));
        File.WriteAllText(customConfig.FullName, BasePriConfig);
        var customOutput = new FileInfo(Path.Combine(_packageDir.FullName, "custom.pri"));

        _buildTools.Handler = (_, args) => (args, "");

        await _service.GeneratePriFileAsync(_packageDir, _taskContext, customConfig, customOutput);

        var (_, invokedArgs) = _buildTools.Invocations[0];
        StringAssert.Contains(invokedArgs, "custom.xml");
        StringAssert.Contains(invokedArgs, "custom.pri");
    }

    #endregion

    #region ExtractLanguagesFromPriAsync

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_ReturnsSortedDistinctLanguages()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");

        const string dump = @"<PriInfo>
  <ResourceMap>
    <Candidate qualifiers=""Language-fr-FR"" />
    <Candidate qualifiers=""Language-en-US, Scale-200"" />
    <Candidate qualifiers=""Language-en-US"" />
    <Candidate qualifiers=""Scale-100"" />
  </ResourceMap>
</PriInfo>";
        _buildTools.Handler = (_, args) =>
        {
            WriteDumpFile(args, dump);
            return ("", "");
        };

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        string[] expected = ["en-US", "fr-FR"];
        CollectionAssert.AreEqual(expected, languages);
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_DeletesDumpFileAfterReading()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        string? dumpPath = null;
        _buildTools.Handler = (_, args) =>
        {
            dumpPath = ExtractOfPath(args);
            File.WriteAllText(dumpPath, "<Candidate qualifiers=\"Language-de-DE\" />");
            return ("", "");
        };

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        string[] expected = ["de-DE"];
        CollectionAssert.AreEqual(expected, languages);
        Assert.IsNotNull(dumpPath);
        Assert.IsFalse(File.Exists(dumpPath), "The temporary dump file should be deleted after parsing.");
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_NoDumpProduced_ReturnsEmpty()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        // Handler writes nothing, so the dump file never exists.
        _buildTools.Handler = (_, _) => ("", "");

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        Assert.AreEqual(0, languages.Count);
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_ToolThrows_ReturnsEmptyWithoutPropagating()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        _buildTools.Handler = (_, _) => throw new InvalidOperationException("makepri dump boom");

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        Assert.AreEqual(0, languages.Count);
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_NoLanguageQualifiers_ReturnsEmpty()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        _buildTools.Handler = (_, args) =>
        {
            WriteDumpFile(args, "<Candidate qualifiers=\"Scale-200\" /><Candidate qualifiers=\"Contrast-high\" />");
            return ("", "");
        };

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        Assert.AreEqual(0, languages.Count);
    }

    #endregion

    private static void WriteDumpFile(string arguments, string content)
    {
        var dumpPath = ExtractOfPath(arguments);
        File.WriteAllText(dumpPath, content);
    }

    /// <summary>Extracts the value of the <c>/of "..."</c> output-file argument, stripping any extended-length prefix.</summary>
    private static string ExtractOfPath(string arguments)
    {
        var match = Regex.Match(arguments, "/of\\s+\"([^\"]+)\"");
        Assert.IsTrue(match.Success, $"Expected an /of argument in: {arguments}");
        var path = match.Groups[1].Value;
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// Fake build-tools service that records invocations and delegates output to a per-test handler.
    /// </summary>
    private sealed class FakeBuildToolsService : IBuildToolsService
    {
        public List<(string Tool, string Args)> Invocations { get; } = [];
        public Func<Tool, string, (string stdout, string stderr)>? Handler { get; set; }

        public FileInfo? GetBuildToolPath(string toolName) => new(Path.Combine(Path.GetTempPath(), toolName));

        public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
            => Task.FromResult(new FileInfo(Path.Combine(Path.GetTempPath(), toolName)));

        public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
            => Task.FromResult<DirectoryInfo?>(null);

        public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, CancellationToken cancellationToken = default)
        {
            Invocations.Add((tool.ExecutableName, arguments));
            var result = Handler?.Invoke(tool, arguments) ?? ("", "");
            return Task.FromResult(result);
        }
    }
}
