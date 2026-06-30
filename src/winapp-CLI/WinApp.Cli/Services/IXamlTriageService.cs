// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Produces WinUI-specific crash triage for a minidump by hosting DbgEng and running
/// the WinUI team's WinDbg JavaScript extension (<c>!xamlstowed</c> / <c>!xamltriage</c>).
/// <para>
/// Most WinUI crashes originate inside a XAML event handler and surface as a stowed
/// exception (<c>0xC000027B</c>) wrapping a managed exception. The standard ClrMD/DbgEng
/// passes only reliably recover the faulting user frame; this service recovers the
/// originating HRESULT, its ErrorContext chain, and the native dispatch stack
/// (Microsoft.UI.Xaml → CXcpDispatcher → CoreMessagingXP → CLR host).
/// </para>
/// </summary>
internal interface IXamlTriageService
{
    /// <summary>
    /// Runs the WinUI triage pass against a dump and returns formatted output suitable
    /// for appending to the debug log, or <c>null</c> when triage is not applicable.
    /// </summary>
    /// <remarks>
    /// This method never throws for missing tooling or unsupported scenarios — it
    /// degrades gracefully, logging diagnostics and returning either an explanatory
    /// note (so the absence is recorded in the log) or <c>null</c>.
    /// </remarks>
    /// <param name="dumpPath">Path to the minidump file (must contain Microsoft.UI.Xaml).</param>
    /// <param name="useSymbols">
    /// When true, symbol resolution against the Microsoft Symbol Server is enabled so the
    /// full native dispatch chain resolves to function names. First run downloads symbols.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted triage text for the log, or <c>null</c> when nothing was produced.</returns>
    Task<string?> TryAnalyzeAsync(string dumpPath, bool useSymbols, CancellationToken cancellationToken = default);
}
