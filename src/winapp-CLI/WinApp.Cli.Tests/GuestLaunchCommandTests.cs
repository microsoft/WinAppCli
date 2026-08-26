// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Proves the SBX-009 follow-up finding is closed: the hidden guest-launch verb -- the unlocked
/// half of a launching packaged <c>run --sandbox</c>, after registration itself completed under
/// the mutation lease -- is structurally incapable of registering, unregistering, or otherwise
/// mutating package state, under any option combination, including when a mismatch is exactly the
/// scenario the mutation-lock split was meant to protect against (another deployment sharing the
/// same package identity registering during the gap between phase 1 and phase 2).
/// </summary>
/// <remarks>
/// Driven directly through <see cref="RunCommand.Handler.InvokeAsync"/> for a parsed
/// <see cref="GuestLaunchCommand"/> -- the same dispatch a guest exec request reaches -- with
/// <see cref="FakePackageRegistrationService"/> standing in for the guest's package state. Every
/// test asserts all five of that fake's mutation call lists
/// (<c>InstallPackageCalls</c>/<c>UnregisterCalls</c>/<c>UnregisterByFullNameCalls</c>/
/// <c>RegisterLooseLayoutCalls</c>/<c>RegisterSparseCalls</c>) stay empty, because the point being
/// proven is not "this particular flag combination happens not to mutate" but "there is no code
/// path in this verb that can reach any of them at all".
/// </remarks>
[TestClass]
public class GuestLaunchCommandTests : BaseCommandTests
{
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakePackageRegistrationService = new FakePackageRegistrationService();
        return services
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService)
            .AddSingleton<IMsixService>(new FakeMsixService())
            .AddSingleton<IDebugOutputService>(new FakeDebugOutputService())
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private static ParseResultFor Parse(
        string packageName,
        string publisher,
        string applicationId,
        string expectedLayout,
        string payload,
        bool withAlias = false,
        bool debugOutput = false,
        bool unregisterOnExit = false,
        bool detach = true,
        bool json = false,
        string? appArgs = null)
    {
        var command = new GuestLaunchCommand();

        List<string> arguments =
        [
            "--package-name", packageName,
            "--publisher", publisher,
            "--application-id", applicationId,
            "--expected-layout", expectedLayout,
            "--payload", payload,
        ];

        if (withAlias)
        {
            arguments.Add("--with-alias");
        }

        if (debugOutput)
        {
            arguments.Add("--debug-output");
        }

        if (unregisterOnExit)
        {
            arguments.Add("--unregister-on-exit");
        }

        if (detach)
        {
            arguments.Add("--detach");
        }

        if (json)
        {
            arguments.Add("--json");
        }

        if (appArgs is not null)
        {
            arguments.Add("--args");
            arguments.Add(appArgs);
        }

        return new ParseResultFor(command.Parse([.. arguments]));
    }

    /// <summary>Thin wrapper so call sites read as intent rather than a bare tuple/ParseResult.</summary>
    private sealed record ParseResultFor(System.CommandLine.ParseResult Value);

    private void AssertNoMutationCalls()
    {
        Assert.AreEqual(0, _fakePackageRegistrationService.InstallPackageCalls.Count, "must never install a package");
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count, "must never unregister by name");
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterByFullNameCalls.Count, "must never unregister by full name");
        Assert.AreEqual(0, _fakePackageRegistrationService.RegisterLooseLayoutCalls.Count, "must never register a loose layout");
        Assert.AreEqual(0, _fakePackageRegistrationService.RegisterSparseCalls.Count, "must never register a sparse package");
    }

    [TestMethod]
    public async Task ExactLayoutMatch_Launches_WithNoMutationCallsWhatsoever()
    {
        var layout = Path.Combine(_tempDirectory.FullName, "layout");
        var payload = Path.Combine(_tempDirectory.FullName, "payload");

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("Pkg_1.0.0.0_x64__abc123", "Pkg", "1.0.0.0", layout, IsDevelopmentMode: true),
        ];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse("Pkg", "CN=Test", "App", layout, payload);

        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreEqual(0, exitCode, "An exact-match registration must launch successfully.");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Must launch via AUMID.");
        StringAssert.EndsWith(_fakeAppLauncherService.LaunchCalls[0].Aumid, "!App");
        AssertNoMutationCalls();
    }

    [TestMethod]
    public async Task MismatchedLayout_RefusesToLaunch_AndNeverAttemptsToRepairTheRegistration()
    {
        // This is the exact SBX-009 follow-up scenario: the caller's own phase 1 registered from
        // "layoutA", but by the time phase 2 (this verb) runs, something else -- another
        // deployment sharing this package identity -- has re-registered the same identity from a
        // different location. The general `run` command would see this mismatch and silently
        // fall through to unregister+register; this verb must refuse outright instead.
        var layoutA = Path.Combine(_tempDirectory.FullName, "layoutA");
        var layoutB = Path.Combine(_tempDirectory.FullName, "layoutB");
        var payload = Path.Combine(_tempDirectory.FullName, "payload");

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("Pkg_2.0.0.0_x64__def456", "Pkg", "2.0.0.0", layoutB, IsDevelopmentMode: true),
        ];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse("Pkg", "CN=Test", "App", layoutA, payload);

        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreNotEqual(0, exitCode, "A mismatched install location must refuse to launch.");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Must never attempt to launch on a mismatch.");
        AssertNoMutationCalls();

        // The other deployment's registration (layoutB) is completely undisturbed: still exactly
        // the one entry this test set up, unchanged.
        Assert.AreEqual(1, _fakePackageRegistrationService.FakeDevPackages.Count);
        Assert.AreEqual(layoutB, _fakePackageRegistrationService.FakeDevPackages[0].InstallLocation);
    }

    [TestMethod]
    public async Task NoRegisteredPackage_RefusesToLaunch_WithNoMutationCalls()
    {
        _fakePackageRegistrationService.FakeDevPackages = [];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse(
            "Pkg", "CN=Test", "App",
            Path.Combine(_tempDirectory.FullName, "layout"),
            Path.Combine(_tempDirectory.FullName, "payload"));

        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
        AssertNoMutationCalls();
    }

    [TestMethod]
    public async Task MultipleRegisteredPackages_RefusesToLaunch_WithNoMutationCalls()
    {
        var layout = Path.Combine(_tempDirectory.FullName, "layout");

        // Ambiguous: two dev-mode registrations under the same identity name. The verb must not
        // guess which one is "right" by mutating toward it -- it refuses.
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("Pkg_1.0.0.0_x64__aaa", "Pkg", "1.0.0.0", layout, IsDevelopmentMode: true),
            new DevPackageInfo("Pkg_1.0.0.0_arm64__bbb", "Pkg", "1.0.0.0", layout, IsDevelopmentMode: true),
        ];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse(
            "Pkg", "CN=Test", "App", layout, Path.Combine(_tempDirectory.FullName, "payload"));

        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
        AssertNoMutationCalls();
    }

    [TestMethod]
    public async Task NonDevelopmentModeRegistration_IsNotTreatedAsAMatch_RefusesToLaunch()
    {
        // A non-dev-mode (e.g. Store-installed) package under the same identity name must not be
        // treated as satisfying the expectation, even if its InstallLocation happened to match --
        // this verb only ever launches what this run's own dev-mode registration produced.
        var layout = Path.Combine(_tempDirectory.FullName, "layout");

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("Pkg_1.0.0.0_x64__ccc", "Pkg", "1.0.0.0", layout, IsDevelopmentMode: false),
        ];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse(
            "Pkg", "CN=Test", "App", layout, Path.Combine(_tempDirectory.FullName, "payload"));

        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
        AssertNoMutationCalls();
    }

    [TestMethod]
    public async Task WithAlias_ExactMatch_NeverCallsLaunchByAumid_AndStillNoMutationCalls()
    {
        var layout = Path.Combine(_tempDirectory.FullName, "layout");
        var payload = _tempDirectory.CreateSubdirectory("aliasPayload").FullName;

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("Pkg_1.0.0.0_x64__ddd", "Pkg", "1.0.0.0", layout, IsDevelopmentMode: true),
        ];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse("Pkg", "CN=Test", "App", layout, payload, withAlias: true, detach: false);

        // No processed manifest / execution alias exists in this test layout, so the alias launch
        // itself fails (exit 1) -- but the point under test is upstream of that: verification must
        // run, must pass, and must still never touch registration.
        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "--with-alias must never launch by AUMID.");
        AssertNoMutationCalls();
    }

    [TestMethod]
    public async Task MismatchedLayout_WithUnregisterOnExit_StillNeverCallsUnregister()
    {
        // --unregister-on-exit only ever fires after a successful launch's process exits. A
        // mismatch must refuse before that point is ever reached, regardless of this flag.
        var layoutA = Path.Combine(_tempDirectory.FullName, "layoutA");
        var layoutB = Path.Combine(_tempDirectory.FullName, "layoutB");

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("Pkg_2.0.0.0_x64__eee", "Pkg", "2.0.0.0", layoutB, IsDevelopmentMode: true),
        ];

        var handler = GetRequiredService<RunCommand.Handler>();
        var parsed = Parse(
            "Pkg", "CN=Test", "App", layoutA,
            Path.Combine(_tempDirectory.FullName, "payload"),
            unregisterOnExit: true);

        var exitCode = await handler.InvokeAsync(parsed.Value, TestContext.CancellationToken);

        Assert.AreNotEqual(0, exitCode);
        AssertNoMutationCalls();
    }
}
