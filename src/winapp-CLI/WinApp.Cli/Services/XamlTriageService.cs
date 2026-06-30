// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Runs the WinUI team's WinDbg JavaScript extension against a minidump by hosting DbgEng
/// directly, producing the stowed-exception breakdown and XAML dispatch triage that the
/// standard ClrMD/DbgEng passes cannot recover.
/// </summary>
internal sealed class XamlTriageService(
    ILogger<XamlTriageService> logger,
    IWinappDirectoryService winappDirectoryService,
    INugetService nugetService) : IXamlTriageService
{
    // Pinned WinUI debugger extension (microsoft/microsoft-ui-xaml). See plan / docs for rationale.
    private const string ExtCommit = "29d537445eaa34d47e66ab8859583ae953c62dd1";
    private const string ExtRepoPath = "dbgext/publicXamlThread/winui-dbgext.js";
    private const string ExtBlobSha1 = "820f8f7d45dc3df82623ac5163dcea8d8212e2d2";
    private const string ExtFileName = "winui-dbgext.js";

    // Hard ceiling for the isolated triage process (symbol downloads can be slow on first run).
    private static readonly TimeSpan TriageTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public async Task<string?> TryAnalyzeAsync(string dumpPath, bool useSymbols, CancellationToken cancellationToken = default)
    {
        try
        {
            var dbgToolsRoot = new DirectoryInfo(Path.Combine(
                winappDirectoryService.GetGlobalWinappDirectory().FullName, "dbgtools"));
            var cacheBinDir = new DirectoryInfo(Path.Combine(dbgToolsRoot.FullName, XamlTriageBinaries.KitsArch));

            // Resolve an existing debugger layout; if none, populate the download-on-first-use cache:
            // engine bits from NuGet (global cache or download) and JsProvider.dll from the WinDbg bundle.
            var binaries = XamlTriageBinaries.ResolveExisting(cacheBinDir, logger);
            if (binaries == null)
            {
                var nugetCacheDir = TryGetNuGetCacheDir();
                await XamlTriageBinaries.TryAcquireFromNuGetAsync(cacheBinDir, nugetCacheDir, logger, cancellationToken);

                // JsProvider.dll only ships in the WinDbg bundle; acquire it once the engine is present.
                if (XamlTriageBinaries.HasEngine(cacheBinDir))
                {
                    await WinDbgJsProviderAcquirer.TryAcquireAsync(cacheBinDir, logger, cancellationToken);
                }

                binaries = XamlTriageBinaries.ResolveExisting(cacheBinDir, logger);
            }

            if (binaries == null)
            {
                logger.LogDebug("WinUI triage skipped: debugging binaries (incl. JsProvider.dll) unavailable.");
                return UnavailableNote();
            }

            var extPath = await EnsureExtensionAsync(dbgToolsRoot, cancellationToken);
            if (extPath == null)
            {
                logger.LogDebug("WinUI triage skipped: could not obtain {Ext}.", ExtFileName);
                return null;
            }

            // Run the DbgEng pass in a dedicated child process. The parent has already loaded the
            // system32 dbghelp.dll (dump capture + ClrMD analysis), which prevents the modern NuGet
            // dbgeng.dll from binding to its co-located dbghelp.dll. A clean process avoids that.
            var output = await RunTriageProcessAsync(dumpPath, binaries, extPath, useSymbols, cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var header = $"WinUI Triage (DbgEng + winui-dbgext.js, source: {binaries.Source}):";
            var note = DescribeSymbolGap(output, useSymbols);
            return note == null
                ? $"{header}\n{output.Trim()}"
                : $"{header}\n{note}\n\n{output.Trim()}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WinUI triage pass failed.");
            return null;
        }
    }

    /// <summary>
    /// Detects the "operating-system symbols unavailable" failure shape — the extension identifies the
    /// stowed exception but can't expand its <c>combase.dll</c> structures without OS symbols — and
    /// returns a clear explanation to prepend, so the log doesn't end on a cryptic internal JS error.
    /// Returns <c>null</c> when the output doesn't show that signature.
    /// </summary>
    internal static string? DescribeSymbolGap(string output, bool useSymbols)
    {
        var symbolsMissing = output.Contains("combase.dll not loaded/unavailable", StringComparison.OrdinalIgnoreCase)
            || (output.Contains("Symbol Loading Error Summary", StringComparison.OrdinalIgnoreCase)
                && output.Contains("combase", StringComparison.OrdinalIgnoreCase));
        var decodeFailed = output.Contains("createPointerObject", StringComparison.OrdinalIgnoreCase);

        if (!symbolsMissing || !decodeFailed)
        {
            return null;
        }

        var remedy = useSymbols
            ? "The public Microsoft symbol server did not have combase.dll symbols for this Windows build " +
              "(it returned 404). Run the same analysis on a build whose OS symbols are published, or point " +
              "the engine at a local symbol store that contains them, to get the full breakdown."
            : "Re-run with --symbols so the operating-system symbols for combase.dll can be downloaded, then " +
              "the stowed exception can be fully expanded.";

        return "Note: a stowed exception (0xC000027B) was detected but could not be fully expanded because " +
            "operating-system symbols for combase.dll were unavailable. " + remedy +
            "\n(The raw extension output below is kept for reference.)";
    }

    /// <summary>
    /// Spawns the hidden <c>__xaml-triage</c> verb in a fresh process and captures its stdout. The
    /// isolation is essential: see <see cref="XamlTriageRunner"/> for the dbghelp.dll loader-collision
    /// rationale. Works both as a published single-file executable and under <c>dotnet winapp.dll</c>.
    /// </summary>
    private async Task<string?> RunTriageProcessAsync(
        string dumpPath, ResolvedTriageBinaries binaries, string extPath, bool useSymbols, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Re-invoke the current binary. When running under the dotnet host (dev/test), ProcessPath is
        // dotnet.exe and we must pass the managed entry assembly as the first argument.
        var processPath = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = processPath;
            // Only reached under the dotnet host (dev/test), where the entry assembly has a real path.
            // A single-file published winapp.exe takes the else-branch, so Location is never empty here.
#pragma warning disable IL3000
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
#pragma warning restore IL3000
        }
        else
        {
            startInfo.FileName = processPath;
        }

        startInfo.ArgumentList.Add(XamlTriageRunner.InternalVerb);
        startInfo.ArgumentList.Add("--dump");
        startInfo.ArgumentList.Add(dumpPath);
        startInfo.ArgumentList.Add("--bin");
        startInfo.ArgumentList.Add(binaries.BinDir);
        startInfo.ArgumentList.Add("--ext");
        startInfo.ArgumentList.Add(extPath);
        if (useSymbols && binaries.HasSymSrv)
        {
            startInfo.ArgumentList.Add("--symbols");
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };

        if (!process.Start())
        {
            logger.LogDebug("WinUI triage child process failed to start.");
            return null;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TriageTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogDebug("WinUI triage child process timed out after {Timeout}.", TriageTimeout);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            logger.LogDebug("WinUI triage child process exited with code {Code}: {Error}",
                process.ExitCode, stderr.ToString().Trim());
            return null;
        }

        return stdout.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Ensures the pinned <c>winui-dbgext.js</c> is present in the cache and matches its pinned
    /// git blob hash, downloading it on first use. Returns the local path or <c>null</c> on failure.
    /// </summary>
    private async Task<string?> EnsureExtensionAsync(DirectoryInfo dbgToolsRoot, CancellationToken cancellationToken)
    {
        var extDir = Path.Combine(dbgToolsRoot.FullName, "ext");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, ExtFileName);

        if (File.Exists(extPath) && GitBlobSha1(await File.ReadAllBytesAsync(extPath, cancellationToken)) == ExtBlobSha1)
        {
            return extPath;
        }

        try
        {
            var url = $"https://raw.githubusercontent.com/microsoft/microsoft-ui-xaml/{ExtCommit}/{ExtRepoPath}";
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(url, cancellationToken);

            var actual = GitBlobSha1(bytes);
            if (actual != ExtBlobSha1)
            {
                logger.LogWarning("Downloaded {Ext} hash mismatch (expected {Expected}, got {Actual}); refusing to use it.",
                    ExtFileName, ExtBlobSha1, actual);
                return null;
            }

            await File.WriteAllBytesAsync(extPath, bytes, cancellationToken);
            return extPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to download {Ext}.", ExtFileName);
            return null;
        }
    }

    /// <summary>Resolves the NuGet global packages cache directory, tolerating provider failures.</summary>
    private DirectoryInfo? TryGetNuGetCacheDir()
    {
        try
        {
            return nugetService.GetNuGetGlobalPackagesDir();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve the NuGet global packages directory for triage binaries.");
            return null;
        }
    }

    /// <summary>Computes the git blob SHA-1 (<c>sha1("blob &lt;len&gt;\0" + content)</c>) of a buffer.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is used only to reproduce git's content-addressed blob identity for integrity pinning, not for security.")]
    private static string GitBlobSha1(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var buffer = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
        Buffer.BlockCopy(content, 0, buffer, header.Length, content.Length);
        return Convert.ToHexStringLower(SHA1.HashData(buffer));
    }

    private static string UnavailableNote() =>
        "WinUI Triage: skipped — the debugger components required for stowed-exception analysis " +
        "(dbgeng.dll + JsProvider.dll) could not be obtained.\n" +
        "The engine bits come from NuGet and JsProvider.dll is extracted from the WinDbg download; " +
        "if your environment blocks those, install Debugging Tools for Windows (Windows SDK) or set " +
        $"the {XamlTriageBinaries.EnvOverride} environment variable to a debugger directory that contains them.";
}
