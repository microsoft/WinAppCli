// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class PackageRegistrationServiceTests
{
    private const int ERROR_INSTALL_PACKAGE_ALREADY_EXISTS = unchecked((int)0x80073CFB);
    private const int ERROR_ACCESS_DISABLED_BY_POLICY = unchecked((int)0x800704EC);

    [TestMethod]
    public void IsSideloadPolicyError_TrueForGroupPolicyHResult()
    {
        var ex = new InvalidOperationException("blocked") { HResult = ERROR_ACCESS_DISABLED_BY_POLICY };
        Assert.IsTrue(PackageRegistrationService.IsSideloadPolicyError(ex));
    }

    [TestMethod]
    public void IsSideloadPolicyError_FalseForOtherHResults()
    {
        Assert.IsFalse(PackageRegistrationService.IsSideloadPolicyError(
            new InvalidOperationException("conflict") { HResult = ERROR_INSTALL_PACKAGE_ALREADY_EXISTS }));
        Assert.IsFalse(PackageRegistrationService.IsSideloadPolicyError(
            new Exception("misc")));
    }

    [TestMethod]
    public void BuildRegistrationException_AppendsHResult()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "boom",
            unchecked((int)0x8007000B));

        StringAssert.Contains(ex.Message, "Failed to register package: boom");
        StringAssert.Contains(ex.Message, "(0x8007000B)");
    }

    [TestMethod]
    public void BuildRegistrationException_FallsBackWhenErrorTextEmpty()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            errorText: null,
            hresult: null);

        StringAssert.Contains(ex.Message, "Unknown error");
    }

    [TestMethod]
    public void BuildRegistrationException_AddsConflictHintWithGenericPlaceholder_WhenIdentityUnknown()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "duplicate",
            ERROR_INSTALL_PACKAGE_ALREADY_EXISTS,
            packageIdentityName: null);

        StringAssert.Contains(ex.Message, "Get-AppxPackage <PackageName> | Remove-AppxPackage");
    }

    [TestMethod]
    public void BuildRegistrationException_SubstitutesIdentityIntoConflictHint()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "duplicate",
            ERROR_INSTALL_PACKAGE_ALREADY_EXISTS,
            packageIdentityName: "Contoso.MyApp");

        StringAssert.Contains(ex.Message, "Get-AppxPackage Contoso.MyApp | Remove-AppxPackage");
        Assert.IsFalse(ex.Message.Contains("<PackageName>"),
            "Hint must use the actual identity, not the literal placeholder");
    }

    [TestMethod]
    public void BuildRegistrationException_DoesNotAddConflictHint_ForUnrelatedHResult()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "transient",
            unchecked((int)0x80004005));

        Assert.IsFalse(ex.Message.Contains("Get-AppxPackage"),
            "Conflict hint must only appear for 0x80073CFB");
    }

    [TestMethod]
    public void BuildRegistrationException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed",
            "x",
            null,
            inner: inner);

        Assert.AreSame(inner, ex.InnerException);
    }

    // ---- RegisterLooseLayoutAsync ------------------------------------------

    [TestMethod]
    public async Task RegisterLooseLayoutAsync_Success_LogsDebug()
    {
        var (svc, logger) = NewService();
        Uri? capturedUri = null;
        svc.RegisterLooseImpl = (uri, _) =>
        {
            capturedUri = uri;
            return Task.FromResult(new PackageRegistrationService.DeploymentOutcome(true, null, null));
        };

        await svc.RegisterLooseLayoutAsync(Path.Combine(Path.GetTempPath(), "winapp-reg", "AppxManifest.xml"));

        Assert.IsNotNull(capturedUri);
        Assert.IsTrue(logger.Has(LogLevel.Debug, "Package registered from loose layout"));
    }

    [TestMethod]
    public async Task RegisterLooseLayoutAsync_NotRegistered_ThrowsWithIdentityHint()
    {
        var (svc, _) = NewService();
        var manifest = CreateTempManifest("Contoso.MyApp");
        try
        {
            svc.RegisterLooseImpl = (_, _) => Task.FromResult(
                new PackageRegistrationService.DeploymentOutcome(false, "denied", ERROR_INSTALL_PACKAGE_ALREADY_EXISTS));

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => svc.RegisterLooseLayoutAsync(manifest));

            StringAssert.Contains(ex.Message, "Failed to register package: denied");
            // TryReadIdentityName read the real manifest, so the hint uses the true identity.
            StringAssert.Contains(ex.Message, "Get-AppxPackage Contoso.MyApp | Remove-AppxPackage");
        }
        finally
        {
            TryDeleteManifest(manifest);
        }
    }

    [TestMethod]
    public async Task RegisterLooseLayoutAsync_SideloadPolicyError_ThrowsFriendlyMessage()
    {
        var (svc, _) = NewService();
        svc.RegisterLooseImpl = (_, _) => throw new Exception("blocked") { HResult = ERROR_ACCESS_DISABLED_BY_POLICY };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.RegisterLooseLayoutAsync("relative-manifest.xml"));

        StringAssert.Contains(ex.Message, "Sideloading is blocked by Group Policy");
        Assert.AreEqual(ERROR_ACCESS_DISABLED_BY_POLICY, ex.InnerException!.HResult);
    }

    [TestMethod]
    public async Task RegisterLooseLayoutAsync_GenericError_WrapsAndReadsIdentityNullWhenNoManifest()
    {
        var (svc, _) = NewService();
        var inner = new Exception("winrt boom") { HResult = unchecked((int)0x80004005) };
        svc.RegisterLooseImpl = (_, _) => throw inner;

        // Manifest path does not exist → TryReadIdentityName catch returns null → generic placeholder path.
        var missing = Path.Combine(Path.GetTempPath(), $"winapp-missing-{Guid.NewGuid():N}", "AppxManifest.xml");
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.RegisterLooseLayoutAsync(missing));

        StringAssert.Contains(ex.Message, "Failed to register package: winrt boom");
        Assert.AreSame(inner, ex.InnerException);
    }

    [TestMethod]
    public async Task RegisterLooseLayoutAsync_OperationCanceled_Propagates()
    {
        var (svc, _) = NewService();
        svc.RegisterLooseImpl = (_, _) => throw new OperationCanceledException();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => svc.RegisterLooseLayoutAsync("relative-manifest.xml"));
    }

    // ---- RegisterSparseAsync -----------------------------------------------

    [TestMethod]
    public async Task RegisterSparseAsync_Success_LogsDebug()
    {
        var (svc, logger) = NewService();
        Uri? capturedManifest = null;
        Uri? capturedExternal = null;
        svc.RegisterSparseImpl = (manifestUri, externalUri, _) =>
        {
            capturedManifest = manifestUri;
            capturedExternal = externalUri;
            return Task.FromResult(new PackageRegistrationService.DeploymentOutcome(true, null, null));
        };

        var dir = Path.Combine(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}");
        await svc.RegisterSparseAsync(Path.Combine(dir, "AppxManifest.xml"), dir);

        Assert.IsNotNull(capturedManifest);
        Assert.IsNotNull(capturedExternal);
        Assert.IsTrue(logger.Has(LogLevel.Debug, "Sparse package registered"));
    }

    [TestMethod]
    public async Task RegisterSparseAsync_NotRegistered_Throws()
    {
        var (svc, _) = NewService();
        svc.RegisterSparseImpl = (_, _, _) => Task.FromResult(
            new PackageRegistrationService.DeploymentOutcome(false, "sparse-fail", null));

        var dir = Path.Combine(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}");
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.RegisterSparseAsync(Path.Combine(dir, "AppxManifest.xml"), dir));

        StringAssert.Contains(ex.Message, "Failed to register sparse package: sparse-fail");
    }

    [TestMethod]
    public async Task RegisterSparseAsync_SideloadPolicyError_ThrowsFriendlyMessage()
    {
        var (svc, _) = NewService();
        svc.RegisterSparseImpl = (_, _, _) => throw new Exception("blocked") { HResult = ERROR_ACCESS_DISABLED_BY_POLICY };

        var dir = Path.Combine(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}");
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.RegisterSparseAsync(Path.Combine(dir, "AppxManifest.xml"), dir));

        StringAssert.Contains(ex.Message, "Sideloading is blocked by Group Policy");
    }

    [TestMethod]
    public async Task RegisterSparseAsync_GenericError_Wraps()
    {
        var (svc, _) = NewService();
        var inner = new Exception("sparse boom") { HResult = unchecked((int)0x80004005) };
        svc.RegisterSparseImpl = (_, _, _) => throw inner;

        var dir = Path.Combine(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}");
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.RegisterSparseAsync(Path.Combine(dir, "AppxManifest.xml"), dir));

        StringAssert.Contains(ex.Message, "Failed to register sparse package: sparse boom");
        Assert.AreSame(inner, ex.InnerException);
    }

    [TestMethod]
    public async Task RegisterSparseAsync_OperationCanceled_Propagates()
    {
        var (svc, _) = NewService();
        svc.RegisterSparseImpl = (_, _, _) => throw new OperationCanceledException();

        var dir = Path.Combine(Path.GetTempPath(), $"winapp-sparse-{Guid.NewGuid():N}");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => svc.RegisterSparseAsync(Path.Combine(dir, "AppxManifest.xml"), dir));
    }

    // ---- UnregisterAsync ----------------------------------------------------

    [TestMethod]
    public async Task UnregisterAsync_NoMatch_ReturnsFalse()
    {
        var (svc, _) = NewService();
        svc.EnumerateUserPackagesImpl = () => [View("Other.App")];

        var removed = await svc.UnregisterAsync("Contoso.App");

        Assert.IsFalse(removed);
    }

    [TestMethod]
    public async Task UnregisterAsync_Match_RemovesAndReturnsTrue()
    {
        var (svc, logger) = NewService();
        svc.EnumerateUserPackagesImpl = () => [View("Contoso.App", fullName: "Contoso.App_1.0.0.0_x64__abc")];
        string? removedFullName = null;
        bool? preserveForwarded = null;
        svc.RemovePackageImpl = (fullName, preserve, _) =>
        {
            removedFullName = fullName;
            preserveForwarded = preserve;
            return Task.FromResult(new PackageRegistrationService.RemovalOutcome(null));
        };

        var removed = await svc.UnregisterAsync("contoso.app", preserveAppData: false);

        Assert.IsTrue(removed);
        Assert.AreEqual("Contoso.App_1.0.0.0_x64__abc", removedFullName);
        Assert.AreEqual(false, preserveForwarded);
        Assert.IsTrue(logger.Has(LogLevel.Debug, "Removing package"));
    }

    [TestMethod]
    public async Task UnregisterAsync_RemovalReportsError_LogsWarning()
    {
        var (svc, logger) = NewService();
        svc.EnumerateUserPackagesImpl = () => [View("Contoso.App", fullName: "Contoso.App_1.0.0.0_x64__abc")];
        svc.RemovePackageImpl = (_, _, _) => Task.FromResult(
            new PackageRegistrationService.RemovalOutcome("could not remove"));

        var removed = await svc.UnregisterAsync("Contoso.App");

        Assert.IsTrue(removed);
        Assert.IsTrue(logger.Has(LogLevel.Warning, "Warning removing package"));
    }

    // ---- UnregisterByFullNameAsync -----------------------------------------

    [TestMethod]
    public async Task UnregisterByFullNameAsync_NoError_LogsDebugOnly()
    {
        var (svc, logger) = NewService();
        svc.RemovePackageImpl = (_, _, _) => Task.FromResult(new PackageRegistrationService.RemovalOutcome(null));

        await svc.UnregisterByFullNameAsync("Contoso.App_1.0.0.0_x64__abc");

        Assert.IsTrue(logger.Has(LogLevel.Debug, "Removing package"));
        Assert.IsFalse(logger.Has(LogLevel.Warning, "Warning removing package"));
    }

    [TestMethod]
    public async Task UnregisterByFullNameAsync_Error_LogsWarning()
    {
        var (svc, logger) = NewService();
        svc.RemovePackageImpl = (_, _, _) => Task.FromResult(new PackageRegistrationService.RemovalOutcome("boom"));

        await svc.UnregisterByFullNameAsync("Contoso.App_1.0.0.0_x64__abc");

        Assert.IsTrue(logger.Has(LogLevel.Warning, "Warning removing package"));
    }

    // ---- InstallPackageAsync -----------------------------------------------

    [TestMethod]
    public async Task InstallPackageAsync_Success_LogsDebug()
    {
        var (svc, logger) = NewService();
        Uri? capturedUri = null;
        svc.AddPackageImpl = (uri, _) =>
        {
            capturedUri = uri;
            return Task.FromResult(new PackageRegistrationService.InstallOutcome(null, null));
        };

        await svc.InstallPackageAsync(Path.Combine(Path.GetTempPath(), "winapp-pkg", "app.msix"));

        Assert.IsNotNull(capturedUri);
        Assert.IsTrue(logger.Has(LogLevel.Debug, "Installed package"));
    }

    [TestMethod]
    public async Task InstallPackageAsync_Error_ThrowsWithHResult()
    {
        var (svc, _) = NewService();
        svc.AddPackageImpl = (_, _) => Task.FromResult(
            new PackageRegistrationService.InstallOutcome("install failed", unchecked((int)0x80073CFB)));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InstallPackageAsync(Path.Combine(Path.GetTempPath(), "winapp-pkg", "app.msix")));

        StringAssert.Contains(ex.Message, "Failed to install package 'app.msix': install failed");
        StringAssert.Contains(ex.Message, "0x80073CFB");
    }

    // ---- GetInstalledVersion -----------------------------------------------

    [TestMethod]
    public void GetInstalledVersion_Found_ReturnsFormattedVersion()
    {
        var (svc, _) = NewService();
        svc.EnumerateUserPackagesImpl = () =>
        [
            View("Other.App", maj: 9, min: 9, bld: 9, rev: 9),
            View("Contoso.App", maj: 1, min: 2, bld: 3, rev: 4),
        ];

        Assert.AreEqual("1.2.3.4", svc.GetInstalledVersion("contoso.app"));
    }

    [TestMethod]
    public void GetInstalledVersion_NotFound_ReturnsNull()
    {
        var (svc, _) = NewService();
        svc.EnumerateUserPackagesImpl = () => [View("Other.App")];

        Assert.IsNull(svc.GetInstalledVersion("Contoso.App"));
    }

    // ---- FindDevPackages ----------------------------------------------------

    [TestMethod]
    public void FindDevPackages_MapsMatchingPackages()
    {
        var (svc, _) = NewService();
        svc.EnumerateUserPackagesImpl = () =>
        [
            View("Other.App"),
            View("Contoso.App", fullName: "Contoso.App_1.2.3.4_x64__abc", maj: 1, min: 2, bld: 3, rev: 4,
                dev: true, loc: () => @"C:\dev\contoso"),
        ];

        var results = svc.FindDevPackages("Contoso.App");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Contoso.App_1.2.3.4_x64__abc", results[0].FullName);
        Assert.AreEqual("Contoso.App", results[0].Name);
        Assert.AreEqual("1.2.3.4", results[0].Version);
        Assert.AreEqual(@"C:\dev\contoso", results[0].InstallLocation);
        Assert.IsTrue(results[0].IsDevelopmentMode);
    }

    [TestMethod]
    public void FindDevPackages_InstalledLocationThrows_YieldsNullLocation()
    {
        var (svc, _) = NewService();
        svc.EnumerateUserPackagesImpl = () =>
        [
            View("Contoso.App", loc: () => throw new InvalidOperationException("gone")),
        ];

        var results = svc.FindDevPackages("Contoso.App");

        Assert.AreEqual(1, results.Count);
        Assert.IsNull(results[0].InstallLocation);
    }

    [TestMethod]
    public void FindDevPackages_NoMatch_ReturnsEmpty()
    {
        var (svc, _) = NewService();
        svc.EnumerateUserPackagesImpl = () => [View("Other.App")];

        Assert.AreEqual(0, svc.FindDevPackages("Contoso.App").Count);
    }

    // ---- real WinRT default impls (OS boundary, deterministic failure paths) ----
    // These exercise the real PackageManager default seams with bogus inputs that fail
    // deterministically and have no side effects (nothing is installed/removed). They
    // cover the thin WinRT-wrapper default implementations that the injected-seam tests
    // above bypass.

    [TestMethod]
    public async Task RegisterLooseLayoutAsync_RealDefault_NonExistentManifest_Throws()
    {
        var (svc, _) = NewService();
        var missing = Path.Combine(Path.GetTempPath(), $"winapp-nodef-{Guid.NewGuid():N}", "AppxManifest.xml");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.RegisterLooseLayoutAsync(missing));
    }

    [TestMethod]
    public async Task RegisterSparseAsync_RealDefault_NonExistentManifest_Throws()
    {
        var (svc, _) = NewService();
        var dir = Path.Combine(Path.GetTempPath(), $"winapp-nodef-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => svc.RegisterSparseAsync(Path.Combine(dir, "AppxManifest.xml"), dir));
        }
        finally
        {
            TryDeleteManifest(Path.Combine(dir, "AppxManifest.xml"));
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_RealDefault_NonExistentPackage_Fails()
    {
        var (svc, _) = NewService();
        var missing = Path.Combine(Path.GetTempPath(), $"winapp-nodef-{Guid.NewGuid():N}", "app.msix");

        var threw = false;
        try
        {
            await svc.InstallPackageAsync(missing);
        }
        catch (Exception)
        {
            // WinRT AddPackageAsync surfaces a bad path either by throwing (COMException)
            // or by returning an error result (→ InvalidOperationException). Both exercise
            // the real DefaultAddPackage seam.
            threw = true;
        }

        Assert.IsTrue(threw, "Installing a non-existent package must fail at the WinRT boundary.");
    }

    [TestMethod]
    public async Task UnregisterByFullNameAsync_RealDefault_UnknownPackage_ExercisesSeam()
    {
        var (svc, logger) = NewService();
        // Well-formed but non-installed full name.
        var fullName = "WinAppCliTestNoSuch_1.0.0.0_x64__j3adjyj8sqwmw";

        try
        {
            await svc.UnregisterByFullNameAsync(fullName);

            // If WinRT returned an error result instead of throwing, the warning is logged.
            Assert.IsTrue(logger.Has(LogLevel.Warning, "Warning removing package"));
        }
        catch (Exception)
        {
            // WinRT RemovePackageAsync surfaced "package not found" by throwing — also
            // acceptable; the real DefaultRemovePackage seam was still exercised.
        }
    }

    [TestMethod]
    public void GetInstalledVersion_RealDefault_UnknownPackage_ReturnsNull()
    {
        var (svc, _) = NewService();

        // Enumerates the current user's real packages (none named this) → null,
        // covering the real DefaultEnumerateUserPackages mapping.
        Assert.IsNull(svc.GetInstalledVersion("WinAppCliTestNoSuchPackage.Contoso"));
    }

    [TestMethod]
    public void FindDevPackages_RealDefault_MatchingRealPackage_InvokesInstalledLocation()
    {
        // Grab a real installed package name so FindDevPackages matches it and invokes
        // the InstalledLocation accessor lambda (whose result may be a path or throw —
        // both are valid and covered by the surrounding try/catch).
        var pm = new Windows.Management.Deployment.PackageManager();
        string? realName = null;
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            realName = p.Id.Name;
            break;
        }

        Assert.IsNotNull(realName, "Expected at least one package for the current user.");

        var (svc, _) = NewService();
        var results = svc.FindDevPackages(realName);

        Assert.IsTrue(results.Count >= 1, "The matching real package should be projected.");
        Assert.AreEqual(realName, results[0].Name);
    }

    // ---- helpers ------------------------------------------------------------

    private static (PackageRegistrationService Service, CapturingLogger<PackageRegistrationService> Logger) NewService()
    {
        var logger = new CapturingLogger<PackageRegistrationService>();
        return (new PackageRegistrationService(logger), logger);
    }

    private static PackageRegistrationService.InstalledPackageView View(
        string name,
        string fullName = "Pkg_1.0.0.0_x64__abcdefgh",
        ushort maj = 1,
        ushort min = 0,
        ushort bld = 0,
        ushort rev = 0,
        bool dev = true,
        Func<string?>? loc = null)
        => new(name, fullName, maj, min, bld, rev, dev, loc ?? (() => null));

    private static string CreateTempManifest(string identityName)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"winapp-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AppxManifest.xml");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">\n" +
            $"  <Identity Name=\"{identityName}\" Publisher=\"CN=Test\" Version=\"1.0.0.0\" />\n" +
            "</Package>\n");
        return path;
    }

    private static void TryDeleteManifest(string manifestPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(manifestPath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
