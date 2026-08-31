// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>
/// Records a window or element region to H.264 MP4. Sits on top of the UI Automation library's
/// capture primitives; the encoding and frame-artifact output are CLI concerns and stay here.
/// </summary>
public interface IUiRecordingService
{
    /// <summary>Records a window or element region to H.264 MP4.</summary>
    /// <param name="onRecordingStarted">Invoked after the first frame; reports whether frame output is active.</param>
    Task<RecordCaptureResult> RecordAsync(
        UiTarget session,
        string? elementId,
        RecordOptions options,
        CancellationToken ct,
        Action<bool>? onRecordingStarted = null);
}
