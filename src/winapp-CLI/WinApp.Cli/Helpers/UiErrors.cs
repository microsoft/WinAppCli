// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Consistent error messages across all winapp ui commands.
/// </summary>
internal static class UiErrors
{
    public static void MissingApp(ILogger logger)
    {
        logger.LogError("{Symbol} Target app required. Use --app <name|title|PID> or --window <HWND>. Run 'winapp ui list-windows' to find running apps.", UiSymbols.Error);
    }

    public static void MissingSelector(ILogger logger, string commandName)
    {
        logger.LogError("{Symbol} A selector is required. Usage: winapp ui {Command} <selector> -a <app>. Use 'winapp ui search <text> -a <app>' to find elements.", UiSymbols.Error, commandName);
    }

    public static void ElementNotFound(ILogger logger, string selector)
    {
        logger.LogError("{Symbol} No element found matching '{Selector}'. The UI may have changed — re-run 'winapp ui inspect' or 'winapp ui search' to find current elements. Prefer targeting by AutomationId (set via AutomationProperties.AutomationId in XAML) — these survive layout changes.", UiSymbols.Error, selector);
    }

    public static void StaleElement(ILogger logger)
    {
        logger.LogError("{Symbol} Element is no longer accessible — the app may have navigated or the element was removed. Re-run 'winapp ui inspect' to refresh the element tree. Prefer targeting by AutomationId — these are stable across layout changes.", UiSymbols.Error);
    }

    public static void GenericError(ILogger logger, Exception ex)
    {
        logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
    }
}
