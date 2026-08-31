// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Security.Cryptography;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AnalyzerInjectionServiceTests
{
    private string _cacheRoot = null!;
    private AnalyzerInjectionService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), $"AnalyzerInject_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheRoot);
        _service = new AnalyzerInjectionService(new StubWinappDirectoryService(new DirectoryInfo(_cacheRoot)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cacheRoot))
        {
            try { Directory.Delete(_cacheRoot, true); } catch { }
        }
    }

    [TestMethod]
    public void PrepareInjection_ExtractsAnalyzerAndHookProps()
    {
        AnalyzerInjection? injection = _service.PrepareInjection();

        Assert.IsNotNull(injection, "The analyzer DLL should be embedded in WinApp.Cli and extractable.");
        Assert.IsTrue(File.Exists(injection.AnalyzerDllPath), "Analyzer DLL must be written to the cache.");
        Assert.IsTrue(File.Exists(injection.HookPropsPath), "Hook props must be written to the cache.");
        Assert.AreEqual(
            AnalyzerInjectionService.AnalyzerDllFileName,
            Path.GetFileName(injection.AnalyzerDllPath));
        Assert.AreEqual(
            AnalyzerInjectionService.HookPropsFileName,
            Path.GetFileName(injection.HookPropsPath));
    }

    [TestMethod]
    public void PrepareInjection_ExtractedDllIsByteIdenticalToEmbeddedResource()
    {
        AnalyzerInjection injection = _service.PrepareInjection()!;

        using Stream resource = typeof(AnalyzerInjectionService).Assembly
            .GetManifestResourceStream(AnalyzerInjectionService.AnalyzerResourceName)!;
        using MemoryStream ms = new();
        resource.CopyTo(ms);
        byte[] embedded = ms.ToArray();
        byte[] extracted = File.ReadAllBytes(injection.AnalyzerDllPath);

        CollectionAssert.AreEqual(embedded, extracted);
    }

    [TestMethod]
    public void PrepareInjection_ContentHashMatchesDllAndKeysTheCacheDirectory()
    {
        AnalyzerInjection injection = _service.PrepareInjection()!;

        string dllHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(injection.AnalyzerDllPath)));
        Assert.AreEqual(dllHash, injection.ContentHash, "ContentHash must be the SHA-256 of the analyzer DLL.");

        // Cache is keyed by the content hash: the parent directory name is the hash.
        string parentDir = Path.GetFileName(Path.GetDirectoryName(injection.AnalyzerDllPath)!);
        Assert.AreEqual(injection.ContentHash, parentDir);
    }

    [TestMethod]
    public void PrepareInjection_HookPropsContainsAnalyzerXamlAndChainedImport()
    {
        AnalyzerInjection injection = _service.PrepareInjection()!;
        string props = File.ReadAllText(injection.HookPropsPath);

        StringAssert.Contains(props, "<Analyzer Include=\"$(MSBuildThisFileDirectory)Microsoft.WindowsAppSDK.Analyzers.dll\" />");
        StringAssert.Contains(props, "<AdditionalFiles Include=\"@(Page)\"");
        StringAssert.Contains(props, "BeforeTargets=\"CoreCompile\"");
        StringAssert.Contains(props, "$(_WinAppChainedCustomAfter)");
    }

    [TestMethod]
    public void PrepareInjection_HookPropsGatesAnalyzerAndXamlOnUseWinUI()
    {
        // The global -p: carrying the hook propagates into the whole ProjectReference closure, so both
        // the <Analyzer> ItemGroup and the XAML target must be gated on UseWinUI (design M1) to stay off
        // any non-WinUI project the closure includes.
        AnalyzerInjection injection = _service.PrepareInjection()!;
        string props = File.ReadAllText(injection.HookPropsPath);

        StringAssert.Contains(props, "<ItemGroup Condition=\"'$(UseWinUI)' == 'true'\">");
        StringAssert.Contains(props, "Condition=\"'$(UseWinUI)' == 'true'\">");
    }

    [TestMethod]
    public void PrepareInjection_HookPropsExemptsWuiIdsFromWarningsAsErrors()
    {
        // 'winapp run' injects the analyzer without the user asking, so under TreatWarningsAsErrors=true
        // the WUIxxxx warnings must NOT be promoted to build-breaking errors (the "warnings only" contract).
        AnalyzerInjection injection = _service.PrepareInjection()!;
        string props = File.ReadAllText(injection.HookPropsPath);

        StringAssert.Contains(props, "<WarningsNotAsErrors>");
        StringAssert.Contains(props, "WUI0001");
        StringAssert.Contains(props, "WUI4103");
    }

    [TestMethod]
    public void PrepareInjection_IsIdempotentAndLeavesNoTempFiles()
    {
        AnalyzerInjection first = _service.PrepareInjection()!;
        AnalyzerInjection second = _service.PrepareInjection()!;

        Assert.AreEqual(first.ContentHash, second.ContentHash);
        Assert.AreEqual(first.HookPropsPath, second.HookPropsPath);
        Assert.AreEqual(first.AnalyzerDllPath, second.AnalyzerDllPath);

        string cacheDir = Path.GetDirectoryName(first.AnalyzerDllPath)!;
        Assert.AreEqual(0, Directory.GetFiles(cacheDir, "*.tmp").Length, "No leftover temp files must remain.");
    }
}
