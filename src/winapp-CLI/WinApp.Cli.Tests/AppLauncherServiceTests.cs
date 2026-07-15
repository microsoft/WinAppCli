// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AppLauncherServiceTests
{
    private readonly AppLauncherService _service = new(
        new Microsoft.Extensions.Logging.Abstractions.NullLogger<AppLauncherService>());

    // Known publisher → publisherId mappings obtained from Get-AppxPackage on Windows.
    // These are the ground truth values computed by the Windows platform.

    [TestMethod]
    [DataRow(
        "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        "cw5n1h2txyewy",
        DisplayName = "Microsoft Windows publisher")]
    [DataRow(
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        "8wekyb3d8bbwe",
        DisplayName = "Microsoft Corporation publisher")]
    [DataRow(
        "CN=CA0D5344-F590-41F9-BE2C-16BE6FCEE1DF",
        "rn9aeerfb38dg",
        DisplayName = "GUID-style publisher")]
    [DataRow(
        "CN=83564403-0B26-46B8-9D84-040F43691D31",
        "dt26b99r8h8gj",
        DisplayName = "GUID-style publisher 2")]
    [DataRow(
        "CN=Metulev",
        "j3adjyj8sqwmw",
        DisplayName = "Simple CN publisher")]
    public void ComputePackageFamilyName_MatchesWindowsValue(string publisher, string expectedPublisherId)
    {
        var pfn = _service.ComputePackageFamilyName("TestPackage", publisher);

        Assert.AreEqual($"TestPackage_{expectedPublisherId}", pfn);
    }

    [TestMethod]
    public void ComputePackageFamilyName_PublisherIsCaseSensitive()
    {
        // Windows treats publisher DN as case-sensitive for hash computation.
        // "CN=Test" and "cn=test" produce different publisher IDs.
        var pfn1 = _service.ComputePackageFamilyName("Pkg", "CN=Test");
        var pfn2 = _service.ComputePackageFamilyName("Pkg", "cn=test");

        Assert.AreNotEqual(pfn1, pfn2, "Publisher comparison should be case-sensitive");
    }

    [TestMethod]
    public void ComputePackageFamilyName_PublisherIdIs13Chars()
    {
        var pfn = _service.ComputePackageFamilyName("Pkg", "CN=AnyPublisher");

        // Format: {name}_{publisherId} where publisherId is exactly 13 chars
        var parts = pfn.Split('_');
        Assert.AreEqual(2, parts.Length, "PFN should have exactly one underscore");
        Assert.AreEqual(13, parts[1].Length, "Publisher ID should be exactly 13 characters");
    }

    [TestMethod]
    public void ComputePackageFamilyName_PublisherIdIsLowercase()
    {
        var pfn = _service.ComputePackageFamilyName("Pkg", "CN=SomePublisher");
        var publisherId = pfn.Split('_')[1];

        Assert.AreEqual(publisherId, publisherId.ToLowerInvariant(),
            "Publisher ID should be lowercase");
    }

    // ---- LaunchByAumid ------------------------------------------------------

    [TestMethod]
    public void LaunchByAumid_InjectedActivator_ReturnsPidAndForwardsArguments()
    {
        string? capturedAumid = null;
        string? capturedArgs = null;
        _service.ActivateApplicationImpl = (aumid, args) =>
        {
            capturedAumid = aumid;
            capturedArgs = args;
            return 4242;
        };

        var pid = _service.LaunchByAumid("Contoso.App_abc!App", "--flag value");

        Assert.AreEqual(4242u, pid);
        Assert.AreEqual("Contoso.App_abc!App", capturedAumid);
        Assert.AreEqual("--flag value", capturedArgs);
    }

    [TestMethod]
    public void LaunchByAumid_DefaultActivator_BogusAumid_Throws()
    {
        // Exercises the real DefaultActivateApplication COM path. A non-existent
        // AUMID cannot be activated, so the shell surfaces an error.
        var threw = false;
        try
        {
            _service.LaunchByAumid("WinApp.Cli.Tests.NoSuchApp_000000000000!App");
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Activating a non-existent AUMID should surface a COM error.");
    }

    // ---- GetPackageFullName -------------------------------------------------

    [TestMethod]
    public void GetPackageFullName_InjectedValue_ReturnsIt()
    {
        _service.FindPackageFullNameImpl = _ => "Contoso.App_1.0.0.0_x64__abcdefgh";

        var result = _service.GetPackageFullName("Contoso.App_abcdefgh");

        Assert.AreEqual("Contoso.App_1.0.0.0_x64__abcdefgh", result);
    }

    [TestMethod]
    public void GetPackageFullName_InjectedThrows_ReturnsNull()
    {
        _service.FindPackageFullNameImpl = _ => throw new InvalidOperationException("boom");

        var result = _service.GetPackageFullName("Contoso.App_abcdefgh");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetPackageFullName_DefaultImpl_UnknownFamily_ReturnsNull()
    {
        // Exercises the real DefaultFindPackageFullName PackageManager query. Uses a
        // well-formed family name (13-char publisher ID) that is guaranteed not to be
        // installed, so FindPackages returns an empty result and the method returns null.
        var family = _service.ComputePackageFamilyName("WinAppCliTestNoSuch", "CN=WinAppCliTests");

        var result = _service.GetPackageFullName(family);

        Assert.IsNull(result);
    }

    // ---- TerminatePackageProcesses -----------------------------------------

    [TestMethod]
    public void TerminatePackageProcesses_ComSucceeds_InvokesComAndReturnsEarly()
    {
        string? captured = null;
        _service.TerminateAllProcessesImpl = fullName => captured = fullName;

        _service.TerminatePackageProcesses("Contoso.App_1.0.0.0_x64__abcdefgh", 0);

        Assert.AreEqual("Contoso.App_1.0.0.0_x64__abcdefgh", captured,
            "COM termination path should be invoked with the package full name.");
    }

    [TestMethod]
    public void TerminatePackageProcesses_ComThrows_FallsBackToPidKill()
    {
        _service.TerminateAllProcessesImpl = _ => throw new InvalidOperationException("com failure");

        using var proc = StartLongRunningProcess();
        try
        {
            _service.TerminatePackageProcesses("Contoso.App_1.0.0.0_x64__abcdefgh", (uint)proc.Id);

            Assert.IsTrue(proc.WaitForExit(15000),
                "COM failure should fall back to killing the process by PID.");
        }
        finally
        {
            KillIfRunning(proc);
        }
    }

    [TestMethod]
    public void TerminatePackageProcesses_NoPackageName_KillsByPid()
    {
        using var proc = StartLongRunningProcess();
        try
        {
            _service.TerminatePackageProcesses(null, (uint)proc.Id);

            Assert.IsTrue(proc.WaitForExit(15000),
                "A null package name should route directly to the PID kill fallback.");
        }
        finally
        {
            KillIfRunning(proc);
        }
    }

    [TestMethod]
    public void TerminatePackageProcesses_ZeroPid_NoOp()
    {
        // packageFullName null + processId 0 → both branches short-circuit; must not throw.
        _service.TerminatePackageProcesses(null, 0);
    }

    [TestMethod]
    public void TerminatePackageProcesses_DeadPid_SwallowsArgumentException()
    {
        var proc = StartLongRunningProcess();
        var pid = (uint)proc.Id;
        proc.Kill(entireProcessTree: true);
        proc.WaitForExit(15000);
        proc.Dispose();

        // The PID is now guaranteed dead → GetProcessById throws ArgumentException,
        // which must be swallowed.
        _service.TerminatePackageProcesses(null, pid);
    }

    [TestMethod]
    public void TerminatePackageProcesses_PidKillThrowsInvalidOperation_IsSwallowed()
    {
        // A process that exits between GetProcessById and Kill surfaces InvalidOperationException.
        // That TOCTOU race can't be forced deterministically, so drive it through the kill seam:
        // the guard must swallow it and complete without throwing.
        _service.KillProcessTreeByPidImpl = _ => throw new InvalidOperationException("process already exited");

        _service.TerminatePackageProcesses(null, 4242);
    }

    [TestMethod]
    public void TerminatePackageProcesses_PidKillSeam_ReceivesResolvedPid()
    {
        // The PID fallback should forward the exact process id to the kill seam.
        uint? captured = null;
        _service.KillProcessTreeByPidImpl = pid => captured = pid;

        _service.TerminatePackageProcesses(null, 7777);

        Assert.AreEqual(7777u, captured, "The resolved PID should be forwarded to the kill seam.");
    }

    [TestMethod]
    public void TerminatePackageProcesses_DefaultComImpl_BogusPackage_SwallowsAndFallsBack()
    {
        // Exercises the real DefaultTerminateAllProcesses COM path with a well-formed
        // package full name that is not installed/running. The COM call is a no-op (or
        // fails and is swallowed), and the PID fallback (processId 0) short-circuits —
        // the call completes without throwing.
        var family = _service.ComputePackageFamilyName("WinAppCliTestNoSuch", "CN=WinAppCliTests");
        var publisherId = family.Split('_')[1];
        var fullName = $"WinAppCliTestNoSuch_1.0.0.0_x64__{publisherId}";

        _service.TerminatePackageProcesses(fullName, 0);
    }

    private static Process StartLongRunningProcess()
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 60 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.IsNotNull(proc);
        return proc;
    }

    private static void KillIfRunning(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
