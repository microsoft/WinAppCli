// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Windows UI Automation engine with a dependency-injection container.
/// </summary>
public static class UiAutomationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the UI Automation services: element inspection and interaction
    /// (<see cref="IUiAutomation"/>), target/window resolution (<see cref="IUiTargetResolver"/>),
    /// selector parsing (<see cref="IUiSelectorParser"/>), window capture
    /// (<see cref="IWindowCapture"/>), and input injection (<see cref="IKeyboardInput"/>,
    /// <see cref="IMouseInput"/>, <see cref="IPointerInput"/>).
    /// </summary>
    public static IServiceCollection AddWinAppUiAutomation(this IServiceCollection services)
    {
        return services
            .AddSingleton<IMouseInput, RealMouseInput>()
            .AddSingleton<IPointerInput, RealPointerInput>()
            .AddSingleton<IKeyboardInput, RealKeyboardInput>()
            .AddSingleton<IForegroundGuard, RealForegroundGuard>()
            .AddSingleton<IOwnedWindowFinder, RealOwnedWindowFinder>()
            .AddSingleton<IPollDelay, RealPollDelay>()
            .AddSingleton<IUiSelectorParser, UiSelectorParser>()
            .AddSingleton<ISystemUiQuery, SystemUiQuery>()
            .AddSingleton<IUiTargetResolver, UiTargetResolver>()
            .AddSingleton<IWindowCapture, WgcWindowCapture>()
            .AddSingleton<IUiAutomation, UiAutomationService>();
    }
}
