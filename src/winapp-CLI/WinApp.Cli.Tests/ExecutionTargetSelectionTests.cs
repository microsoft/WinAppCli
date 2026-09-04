// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Keeps the set of commands that honour <c>--on</c> honest, in both languages that describe it.
/// </summary>
[TestClass]
public class ExecutionTargetSelectionTests : BaseCommandTests
{
    /// <summary>The trees the design says accept a target in Stage 1.</summary>
    private static readonly string[] Expected = ["run", "ui", "unregister"];

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    /// <summary>
    /// Adding <c>ITargetAwareCommand</c> to a command is a public promise that it can run somewhere
    /// else. Making that deliberate is the point: a command that claims it without a routing path
    /// would accept <c>--on</c> and then run here anyway.
    /// </summary>
    [TestMethod]
    public void OnlyTheDesignedCommandsAreTargetAware()
    {
        var root = GetRequiredService<WinAppRootCommand>();

        var actual = root.Subcommands
            .OfType<ITargetAwareCommand>()
            .Cast<Command>()
            .Select(command => command.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actual);
    }

}
