// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// split of the historical DynWinrtCodegenServiceTests.
// Scope: argv construction (BuildBulkArgs / BuildExtraTypeArgs) +
// upstream input-collection helpers that feed the argv builders.
[TestClass]
public class DynWinrtCodegenArgvTests
{
    public TestContext TestContext { get; set; } = null!;

    private DirectoryInfo _temp = null!;

    [TestInitialize]
    public void Init()
    {
        _temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"DynWinrtCodegenArgvTests_{Guid.NewGuid():N}"));
        _temp.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _temp.Delete(recursive: true); } catch { /* ignore */ }
    }

    // -------------------------------------------------------------------------
    // CollectListedWinmds — dedup + Windows SDK appended at the end.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void CollectListedWinmds_DedupesAcrossSources()
    {
        var a = new FileInfo(Path.Combine(_temp.FullName, "A.winmd"));
        var b = new FileInfo(Path.Combine(_temp.FullName, "B.winmd"));
        var sdk = new FileInfo(Path.Combine(_temp.FullName, "Windows.winmd"));
        var winmds = new[] { a, b };
        var userAdditional = new[] { b, a };

        var result = DynWinrtCodegenService.CollectListedWinmds(winmds, userAdditional, sdk);

        Assert.AreEqual(3, result.Count, "Three unique winmds expected after dedup.");
        Assert.AreEqual(a.FullName, result[0].FullName);
        Assert.AreEqual(b.FullName, result[1].FullName);
        Assert.AreEqual(sdk.FullName, result[2].FullName, "Windows SDK winmd must come last.");
    }

    [TestMethod]
    public void CollectListedWinmds_NullWindowsSdkWinmd_OmittedSilently()
    {
        var a = new FileInfo(Path.Combine(_temp.FullName, "A.winmd"));
        var result = DynWinrtCodegenService.CollectListedWinmds(new[] { a }, userAdditional: null, windowsSdkWinmd: null);
        Assert.AreEqual(1, result.Count);
    }

    // -------------------------------------------------------------------------
    // CollectRefWinmds — additionalWinmds wins over additionalRefs.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void CollectRefWinmds_FileAlsoInRsp_DroppedFromRefs()
    {
        var shared = new FileInfo(Path.Combine(_temp.FullName, "Shared.winmd"));
        var refOnly = new FileInfo(Path.Combine(_temp.FullName, "RefOnly.winmd"));
        var list = new[] { shared };
        var refs = new[] { shared, refOnly };

        var result = DynWinrtCodegenService.CollectRefWinmds(refs, list);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(refOnly.FullName, result[0].FullName,
            "When a path appears in both additionalWinmds and additionalRefs, additionalWinmds wins so refs only keeps the unique entry.");
    }

    [TestMethod]
    public void CollectRefWinmds_NullOrEmpty_ReturnsEmpty()
    {
        var list = Array.Empty<FileInfo>();
        Assert.AreEqual(0, DynWinrtCodegenService.CollectRefWinmds(null, list).Count);
        Assert.AreEqual(0, DynWinrtCodegenService.CollectRefWinmds(Array.Empty<FileInfo>(), list).Count);
    }

    // -------------------------------------------------------------------------
    // ScopeUsedVersionsToBindingPackages — preset slicing primitive.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ScopeUsedVersionsToBindingPackages_NullOrEmptyPackages_ReturnsAll()
    {
        var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.WindowsAppSDK"] = "1.8.39",
            ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
        };

        var resultNull = JsBindingsWorkspaceService.ScopeUsedVersionsToBindingPackages(input, null);
        Assert.AreEqual(2, resultNull.Count, "Null packages list = all packages participate (pre-preset default).");

        var resultEmpty = JsBindingsWorkspaceService.ScopeUsedVersionsToBindingPackages(input, Array.Empty<string>());
        Assert.AreEqual(2, resultEmpty.Count, "Empty packages list = all packages participate.");
    }

    [TestMethod]
    public void ScopeUsedVersionsToBindingPackages_FiltersDictionaryToAllowSet_CaseInsensitive()
    {
        var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.WindowsAppSDK"] = "1.8.39",
            ["Microsoft.WindowsAppSDK.AI"] = "1.8.39",
            ["Microsoft.Windows.SDK.NET.Ref"] = "10.0.26100.93",
        };
        var preset = new[] { "microsoft.windowsappsdk.ai" };

        var result = JsBindingsWorkspaceService.ScopeUsedVersionsToBindingPackages(input, preset);

        Assert.AreEqual(1, result.Count, "Only the preset-listed package should survive the scope.");
        Assert.IsTrue(result.ContainsKey("Microsoft.WindowsAppSDK.AI"));
        Assert.AreEqual("1.8.39", result["Microsoft.WindowsAppSDK.AI"]);
    }

    // -------------------------------------------------------------------------
    // MergeRefWinmds — combines (package-derived ref-only winmds) with
    // (user-supplied jsBindings.additionalRefs). Pure list-merge + dedup.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void MergeRefWinmds_BothEmpty_ReturnsEmpty()
    {
        var result = JsBindingsWorkspaceService.MergeRefWinmds(Array.Empty<FileInfo>(), null);
        Assert.AreEqual(0, result.Count);
        var result2 = JsBindingsWorkspaceService.MergeRefWinmds(Array.Empty<FileInfo>(), Array.Empty<FileInfo>());
        Assert.AreEqual(0, result2.Count);
    }

    [TestMethod]
    public void MergeRefWinmds_PreservesInputOrder_FirstThenSecond()
    {
        var first = new[]
        {
            new FileInfo(Path.Combine(_temp.FullName, "pkg-A.winmd")),
            new FileInfo(Path.Combine(_temp.FullName, "pkg-B.winmd")),
        };
        var second = new[]
        {
            new FileInfo(Path.Combine(_temp.FullName, "user-X.winmd")),
            new FileInfo(Path.Combine(_temp.FullName, "user-Y.winmd")),
        };

        var result = JsBindingsWorkspaceService.MergeRefWinmds(first, second);

        Assert.AreEqual(4, result.Count);
        Assert.IsTrue(result[0].FullName.EndsWith("pkg-A.winmd", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result[1].FullName.EndsWith("pkg-B.winmd", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result[2].FullName.EndsWith("user-X.winmd", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result[3].FullName.EndsWith("user-Y.winmd", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MergeRefWinmds_DedupesByFullName_CaseInsensitive()
    {
        var path = Path.Combine(_temp.FullName, "Foo.winmd");
        var first = new[] { new FileInfo(path) };
        var second = new[]
        {
            new FileInfo(path.ToUpperInvariant()),
            new FileInfo(Path.Combine(_temp.FullName, "Bar.winmd")),
        };

        var result = JsBindingsWorkspaceService.MergeRefWinmds(first, second);

        Assert.AreEqual(2, result.Count, "Foo.winmd must appear once even though second list has a case-variant duplicate.");
    }

    // -------------------------------------------------------------------------
    // IsNetworkPath — classify UNC / network paths so the
    // additionalWinmds / additionalRefs / lockfile path probes can refuse
    // to negotiate SMB with attacker-controlled hosts.
    // -------------------------------------------------------------------------

    [TestMethod]
    [DataRow("\\\\server\\share\\file.winmd", true, "Plain UNC")]
    [DataRow("//server/share/file.winmd", true, "Forward-slash UNC")]
    [DataRow("\\\\attacker.example.com\\share\\evil.winmd", true, "Hostname UNC")]
    [DataRow("\\\\?\\UNC\\server\\share\\file.winmd", true, "Long-path UNC")]
    [DataRow("\\\\.\\UNC\\server\\share\\file.winmd", true, "Device-path UNC")]
    [DataRow("\\\\?\\unc\\server\\share\\file.winmd", true, "Long-path UNC lowercase")]
    [DataRow("C:\\Users\\me\\winmds\\Foo.winmd", false, "Local DOS path")]
    [DataRow("C:/Users/me/winmds/Foo.winmd", false, "Local forward-slash DOS path")]
    [DataRow("\\\\?\\C:\\Users\\me\\Foo.winmd", false, "Local long-path DOS")]
    [DataRow("\\\\.\\C:\\Users\\me\\Foo.winmd", false, "Local device DOS")]
    [DataRow("relative/file.winmd", false, "Relative path")]
    [DataRow("", false, "Empty")]
    public void IsNetworkPath_ClassifiesPathsCorrectly(string path, bool expected, string label)
    {
        Assert.AreEqual(expected, JsBindingsWorkspaceService.IsNetworkPath(path),
            $"Path classification mismatch for {label}: {path}");
    }

    // -------------------------------------------------------------------------
    // BuildBulkArgs / BuildExtraTypeArgs — full argv construction.
    // -------------------------------------------------------------------------

    [TestMethod]
    public void BuildBulkArgs_IncludesGenerateAndWinmdAndOutputAndLang()
    {
        var winmds = new[]
        {
            new FileInfo(Path.Combine(_temp.FullName, "A.winmd")),
            new FileInfo(Path.Combine(_temp.FullName, "B.winmd")),
        };
        var refs = new List<FileInfo>();
        var config = new JsBindingsConfig { Lang = "js", Output = "bindings/winrt" };
        var args = DynWinrtCodegenService.BuildBulkArgs(Array.Empty<string>(), winmds, _temp, config, refs);

        Assert.AreEqual("generate", args[0]);
        CollectionAssert.Contains(args, "--winmd");
        Assert.IsTrue(args.Any(a => a.Contains("A.winmd") && a.Contains("B.winmd") && a.Contains(';')),
            "Multiple winmds must be semicolon-joined under --winmd.");
        CollectionAssert.Contains(args, "--output");
        CollectionAssert.Contains(args, "--lang");
        CollectionAssert.Contains(args, "js");
        Assert.IsFalse(args.Contains("--ref"), "No --ref when ref list is empty.");
        Assert.IsFalse(args.Contains("--pyi"), "No --pyi unless lang=py.");
    }

    [TestMethod]
    public void BuildBulkArgs_WithRefs_IncludesRefFlag()
    {
        var winmds = new[] { new FileInfo(Path.Combine(_temp.FullName, "A.winmd")) };
        var refs = new List<FileInfo>
        {
            new(Path.Combine(_temp.FullName, "R1.winmd")),
            new(Path.Combine(_temp.FullName, "R2.winmd")),
        };
        var config = new JsBindingsConfig { Lang = "js" };
        var args = DynWinrtCodegenService.BuildBulkArgs(Array.Empty<string>(), winmds, _temp, config, refs);

        var refIdx = args.IndexOf("--ref");
        Assert.IsTrue(refIdx >= 0, "Expected --ref flag.");
        Assert.IsTrue(args[refIdx + 1].Contains("R1.winmd") && args[refIdx + 1].Contains("R2.winmd")
            && args[refIdx + 1].Contains(';'),
            $"Ref winmds must be semicolon-joined. Got: {args[refIdx + 1]}");
    }

    [TestMethod]
    public void BuildBulkArgs_PyLang_AddsPyiFlag()
    {
        var winmds = new[] { new FileInfo(Path.Combine(_temp.FullName, "A.winmd")) };
        var config = new JsBindingsConfig { Lang = "py" };
        var args = DynWinrtCodegenService.BuildBulkArgs(Array.Empty<string>(), winmds, _temp, config, new List<FileInfo>());

        CollectionAssert.Contains(args, "--pyi", "Python lang must emit --pyi.");
        CollectionAssert.Contains(args, "py");
    }

    [TestMethod]
    public void BuildBulkArgs_PrefixArgsPreserved()
    {
        var winmds = new[] { new FileInfo(Path.Combine(_temp.FullName, "A.winmd")) };
        var prefix = new[] { "C:\\Node\\node.exe", "cli.js" };
        var config = new JsBindingsConfig { Lang = "js" };
        var args = DynWinrtCodegenService.BuildBulkArgs(prefix, winmds, _temp, config, new List<FileInfo>());

        Assert.AreEqual("C:\\Node\\node.exe", args[0]);
        Assert.AreEqual("cli.js", args[1]);
        Assert.AreEqual("generate", args[2]);
    }

    [TestMethod]
    public void BuildExtraTypeArgs_IncludesNamespaceAndClassFlags()
    {
        var winmds = new[] { new FileInfo(Path.Combine(_temp.FullName, "Windows.winmd")) };
        var extra = new JsBindingsExtraType
        {
            Namespace = "Windows.Foundation",
            Classes = { "Uri", "Calendar" },
        };
        var config = new JsBindingsConfig { Lang = "js" };
        var args = DynWinrtCodegenService.BuildExtraTypeArgs(
            Array.Empty<string>(), winmds, _temp, config, new List<FileInfo>(), extra);

        Assert.AreEqual("generate", args[0]);
        var nsIdx = args.IndexOf("--namespace");
        Assert.IsTrue(nsIdx >= 0);
        Assert.AreEqual("Windows.Foundation", args[nsIdx + 1]);
        var classIdx = args.IndexOf("--class-name");
        Assert.IsTrue(classIdx >= 0);
        Assert.AreEqual("Uri,Calendar", args[classIdx + 1],
            "Classes must be comma-joined.");
    }

    // extraTypes-only cherry-pick workflow must produce a
    // valid argv without an empty --winmd flag. When the user supplies
    // refs + extraTypes alone (no bulk emit set), BuildExtraTypeArgs must
    // omit --winmd entirely so codegen doesn't see `--winmd ""`.

    [TestMethod]
    public void BuildExtraTypeArgs_EmptyEmitWinmds_OmitsWinmdFlag()
    {
        var refs = new List<FileInfo>
        {
            new(Path.Combine(_temp.FullName, "Vendor.SDK.winmd")),
        };
        var extra = new JsBindingsExtraType
        {
            Namespace = "Vendor.SDK.Camera",
            Classes = { "Lens" },
        };
        var config = new JsBindingsConfig { Lang = "js" };

        var args = DynWinrtCodegenService.BuildExtraTypeArgs(
            Array.Empty<string>(), Array.Empty<FileInfo>(), _temp, config, refs, extra);

        Assert.IsFalse(args.Contains("--winmd"),
            "When emit winmds are empty, --winmd must be omitted entirely (no empty arg).");
        CollectionAssert.Contains(args, "--ref",
            "extraTypes-only flow still passes --ref for type resolution.");
        CollectionAssert.Contains(args, "--namespace");
        CollectionAssert.Contains(args, "--class-name");
        CollectionAssert.Contains(args, "--lang");
    }

    [TestMethod]
    public void BuildExtraTypeArgs_NonEmptyEmitWinmds_IncludesWinmdFlag()
    {
        // Regression guard: ensure the M2 fix didn't accidentally drop
        // --winmd for the normal bulk + extraType combo.
        var winmds = new[] { new FileInfo(Path.Combine(_temp.FullName, "Windows.winmd")) };
        var extra = new JsBindingsExtraType
        {
            Namespace = "Windows.Foundation",
            Classes = { "Uri" },
        };
        var config = new JsBindingsConfig { Lang = "js" };

        var args = DynWinrtCodegenService.BuildExtraTypeArgs(
            Array.Empty<string>(), winmds, _temp, config, new List<FileInfo>(), extra);

        var winmdIdx = args.IndexOf("--winmd");
        Assert.IsTrue(winmdIdx >= 0, "Non-empty emit winmds must include --winmd.");
        Assert.IsTrue(args[winmdIdx + 1].EndsWith("Windows.winmd", StringComparison.OrdinalIgnoreCase));
    }
}
