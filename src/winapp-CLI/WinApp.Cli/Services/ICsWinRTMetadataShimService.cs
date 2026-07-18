// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// SHIM (temporary): auto-resolves a <c>CsWinRTWindowsMetadata</c> folder for project-mode builds on
/// hosts with no registered Windows SDK, so C#/WinRT authoring projects (anything importing
/// <c>Microsoft.Windows.CsWinRT</c>) can build off the <c>Microsoft.Windows.SDK.NET.Ref</c> NuGet
/// ref-pack winmds instead of a bare SDK version that <c>cswinrt.exe</c> would resolve via a failing
/// registry lookup. Remove once the upstream <c>Microsoft.Windows.CsWinRT.targets</c> default is fixed.
/// See <see cref="CsWinRTMetadataShimService"/>.
/// </summary>
internal interface ICsWinRTMetadataShimService
{
    /// <summary>
    /// Returns the absolute path of a folder of contract winmds to inject as
    /// <c>-p:CsWinRTWindowsMetadata=&lt;folder&gt;</c>, or <c>null</c> when no injection is needed
    /// (a Windows SDK is registered) or possible (the ref pack is not restored). Never throws for a
    /// missing ref pack — it no-ops so the normal build error can surface.
    /// </summary>
    /// <param name="targetFrameworkMoniker">
    /// The project's target framework moniker (e.g. <c>net10.0-windows10.0.19041.0</c>) used to prefer a
    /// ref-pack version matching the targeted platform; may be <c>null</c>, in which case the highest
    /// available ref-pack version is used.
    /// </param>
    string? ResolveMetadataFolder(string? targetFrameworkMoniker);

    /// <summary>
    /// True when no Windows SDK is registered on the host — i.e. the situation in which the shim WOULD
    /// inject the ref-pack winmd folder if it were restored. Callers use this to decide whether an
    /// explicit <c>dotnet restore</c> (to populate <c>Microsoft.Windows.SDK.NET.Ref</c>) is worth doing
    /// before resolving the folder, so the FIRST build on a clean host isn't handed a missing folder.
    /// </summary>
    bool IsWindowsSdkAbsent();
}
