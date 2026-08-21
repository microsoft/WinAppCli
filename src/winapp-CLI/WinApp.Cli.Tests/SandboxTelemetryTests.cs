// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Tests;

/// <summary>
/// What telemetry may record about the generic Sandbox escape hatches
/// (spec §"Telemetry").
/// </summary>
/// <remarks>
/// `sandbox exec` and `sandbox cp` carry whatever a caller needs to run or move: credentials in an
/// argument, a customer name in a path, a token on a command line. The specification therefore
/// excludes executable and argument values, environment variables, host and guest paths, streams,
/// and file names outright.
/// <para>
/// The existing redaction already satisfies that — string, file, and directory values are recorded
/// as the constant <c>[string]</c> rather than their content. These tests exist so that stays true:
/// a future change to the redaction rule that started recording values would be a privacy
/// regression that nothing else in the suite would notice.
/// </para>
/// </remarks>
[TestClass]
public class SandboxTelemetryTests : BaseCommandTests
{
    /// <summary>Values that must never reach telemetry, whatever shape the command takes.</summary>
    private static readonly string[] MustNotAppear =
    [
        "dotnet",
        "--token",
        "s3cr3t-value",
        @"C:\Customers\Contoso",
        "secret-file.txt",
        @"C:\Guest\Results",
    ];

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    [TestMethod]
    public void SandboxExec_RecordsNeitherTheCommandNorItsArgumentsNorPaths()
    {
        var context = CreateContextFor(
            "sandbox", "exec", "--cwd", @"C:\Customers\Contoso",
            "--", "dotnet", "run", "--token", "s3cr3t-value");

        AssertNothingSensitive(context);
    }

    [TestMethod]
    public void SandboxCopy_RecordsNeitherEndpointNorFileName()
    {
        var context = CreateContextFor(
            "sandbox", "cp", @"C:\Customers\Contoso\secret-file.txt", @"sandbox:C:\Guest\Results");

        AssertNothingSensitive(context);
    }

    /// <summary>
    /// The same guarantee for the commands that carry application arguments and paths.
    /// </summary>
    [TestMethod]
    public void Run_RecordsNeitherApplicationArgumentsNorPaths()
    {
        var context = CreateContextFor(
            "run", @"C:\Customers\Contoso", "--sandbox", "--args", "--token s3cr3t-value");

        AssertNothingSensitive(context);
    }

    private static void AssertNothingSensitive(string context)
    {
        foreach (var value in MustNotAppear)
        {
            Assert.DoesNotContain(
                value,
                context,
                $"'{value}' must never reach telemetry from a Sandbox command; recorded context was: {context}");
        }
    }

    private string CreateContextFor(params string[] arguments)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var parseResult = root.Parse(arguments, WinAppParserConfiguration.Default);

        return CommandInvokedEvent.CreateContext(parseResult.CommandResult.Children);
    }
}
