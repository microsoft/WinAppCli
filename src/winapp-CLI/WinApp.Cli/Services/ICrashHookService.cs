// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Captures managed exception details via a startup hook DLL injected into the target
/// process using <c>DOTNET_STARTUP_HOOKS</c>. The hook subscribes to
/// <c>AppDomain.FirstChanceException</c> inside the target process and writes exception
/// info (type, message, stack trace) to a named pipe that this service reads.
/// </summary>
internal interface ICrashHookService
{
    /// <summary>
    /// Sets up the named pipe server, extracts the crash hook DLL to the AppX directory,
    /// copies PDB files for source line resolution, and injects the hook into runtimeconfig.json.
    /// </summary>
    /// <param name="appxDirectory">The AppX loose layout directory to copy the hook DLL into.</param>
    /// <returns>
    /// An environment string for <c>--with-alias</c> (ProcessStartInfo.Environment),
    /// e.g. <c>"DOTNET_STARTUP_HOOKS=...\0WINAPP_CRASH_PIPE=...\0\0"</c>.
    /// For AUMID launch, the hook is injected via runtimeconfig.json instead.
    /// Returns null if setup fails.
    /// </returns>
    string? Setup(string appxDirectory);

    /// <summary>
    /// Starts reading exception data from the named pipe on a background thread.
    /// Call after the target process has launched and connected to the pipe.
    /// </summary>
    void StartReading();

    /// <summary>
    /// Waits for the pipe reader to finish (target process exited and pipe closed).
    /// </summary>
    Task WaitForCompletionAsync(TimeSpan timeout);

    /// <summary>
    /// Returns the most recently captured managed exceptions (most recent first).
    /// </summary>
    IReadOnlyList<CrashHookException> CapturedExceptions { get; }
}

/// <summary>
/// A managed exception captured from the startup hook via named pipe.
/// </summary>
internal sealed record CrashHookException(
    string ExceptionType,
    string ExceptionMessage,
    string HResult,
    string StackTrace);
