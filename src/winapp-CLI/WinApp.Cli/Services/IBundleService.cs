// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal interface IBundleService
{
    /// <summary>
    /// Creates an MSIX bundle from a list of intermediate .msix files using makeappx bundle.
    /// </summary>
    /// <param name="msixFiles">The intermediate .msix files to include in the bundle.</param>
    /// <param name="output">The output .msixbundle file path.</param>
    /// <param name="taskContext">Task context for logging.</param>
    /// <param name="bundleVersion">
    /// Optional version to stamp onto <c>Bundle.Identity/@Version</c> in the generated
    /// <c>AppxBundleManifest.xml</c>, passed to <c>makeappx bundle</c> via the <c>/bv</c> switch.
    /// When null, <c>makeappx</c> falls back to its own default (timestamp-derived) bundle version.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateBundleAsync(IReadOnlyList<FileInfo> msixFiles, FileInfo output, TaskContext taskContext, MsixVersion? bundleVersion = null, CancellationToken cancellationToken = default);
}
