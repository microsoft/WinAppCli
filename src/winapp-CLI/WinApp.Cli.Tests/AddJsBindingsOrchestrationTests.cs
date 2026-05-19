// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tests.TestDoubles;

namespace WinApp.Cli.Tests;

// Hermetic orchestration tests for AddJsBindingsAsync. Injects a fake
// codegen so the executable is never spawned. Fast-path vs fallback is
// driven by writing (or omitting) .winapp/winmds.lock.json.
// [DoNotParallelize] because the tests mutate WINAPP_CLI_CALLER.
[TestClass]
[DoNotParallelize]
public class AddJsBindingsOrchestrationTests : BaseCommandTests
{
    private static readonly string[] _arr00 = ["Lens", "Sensor"];

    private FakeDynWinrtCodegenService _fakeCodegen = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        // Swap the real codegen for the recording fake.
        _fakeCodegen = new FakeDynWinrtCodegenService();
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IDynWinrtCodegenService));
        if (existing is not null)
        {
            services.Remove(existing);
        }
        services.AddSingleton<IDynWinrtCodegenService>(_fakeCodegen);
        return services;
    }

    [TestInitialize]
    public void SetNpmCallerEnv()
    {
        // AddJsBindingsCommand gates on this exact env value (NpmShimCaller).
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
    }

    [TestCleanup]
    public void ClearNpmCallerEnv()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
    }

    private DirectoryInfo SetUpWorkspaceWithLockfile(
        string yamlPackagesBlock = "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n",
        params (string name, string version, string category, string[] winmdPaths)[] lockfilePackages)
    {
        var ws = _tempDirectory;
        File.WriteAllText(Path.Combine(ws.FullName, "winapp.yaml"), yamlPackagesBlock);

        var winappDir = ws.CreateSubdirectory(".winapp");

        // Match the hash AddJsBindingsAsync computes; otherwise fast-path
        // rejects as stale.
        var loadedConfig = new ConfigService(new CurrentDirectoryProvider(ws.FullName))
        {
            ConfigPath = new FileInfo(Path.Combine(ws.FullName, "winapp.yaml")),
        };
        var configForHash = loadedConfig.Load();
        var hash = YamlPackagesHasher.Compute(configForHash.Packages);

        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = ws.FullName,
            YamlPackagesHash = hash,
            Packages = lockfilePackages.Select(p => new WinmdsLockfilePackage
            {
                Name = p.name,
                Version = p.version,
                Category = p.category,
                Winmds = p.winmdPaths.ToList(),
            }).ToList(),
        };

        // Ensure every winmd path exists on disk — fast-path validates this.
        foreach (var pkg in lockfilePackages)
        {
            foreach (var path in pkg.winmdPaths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "stub winmd");
                }
            }
        }

        var json = System.Text.Json.JsonSerializer.Serialize(
            lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
        File.WriteAllText(Path.Combine(winappDir.FullName, "winmds.lock.json"), json);

        return ws;
    }

    // -------------------------------------------------------------------------
    // true success-path test
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task AddJsBindings_HappyPath_ExitsZero_GeneratesBindings_InjectsRuntimeDep()
    {
        // Workspace has a lockfile with one AI package + its winmds; fast-path
        // partitions, calls (fake) codegen, exits 0.
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");

        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK", "1.8.39", "emit", Array.Empty<string>()),
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Seed a minimal package.json so the runtime-dep injection has a
        // file to read/write.
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"hosting-app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--ai", "--force" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(0, exitCode,
            "Happy path must exit 0. Stderr: " + ConsoleStdErr.ToString());

        // Fake codegen was invoked exactly once.
        Assert.AreEqual(1, _fakeCodegen.Calls.Count,
            "Codegen should be called exactly once for the bulk pass (no extraTypes).");

        // Args sanity-check: the AI winmd is in the emit list.
        var call = _fakeCodegen.Calls[0];
        Assert.IsTrue(call.EmitWinmds.Any(p => p.EndsWith("Microsoft.Windows.AI.winmd", StringComparison.OrdinalIgnoreCase)),
            $"AI winmd must be in emit list. Got: {string.Join(", ", call.EmitWinmds)}");

        // Output dir created with marker + stub file.
        var output = Path.Combine(_tempDirectory.FullName, "bindings", "winrt");
        Assert.IsTrue(Directory.Exists(output), "Output dir must exist.");
        Assert.IsTrue(File.Exists(Path.Combine(output, ".dynwinrt-managed")),
            "Marker file must be written for next-run wipe gating.");
        Assert.IsTrue(File.Exists(Path.Combine(output, "index.js")),
            "Stub codegen output must be present.");

        // Yaml was patched with the AI preset.
        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));
        StringAssert.Contains(yaml, "Microsoft.WindowsAppSDK.AI",
            "yaml's jsBindings.packages should now contain the AI preset.");
    }

    // -------------------------------------------------------------------------
    // lockfile fast-path / stale-hash / missing-paths / fallback
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task AddJsBindings_LockfileFastPath_UsedWhenHashMatches()
    {
        // Setup matches happy path; assert that fast-path was taken by
        // verifying we never needed the NuGet cache (which is empty).
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK", "1.8.39", "emit", Array.Empty<string>()),
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--ai", "--force" });

        Assert.AreEqual(0, exitCode);
        // Lockfile path supplies the winmd directly — no NuGet cache glob.
        Assert.AreEqual(1, _fakeCodegen.Calls.Count);
        Assert.AreEqual(1, _fakeCodegen.Calls[0].EmitWinmds.Length);
    }

    [TestMethod]
    public async Task AddJsBindings_StaleYamlHash_FallsBackOrFailsCleanly()
    {
        // Stale-hash lockfile → fast-path rejects → fallback fails (no
        // NuGet cache); never silently uses stale data.
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(aiWinmd)!);
        File.WriteAllText(aiWinmd, "stub");

        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n");

        var winappDir = _tempDirectory.CreateSubdirectory(".winapp");

        // Hash is "deadbeef" — guaranteed not to match the actual yaml.
        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = _tempDirectory.FullName,
            YamlPackagesHash = "deadbeef-not-a-real-hash",
            Packages = new List<WinmdsLockfilePackage>
            {
                new() { Name = "Microsoft.WindowsAppSDK.AI", Version = "1.8.39", Category = "emit",
                        Winmds = { aiWinmd } },
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
        File.WriteAllText(Path.Combine(winappDir.FullName, "winmds.lock.json"), json);

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--ai", "--force" });

        // No real NuGet cache → slow-path can't resolve sub-packages.
        Assert.AreNotEqual(0, exitCode,
            "Stale lockfile + missing NuGet cache must fail cleanly, not silently use stale data.");
        // Fake codegen must not be invoked — discovery failed first.
        Assert.AreEqual(0, _fakeCodegen.Calls.Count,
            "Codegen must not be called when discovery can't find any winmds.");
    }

    [TestMethod]
    public async Task AddJsBindings_LockfileMissingWinmdPaths_FallsBackOrFailsCleanly()
    {
        // Lockfile references winmd paths that don't exist on disk
        // (e.g. `nuget locals all -clear` between restore and add).
        var bogusAiWinmd = Path.Combine(_tempDirectory.FullName, "deleted-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        // Intentionally do NOT create the file.

        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n");

        var winappDir = _tempDirectory.CreateSubdirectory(".winapp");

        var loadedConfig = new ConfigService(new CurrentDirectoryProvider(_tempDirectory.FullName))
        {
            ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml")),
        }.Load();
        var hash = YamlPackagesHasher.Compute(loadedConfig.Packages);

        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = _tempDirectory.FullName,
            YamlPackagesHash = hash,
            Packages = new List<WinmdsLockfilePackage>
            {
                new() { Name = "Microsoft.WindowsAppSDK.AI", Version = "1.8.39", Category = "emit",
                        Winmds = { bogusAiWinmd } },
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
        File.WriteAllText(Path.Combine(winappDir.FullName, "winmds.lock.json"), json);

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--ai", "--force" });

        // Expect failure (no fallback can find anything either), but the
        // key assertion is the codegen wasn't called with bogus paths.
        Assert.AreNotEqual(0, exitCode,
            "Stale paths + no fallback data must fail cleanly.");
        Assert.AreEqual(0, _fakeCodegen.Calls.Count,
            "Codegen must NOT be invoked with bogus winmd paths from a stale lockfile.");
    }

    [TestMethod]
    public async Task AddJsBindings_CodegenThrows_PropagatesAsExit1()
    {
        // FailWith makes fake codegen throw; caller must surface exit 1.
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        _fakeCodegen.FailWith = new InvalidOperationException("simulated codegen failure");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--ai", "--force" });

        Assert.AreEqual(1, exitCode, "Codegen failure must surface as non-zero exit.");
        Assert.AreEqual(1, _fakeCodegen.Calls.Count, "Codegen was invoked (and threw).");
    }

    [TestMethod]
    public async Task AddJsBindings_AllScopedPackagesCategorizedAsSkip_FailsBeforeCodegen()
    {
        // Scope narrows to a single Skip-categorized package → empty emit
        // set → must fail before spawning codegen.
        var winuiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.winui", "1.8.39", "metadata", "Microsoft.WindowsAppSDK.WinUI.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(winuiWinmd)!);
        File.WriteAllText(winuiWinmd, "stub");

        // WinUI is in the default Skip set.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.WinUI\n");

        var winappDir = _tempDirectory.CreateSubdirectory(".winapp");
        var loadedConfig = new ConfigService(new CurrentDirectoryProvider(_tempDirectory.FullName))
        {
            ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml")),
        }.Load();
        var hash = YamlPackagesHasher.Compute(loadedConfig.Packages);

        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = _tempDirectory.FullName,
            YamlPackagesHash = hash,
            Packages = new List<WinmdsLockfilePackage>
            {
                new() { Name = "Microsoft.WindowsAppSDK.WinUI", Version = "1.8.39", Category = "skip",
                        Winmds = { winuiWinmd } },
            },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
        File.WriteAllText(Path.Combine(winappDir.FullName, "winmds.lock.json"), json);

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName });

        Assert.AreNotEqual(0, exitCode,
            "All-skipped scope must fail cleanly, not invoke codegen with no emit set.");
        Assert.AreEqual(0, _fakeCodegen.Calls.Count,
            "Codegen MUST NOT be invoked when there's nothing to emit.");
    }

    [TestMethod]
    public async Task AddJsBindings_ForceChangesOutput_OldOutputCleanupOnlyAfterCodegenSuccess()
    {
        // M7: --force --output change wipes a managed old dir on success,
        // preserves an unmanaged one (marker-gated).
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Case A: managed old dir → must be wiped after codegen succeeds.
        var managedOld = Path.Combine(_tempDirectory.FullName, "managed-old");
        Directory.CreateDirectory(managedOld);
        File.WriteAllText(Path.Combine(managedOld, "Uri.js"), "// generated");
        File.WriteAllText(Path.Combine(managedOld, DynWinrtCodegenService.ManagedMarkerFileName), "# managed");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: managed-old\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd,
            new[] { _tempDirectory.FullName, "--force", "--output", "fresh-out" });

        Assert.AreEqual(0, exit, "Codegen should succeed via fake.");
        Assert.IsFalse(File.Exists(Path.Combine(managedOld, "Uri.js")),
            "Managed old dir's files must be wiped after a successful output: change.");

        // Case B: unmanaged old dir → preserved even on success.
        var unmanagedOld = Path.Combine(_tempDirectory.FullName, "unmanaged-old");
        Directory.CreateDirectory(unmanagedOld);
        File.WriteAllText(Path.Combine(unmanagedOld, "user-handcraft.js"), "// hand-written");
        // NO marker.

        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: unmanaged-old\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n");

        var exit2 = await ParseAndInvokeWithCaptureAsync(addCmd,
            new[] { _tempDirectory.FullName, "--force", "--output", "fresh-out-2" });

        Assert.AreEqual(0, exit2);
        Assert.IsTrue(File.Exists(Path.Combine(unmanagedOld, "user-handcraft.js")),
            "Unmanaged old dir's user files must NOT be wiped — marker-gated safety.");
    }

    [TestMethod]
    public async Task AddJsBindings_CodegenFails_OldOutputIsPreserved()
    {
        // M7: codegen failure must leave the old bindings dir untouched.
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        var managedOld = Path.Combine(_tempDirectory.FullName, "managed-old");
        Directory.CreateDirectory(managedOld);
        File.WriteAllText(Path.Combine(managedOld, "Uri.js"), "// generated");
        File.WriteAllText(Path.Combine(managedOld, DynWinrtCodegenService.ManagedMarkerFileName), "# managed");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: managed-old\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        _fakeCodegen.FailWith = new InvalidOperationException("simulated codegen failure");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd,
            new[] { _tempDirectory.FullName, "--force", "--output", "fresh-out" });

        Assert.AreNotEqual(0, exit, "Codegen failure must surface non-zero.");
        Assert.IsTrue(File.Exists(Path.Combine(managedOld, "Uri.js")),
            "Codegen failure must NOT wipe the previous bindings.");
    }

    [TestMethod]
    public async Task AddJsBindings_AdditionalWinmds_FlowsIntoCodegenEmitSet()
    {
        // additionalWinmds entries must reach codegen as user-additional emit.
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Seed a real vendor winmd file referenced by additionalWinmds.
        var vendorWinmd = Path.Combine(_tempDirectory.FullName, "vendor", "MyCo.Foo.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorWinmd)!);
        File.WriteAllText(vendorWinmd, "stub");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n"
            + "  additionalWinmds:\n"
            + "    - vendor/MyCo.Foo.winmd\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });

        Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");
        Assert.AreEqual(1, _fakeCodegen.Calls.Count);
        var call = _fakeCodegen.Calls[0];
        Assert.IsTrue(call.UserAdditionalWinmds.Any(p => p.EndsWith("MyCo.Foo.winmd", StringComparison.OrdinalIgnoreCase)),
            $"additionalWinmds must surface to codegen via UserAdditionalWinmds. Got: {string.Join(", ", call.UserAdditionalWinmds)}");
    }

    [TestMethod]
    public async Task AddJsBindings_AdditionalRefs_FlowsIntoCodegenRefSet()
    {
        // jsBindings.additionalRefs entries must flow via UserAdditionalRefs.
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        var vendorRefWinmd = Path.Combine(_tempDirectory.FullName, "vendor", "BigSDK.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorRefWinmd)!);
        File.WriteAllText(vendorRefWinmd, "stub");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n"
            + "  additionalRefs:\n"
            + "    - vendor/BigSDK.winmd\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });

        Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");
        var call = _fakeCodegen.Calls[0];
        Assert.IsTrue(call.UserAdditionalRefs.Any(p => p.EndsWith("BigSDK.winmd", StringComparison.OrdinalIgnoreCase)),
            $"additionalRefs must surface to codegen via UserAdditionalRefs. Got: {string.Join(", ", call.UserAdditionalRefs)}");
    }

    // UNC paths in additionalWinmds must be rejected without probing
    // (FileInfo.Exists on a UNC triggers SMB / NTLM leak).
    [TestMethod]
    public async Task AddJsBindings_AdditionalWinmds_UncEntry_Rejected_NotProbedNotPassedToCodegen()
    {
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Yaml has a benign local entry + a UNC entry; only the benign one
        // should reach codegen.
        var legitWinmd = Path.Combine(_tempDirectory.FullName, "vendor", "Legit.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(legitWinmd)!);
        File.WriteAllText(legitWinmd, "stub");

        // RFC 2606 `.invalid` TLD — never resolves even if our guard fails.
        var uncWinmd = @"\\nonexistent-attacker.invalid\share\evil.winmd";

        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n"
            + "  additionalWinmds:\n"
            + "    - vendor/Legit.winmd\n"
            + $"    - {uncWinmd.Replace("\\", "\\\\")}\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();

        // Cap runtime: a failed guard means a 20s+ SMB timeout per UNC entry.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });
        sw.Stop();

        Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");
        Assert.IsTrue(sw.ElapsedMilliseconds < 10_000,
            $"UNC entry must be rejected without SMB probe (took {sw.ElapsedMilliseconds}ms; "
            + "anything >5s suggests we did probe).");

        Assert.AreEqual(1, _fakeCodegen.Calls.Count);
        var call = _fakeCodegen.Calls[0];
        Assert.IsTrue(
            call.UserAdditionalWinmds.Any(p => p.EndsWith("Legit.winmd", StringComparison.OrdinalIgnoreCase)),
            "Legit local entry must still reach codegen.");
        Assert.IsFalse(
            call.UserAdditionalWinmds.Any(p => p.Contains("nonexistent-attacker.invalid", StringComparison.OrdinalIgnoreCase)),
            $"UNC entry MUST be dropped — codegen received: {string.Join(", ", call.UserAdditionalWinmds)}");
    }

    // extraTypes-only: additionalRefs + extraTypes, no bulk emit.
    [TestMethod]
    public async Task AddJsBindings_ExtraTypesOnlyWithAdditionalRefs_Succeeds()
    {
        // jsBindings declares only additionalRefs + extraTypes (no packages,
        // no additionalWinmds).
        var vendorWinmd = Path.Combine(_tempDirectory.FullName, "vendor", "Vendor.SDK.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorWinmd)!);
        File.WriteAllText(vendorWinmd, "stub");

        // Empty lockfile (no emit packages) — reaches the empty-emit guard.
        var configForHash = "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n";
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"), configForHash);
        var loadedConfig = new ConfigService(new CurrentDirectoryProvider(_tempDirectory.FullName))
        {
            ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml")),
        }.Load();
        var hash = YamlPackagesHasher.Compute(loadedConfig.Packages);

        var winappDir = _tempDirectory.CreateSubdirectory(".winapp");
        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = _tempDirectory.FullName,
            YamlPackagesHash = hash,
            // No emit/refOnly packages — the only way to feed metadata is
            // via additionalRefs in the yaml below.
            Packages = new List<WinmdsLockfilePackage>(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
        File.WriteAllText(Path.Combine(winappDir.FullName, "winmds.lock.json"), json);

        // Rewrite the yaml with jsBindings: additionalRefs + extraTypes only.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  additionalRefs:\n"
            + "    - vendor/Vendor.SDK.winmd\n"
            + "  extraTypes:\n"
            + "    - namespace: Vendor.SDK.Camera\n"
            + "      classes:\n"
            + "        - Lens\n"
            + "        - Sensor\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });

        Assert.AreEqual(0, exit,
            $"extraTypes-only cherry-pick workflow must succeed. stderr: {ConsoleStdErr}");
        Assert.AreEqual(1, _fakeCodegen.Calls.Count, "Codegen must be invoked.");
        var call = _fakeCodegen.Calls[0];
        Assert.AreEqual(0, call.EmitWinmds.Length,
            "extraTypes-only flow has no bulk emit set — codegen sees zero emit winmds.");
        Assert.IsTrue(call.UserAdditionalRefs.Any(p => p.EndsWith("Vendor.SDK.winmd", StringComparison.OrdinalIgnoreCase)),
            "Vendor ref winmd must reach codegen as a ref.");
        Assert.AreEqual(1, call.Config.ExtraTypes.Count, "extraTypes must be passed through.");
        Assert.AreEqual("Vendor.SDK.Camera", call.Config.ExtraTypes[0].Namespace);
        CollectionAssert.AreEquivalent(
            _arr00,
            call.Config.ExtraTypes[0].Classes.ToList());
    }

    // Only-malformed extraTypes (blank ns / empty classes) must fail
    // before codegen — otherwise we'd return success with zero bindings.
    [TestMethod]
    public async Task AddJsBindings_ExtraTypesOnlyWithMalformedEntries_FailsBeforeCodegen()
    {
        var vendorWinmd = Path.Combine(_tempDirectory.FullName, "vendor", "Vendor.SDK.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(vendorWinmd)!);
        File.WriteAllText(vendorWinmd, "stub");

        var configForHash = "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n";
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"), configForHash);
        var loadedConfig = new ConfigService(new CurrentDirectoryProvider(_tempDirectory.FullName))
        {
            ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml")),
        }.Load();
        var hash = YamlPackagesHasher.Compute(loadedConfig.Packages);

        var winappDir = _tempDirectory.CreateSubdirectory(".winapp");
        var lockfile = new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = _tempDirectory.FullName,
            YamlPackagesHash = hash,
            Packages = new List<WinmdsLockfilePackage>(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
        File.WriteAllText(Path.Combine(winappDir.FullName, "winmds.lock.json"), json);

        // Two malformed entries (blank ns + empty classes) → codegen would
        // silently skip both.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  additionalRefs:\n"
            + "    - vendor/Vendor.SDK.winmd\n"
            + "  extraTypes:\n"
            + "    - namespace: ''\n"
            + "      classes:\n"
            + "        - Lens\n"
            + "    - namespace: Vendor.SDK.Camera\n"
            + "      classes: []\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });

        Assert.AreNotEqual(0, exit,
            "Malformed-only extraTypes must fail rather than silently produce zero bindings.");
        Assert.AreEqual(0, _fakeCodegen.Calls.Count,
            "Codegen MUST NOT be invoked when all extraTypes would be skipped.");
    }

    // Companion to the additionalWinmds UNC test: refs flow through the
    // same lockfile-bypass route, so UNC entries must also be dropped.
    [TestMethod]
    public async Task AddJsBindings_AdditionalRefs_UncEntry_Rejected_NotProbedNotPassedToCodegen()
    {
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Yaml has a benign local ref + a UNC ref; only the benign reaches codegen.
        var legitRef = Path.Combine(_tempDirectory.FullName, "vendor", "Legit.Ref.winmd");
        Directory.CreateDirectory(Path.GetDirectoryName(legitRef)!);
        File.WriteAllText(legitRef, "stub");

        // RFC 2606 reserved TLD — never resolves, even if our guard fails.
        var uncRef = @"\\nonexistent-attacker.invalid\share\evil.ref.winmd";

        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n"
            + "  additionalRefs:\n"
            + "    - vendor/Legit.Ref.winmd\n"
            + $"    - {uncRef.Replace("\\", "\\\\")}\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });
        sw.Stop();

        Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");
        Assert.IsTrue(sw.ElapsedMilliseconds < 10_000,
            $"UNC ref must be rejected without SMB probe (took {sw.ElapsedMilliseconds}ms; "
            + "anything >5s suggests we did probe).");

        Assert.AreEqual(1, _fakeCodegen.Calls.Count);
        var call = _fakeCodegen.Calls[0];
        Assert.IsTrue(
            call.UserAdditionalRefs.Any(p => p.EndsWith("Legit.Ref.winmd", StringComparison.OrdinalIgnoreCase)),
            "Legit local ref must still reach codegen.");
        Assert.IsFalse(
            call.UserAdditionalRefs.Any(p => p.Contains("nonexistent-attacker.invalid", StringComparison.OrdinalIgnoreCase)),
            $"UNC ref MUST be dropped — codegen received: {string.Join(", ", call.UserAdditionalRefs)}");
    }

    // M1 (round-6): absolute paths outside the workspace must be accepted.
    // docs/js-bindings.md:85,216,400 advertise absolute-path support; pre-r6
    // the reparse-point guard used workspaceDir as boundary and silently
    // dropped any out-of-workspace absolute path.
    [TestMethod]
    public async Task AddJsBindings_AdditionalWinmds_AbsolutePathOutsideWorkspace_ReachesCodegen()
    {
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Stage a vendor winmd in a SIBLING directory (outside workspace).
        var siblingDir = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            string.Concat("winapp-r6-abs-".AsSpan(), Guid.NewGuid().ToString("N").AsSpan(0, 8))));
        siblingDir.Create();
        var externalWinmd = Path.Combine(siblingDir.FullName, "External.winmd");
        File.WriteAllText(externalWinmd, "stub");

        try
        {
            File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
                "packages:\n"
                + "  - name: Microsoft.WindowsAppSDK\n"
                + "    version: 1.8.39\n"
                + "jsBindings:\n"
                + "  output: bindings/winrt\n"
                + "  lang: js\n"
                + "  packages:\n"
                + "    - Microsoft.WindowsAppSDK.AI\n"
                + "  additionalWinmds:\n"
                + $"    - {externalWinmd.Replace("\\", "\\\\")}\n");

            File.WriteAllText(
                Path.Combine(_tempDirectory.FullName, "package.json"),
                """{"name":"app","version":"1.0.0","dependencies":{}}""");

            var addCmd = GetRequiredService<AddJsBindingsCommand>();
            var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });

            Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");
            Assert.AreEqual(1, _fakeCodegen.Calls.Count);
            var call = _fakeCodegen.Calls[0];
            Assert.IsTrue(
                call.UserAdditionalWinmds.Any(p => p.EndsWith("External.winmd", StringComparison.OrdinalIgnoreCase)),
                $"Absolute path outside workspace must reach codegen. Got: {string.Join(", ", call.UserAdditionalWinmds)}");
        }
        finally
        {
            try { siblingDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // M1 (round-6) companion: absolute additionalRefs must also be accepted
    // (same boundary fix; both fields flow through ResolveAdditionalWinmds).
    [TestMethod]
    public async Task AddJsBindings_AdditionalRefs_AbsolutePathOutsideWorkspace_ReachesCodegen()
    {
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        var siblingDir = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            string.Concat("winapp-r6-absref-".AsSpan(), Guid.NewGuid().ToString("N").AsSpan(0, 8))));
        siblingDir.Create();
        var externalRef = Path.Combine(siblingDir.FullName, "External.Ref.winmd");
        File.WriteAllText(externalRef, "stub");

        try
        {
            File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
                "packages:\n"
                + "  - name: Microsoft.WindowsAppSDK\n"
                + "    version: 1.8.39\n"
                + "jsBindings:\n"
                + "  output: bindings/winrt\n"
                + "  lang: js\n"
                + "  packages:\n"
                + "    - Microsoft.WindowsAppSDK.AI\n"
                + "  additionalRefs:\n"
                + $"    - {externalRef.Replace("\\", "\\\\")}\n");

            File.WriteAllText(
                Path.Combine(_tempDirectory.FullName, "package.json"),
                """{"name":"app","version":"1.0.0","dependencies":{}}""");

            var addCmd = GetRequiredService<AddJsBindingsCommand>();
            var exit = await ParseAndInvokeWithCaptureAsync(addCmd, new[] { _tempDirectory.FullName, "--force" });

            Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");
            Assert.AreEqual(1, _fakeCodegen.Calls.Count);
            var call = _fakeCodegen.Calls[0];
            Assert.IsTrue(
                call.UserAdditionalRefs.Any(p => p.EndsWith("External.Ref.winmd", StringComparison.OrdinalIgnoreCase)),
                $"Absolute ref outside workspace must reach codegen. Got: {string.Join(", ", call.UserAdditionalRefs)}");
        }
        finally
        {
            try { siblingDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // M8: when old/new output dirs nest (either direction), cleanup must
    // be skipped or wiping old would erase the freshly generated bindings.
    [TestMethod]
    public async Task AddJsBindings_OutputChange_NewNestedInsideOld_CleanupSkipped()
    {
        // old = "bindings", new = "bindings/winrt" (child of old).
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        // Marker-gated old dir would normally be wiped — overlap guard skips it.
        var oldDir = Path.Combine(_tempDirectory.FullName, "bindings");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "stale.js"), "// old");
        File.WriteAllText(Path.Combine(oldDir, DynWinrtCodegenService.ManagedMarkerFileName), "# managed");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd,
            new[] { _tempDirectory.FullName, "--force", "--output", "bindings/winrt" });

        Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");

        var newFile = Path.Combine(_tempDirectory.FullName, "bindings", "winrt", "index.js");
        Assert.IsTrue(File.Exists(newFile),
            "Freshly generated bindings MUST survive — overlap cleanup must not delete them.");
    }

    [TestMethod]
    public async Task AddJsBindings_OutputChange_OldNestedInsideNew_CleanupSkipped()
    {
        // old = "bindings/winrt" (child), new = "bindings" (parent).
        var aiWinmd = Path.Combine(_tempDirectory.FullName, "fake-cache",
            "microsoft.windowsappsdk.ai", "1.8.39", "metadata", "Microsoft.Windows.AI.winmd");
        SetUpWorkspaceWithLockfile(
            lockfilePackages: new[]
            {
                ("Microsoft.WindowsAppSDK.AI", "1.8.39", "emit", new[] { aiWinmd }),
            });

        var oldDir = Path.Combine(_tempDirectory.FullName, "bindings", "winrt");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "stale.js"), "// old");
        File.WriteAllText(Path.Combine(oldDir, DynWinrtCodegenService.ManagedMarkerFileName), "# managed");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: bindings/winrt\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.AI\n");

        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(addCmd,
            new[] { _tempDirectory.FullName, "--force", "--output", "bindings" });

        Assert.AreEqual(0, exit, $"Expected success; stderr: {ConsoleStdErr}");

        var newFile = Path.Combine(_tempDirectory.FullName, "bindings", "index.js");
        Assert.IsTrue(File.Exists(newFile),
            "Freshly generated bindings MUST survive at the new (parent) location.");
    }
}
