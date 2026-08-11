// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Telemetry;

/// <summary>
/// Creates instance of Telemetry
/// This would be useful for the future when interfaces have been updated for logger like ITelemetry2, ITelemetry3 and so on
/// </summary>
internal class TelemetryFactory
{
    private static readonly Lock LockObj = new();

    private static Telemetry? telemetryInstance;

    private static ITelemetry? overrideInstance;

    /// <summary>
    /// Test-only hook: substitute the singleton with a fake <see cref="ITelemetry"/> so tests can
    /// capture logged events. Pass <c>null</c> to restore the real instance.
    /// </summary>
    internal static void SetOverrideForTesting(ITelemetry? instance) => overrideInstance = instance;

    private static Telemetry GetTelemetryInstance()
    {
        if (telemetryInstance == null)
        {
            lock (LockObj)
            {
                telemetryInstance ??= new Telemetry();
                telemetryInstance.AddWellKnownSensitiveStrings();
            }
        }

        return telemetryInstance;
    }

    /// <summary>
    /// Gets a singleton instance of Telemetry
    /// This would be useful for the future when interfaces have been updated for logger like ITelemetry2, ITelemetry3 and so on
    /// </summary>
    /// <typeparam name="T">The type of telemetry interface.</typeparam>
    /// <returns>A singleton instance of the specified telemetry interface.</returns>
    public static T Get<T>()
        where T : ITelemetry
    {
        if (overrideInstance is not null)
        {
            return (T)overrideInstance;
        }

        return (T)(object)GetTelemetryInstance();
    }
}
