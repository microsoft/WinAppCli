// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Shared harness for tests that invoke <see cref="WinApp.Cli.Program.Main"/> directly. Centralizes
/// the CI environment-variable list and the stdout/stderr capture wrapper so the Program.Main
/// integration tests (e.g. <c>ProgramMainTests</c> and <c>UpdateNotificationGatingTests</c>) do not
/// each carry their own identical copy.
/// </summary>
internal static class ProgramMainTestHarness
{
    /// <summary>
    /// All environment-variable names inspected by the telemetry CI detector. Tests clear these so a
    /// developer or CI host's ambient CI markers do not change <see cref="WinApp.Cli.Program.Main"/>'s
    /// behavior under test.
    /// </summary>
    internal static readonly string[] CiVarNames =
    [
        "CI", "GITHUB_ACTIONS", "TF_BUILD", "APPVEYOR", "TRAVIS", "CIRCLECI",
        "TEAMCITY_VERSION", "JB_SPACE_API_URL",
        "CODEBUILD_BUILD_ID", "AWS_REGION", "BUILD_ID", "BUILD_URL", "PROJECT_ID"
    ];

    /// <summary>
    /// Invokes <see cref="WinApp.Cli.Program.Main"/> with <paramref name="args"/> while capturing
    /// stdout/stderr, restoring the original console streams afterward. The capture writers are
    /// intentionally not disposed: Spectre.Console's static <c>AnsiConsole</c> may reference them
    /// after Main returns, so disposing here would risk an <see cref="ObjectDisposedException"/>.
    /// </summary>
    internal static async Task<(string Stdout, string Stderr, int ExitCode)> InvokeProgramAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        try
        {
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var exitCode = await WinApp.Cli.Program.Main(args);

            return (stdoutWriter.ToString(), stderrWriter.ToString(), exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
