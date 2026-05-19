// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text.Json;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Default IJsBindingsWorkspaceService — orchestration entrypoint.
// Split into partials: .WinmdDiscovery.cs and .RuntimeDependency.cs.
internal sealed partial class JsBindingsWorkspaceService(
    IPackageLayoutService packageLayoutService,
    IWinmdsLockfileService winmdsLockfileService,
    IDynWinrtCodegenService dynWinrtCodegenService,
    INugetService nugetService,
    IUserPackageJsonService userPackageJsonService,
    INpmWrapperVersionProvider npmWrapperVersionProvider,
    IPackageManagerDetector packageManagerDetector,
    IConfigService configService,
    IStatusService statusService,
    IWinappDirectoryService winappDirectoryService,
    IAnsiConsole ansiConsole,
    ILogger<JsBindingsWorkspaceService> logger) : IJsBindingsWorkspaceService
{
    public async Task<JsBindingsOrchestrationResult> RunAsync(
        JsBindingsOrchestrationContext context,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // User winmds first — they can satisfy the no-package-winmds case.
            var userWinmds = ResolveAdditionalWinmds(
                context.JsBindingsConfig.AdditionalWinmds,
                context.WorkspaceDir,
                taskContext,
                fieldName: "additionalWinmds");
            var userRefs = ResolveAdditionalWinmds(
                context.JsBindingsConfig.AdditionalRefs,
                context.WorkspaceDir,
                taskContext,
                fieldName: "additionalRefs");

            // Step 2: discover package winmds.
            var (bindingWinmds, packageRefs, skippedCount, usedVersions) =
                await DiscoverWinmdsAsync(context, taskContext, cancellationToken);

            // Empty package discovery is OK if the user supplied their own.
            if (bindingWinmds is null || packageRefs is null)
            {
                if (userWinmds.Count == 0)
                {
                    return new JsBindingsOrchestrationResult
                    {
                        ExitCode = 1,
                        Message = "No .winmd files found for JS binding generation. "
                            + "Likely causes:\n"
                            + "  • Packages aren't restored yet → run [bold]npx winapp restore[/]\n"
                            + "  • Stale [bold].winapp/winmds.lock.json[/] → re-run restore to regenerate\n"
                            + "  • [bold]jsBindings.packages[/] in winapp.yaml lists package IDs that aren't installed",
                    };
                }
                bindingWinmds = new List<FileInfo>();
                packageRefs = new List<FileInfo>();
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} No package .winmd files in scope; emitting bindings from additionalWinmds: only ({userWinmds.Count} file(s)).");
            }

            // Reject empty emit unless this is a valid extraTypes-only flow
            // (refs + at least one valid extraType). Otherwise codegen would
            // see --winmd "".
            var validExtraTypeCount = CountValidExtraTypes(context.JsBindingsConfig.ExtraTypes);
            var hasExtraTypesOnlyFlow =
                validExtraTypeCount > 0
                && (userRefs.Count > 0 || packageRefs.Count > 0);
            if (bindingWinmds.Count + userWinmds.Count == 0 && !hasExtraTypesOnlyFlow)
            {
                var extraTypesHint = context.JsBindingsConfig.ExtraTypes.Count > 0
                        && validExtraTypeCount == 0
                    ? "\n  • [bold]extraTypes[/] entries are all malformed (missing namespace or classes) — codegen would skip them all"
                    : "\n  • For an extraTypes-only cherry-pick, ensure [bold]additionalRefs[/] (or a refOnly package) is also set";
                return new JsBindingsOrchestrationResult
                {
                    ExitCode = 1,
                    Message = "No .winmd files left to emit bindings for after applying jsBindings overrides. "
                        + "Likely causes:\n"
                        + "  • All packages in [bold]jsBindings.packages[/] are categorized as skip/refOnly (check [bold]skipPackages[/] / [bold]refOnlyPackages[/])\n"
                        + "  • [bold]jsBindings.packages[/] doesn't match any restored package — verify package IDs against winapp.yaml\n"
                        + "  • Add at least one emit-set package, or use [bold]additionalWinmds[/] to supply winmds directly"
                        + extraTypesHint,
                };
            }

            if (packageRefs.Count > 0 || skippedCount > 0)
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} winmd partition: emit={bindingWinmds.Count}, ref-only={packageRefs.Count}, skipped={skippedCount}");
            }

            var combinedRefs = MergeRefWinmds(packageRefs, userRefs);

            // Step 3: codegen (staging-then-swap internally).
            taskContext.UpdateSubStatus("Generating bindings");
            var outputDir = await dynWinrtCodegenService.RunAsync(
                context.JsBindingsConfig,
                bindingWinmds,
                windowsSdkWinmd: null,
                workspaceDir: context.WorkspaceDir,
                winappDir: context.LocalWinappDir,
                taskContext: taskContext,
                userAdditionalWinmds: userWinmds,
                userAdditionalRefs: combinedRefs,
                cancellationToken: cancellationToken);

            // (No lockfile write here: the restore step already writes the
            //  full lockfile before this overlay runs — rewriting it with the
            //  scoped emit subset would lose packages outside jsBindings.packages.)

            // Ensure @microsoft/dynwinrt is a production dep so generated
            // bindings resolve at runtime.
            if (context.EnsureRuntimeDependency)
            {
                EnsureRuntimeDependencyAndPrintHint(context.WorkspaceDir);
            }

            return new JsBindingsOrchestrationResult
            {
                ExitCode = 0,
                Message = $"JS bindings generated → [underline]{outputDir.FullName}[/]",
                OutputDir = outputDir,
            };
        }
        catch (OperationCanceledException)
        {
            return new JsBindingsOrchestrationResult { ExitCode = 1, Message = "JS binding generation cancelled." };
        }
        catch (InvalidOperationException ex)
        {
            // Surface actionable codegen/config errors verbatim (they're
            // safe-to-display by contract).
            taskContext.AddDebugMessage($"{UiSymbols.Note} JS binding generation failed: {ex.Message}");
            logger.LogDebug(ex, "JS binding generation failed");
            return new JsBindingsOrchestrationResult { ExitCode = 1, Message = ex.Message };
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} JS binding generation failed: {ex.Message}");
            logger.LogDebug(ex, "JS binding generation failed");
            return new JsBindingsOrchestrationResult
            {
                ExitCode = 1,
                Message = $"JS binding generation failed: {ex.Message}",
            };
        }
    }

    // Lockfile fast-path or live discovery. Returns nulls when no winmds found.
    private async Task<(
        List<FileInfo>? BindingWinmds,
        List<FileInfo>? PackageRefs,
        int SkippedCount,
        IReadOnlyDictionary<string, string>? UsedVersions)>
        DiscoverWinmdsAsync(
            JsBindingsOrchestrationContext context,
            TaskContext taskContext,
            CancellationToken cancellationToken)
    {
        // Init/restore already has usedVersions → skip the lockfile fast-path.
        if (context.UsedVersions is not null)
        {
            return await LiveDiscoveryAsync(context.UsedVersions, context, taskContext, cancellationToken);
        }

        var lockfile = await winmdsLockfileService.TryReadAsync(context.LocalWinappDir, cancellationToken);
        var currentYamlHash = YamlPackagesHasher.Compute(context.WinappConfig.Packages);
        if (lockfile is not null
            && !string.IsNullOrEmpty(lockfile.YamlPackagesHash)
            && !string.Equals(lockfile.YamlPackagesHash, currentYamlHash, StringComparison.Ordinal))
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} Winmds lockfile is stale (yaml packages: changed since restore); falling back to live discovery.");
            lockfile = null;
        }

        if (lockfile is not null)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} Using winmds lockfile (generated {lockfile.GeneratedAt}, {lockfile.Packages.Count} packages)");
            var (emit, refOnly, skipped) = PartitionFromLockfile(
                lockfile,
                context.JsBindingsConfig.Packages,
                JsBindingsPresets.PackageCategoryOverrides.From(context.JsBindingsConfig));

            // Recorded paths still exist?
            var missing = emit.Concat(refOnly).Count(f => !f.Exists);
            if (missing > 0)
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} Winmds lockfile references {missing} missing file(s) (NuGet cache cleared?); falling back to live discovery.");
                lockfile = null;
            }
            else if (emit.Count == 0 && refOnly.Count == 0)
            {
                // Empty after partition — distinct from "no winmds at all".
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} Lockfile has no winmds matching the configured packages. Adjust jsBindings.packages or re-run winapp restore.");
                return (emit, refOnly, skipped, null);
            }
            else
            {
                return (emit, refOnly, skipped, null);
            }
        }

        // Slow path: cache walk + transitive expansion.
        taskContext.AddDebugMessage(
            $"{UiSymbols.Note} No usable winmds lockfile; falling back to live discovery (re-run [bold]winapp restore[/] to enable the fast path).");

        var explicitVersions = context.WinappConfig.Packages.ToDictionary(
            p => p.Name, p => p.Version, StringComparer.OrdinalIgnoreCase);
        if (explicitVersions.Count == 0)
        {
            return (null, null, 0, null);
        }

        taskContext.UpdateSubStatus("Resolving package graph");
        var derivedUsedVersions = await ExpandTransitiveDependenciesAsync(explicitVersions, taskContext, cancellationToken);
        if (derivedUsedVersions.Count > explicitVersions.Count)
        {
            taskContext.AddDebugMessage(
                $"{UiSymbols.Note} Expanded {explicitVersions.Count} pinned package(s) → {derivedUsedVersions.Count} total (with transitive deps)");
        }
        return await LiveDiscoveryAsync(derivedUsedVersions, context, taskContext, cancellationToken);
    }

    private async Task<(
        List<FileInfo>? BindingWinmds,
        List<FileInfo>? PackageRefs,
        int SkippedCount,
        IReadOnlyDictionary<string, string>? UsedVersions)>
        LiveDiscoveryAsync(
            IReadOnlyDictionary<string, string> usedVersions,
            JsBindingsOrchestrationContext context,
            TaskContext taskContext,
            CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        taskContext.UpdateSubStatus("Discovering .winmd metadata");

        // Discover ALL package winmds; the scope narrows EMIT output, not
        // codegen's metadata visibility — non-scoped dependencies still flow
        // through as RefOnly for cross-package type resolution.
        var allPackageWinmds = packageLayoutService.FindWinmds(
            context.NugetCacheDir,
            new Dictionary<string, string>(usedVersions, StringComparer.OrdinalIgnoreCase)).ToList();
        if (allPackageWinmds.Count == 0)
        {
            return (null, null, 0, usedVersions);
        }

        var partition = JsBindingsPresets.PartitionByPackageCategory(
            allPackageWinmds,
            JsBindingsPresets.PackageCategoryOverrides.From(context.JsBindingsConfig),
            context.NugetCacheDir.FullName,
            emitScope: context.JsBindingsConfig.Packages);
        return (partition.Emit.ToList(), partition.RefOnly.ToList(), partition.Skipped.Count, usedVersions);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers (originally lived in WorkspaceSetupService).
    // ─────────────────────────────────────────────────────────────────────

    internal static Dictionary<string, string> ScopeUsedVersionsToBindingPackages(
        Dictionary<string, string> usedVersions,
        IReadOnlyList<string>? bindingPackages)
    {
        if (bindingPackages is null || bindingPackages.Count == 0)
        {
            return usedVersions;
        }
        var allow = new HashSet<string>(bindingPackages, StringComparer.OrdinalIgnoreCase);
        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pkg, ver) in usedVersions)
        {
            if (allow.Contains(pkg))
            {
                filtered[pkg] = ver;
            }
        }
        return filtered;
    }

    internal static (List<FileInfo> Emit, List<FileInfo> RefOnly, int SkippedCount) PartitionFromLockfile(
        WinmdsLockfile lockfile,
        IReadOnlyList<string>? scopePackages,
        JsBindingsPresets.PackageCategoryOverrides? overrides = null)
    {
        HashSet<string>? scope = null;
        if (scopePackages is { Count: > 0 })
        {
            scope = new HashSet<string>(scopePackages, StringComparer.OrdinalIgnoreCase);
        }

        var emit = new List<FileInfo>();
        var refOnly = new List<FileInfo>();
        var skippedCount = 0;
        foreach (var pkg in lockfile.Packages)
        {
            // Scope is applied AFTER classification — unscoped emit packages
            // are demoted to RefOnly so codegen still sees them for type
            // resolution. Skip/RefOnly classifications are scope-independent.
            var cat = JsBindingsPresets.ClassifyPackage(pkg.Name, overrides);
            if (scope is not null
                && cat == WinmdPackageCategory.Emit
                && !scope.Contains(pkg.Name))
            {
                cat = WinmdPackageCategory.RefOnly;
            }

            switch (cat)
            {
                case WinmdPackageCategory.Skip:
                    skippedCount += pkg.Winmds.Count > 0 ? 1 : 0;
                    break;
                case WinmdPackageCategory.RefOnly:
                    foreach (var path in pkg.Winmds)
                    {
                        // Drop UNC paths so a tampered lockfile can't
                        // trigger credential-leaking SMB probes downstream.
                        if (PathSafety.IsNetworkPath(path))
                        {
                            continue;
                        }
                        refOnly.Add(new FileInfo(path));
                    }
                    break;
                default:
                    foreach (var path in pkg.Winmds)
                    {
                        if (PathSafety.IsNetworkPath(path))
                        {
                            continue;
                        }
                        emit.Add(new FileInfo(path));
                    }
                    break;
            }
        }
        return (emit, refOnly, skippedCount);
    }

    internal static List<FileInfo> MergeRefWinmds(
        IReadOnlyList<FileInfo> first,
        IReadOnlyList<FileInfo>? second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FileInfo>();
        foreach (var f in first)
        {
            if (seen.Add(f.FullName))
            {
                result.Add(f);
            }
        }
        if (second is not null)
        {
            foreach (var f in second)
            {
                if (seen.Add(f.FullName))
                {
                    result.Add(f);
                }
            }
        }
        return result;
    }

    public async Task<int> AddAsync(AddJsBindingsOptions options, CancellationToken cancellationToken = default)
    {
        configService.ConfigPath = new FileInfo(Path.Combine(options.ConfigDir.FullName, "winapp.yaml"));

        if (!configService.Exists())
        {
            logger.LogError(
                "{UISymbol} winapp.yaml not found at {ConfigPath}. Run 'npx winapp init' first to bootstrap a workspace. "
                + "Tip: --config-dir resolves relative to the current directory — verify it points to the same workspace 'init' targeted.",
                UiSymbols.Error,
                configService.ConfigPath.FullName);
            return 1;
        }

        WinappConfig config;
        try
        {
            config = configService.Load();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{UISymbol} Failed to parse winapp.yaml at {ConfigPath}: {Message}",
                UiSymbols.Error,
                configService.ConfigPath.FullName,
                ex.Message);
            return 1;
        }

        // Existing jsBindings? --force replaces; --use-defaults preserves
        // (idempotent no-op); interactive prompts; non-interactive errors.
        if (config.JsBindings is not null && !options.Force)
        {
            if (options.UseDefaults)
            {
                logger.LogInformation(
                    "{UISymbol} jsBindings already declared; preserving (use --force to patch).",
                    UiSymbols.Note);
                return 0;
            }

            bool overwrite;
            try
            {
                overwrite = await ShowConfirmationPromptAsync(
                    "winapp.yaml already declares jsBindings. Overwrite?",
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;  // Real user/parent cancellation — let it propagate.
            }
            catch (Exception)
            {
                logger.LogError(
                    "{UISymbol} winapp.yaml already declares jsBindings. Re-run with --force to patch (output and preset packages get overwritten; all other fields preserved). Pass --use-defaults to preserve and exit 0 instead.",
                    UiSymbols.Error);
                return 1;
            }

            if (!overwrite)
            {
                logger.LogInformation("{UISymbol} No changes; existing jsBindings preserved.", UiSymbols.Note);
                return 0;
            }
        }

        // Build the (new or patched) jsBindings block. On --force we patch
        // in place — only CLI-supplied fields (output, preset packages)
        // overwrite; everything else survives.
        var oldOutput = config.JsBindings?.Output;
        var newJs = config.JsBindings ?? new JsBindingsConfig();
        if (!string.IsNullOrWhiteSpace(options.Output))
        {
            newJs.Output = options.Output!.Trim();
        }
        if (options.Presets is { Count: > 0 } presetNames)
        {
            var packageIds = JsBindingsPresets.ResolveAndUnion(presetNames);
            if (packageIds.Count > 0)
            {
                newJs.Packages = new List<string>(packageIds);
                logger.LogDebug(
                    "{UISymbol} jsBindings presets [{Presets}] → packages=[{Packages}]",
                    UiSymbols.New,
                    string.Join(", ", presetNames),
                    string.Join(", ", packageIds));
            }
        }

        // Validate the resolved output path BEFORE we touch yaml.
        try
        {
            DynWinrtCodegenService.ResolveOutputDir(options.BaseDirectory, newJs.Output);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError("{UISymbol} Invalid jsBindings.output: {Reason}", UiSymbols.Error, ex.Message);
            return 1;
        }

        config.JsBindings = newJs;

        try
        {
            configService.SaveJsBindingsOnly(config);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{UISymbol} Failed to write winapp.yaml at {ConfigPath}: {Message}",
                UiSymbols.Error,
                configService.ConfigPath.FullName,
                ex.Message);
            return 1;
        }

        logger.LogInformation("{UISymbol} Updated winapp.yaml with jsBindings block", UiSymbols.Save);

        var codegenExit = await statusService.ExecuteWithStatusAsync(
            "Generating JS bindings",
            async (taskContext, ct) =>
            {
                var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
                var localWinappDir = winappDirectoryService.GetLocalWinappDirectory(options.BaseDirectory);

                var orchResult = await RunAsync(
                    new JsBindingsOrchestrationContext
                    {
                        JsBindingsConfig = config.JsBindings!,
                        WinappConfig = config,
                        WorkspaceDir = options.BaseDirectory,
                        LocalWinappDir = localWinappDir,
                        NugetCacheDir = nugetCacheDir,
                        UsedVersions = null,
                    },
                    taskContext,
                    ct);
                return (orchResult.ExitCode, orchResult.Message);
            },
            cancellationToken);

        // Cleanup only after codegen succeeds.
        if (codegenExit == 0
            && !string.IsNullOrEmpty(oldOutput)
            && !string.Equals(oldOutput, newJs.Output, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var oldDir = DynWinrtCodegenService.ResolveOutputDir(options.BaseDirectory, oldOutput);
                var newDir = DynWinrtCodegenService.ResolveOutputDir(options.BaseDirectory, newJs.Output);
                if (oldDir.Exists
                    && !string.Equals(oldDir.FullName, newDir.FullName, StringComparison.OrdinalIgnoreCase)
                    && !IsNestedPath(oldDir.FullName, newDir.FullName)
                    && !IsNestedPath(newDir.FullName, oldDir.FullName))
                {
                    DynWinrtCodegenService.WipeOutputDirSafely(oldDir);
                    oldDir.Refresh();
                    if (oldDir.Exists && !oldDir.EnumerateFileSystemInfos().Any())
                    {
                        oldDir.Delete();
                    }
                    logger.LogInformation(
                        "{UISymbol} Removed previous bindings dir {OldDir} (output: changed)",
                        UiSymbols.Trash, oldDir.FullName);
                }
                else if (oldDir.Exists
                    && (IsNestedPath(oldDir.FullName, newDir.FullName) || IsNestedPath(newDir.FullName, oldDir.FullName)))
                {
                    // Nested paths — wiping old would wipe new (or vice versa).
                    // Skip cleanup so we never delete the bindings we just generated.
                    logger.LogInformation(
                        "{UISymbol} Previous bindings dir {OldDir} overlaps new output {NewDir}; skipping cleanup. Delete manually if no longer needed.",
                        UiSymbols.Note, oldDir.FullName, newDir.FullName);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogInformation(
                    "{UISymbol} Previous bindings dir not removed: {Reason}. Delete manually if no longer needed.",
                    UiSymbols.Note, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "{UISymbol} Old output dir cleanup failed: {Reason}.",
                    UiSymbols.Warning, ex.Message);
            }
        }

        return codegenExit;
    }

    public async Task<int> GenerateAsync(GenerateJsBindingsOptions options, CancellationToken cancellationToken = default)
    {
        configService.ConfigPath = new FileInfo(Path.Combine(options.ConfigDir.FullName, "winapp.yaml"));

        if (!configService.Exists())
        {
            logger.LogError(
                "{UISymbol} winapp.yaml not found at {ConfigPath}. Run 'npx winapp init' first to bootstrap a workspace. "
                + "Tip: --config-dir resolves relative to the current directory — verify it points to the same workspace 'init' targeted.",
                UiSymbols.Error,
                configService.ConfigPath.FullName);
            return 1;
        }

        WinappConfig config;
        try
        {
            config = configService.Load();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{UISymbol} Failed to parse winapp.yaml at {ConfigPath}: {Message}",
                UiSymbols.Error,
                configService.ConfigPath.FullName,
                ex.Message);
            return 1;
        }

        if (config.JsBindings is null)
        {
            logger.LogError(
                "{UISymbol} No jsBindings: block in winapp.yaml. Run 'npx winapp node jsbindings add' first to declare one.",
                UiSymbols.Error);
            return 1;
        }

        try
        {
            DynWinrtCodegenService.ResolveOutputDir(options.BaseDirectory, config.JsBindings.Output);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError("{UISymbol} Invalid jsBindings.output: {Reason}", UiSymbols.Error, ex.Message);
            return 1;
        }

        var codegenExit = await statusService.ExecuteWithStatusAsync(
            "Generating JS bindings",
            async (taskContext, ct) =>
            {
                var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
                var localWinappDir = winappDirectoryService.GetLocalWinappDirectory(options.BaseDirectory);

                var orchResult = await RunAsync(
                    new JsBindingsOrchestrationContext
                    {
                        JsBindingsConfig = config.JsBindings!,
                        WinappConfig = config,
                        WorkspaceDir = options.BaseDirectory,
                        LocalWinappDir = localWinappDir,
                        NugetCacheDir = nugetCacheDir,
                        UsedVersions = null,
                        // Read-only contract: `node jsbindings generate` is
                        // documented as a no-op on yaml AND package.json.
                        // The runtime dep is added by `node jsbindings add`
                        // and by `init --js-bindings`; re-adding it here
                        // would silently un-do a deliberate user removal.
                        EnsureRuntimeDependency = false,
                    },
                    taskContext,
                    ct);
                return (orchResult.ExitCode, orchResult.Message);
            },
            cancellationToken);

        return codegenExit;
    }

    private async Task<bool> ShowConfirmationPromptAsync(string prompt, CancellationToken cancellationToken)
    {
        var result = await ansiConsole.PromptAsync(new ConfirmationPrompt(prompt), cancellationToken);
        ansiConsole.Cursor.MoveUp();
        ansiConsole.Write("\x1b[2K");
        ansiConsole.MarkupLine($"{prompt}: [underline]{(result ? "Yes" : "No")}[/]");
        return result;
    }

    // True when `child` is at or below `parent` in the file system tree
    // (case-insensitive on Windows). Used to skip cleanup of nested
    // old/new output dirs where wiping one would wipe the other.
    private static bool IsNestedPath(string parent, string child)
    {
        var p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(p, c, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = p + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
