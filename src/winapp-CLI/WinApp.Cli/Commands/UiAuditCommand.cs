// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Helpers.UiAudit;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiAuditCommand : Command, IShortDescription
{
    public string ShortDescription => "Audit the current app view for accessibility and contrast issues";

    public static Option<string[]> AreaOption { get; }
    public static Option<string> LevelOption { get; }

    // Depth deep enough to walk an entire window's element tree.
    private const int AuditDepth = 40;

    static UiAuditCommand()
    {
        AreaOption = new Option<string[]>("--area")
        {
            Description = "Accessibility area(s) to audit (repeatable). Allowed: " +
                          $"{string.Join(", ", AuditArea.Selectable)}. Default: all.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        LevelOption = new Option<string>("--level")
        {
            Description = "Audit depth: basic (essential rules + WCAG AA contrast thresholds) or " +
                          "thorough (deeper rules + WCAG AAA contrast thresholds). Default: basic.",
            DefaultValueFactory = _ => AuditProfile.Basic,
        };
    }

    public UiAuditCommand()
        : base("audit", "Audit the currently visible view of a running app for accessibility and contrast issues. " +
               "Walks the element tree and evaluates modular audit areas (names, keyboard, " +
               "screen-reader, contrast, roles) at a chosen level (basic/thorough). " +
               "Audits one view at a time — it does not navigate; drive the other ui commands " +
               "(invoke, send-keys) to move through other pages/tabs/states and audit each. " +
               "Exits non-zero when any fail-severity issue is found, so it can gate CI.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(AreaOption);
        Options.Add(LevelOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        UiAuditOrchestrator orchestrator,
        IAnsiConsole ansiConsole,
        ILogger<UiAuditCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var rawAreas = parseResult.GetValue(AreaOption) ?? [];

            // Resolve the selected areas (WHAT to audit).
            var areas = AuditArea.Resolve(rawAreas, out var invalidArea);
            if (areas is null)
            {
                var msg = $"Invalid --area '{invalidArea}'. Allowed values: {string.Join(", ", AuditArea.Selectable)}.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, msg);
                return 1;
            }

            // Resolve the audit level (HOW DEEP).
            var level = AuditProfile.Normalize(parseResult.GetValue(LevelOption));
            if (level is null)
            {
                var msg = $"Invalid --level '{parseResult.GetValue(LevelOption)}'. Allowed values: {string.Join(", ", AuditProfile.All)}.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, msg);
                return 1;
            }

            // The level drives the WCAG contrast thresholds: basic => AA (4.5 / 3.0),
            // thorough => AAA (7.0 / 4.5).
            var thorough = level == AuditProfile.Thorough;
            var normalThreshold = thorough ? 7.0 : 4.5;
            var largeThreshold = thorough ? 4.5 : 3.0;
            var wcagLevel = thorough ? "AAA" : "AA";

            // Contrast is measured only when a selected area requires a pixel capture.
            var needsContrast = orchestrator.AnyRequiresContrastCapture(areas);

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var elements = await uiAutomation.InspectAsync(session, selector, AuditDepth, cancellationToken);

                // Build the contrast provider (best-effort): capture the window once, then sample
                // each text element's bounding rectangle. If capture fails, contrast is skipped.
                Func<UiElement, double?>? contrastProvider = null;
                var contrastMeasured = false;
                if (needsContrast)
                {
                    var ratios = await TryComputeContrastAsync(session, elements, cancellationToken);
                    if (ratios is not null)
                    {
                        contrastMeasured = true;
                        contrastProvider = el => ratios.TryGetValue(el, out var r) ? r : null;
                    }
                }

                var context = new UiAuditContext
                {
                    Elements = elements,
                    Profile = level,
                    NormalContrast = normalThreshold,
                    LargeContrast = largeThreshold,
                    WcagLevel = wcagLevel,
                    ContrastProvider = contrastProvider,
                };
                var result = orchestrator.Run(areas, context);

                var exitCode = result.Summary.Fail > 0 ? 1 : 0;

                if (json)
                {
                    var payload = JsonSerializer.Serialize(result, UiJsonContext.Default.UiAuditResult);
                    ansiConsole.Profile.Out.Writer.WriteLine(payload);
                    if (!string.IsNullOrEmpty(output))
                    {
                        await WriteReportFileAsync(output, payload, cancellationToken);
                    }
                }
                else
                {
                    var report = BuildHumanReport(result, session, level, areas, needsContrast, contrastMeasured);
                    ansiConsole.Markup(report.Markup);
                    if (!string.IsNullOrEmpty(output))
                    {
                        await WriteReportFileAsync(output, report.PlainText, cancellationToken);
                        ansiConsole.MarkupLine($"[grey]Report written to {Markup.Escape(Path.GetFullPath(output))}[/]");
                    }
                }

                logger.LogDebug("Audit evaluated {Count} elements: pass={Pass} warn={Warn} fail={Fail}",
                    elements.Length, result.Summary.Pass, result.Summary.Warn, result.Summary.Fail);
                return exitCode;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        /// <summary>
        /// Best-effort contrast measurement: capture the target window once and sample each text
        /// element's bounding rectangle. Returns null when the window could not be captured.
        /// </summary>
        private async Task<Dictionary<UiElement, double?>?> TryComputeContrastAsync(
            UiSessionInfo session, UiElement[] elements, CancellationToken ct)
        {
            try
            {
                var (pixels, width, height, originX, originY) = await uiAutomation.CaptureWindowAsync(session, ct);
                var ratios = new Dictionary<UiElement, double?>(ReferenceEqualityComparer.Instance);
                foreach (var el in elements)
                {
                    if (el.Type == "---" || el.Width <= 0 || el.Height <= 0)
                    {
                        continue;
                    }

                    // Multi-HWND guard: the captured buffer belongs to the session's root window.
                    // Elements that belong to a different HWND (e.g. popups / secondary windows)
                    // must NOT be sampled against it — mark them "not measured" (null) instead of
                    // reading the wrong pixels.
                    if (el.WindowHandle is { } elHwnd && elHwnd != 0 && elHwnd != session.WindowHandle)
                    {
                        ratios[el] = null;
                        continue;
                    }

                    var rect = new ContrastAnalyzer.PixelRect(
                        (int)Math.Round(el.X - originX),
                        (int)Math.Round(el.Y - originY),
                        (int)Math.Round(el.Width),
                        (int)Math.Round(el.Height));

                    // Bounds guard: only sample elements whose rect lies within the captured
                    // buffer. Anything outside the captured origin+size is a different surface —
                    // return null rather than clamping onto unrelated pixels.
                    if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > width || rect.Y + rect.Height > height)
                    {
                        ratios[el] = null;
                        continue;
                    }

                    ratios[el] = ContrastAnalyzer.ComputeContrastRatio(pixels, width, height, rect);
                }
                return ratios;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Contrast capture failed; skipping contrast checks");
                return null;
            }
        }

        private static async Task WriteReportFileAsync(string output, string content, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(output, content, ct);
        }

        private static (string Markup, string PlainText) BuildHumanReport(
            UiAuditResult result, UiSessionInfo session, string level,
            IReadOnlyList<string> scope, bool needsContrast, bool contrastMeasured)
        {
            var markup = new StringBuilder();
            var plain = new StringBuilder();

            void Line(string markupText, string plainText)
            {
                markup.AppendLine(markupText);
                plain.AppendLine(plainText);
            }

            var title = session.WindowTitle ?? session.ProcessName;
            Line($"[bold]Accessibility audit:[/] {Markup.Escape(title)} (PID {session.ProcessId})",
                 $"Accessibility audit: {title} (PID {session.ProcessId})");

            var scopeText = string.Join(", ", scope);
            Line($"[grey]Areas: {scopeText} · Level: {level}[/]",
                 $"Areas: {scopeText} · Level: {level}");

            if (needsContrast && !contrastMeasured)
            {
                Line("[yellow]⚠  Contrast could not be measured (window capture unavailable) — contrast checks were skipped.[/]",
                     "!  Contrast could not be measured (window capture unavailable) — contrast checks were skipped.");
            }

            markup.AppendLine();
            plain.AppendLine();

            if (result.Issues.Length == 0)
            {
                Line("[green]✓ No accessibility issues found.[/]", "No accessibility issues found.");
            }
            else
            {
                foreach (var issue in result.Issues)
                {
                    var isFail = issue.Severity == UiAuditEngine.SeverityFail;
                    var sevMarkup = isFail ? "[red]FAIL[/]" : "[yellow]WARN[/]";
                    var sevPlain = isFail ? "FAIL" : "WARN";
                    var sel = string.IsNullOrEmpty(issue.Selector) ? "" : $" [cyan]{Markup.Escape(issue.Selector)}[/]";
                    var selPlain = string.IsNullOrEmpty(issue.Selector) ? "" : $" {issue.Selector}";
                    Line($"{sevMarkup} [grey]({issue.RuleId})[/]{sel} {Markup.Escape(issue.Message)}",
                         $"{sevPlain} ({issue.RuleId}){selPlain} {issue.Message}");
                }
            }

            markup.AppendLine();
            plain.AppendLine();

            var s = result.Summary;
            Line($"[bold]Summary:[/] [green]{s.Pass} passed[/], [yellow]{s.Warn} warnings[/], [red]{s.Fail} failures[/]",
                 $"Summary: {s.Pass} passed, {s.Warn} warnings, {s.Fail} failures");

            return (markup.ToString(), plain.ToString());
        }
    }
}
