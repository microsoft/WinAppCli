// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class SolutionProjectReaderTests
{
    [TestMethod]
    public void TryResolveRelativePath_LocalRelativePath_ResolvesUnderTheSolution()
    {
        string resolved = SolutionProjectReader.TryResolveRelativePath(@"C:\repo", @"src/App/App.csproj")!;

        Assert.AreEqual(@"C:\repo\src\App\App.csproj", resolved);
    }

    [TestMethod]
    public void TryResolveRelativePath_UncPath_IsRejected()
    {
        // A solution file is repo content, so cloning an untrusted repository is enough
        // to choose this string. Path.Combine discards the solution directory when the
        // second argument is rooted, so the UNC path survives verbatim — and callers then
        // probe it with File.Exists, which opens an SMB connection and authenticates to
        // whoever answers. Rejecting it here is what keeps that probe from happening.
        Assert.IsNull(SolutionProjectReader.TryResolveRelativePath(@"C:\repo", @"\\attacker.example\share\Evil.csproj"));
    }

    [TestMethod]
    public void TryResolveRelativePath_ForwardSlashUncPath_IsRejected()
    {
        // .slnx paths are commonly written with forward slashes, and both flavors reach
        // the same UNC location.
        Assert.IsNull(SolutionProjectReader.TryResolveRelativePath(@"C:\repo", "//attacker.example/share/Evil.csproj"));
    }

    [TestMethod]
    public void TryResolveRelativePath_DeviceNamespaceNetworkPath_IsRejected()
    {
        // \\?\GLOBALROOT\Device\Mup\... reaches the SMB redirector without spelling
        // "UNC", and being rooted it discards the solution directory entirely.
        Assert.IsNull(SolutionProjectReader.TryResolveRelativePath(
            @"C:\repo", @"\\?\GLOBALROOT\Device\Mup\attacker.example\share\Evil.csproj"));
    }

    [TestMethod]
    public void ReadProjectPaths_UncProject_IsSkipped()
    {
        // End to end through the solution reader: the malicious entry is dropped and the
        // legitimate sibling is still returned, so a poisoned solution degrades to the
        // projects it can safely name rather than failing the whole command.
        string dir = Path.Combine(Path.GetTempPath(), "winapp-slnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string slnxPath = Path.Combine(dir, "App.slnx");
            File.WriteAllText(slnxPath, """
                <Solution>
                  <Project Path="\\attacker.example\share\Evil.csproj" />
                  <Project Path="src/Real/Real.csproj" />
                </Solution>
                """);

            List<string> paths = SolutionProjectReader.ReadProjectPaths(slnxPath);

            CollectionAssert.AreEqual(new[] { Path.Combine(dir, "src", "Real", "Real.csproj") }, paths);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
