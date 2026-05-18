// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Spawns dynwinrt-codegen against discovered .winmd metadata.
internal sealed class DynWinrtCodegenService(
    INpmWrapperVersionProvider npmWrapperVersionProvider,
    ILogger<DynWinrtCodegenService> logger) : IDynWinrtCodegenService
{
    private const string CodegenPackageName = "@microsoft/dynwinrt-codegen";

    // Dev/test fallback when no npm wrapper layout is available. Production
    // reads the version from INpmWrapperVersionProvider (the wrapper's own
    // package.json pin). Keep this in sync with src/winapp-npm/package.json.
    internal const string CodegenPinnedVersionFallback = "0.1.0-preview.1";

    // Marker written into the output dir after a successful run; its
    // presence authorises the next run to wipe.
    public const string ManagedMarkerFileName = ".dynwinrt-managed";

    public async Task<DirectoryInfo> RunAsync(
        JsBindingsConfig config,
        IReadOnlyList<FileInfo> winmds,
        FileInfo? windowsSdkWinmd,
        DirectoryInfo workspaceDir,
        DirectoryInfo winappDir,
        TaskContext taskContext,
        IReadOnlyList<FileInfo>? userAdditionalWinmds = null,
        IReadOnlyList<FileInfo>? userAdditionalRefs = null,
        CancellationToken cancellationToken = default)
    {
        winappDir.Create();

        var outputDir = ResolveOutputDir(workspaceDir, config.Output);
        outputDir.Parent?.Create();

        var listedWinmds = CollectListedWinmds(winmds, userAdditionalWinmds, windowsSdkWinmd);
        var refWinmds = CollectRefWinmds(userAdditionalRefs, listedWinmds);

        // Locate codegen BEFORE touching the output dir so a missing install
        // doesn't first wipe the user's previous bindings. Use the npm
        // wrapper's pinned version for any error hints so they don't drift.
        var versionHint = TryReadCodegenVersionHint();
        var (executable, prefixArgs) = ResolveCodegenInvocation(workspaceDir, versionHint);
        logger.LogInformation(
            "{UISymbol} Resolved dynwinrt-codegen → {Executable} {PrefixArgs}",
            UiSymbols.Tools, executable, string.Join(' ', prefixArgs));
        taskContext.AddDebugMessage($"{UiSymbols.Tools} Using codegen → {executable} {string.Join(' ', prefixArgs)}");
        taskContext.AddDebugMessage($"{UiSymbols.Note} Codegen inputs: {listedWinmds.Count} emit + {refWinmds.Count} ref winmd(s)");

        // Stage-then-swap: failure leaves previous output intact.
        await RunWithStagingAsync(outputDir, async stagingDir =>
        {
            // Skip the bulk pass when no emit winmds — extraTypes-only
            // cherry-pick (refs + extraTypes, no bulk emit) still runs the
            // per-extraType loop below.
            if (listedWinmds.Count > 0)
            {
                var bulkArgs = BuildBulkArgs(prefixArgs, listedWinmds, stagingDir, config, refWinmds);
                await SpawnCodegenAsync(executable, bulkArgs, workspaceDir, taskContext, cancellationToken);
            }

            // One pass per extraType — cherry-picks a class from the same
            // metadata universe as the bulk pass.
            foreach (var et in config.ExtraTypes)
            {
                if (string.IsNullOrWhiteSpace(et.Namespace) || et.Classes.Count == 0)
                {
                    continue;
                }
                var extraArgs = BuildExtraTypeArgs(prefixArgs, listedWinmds, stagingDir, config, refWinmds, et);
                await SpawnCodegenAsync(executable, extraArgs, workspaceDir, taskContext, cancellationToken);
            }
        });

        outputDir.Refresh();
        return outputDir;
    }

    // Stage → backup-old → swap → drop-backup. Failure at any swap step
    // restores the previous output. Internal for tests.
    internal static async Task RunWithStagingAsync(
        DirectoryInfo outputDir,
        Func<DirectoryInfo, Task> generate)
    {
        var stagingDir = new DirectoryInfo(
            Path.Combine(outputDir.Parent!.FullName, $"{outputDir.Name}.staging.{Guid.NewGuid():N}"));
        DirectoryInfo? backupDir = null;
        stagingDir.Create();
        try
        {
            await generate(stagingDir);

            WriteManagedMarker(stagingDir);

            ValidateOutputDirIsWipeable(outputDir);

            if (outputDir.Exists)
            {
                backupDir = new DirectoryInfo(
                    Path.Combine(outputDir.Parent!.FullName, $"{outputDir.Name}.backup.{Guid.NewGuid():N}"));
                Directory.Move(outputDir.FullName, backupDir.FullName);
            }

            try
            {
                Directory.Move(stagingDir.FullName, outputDir.FullName);
                // Null the local immediately on success so the finally-block
                // cleanup can't ever re-target the now-renamed staging dir
                // (which IS the user's new output).
                stagingDir = null!;
            }
            catch
            {
                // Restore the previous output so the user isn't left empty.
                if (backupDir is not null && backupDir.Exists)
                {
                    try { Directory.Move(backupDir.FullName, outputDir.FullName); backupDir = null; }
                    catch (Exception restoreEx)
                    {
                        // Restore failed — preserve the backup on disk (it's
                        // the only surviving copy) and surface the path so
                        // the user can recover manually. Null the local so
                        // the finally block doesn't delete it.
                        var preserved = backupDir!.FullName;
                        backupDir = null;
                        throw new IOException(
                            $"Codegen failed AND the previous output could not be restored. "
                            + $"Your previous bindings are preserved at: {preserved}. "
                            + $"Move them back manually if needed. Restore error: {restoreEx.Message}");
                    }
                }
                throw;
            }
        }
        finally
        {
            if (stagingDir is not null)
            {
                try { stagingDir.Delete(recursive: true); }
                catch { /* orphan staging is harmless */ }
            }
            if (backupDir is not null)
            {
                try { backupDir.Delete(recursive: true); }
                catch { /* orphan backup is harmless */ }
            }
        }
    }

    // Resolve output dir, refusing escape (typos / reparse points) so the
    // pre-codegen wipe stays inside the workspace.
    internal static DirectoryInfo ResolveOutputDir(DirectoryInfo workspaceDir, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            output = "bindings/winrt";
        }
        var path = Path.IsPathRooted(output)
            ? output
            : Path.Combine(workspaceDir.FullName, output);
        var resolvedFull = Path.GetFullPath(path);
        var workspaceFull = Path.GetFullPath(workspaceDir.FullName)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Lexical containment check.
        var sep = Path.DirectorySeparatorChar;
        var prefix = workspaceFull + sep;
        var insideWorkspace = resolvedFull.Length > prefix.Length
            && resolvedFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (!insideWorkspace)
        {
            throw new InvalidOperationException(
                $"jsBindings.output ('{output}') resolves to '{resolvedFull}' which is outside the workspace "
                + $"('{workspaceFull}'). The output directory is wiped before each codegen run, so it must be "
                + "a path strictly inside the workspace. Use a relative path like 'bindings/winrt' or an absolute "
                + "path that descends from the workspace root.");
        }

        // Physical containment: reject reparse points in the chain so the
        // recursive delete can't follow a junction outside the workspace.
        for (var probe = new DirectoryInfo(resolvedFull);
             probe is not null && probe.FullName.Length >= workspaceFull.Length;
             probe = probe.Parent)
        {
            if (probe.Exists && (probe.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"jsBindings.output ('{output}') resolves through a reparse point at '{probe.FullName}'. "
                    + "Reparse points (symlinks / junctions) are rejected because they could redirect the output "
                    + "wipe outside the workspace. Move the output to a regular subdirectory of the workspace.");
            }
            if (string.Equals(probe.FullName.TrimEnd(sep, Path.AltDirectorySeparatorChar),
                              workspaceFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new DirectoryInfo(resolvedFull);
    }

    // Deduplicated emit-set: packages + user additionalWinmds + optional SDK winmd.
    internal static List<FileInfo> CollectListedWinmds(
        IReadOnlyList<FileInfo> winmds,
        IReadOnlyList<FileInfo>? userAdditional,
        FileInfo? windowsSdkWinmd)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FileInfo>();
        void Add(FileInfo? f)
        {
            if (f is null)
            {
                return;
            }
            if (seen.Add(f.FullName))
            {
                result.Add(f);
            }
        }
        foreach (var w in winmds)
        {
            Add(w);
        }
        if (userAdditional is not null)
        {
            foreach (var w in userAdditional)
            {
                Add(w);
            }
        }
        Add(windowsSdkWinmd);
        return result;
    }

    // Deduplicated ref-set. Entries already in listedWinmds are dropped
    // (a file in both wins as emit).
    internal static List<FileInfo> CollectRefWinmds(
        IReadOnlyList<FileInfo>? userAdditionalRefs,
        IReadOnlyList<FileInfo> listedWinmds)
    {
        var result = new List<FileInfo>();
        if (userAdditionalRefs is null || userAdditionalRefs.Count == 0)
        {
            return result;
        }
        var listedSet = new HashSet<string>(listedWinmds.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in userAdditionalRefs)
        {
            if (listedSet.Contains(f.FullName))
            {
                continue;
            }
            if (seen.Add(f.FullName))
            {
                result.Add(f);
            }
        }
        return result;
    }

    // Wipe outputDir only when safe (empty, or has the managed marker).
    // Refuses if the dir or any top-level child is a reparse point.
    internal static void WipeOutputDirSafely(DirectoryInfo outputDir)
    {
        if (!outputDir.Exists)
        {
            return;
        }

        // Validation throws on any unsafe state.
        ValidateOutputDirIsWipeable(outputDir);

        var entries = outputDir.EnumerateFileSystemInfos().ToList();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case DirectoryInfo d:
                    d.Delete(recursive: true);
                    break;
                case FileInfo f:
                    f.Delete();
                    break;
            }
        }
    }

    // Throws if outputDir cannot be safely wiped. Does NOT mutate anything.
    // Used by RunWithStagingAsync so we can validate-then-rename-to-backup
    // instead of validate-and-delete-then-rename — the latter would lose old
    // bindings if the post-wipe rename failed.
    internal static void ValidateOutputDirIsWipeable(DirectoryInfo outputDir)
    {
        if (!outputDir.Exists)
        {
            return;
        }

        if ((outputDir.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to wipe '{outputDir.FullName}': it is a reparse point (symlink or junction). "
                + "The wipe could follow the link and delete files outside the workspace. "
                + "Move the output to a regular directory and try again.");
        }

        var entries = outputDir.EnumerateFileSystemInfos().ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var marker = Path.Combine(outputDir.FullName, ManagedMarkerFileName);
        if (!File.Exists(marker))
        {
            throw new InvalidOperationException(
                $"Refusing to wipe non-managed output directory '{outputDir.FullName}'. "
                + $"This directory contains files but does not have a '{ManagedMarkerFileName}' marker, "
                + "which indicates it was created or modified outside winapp. "
                + "Move or delete its contents manually if you intended to reuse this path for JS bindings.");
        }

        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to wipe '{outputDir.FullName}': child entry '{entry.Name}' is a reparse point. "
                    + "Delete it manually before re-running codegen.");
            }
        }
    }

    // Write the managed marker. Only its existence is checked; the body
    // (timestamp) is a debugging aid.
    internal static void WriteManagedMarker(DirectoryInfo outputDir)
    {
        outputDir.Create();
        var markerPath = Path.Combine(outputDir.FullName, ManagedMarkerFileName);
        var lines = new[]
        {
            "# Generated by winapp dynwinrt-codegen integration. Do not edit.",
            "# Presence of this file authorises winapp to wipe the directory on the next run.",
            $"generated_at: {DateTimeOffset.UtcNow:O}",
            "",
        };
        File.WriteAllText(markerPath, string.Join('\n', lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static List<string> BuildBulkArgs(
        IReadOnlyList<string> prefixArgs,
        IReadOnlyList<FileInfo> emitWinmds,
        DirectoryInfo outputDir,
        JsBindingsConfig config,
        List<FileInfo> refWinmds)
    {
        var args = new List<string>(prefixArgs)
        {
            "generate",
            "--winmd", string.Join(';', emitWinmds.Select(f => f.FullName)),
            "--output", outputDir.FullName,
            "--lang", config.Lang,
        };
        if (refWinmds.Count > 0)
        {
            args.Add("--ref");
            args.Add(string.Join(';', refWinmds.Select(f => f.FullName)));
        }
        if (config.Lang == "py")
        {
            args.Add("--pyi");
        }
        return args;
    }

    internal static List<string> BuildExtraTypeArgs(
        IReadOnlyList<string> prefixArgs,
        IReadOnlyList<FileInfo> emitWinmds,
        DirectoryInfo outputDir,
        JsBindingsConfig config,
        List<FileInfo> refWinmds,
        JsBindingsExtraType extra)
    {
        var args = new List<string>(prefixArgs)
        {
            "generate",
        };
        // Emit winmds may be empty in the extraTypes-only cherry-pick
        // workflow (refs supply metadata for the named types).
        if (emitWinmds.Count > 0)
        {
            args.Add("--winmd");
            args.Add(string.Join(';', emitWinmds.Select(f => f.FullName)));
        }
        args.AddRange(new[]
        {
            "--namespace", extra.Namespace,
            "--class-name", string.Join(',', extra.Classes),
            "--output", outputDir.FullName,
            "--lang", config.Lang,
        });
        if (refWinmds.Count > 0)
        {
            args.Add("--ref");
            args.Add(string.Join(';', refWinmds.Select(f => f.FullName)));
        }
        return args;
    }

    private async Task SpawnCodegenAsync(
        string executable,
        IReadOnlyList<string> args,
        DirectoryInfo workspaceDir,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspaceDir.FullName,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start codegen process: {executable}");

        try
        {
            // Drain stdout+stderr in parallel to avoid pipe-fill deadlock
            // (~4KB Windows kernel buffer → serialised reads can hang).
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = p.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await p.WaitForExitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                taskContext.AddDebugMessage(stdout.TrimEnd());
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                taskContext.AddDebugMessage(stderr.TrimEnd());
            }

            if (p.ExitCode != 0)
            {
                logger.LogError("dynwinrt-codegen exited with code {Code}: {Err}", p.ExitCode, stderr);
                throw new InvalidOperationException($"dynwinrt-codegen failed (exit {p.ExitCode}). See debug output for details.");
            }
        }
        catch (OperationCanceledException)
        {
            // Kill the child tree so we don't leak a zombie holding file
            // locks on staging.
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    try
                    {
                        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await p.WaitForExitAsync(killCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // OS will reap eventually; let cancellation propagate.
                    }
                }
            }
            catch (Exception killEx)
            {
                logger.LogDebug(killEx, "Failed to kill cancelled codegen process (pid {Pid})", p.Id);
            }
            throw;
        }
    }

    // Walk parent dirs for node_modules/@microsoft/dynwinrt-codegen (Node.js
    // bare-specifier resolution). Prefers the pre-built .exe; falls back to
    // cli.js via a PATHEXT-resolved `node`.
    internal static (string Executable, List<string> PrefixArgs) ResolveCodegenInvocation(
        DirectoryInfo workspaceDir,
        string? codegenVersionHint = null)
    {
        var arch = ResolveArchSubdir();
        DirectoryInfo? lastChecked = null;

        // Search workspace ancestry first (user-installed override), then fall
        // back to the wrapper's own node_modules near Environment.ProcessPath.
        // pnpm / yarn-Berry layouts often place the codegen under the wrapper
        // package rather than the workspace.
        var roots = new List<DirectoryInfo> { workspaceDir };
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var exeDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                var d = new DirectoryInfo(exeDir);
                if (!string.Equals(d.FullName, workspaceDir.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(d);
                }
            }
        }

        foreach (var root in roots)
        {
            for (var probe = root; probe is not null; probe = probe.Parent)
            {
                var packageDir = Path.Combine(probe.FullName, "node_modules", "@microsoft", "dynwinrt-codegen");
                if (!Directory.Exists(packageDir))
                {
                    continue;
                }

                // Priority 1: pre-built .exe (no Node startup needed).
                var directExe = new FileInfo(Path.Combine(packageDir, "bin", arch, "dynwinrt-codegen.exe"));
                if (directExe.Exists)
                {
                    return (directExe.FullName, new List<string>());
                }

                // Priority 2: cli.js via node.exe (defensive fallback).
                var localCli = new FileInfo(Path.Combine(packageDir, "cli.js"));
                if (localCli.Exists)
                {
                    // Reject .bat/.cmd/.ps1 — those go through cmd.exe parsing
                    // where user-derived args could be misinterpreted. Only
                    // spawn native executables (node.exe / node.com).
                    var nodePath = ResolveExecutableOnPath("node", nativeOnly: true)
                        ?? throw new InvalidOperationException(
                            $"The codegen at '{localCli.FullName}' requires a native Node.js executable "
                            + "(node.exe) on PATH. Install Node 18+ (winget install OpenJS.NodeJS) "
                            + $"or install {CodegenPackageName} so the pre-built .exe is available.");
                    return (nodePath, new List<string> { localCli.FullName });
                }

                // Partial install (no exe + no cli.js); remember and keep walking.
                lastChecked = new DirectoryInfo(packageDir);
            }
        }

        var hint = lastChecked is not null
            ? $"Found {CodegenPackageName} at '{lastChecked.FullName}' but no executable inside "
                + $"(expected 'bin/{arch}/dynwinrt-codegen.exe' or 'cli.js'). The npm package may be corrupt; reinstall it.\n\n"
            : $"Searched {CodegenPackageName} upward from '{workspaceDir.FullName}' and the wrapper install — no node_modules/@microsoft/dynwinrt-codegen found.\n\n";

        var versionForHint = codegenVersionHint ?? CodegenPinnedVersionFallback;
        throw new InvalidOperationException(
            hint
            + "To enable JS bindings, install the codegen via one of:\n"
            + "  • npm/yarn classic/pnpm (default):  npm i -D @microsoft/winappcli\n"
            + "    (bundles " + CodegenPackageName + " as a transitive dependency)\n"
            + "  • Install the codegen directly:     npm i -D "
            + CodegenPackageName + "@" + versionForHint + "\n"
            + "  • yarn berry (PnP):                 set 'nodeLinker: node-modules' in .yarnrc.yml, then yarn install\n"
            + "  • pnpm with isolated linker:        set 'node-linker=hoisted' in .npmrc, then pnpm install\n\n"
            + "See https://github.com/microsoft/WinAppCli#electron--nodejs for setup details.");
    }

    // Read the codegen version from the npm wrapper's package.json; falls
    // back to the in-source constant when the provider can't locate it
    // (dev / test scenarios outside the npm install layout).
    private string? TryReadCodegenVersionHint()
    {
        try
        {
            return npmWrapperVersionProvider.DynWinrtCodegenVersion;
        }
        catch
        {
            return null;
        }
    }

    // Resolve `command` via PATH + PATHEXT, skipping CWD-equivalent entries
    // to prevent local hijack (e.g. node.exe dropped in the workspace).
    // When `nativeOnly: true`, only .exe / .com matches are returned —
    // .bat / .cmd / .ps1 dispatch through cmd.exe / pwsh and would re-parse
    // any user-derived args, so we reject them in security-sensitive paths.
    internal static string? ResolveExecutableOnPath(string command, bool nativeOnly = false)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extEnv = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            : string.Empty;
        var exts = extEnv.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (nativeOnly)
        {
            exts = exts.Where(e =>
                e.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || e.Equals(".com", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        string? cwdFull = null;
        try
        {
            cwdFull = Path.GetFullPath(Directory.GetCurrentDirectory());
        }
        catch
        {
            // Best-effort; literal "." / "" skips still apply.
        }

        foreach (var dir in pathDirs)
        {
            var trimmed = dir.Trim().Trim('"');
            if (string.IsNullOrEmpty(trimmed) || trimmed == ".")
            {
                continue;
            }
            // Reject relative PATH entries entirely — they would be resolved
            // against CWD and let a workspace-local `./bin` shadow trusted
            // system locations.
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue;
            }
            if (cwdFull is not null)
            {
                string? resolved = null;
                try
                {
                    resolved = Path.GetFullPath(trimmed);
                }
                catch
                {
                    continue;
                }
                if (string.Equals(resolved, cwdFull, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            // Bare match (command may already include an extension). In
            // nativeOnly mode, only accept when the existing extension is
            // .exe/.com — otherwise the caller would unknowingly spawn a
            // .bat/.cmd.
            var bare = Path.Combine(trimmed, command);
            if (File.Exists(bare))
            {
                if (nativeOnly)
                {
                    var bareExt = Path.GetExtension(bare);
                    if (!bareExt.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        && !bareExt.Equals(".com", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                return Path.GetFullPath(bare);
            }
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(trimmed, command + ext);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        return null;
    }

    // Pick the bin/<subdir>/ for the process arch. The codegen npm package
    // only ships x64 / arm64; other arches map to x64.
    private static string ResolveArchSubdir() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        _ => "x64",
    };
}
