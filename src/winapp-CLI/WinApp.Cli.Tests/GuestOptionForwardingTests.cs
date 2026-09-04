// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Every run option a user gives must reach the guest, and none must reach it in a form the guest
/// only warns about.
/// </summary>
/// <remarks>
/// Both planners are pure translation, so the only thing that can be wrong is which flags are
/// forwarded — which is exactly what these pin, with no Sandbox involved. <c>--symbols</c> is here
/// because it was silently dropped: <c>winapp run . --on sandbox --debug-output --symbols</c>
/// downloaded no symbols in the guest and produced the unsymbolicated stack the same command
/// resolves locally, with nothing on screen to say why.
/// </remarks>
[TestClass]
public class GuestOptionForwardingTests
{
    private const string PayloadPath = @"C:\WinApp\deployments\abc";
    private const string LayoutPath = @"C:\WinApp\deployments\abc-layout";

    private static readonly string[] FullLaunchArguments =
    [
        GuestLaunchPlanner.Verb,
        "--package-name", "Contoso.App",
        "--publisher", "CN=Contoso",
        "--application-id", "App",
        "--expected-layout", LayoutPath,
        "--payload", PayloadPath,
        "--with-alias", "--debug-output", "--symbols", "--detach", "--json",
        "--args", "--flag value",
    ];

    private static readonly string[] MinimalLaunchArguments =
    [
        GuestLaunchPlanner.Verb,
        "--package-name", "Contoso.App",
        "--publisher", "CN=Contoso",
        "--application-id", "App",
        "--expected-layout", LayoutPath,
        "--payload", PayloadPath,
    ];

    private static List<string> Launch(GuestRunOptions options) =>
        GuestLaunchPlanner.BuildLaunchArguments("Contoso.App", "CN=Contoso", "App", LayoutPath, PayloadPath, options);

    private static List<string> Run(GuestRunOptions options) =>
        GuestRunPlanner.BuildRunArguments(PayloadPath, LayoutPath, options);

    /// <summary>The launch verb forwards every option it has a code path for.</summary>
    /// <remarks>
    /// This is the verb a launching <c>--on &lt;target&gt;</c> run actually uses, so an option
    /// missing here is an option that does nothing for the run the user asked for.
    /// </remarks>
    [TestMethod]
    public void BuildLaunchArguments_ForwardsTheWholeOptionMatrix()
    {
        var arguments = Launch(new GuestRunOptions(
            WithAlias: true,
            DebugOutput: true,
            Detach: true,
            Json: true,
            Symbols: true,
            AppArguments: "--flag value"));

        CollectionAssert.AreEqual(FullLaunchArguments, arguments);
    }

    /// <summary>With no options, only the identity the verb verifies is sent.</summary>
    [TestMethod]
    public void BuildLaunchArguments_WithNoOptions_SendsOnlyTheIdentityToVerify()
    {
        CollectionAssert.AreEqual(MinimalLaunchArguments, Launch(new GuestRunOptions()));
    }

    /// <summary>
    /// The options this verb has no code path for are never forwarded, whatever the caller passes.
    /// </summary>
    /// <remarks>
    /// <c>guest-launch</c> does not accept <c>--no-launch</c>, <c>--clean</c>, or
    /// <c>--unregister-on-exit</c> at all, so forwarding one would turn a legitimate run into a
    /// guest-side parse failure.
    /// </remarks>
    [TestMethod]
    public void BuildLaunchArguments_NeverForwardsRegistrationOptions()
    {
        var arguments = Launch(new GuestRunOptions(NoLaunch: true, Clean: true, UnregisterOnExit: true));

        CollectionAssert.AreEqual(MinimalLaunchArguments, arguments);
    }

    /// <summary>
    /// <c>--symbols</c> reaches the guest when there is a debug loop to consume it.
    /// </summary>
    [TestMethod]
    public void Symbols_WithDebugOutput_IsForwardedToBothVerbs()
    {
        var options = new GuestRunOptions(DebugOutput: true, Symbols: true);

        CollectionAssert.Contains(
            Launch(options),
            "--symbols",
            "A launching debug run is the case --symbols exists for; without this the guest resolves no symbols.");

        CollectionAssert.Contains(Run(options), "--symbols");
    }

    /// <summary>
    /// <c>--symbols</c> alone is not forwarded, because the guest could only warn and ignore it.
    /// </summary>
    /// <remarks>
    /// The host already warns that the flag has no effect. Forwarding it as well would make the
    /// guest print the same warning into the run's own output, so one mistake would be reported
    /// twice — once about the command the user typed, once about a command they never saw.
    /// </remarks>
    [TestMethod]
    public void Symbols_WithoutDebugOutput_IsNotForwarded()
    {
        var options = new GuestRunOptions(Symbols: true);

        CollectionAssert.DoesNotContain(Launch(options), "--symbols");
        CollectionAssert.DoesNotContain(Run(options), "--symbols");
    }

    /// <summary>The debug loop still runs without symbols when only it was asked for.</summary>
    [TestMethod]
    public void DebugOutput_WithoutSymbols_ForwardsOnlyDebugOutput()
    {
        var arguments = Launch(new GuestRunOptions(DebugOutput: true));

        CollectionAssert.Contains(arguments, "--debug-output");
        CollectionAssert.DoesNotContain(arguments, "--symbols");
    }

    /// <summary>
    /// The registration-only guest call carries no debug options at all.
    /// </summary>
    /// <remarks>
    /// That call exists to register the package under the mutation lease and nothing else. Sending
    /// it <c>--debug-output</c> or <c>--symbols</c> would attach a debug loop to a call that never
    /// launches anything.
    /// </remarks>
    [TestMethod]
    public void BuildRunArguments_ForRegistrationOnly_CarriesNoDebugOptions()
    {
        var arguments = Run(new GuestRunOptions(NoLaunch: true, Clean: true, Json: true));

        CollectionAssert.DoesNotContain(arguments, "--debug-output");
        CollectionAssert.DoesNotContain(arguments, "--symbols");
        CollectionAssert.Contains(arguments, "--no-launch");
    }

    /// <summary>
    /// Every option the planners emit is one the guest verb actually accepts.
    /// </summary>
    /// <remarks>
    /// A forwarded flag the guest does not define fails guest-side argument parsing, and what the
    /// user sees is the guest's usage help instead of their application. Checked against the verb's
    /// real option set rather than a list copied by hand, so adding an option to one side without
    /// the other is caught here.
    /// </remarks>
    [TestMethod]
    public void BuildLaunchArguments_EmitsOnlyOptionsTheGuestVerbDefines()
    {
        var accepted = new GuestLaunchCommand().Options
            .SelectMany(option => new[] { option.Name }.Concat(option.Aliases))
            .ToHashSet(StringComparer.Ordinal);

        var emitted = Launch(new GuestRunOptions(
            NoLaunch: true,
            WithAlias: true,
            DebugOutput: true,
            UnregisterOnExit: true,
            Detach: true,
            Clean: true,
            Json: true,
            Symbols: true,
            AppArguments: "value"))
            .Where(argument => argument.StartsWith("--", StringComparison.Ordinal));

        foreach (var option in emitted)
        {
            Assert.IsTrue(
                accepted.Contains(option),
                $"'{option}' is forwarded to '{GuestLaunchPlanner.Verb}', which does not accept it.");
        }
    }
}
