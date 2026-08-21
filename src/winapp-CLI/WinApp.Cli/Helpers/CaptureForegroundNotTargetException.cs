// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Thrown when a screen-DC capture is about to run but the target did not actually reach the
/// foreground.
/// </summary>
/// <remarks>
/// <c>--capture-screen</c> BitBlts the live screen rather than a specific window, so it records
/// whatever is in front. <c>SetForegroundWindow</c> is only a request — Windows refuses it under
/// focus-stealing prevention, a UAC prompt, a locked session, or when another app activates itself in
/// the same instant. Without this check the command exits 0 and hands back a PNG/MP4 of an unrelated
/// window, which is worse than failing: the caller has no way to tell.
/// <para>
/// Commands map this to the existing <c>foreground_not_target</c> contract — the same code the
/// pre-injection foreground guard emits — rather than reporting <c>internal_error</c>.
/// </para>
/// </remarks>
internal sealed class CaptureForegroundNotTargetException(string message) : InvalidOperationException(message);
