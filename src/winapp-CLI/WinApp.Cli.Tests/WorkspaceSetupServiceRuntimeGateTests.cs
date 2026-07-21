// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the framework-dependent Windows App Runtime presence gate
/// (<see cref="IWindowsAppRuntimeService.IsWindowsAppRuntimeRegistered"/>), added for spec H1. A
/// <see cref="FakePackageRegistrationService"/> models which packages are registered so the gate can
/// be verified deterministically (no WinRT / real machine state). The gate must require BOTH a
/// framework package and its matching-arch DDLM, forward the resolved arch, and exclude the CBS
/// system component.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceRuntimeGateTests : BaseCommandTests
{
    private FakePackageRegistrationService _fakePackageRegistration = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakePackageRegistration = new FakePackageRegistrationService();
        return services.AddSingleton<IPackageRegistrationService>(_fakePackageRegistration);
    }

    private const string FrameworkPrefix = "Microsoft.WindowsAppRuntime.";
    private const string DdlmPrefix = "Microsoft.WinAppRuntime.DDLM.";

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_FrameworkAndDdlmPresent_ReturnsTrue()
    {
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered("x64"));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_FrameworkPresentDdlmMissing_ReturnsFalse()
    {
        // The DDLM is what an unpackaged app's bootstrapper resolves; a Framework without the
        // matching DDLM must NOT be reported as registered (would crash at bootstrap).
        _fakePackageRegistration.IsPackageInstalledPredicate = prefix => prefix == FrameworkPrefix;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64"));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_DdlmPresentFrameworkMissing_ReturnsFalse()
    {
        _fakePackageRegistration.IsPackageInstalledPredicate = prefix => prefix == DdlmPrefix;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64"));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_ForwardsArchAndExcludesCbs()
    {
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        service.IsWindowsAppRuntimeRegistered("arm64");

        var frameworkCall = _fakePackageRegistration.IsPackageInstalledCalls
            .Single(c => c.NamePrefix == FrameworkPrefix);
        Assert.AreEqual("arm64", frameworkCall.Architecture, "the resolved arch must be forwarded to the framework check");
        Assert.AreEqual(".CBS.", frameworkCall.ExcludeNameSubstring, "the CBS system component must be excluded from the framework check");

        var ddlmCall = _fakePackageRegistration.IsPackageInstalledCalls
            .Single(c => c.NamePrefix == DdlmPrefix);
        Assert.AreEqual("arm64", ddlmCall.Architecture, "the resolved arch must be forwarded to the DDLM check");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_NullArch_DefaultsToHostArchForBothChecks()
    {
        // Folder-mode / legacy callers pass null; the check must still run against a concrete host arch
        // rather than throwing or matching every arch.
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        service.IsWindowsAppRuntimeRegistered(null);

        var expectedArch = WorkspaceSetupService.GetSystemArchitecture();
        Assert.IsTrue(_fakePackageRegistration.IsPackageInstalledCalls.All(c => c.Architecture == expectedArch),
            "a null arch must resolve to the host architecture for both the framework and DDLM checks");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_ExpectedVersionPresent_ReturnsTrueAndForwardsArch()
    {
        // Spec R2-M1: when the resolved runtime identities are supplied, each must also be registered
        // for the arch at a version >= the required one. Here the exact version is present, so it passes.
        const string expected = "Microsoft.WindowsAppRuntime.1.8";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == expected ? "8000.144.0.0" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered("arm64", new[] { (expected, "8000.144.0.0") }));

        var expectedCall = _fakePackageRegistration.GetInstalledVersionCalls.Single(c => c.PackageName == expected);
        Assert.AreEqual("arm64", expectedCall.Architecture, "the version-specific check must be arch-scoped too");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_DifferentVersionRegistered_ReturnsFalse()
    {
        // Spec R2-M1: the generic Framework + DDLM prefixes are present (a DIFFERENT WinAppSDK version
        // is registered for the arch — common on dev boxes), but the SPECIFIC version the app was built
        // against silently failed to install. The gate must fail instead of false-passing and booting an
        // app that crashes at bootstrap.
        const string required = "Microsoft.WindowsAppRuntime.1.8";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (_, _) => null; // the required identity isn't registered
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64", new[] { (required, "8000.144.0.0") }));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_OlderPatchRegistered_ReturnsFalse()
    {
        // Spec R2-M1 residual: the Framework family name is only major.minor, so a stale OLDER patch of
        // the same minor is registered (name present) while the NEWER patch the app was built against
        // failed to install. A name-presence check would false-pass; the version compare must reject it so
        // the launch aborts with an actionable error instead of crashing at bootstrap on a MinVersion gap.
        const string framework = "Microsoft.WindowsAppRuntime.1.8";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == framework ? "8000.144.1000.0" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        // App requires a newer patch (…2000) than what's registered (…1000).
        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64", new[] { (framework, "8000.144.2000.0") }));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_NewerPatchRegistered_ReturnsTrue()
    {
        // The gate requires installed >= required, so a NEWER patch than the app was built against still
        // satisfies it (WinAppSDK framework packages are backward-compatible within a minor).
        const string framework = "Microsoft.WindowsAppRuntime.1.8";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == framework ? "8000.144.2000.0" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered("x64", new[] { (framework, "8000.144.1000.0") }));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_ExpectedDdlmNotExactlyRegistered_StillReturnsTrue()
    {
        // Spec R4-L1: DDLM package names embed the FULL version (e.g.
        // Microsoft.WinAppRuntime.DDLM.8000.144.1000.0-x64) and install side-by-side, so the gate must NOT
        // demand the app's EXACT DDLM identity — a newer compatible DDLM for the same framework minor
        // satisfies the bootstrapper. Only the generic DDLM presence (checked at the top of the gate) is
        // required for the DDLM; the exact/version check applies to the app-facing Framework family only.
        const string framework = "Microsoft.WindowsAppRuntime.1.8";
        const string expectedDdlm = "Microsoft.WinAppRuntime.DDLM.8000.144.1000.0-x64";
        // Generic Framework + DDLM presence both satisfied (a newer DDLM is registered side-by-side).
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        // The Framework is registered at the required version; the app's EXACT DDLM is NOT (returns null).
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == framework ? "8000.144.2000.0" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered(
            "x64",
            new[] { (framework, "8000.144.2000.0"), (expectedDdlm, "8000.144.1000.0") }),
            "an unregistered exact DDLM must not fail the gate when a DDLM is present and the Framework version matches");

        Assert.IsFalse(_fakePackageRegistration.GetInstalledVersionCalls.Any(c => c.PackageName == expectedDdlm),
            "the DDLM identity must not be exact-version-checked; the generic DDLM presence check covers it");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_OnlyOlderDdlmRegistered_ReturnsFalse()
    {
        // Spec R4-L1 (C29): the generic DDLM presence check accepts ANY DDLM for the arch, so if the app's
        // release-specific DDLM install silently failed while only an OLDER DDLM is registered side-by-side,
        // a presence-only gate would false-pass and the app would still crash resolving its runtime. The
        // release gate must reject when the HIGHEST installed DDLM is older than the required release.
        const string framework = "Microsoft.WindowsAppRuntime.1.8";
        const string expectedDdlm = "Microsoft.WinAppRuntime.DDLM.8000.144.2000.0-x64";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == framework ? "8000.144.2000.0" : null;
        // Highest DDLM registered for the arch is an OLDER release than the app requires.
        _fakePackageRegistration.GetHighestInstalledVersionFunc = (prefix, _) =>
            prefix == DdlmPrefix ? "8000.144.1000.0" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered(
            "x64",
            new[] { (framework, "8000.144.2000.0"), (expectedDdlm, "8000.144.2000.0") }),
            "an older-only registered DDLM must fail the gate when the app needs a newer DDLM release");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_NewerDdlmRegistered_ReturnsTrue()
    {
        // The DDLM release gate is a >= compare against the NEWEST installed DDLM, so a DDLM newer than the
        // app was built against still satisfies it — preserving the side-by-side newer-compatible behavior
        // (a forced exact-identity match would over-strictly false-fail this case).
        const string framework = "Microsoft.WindowsAppRuntime.1.8";
        const string expectedDdlm = "Microsoft.WinAppRuntime.DDLM.8000.144.1000.0-x64";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == framework ? "8000.144.1000.0" : null;
        // A NEWER DDLM than required is registered side-by-side.
        _fakePackageRegistration.GetHighestInstalledVersionFunc = (prefix, _) =>
            prefix == DdlmPrefix ? "8000.144.2000.0" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered(
            "x64",
            new[] { (framework, "8000.144.1000.0"), (expectedDdlm, "8000.144.1000.0") }),
            "a newer registered DDLM must satisfy the release gate");

        var ddlmVersionCall = _fakePackageRegistration.GetHighestInstalledVersionCalls
            .Single(c => c.NamePrefix == DdlmPrefix);
        Assert.AreEqual("x64", ddlmVersionCall.Architecture, "the DDLM release gate must be arch-scoped");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_UnparseableInstalledDdlmVersion_FallsBackToPresence()
    {
        // If the required DDLM release is known but the installed DDLM version can't be parsed (unexpected
        // string), don't block a launch on it — a DDLM IS present (caller confirmed), so fall back to the
        // generic presence rather than failing the gate on a version we can't interpret.
        const string framework = "Microsoft.WindowsAppRuntime.1.8";
        const string expectedDdlm = "Microsoft.WinAppRuntime.DDLM.8000.144.1000.0-x64";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        _fakePackageRegistration.GetInstalledVersionFunc = (name, _) => name == framework ? "8000.144.1000.0" : null;
        _fakePackageRegistration.GetHighestInstalledVersionFunc = (prefix, _) =>
            prefix == DdlmPrefix ? "not-a-version" : null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered(
            "x64",
            new[] { (framework, "8000.144.1000.0"), (expectedDdlm, "8000.144.1000.0") }),
            "an unparseable installed DDLM version must fall back to the generic DDLM presence check");
    }

    [TestMethod]
    public void WinAppRuntimeFrameworkPrefix_MatchesFrameworkNotDdlm()
    {
        // Guard the Framework-vs-DDLM discrimination the gate relies on to decide which expected identities
        // get the exact/version check (Framework) vs generic presence (DDLM). The two use DIFFERENT prefixes
        // (WindowsAppRuntime vs WinAppRuntime.DDLM), so a DDLM name must not be treated as a Framework.
        const string frameworkName = "Microsoft.WindowsAppRuntime.1.8";
        const string ddlmName = "Microsoft.WinAppRuntime.DDLM.8000.144.1000.0-x64";

        Assert.IsTrue(frameworkName.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.Ordinal),
            "a real Framework package name must match the Framework prefix");
        Assert.IsFalse(ddlmName.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.Ordinal),
            "a real DDLM package name must NOT match the Framework prefix (so it isn't exact/version-checked)");
    }

    [TestMethod]
    public void WinAppRuntimeCbsInfix_DiscriminatesCbsFromFramework()
    {
        // Spec R2-L1: guard the exclusion substring against a real CBS name vs a real Framework name,
        // rather than pinning the constant to a literal copy of itself.
        const string cbsName = "Microsoft.WindowsAppRuntime.CBS.1.8";
        const string frameworkName = "Microsoft.WindowsAppRuntime.1.8";

        Assert.IsTrue(cbsName.Contains(WindowsAppRuntimeService.WinAppRuntimeCbsInfix, StringComparison.Ordinal),
            "the CBS system component name must contain the exclusion infix");
        Assert.IsFalse(frameworkName.Contains(WindowsAppRuntimeService.WinAppRuntimeCbsInfix, StringComparison.Ordinal),
            "a real Framework package name must NOT contain the exclusion infix (so it isn't wrongly excluded)");
    }
}

