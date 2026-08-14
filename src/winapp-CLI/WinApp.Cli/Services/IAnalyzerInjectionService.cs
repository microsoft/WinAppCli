// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Materialized WinUI analyzer injection assets: the extracted analyzer DLL and the
/// MSBuild hook props that adds it as a restore-invisible <c>&lt;Analyzer&gt;</c> item.
/// </summary>
internal sealed record AnalyzerInjection(string HookPropsPath, string AnalyzerDllPath, string ContentHash);

/// <summary>
/// Prepares the WinUI Roslyn analyzer for injection into a project-mode
/// <c>winapp run</c> build (issue #634).
///
/// The analyzer DLL is embedded in the CLI (see WinApp.Cli.csproj). This service
/// extracts it — and a small MSBuild hook props that references it — into a
/// per-user cache keyed by the DLL's SHA-256 content hash, so the same bits are
/// reused across runs and refreshed automatically when the embedded analyzer
/// changes. The caller threads the returned <see cref="AnalyzerInjection.HookPropsPath"/>
/// through <c>-p:CustomAfterMicrosoftCommonTargets=</c> on the build pass only.
/// </summary>
internal interface IAnalyzerInjectionService
{
    /// <summary>
    /// Extracts the embedded analyzer + hook props into the content-hash cache
    /// (idempotent) and returns their paths. Returns <see langword="null"/> when
    /// the analyzer resource is not present in this build of the CLI.
    /// </summary>
    AnalyzerInjection? PrepareInjection();
}
