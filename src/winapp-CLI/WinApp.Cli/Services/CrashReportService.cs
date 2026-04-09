// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <inheritdoc/>
internal sealed class CrashReportService(IAnsiConsole console, ILogger<CrashReportService> logger) : ICrashReportService
{
    /// <inheritdoc/>
    public void PrintExitCodeSummary(int exitCode)
    {
        if (exitCode == 0)
        {
            return;
        }

        uint unsigned = unchecked((uint)exitCode);
        if (unsigned >= 0x80000000)
        {
            var name = DebugOutputService.GetExceptionName(unsigned);
            console.MarkupLine($"[red]{UiSymbols.Error} App crashed with code {exitCode} (0x{unsigned:X8}: {name.EscapeMarkup()})[/]");
        }
        else
        {
            console.MarkupLine($"[yellow]{UiSymbols.Warning} App exited with code {exitCode}[/]");
        }
    }

    /// <inheritdoc/>
    public async Task PrintCrashReportAsync(ICrashHookService crashHookService, uint processId, DateTime crashTimeUtc)
    {
        // Prefer crash hook (has full stack with file:line), only fall back to
        // Event Log when the crash hook didn't capture anything (e.g., FailFast,
        // StackOverflow, or non-.NET apps that bypass FirstChanceException).
        if (PrintCrashHookExceptions(crashHookService))
        {
            return;
        }

        await PrintEventLogCrashDetailsAsync(processId, crashTimeUtc);
    }

    /// <summary>
    /// Prints managed exception details captured by the startup hook DLL
    /// via the named pipe. Shows the most relevant exception with its full
    /// managed stack trace including file:line information.
    /// Returns true if a relevant exception was found and printed.
    /// </summary>
    private bool PrintCrashHookExceptions(ICrashHookService crashHookService)
    {
        var exceptions = crashHookService.CapturedExceptions;
        if (exceptions.Count == 0)
        {
            return false;
        }

        // Find the most relevant exception — skip noise and hook-internal exceptions
        CrashHookException? best = null;
        foreach (var ex in exceptions)
        {
            if (ex.ExceptionType.Contains("OperationCanceledException", StringComparison.Ordinal) ||
                ex.ExceptionType.Contains("TaskCanceledException", StringComparison.Ordinal) ||
                ex.ExceptionType.Contains("FileNotFoundException", StringComparison.Ordinal) ||
                ex.ExceptionType.Contains("TimeoutException", StringComparison.Ordinal) ||
                ex.StackTrace.Contains("StartupHook", StringComparison.Ordinal) ||
                ex.StackTrace.Contains("InvokeStub_StartupHook", StringComparison.Ordinal))
            {
                continue;
            }

            best = ex;
            break; // Most recent first
        }

        if (best == null)
        {
            return false;
        }

        console.WriteLine();
        console.MarkupLine("[red bold].NET Exception Details:[/]");
        console.MarkupLine($"[red]  Type:    {best.ExceptionType.EscapeMarkup()}[/]");

        if (!string.IsNullOrWhiteSpace(best.ExceptionMessage))
        {
            console.MarkupLine($"[red]  Message: {best.ExceptionMessage.EscapeMarkup()}[/]");
        }

        if (!string.IsNullOrWhiteSpace(best.HResult))
        {
            console.MarkupLine($"[red]  HResult: {best.HResult.EscapeMarkup()}[/]");
        }

        if (!string.IsNullOrWhiteSpace(best.StackTrace))
        {
            console.MarkupLine("[red]  Stack trace:[/]");
            foreach (var line in best.StackTrace.Split('\n'))
            {
                var trimmed = line.TrimEnd();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                // Highlight frames with source file:line (user code)
                if (trimmed.Contains(":line ", StringComparison.Ordinal))
                {
                    console.MarkupLine($"[red bold]  → {trimmed.EscapeMarkup()}[/]");
                }
                else
                {
                    console.MarkupLine($"[dim]    {trimmed.EscapeMarkup()}[/]");
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Queries the Windows Event Log for crash details after a process exits abnormally.
    /// Checks for .NET Runtime events (Event ID 1026, contains full exception type + message
    /// + managed stack trace) and Application Error events (Event ID 1000, contains faulting
    /// module + offset). WinUI/WinRT stowed exceptions typically only produce Event ID 1000.
    /// </summary>
    private async Task PrintEventLogCrashDetailsAsync(uint processId, DateTime sinceUtc)
    {
        try
        {
            var sinceLocal = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

            // Try .NET Runtime (Event ID 1026) first — has full exception + stack trace
            var dotnetEvent = await PollEventLogAsync(
                $"*[System[Provider[@Name='.NET Runtime'] and (EventID=1026) and Execution[@ProcessID={processId}] and TimeCreated[@SystemTime>='{sinceLocal}']]]");

            if (dotnetEvent != null)
            {
                PrintDotNetRuntimeEvent(dotnetEvent);
                return;
            }

            // Fall back to Application Error (Event ID 1000) — has faulting module info.
            // WER writes events under its own PID, so we can't filter by ProcessID in XPath.
            // Instead, query broadly and match PID from the event's data properties.
            var appErrorQuery = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "Application",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[@SystemTime>='{sinceLocal}']]]");

            var appErrorTimeout = TimeSpan.FromSeconds(3);
            var appErrorStart = DateTime.UtcNow;

            while ((DateTime.UtcNow - appErrorStart) < appErrorTimeout)
            {
                using var appErrorReader = new System.Diagnostics.Eventing.Reader.EventLogReader(appErrorQuery);
                System.Diagnostics.Eventing.Reader.EventRecord? appErrorRecord;
                while ((appErrorRecord = appErrorReader.ReadEvent()) != null)
                {
                    // Event 1000 data[8] is the decimal PID
                    var eventPidStr = appErrorRecord.Properties.Count > 8
                        ? appErrorRecord.Properties[8]?.Value?.ToString()
                        : null;

                    if (eventPidStr != null &&
                        uint.TryParse(eventPidStr, out var eventPid) &&
                        eventPid == processId)
                    {
                        PrintApplicationErrorEvent(appErrorRecord);
                        return;
                    }
                }

                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Failed to read crash event log: {Message}", ex.Message);
        }
    }

    private static async Task<System.Diagnostics.Eventing.Reader.EventRecord?> PollEventLogAsync(string xpath)
    {
        var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
            "Application",
            System.Diagnostics.Eventing.Reader.PathType.LogName,
            xpath);

        var timeout = TimeSpan.FromSeconds(3);
        var startTime = DateTime.UtcNow;

        while ((DateTime.UtcNow - startTime) < timeout)
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            var record = reader.ReadEvent();
            if (record != null)
            {
                return record;
            }

            await Task.Delay(500);
        }

        return null;
    }

    /// <summary>
    /// Prints .NET Runtime Event ID 1026 — contains full exception type, message, and stack trace.
    /// </summary>
    private void PrintDotNetRuntimeEvent(System.Diagnostics.Eventing.Reader.EventRecord record)
    {
        var description = record.FormatDescription();
        if (string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        // Extract exception info from the event description.
        // Format: "Application: <name>\n...\nException Info: <type>: <message>\n   at ..."
        var exceptionStart = description.IndexOf("Exception Info:", StringComparison.OrdinalIgnoreCase);
        if (exceptionStart < 0)
        {
            exceptionStart = description.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase);
        }

        var exceptionText = exceptionStart >= 0
            ? description[exceptionStart..].Trim()
            : description.Trim();

        console.WriteLine();
        console.MarkupLine("[red bold].NET Exception Details:[/]");
        foreach (var line in exceptionText.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                console.MarkupLine($"[red]  {trimmed.EscapeMarkup()}[/]");
            }
        }
    }

    /// <summary>
    /// Prints Application Error Event ID 1000 — contains faulting module, exception code, offset.
    /// The event properties are: [0]=app name, [1]=app version, [2]=timestamp,
    /// [3]=module name, [4]=module version, [5]=module timestamp, [6]=exception code,
    /// [7]=fault offset, [8]=PID hex, ...
    /// </summary>
    private void PrintApplicationErrorEvent(System.Diagnostics.Eventing.Reader.EventRecord record)
    {
        var props = record.Properties;
        if (props.Count < 8)
        {
            return;
        }

        var moduleName = props[3]?.Value?.ToString();
        var exceptionCodeStr = props[6]?.Value?.ToString();
        var faultOffset = props[7]?.Value?.ToString();

        if (moduleName == null)
        {
            return;
        }

        console.WriteLine();
        console.MarkupLine("[red bold]Crash Details (Windows Error Reporting):[/]");
        console.MarkupLine($"[red]  Faulting module: {moduleName.EscapeMarkup()}[/]");

        if (exceptionCodeStr != null)
        {
            console.MarkupLine($"[red]  Exception code:  0x{exceptionCodeStr.EscapeMarkup()}[/]");
        }

        if (faultOffset != null)
        {
            console.MarkupLine($"[red]  Fault offset:    0x{faultOffset.EscapeMarkup()}[/]");
        }
    }
}
