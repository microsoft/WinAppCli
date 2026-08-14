// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Test double for <see cref="IAnalyzerInjectionService"/>. By default returns <see langword="null"/>
/// (no injection), so ProjectRunService tests that don't exercise analyzer injection are unaffected.
/// A canned <see cref="AnalyzerInjection"/> can be supplied to drive the injection path.
/// </summary>
internal sealed class FakeAnalyzerInjectionService(AnalyzerInjection? injection = null) : IAnalyzerInjectionService
{
    /// <summary>Shared no-op instance for tests that never trigger injection.</summary>
    public static FakeAnalyzerInjectionService None { get; } = new();

    public AnalyzerInjection? PrepareInjection() => injection;
}
