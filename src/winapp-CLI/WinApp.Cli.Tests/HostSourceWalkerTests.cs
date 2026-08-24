// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Host-side containment: a directory junction or symlink in a source folder must never widen what
/// gets hashed, deployed, or copied into the guest.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately real links on a real filesystem rather than an abstracted file system.
/// The bug being guarded against is precisely that
/// <see cref="SearchOption.AllDirectories"/> follows a directory reparse point while every file
/// reached through it is an ordinary file with no reparse attribute — so a file-only check passes
/// all of them. Only a real link exercises that.
/// </para>
/// <para>
/// Junctions are attempted first because, unlike symbolic links, creating one needs no Developer
/// Mode and no elevation, so these tests actually run on an ordinary machine. When neither can be
/// created the outcome is <see cref="Assert.Inconclusive(string)"/> — never a silent pass, which
/// for a containment test would be worse than no test at all.
/// </para>
/// </remarks>
[TestClass]
public class HostSourceWalkerTests
{
    private const string SecretName = "secret.txt";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The deployment snapshot must not hash a file that is only reachable through a link.
    /// </summary>
    [TestMethod]
    public async Task DeploymentSnapshot_RefusesToWalkThroughADirectoryLink()
    {
        var root = TestPaths.TempRoot(nameof(DeploymentSnapshot_RefusesToWalkThroughADirectoryLink));
        var outside = TestPaths.TempRoot("outside-deploy");

        try
        {
            PrepareSourceAndOutside(root, outside);

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            // Fails closed. A snapshot that quietly omitted the link would also be acceptable
            // containment, but deployment already refuses reparse *files*, and silently deploying a
            // tree that is not the folder the developer named is the outcome worth refusing loudly.
            var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
                () => DeploymentPlanner.CreateSnapshotAsync(
                    new DirectoryInfo(root), "deployment", TestContext.CancellationToken));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// The snapshot still captures the real files when the link is removed, so the test above is
    /// failing for the right reason rather than because the folder was unreadable.
    /// </summary>
    [TestMethod]
    public async Task DeploymentSnapshot_CapturesOrdinaryFiles()
    {
        var root = TestPaths.TempRoot(nameof(DeploymentSnapshot_CapturesOrdinaryFiles));

        try
        {
            Directory.CreateDirectory(TestPaths.Under(root, "nested"));
            await File.WriteAllTextAsync(
                TestPaths.Under(root, "nested", "app.dll"), "real", TestContext.CancellationToken);

            var snapshot = await DeploymentPlanner.CreateSnapshotAsync(
                new DirectoryInfo(root), "deployment", TestContext.CancellationToken);

            Assert.AreEqual(1, snapshot.Files.Count);
            Assert.AreEqual(@"nested\app.dll", snapshot.Files[0].RelativePath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// <c>sandbox cp</c> must not enumerate — and therefore can never copy — a file that only
    /// exists outside the folder the caller asked to copy.
    /// </summary>
    [TestMethod]
    public void SandboxCopy_NeverEnumeratesAFileBehindADirectoryLink()
    {
        var root = TestPaths.TempRoot(nameof(SandboxCopy_NeverEnumeratesAFileBehindADirectoryLink));
        var outside = TestPaths.TempRoot("outside-copy");

        try
        {
            PrepareSourceAndOutside(root, outside);

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var sources = SandboxCopyService.EnumerateHostSources(
                root, TestContext.CancellationToken, out _);

            Assert.IsFalse(
                sources.Any(f => f.Name.Equals(SecretName, StringComparison.OrdinalIgnoreCase)),
                "A file reachable only through a directory link must never be enumerated for copy.");

            // The genuine content is still copied: containment, not refusing to do the job.
            Assert.IsTrue(sources.Any(f => f.Name.Equals("app.dll", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>A source that is itself a link is refused rather than silently resolved.</summary>
    [TestMethod]
    public void SandboxCopy_RefusesALinkedSourceDirectory()
    {
        var root = TestPaths.TempRoot(nameof(SandboxCopy_RefusesALinkedSourceDirectory));
        var outside = TestPaths.TempRoot("outside-linked-source");

        try
        {
            PrepareSourceAndOutside(root, outside);
            var link = TestPaths.Under(root, "linked");

            if (!TryCreateDirectoryLink(link, outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => SandboxCopyService.EnumerateHostSources(link, TestContext.CancellationToken, out _));

            Assert.AreEqual(ExecutionTargetErrorCodes.ArtifactFailed, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>
    /// A junction pointing back at its own ancestor must end the walk, not recurse until the path
    /// length or the stack gives out.
    /// </summary>
    /// <remarks>
    /// The time bound is the assertion. Following the loop even a few levels deep would multiply the
    /// tree on every pass, so anything that completes promptly demonstrably did not follow it.
    /// </remarks>
    [TestMethod]
    public void SelfReferencingLink_TerminatesPromptly()
    {
        var root = TestPaths.TempRoot(nameof(SelfReferencingLink_TerminatesPromptly));

        try
        {
            Directory.CreateDirectory(TestPaths.Under(root, "nested"));
            File.WriteAllText(TestPaths.Under(root, "nested", "app.dll"), "real");

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "nested", "loop"), root))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var files = HostSourceWalker.EnumerateFiles(
                root, HostReparsePolicy.Skip, TestContext.CancellationToken);
            stopwatch.Stop();

            Assert.AreEqual(1, files.Count, "The loop edge must be refused, leaving only the real file.");
            Assert.IsLessThan(
                TimeSpan.FromSeconds(15),
                stopwatch.Elapsed,
                "A self-referencing link must terminate the walk immediately.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// The pre-read re-check refuses a path whose ancestor became a link after the walk.
    /// </summary>
    [TestMethod]
    public void EnsureNoReparseAncestor_RefusesAPathThroughALink()
    {
        var root = TestPaths.TempRoot(nameof(EnsureNoReparseAncestor_RefusesAPathThroughALink));
        var outside = TestPaths.TempRoot("outside-ancestor");

        try
        {
            PrepareSourceAndOutside(root, outside);

            if (!TryCreateDirectoryLink(TestPaths.Under(root, "linked"), outside))
            {
                Assert.Inconclusive("Creating a directory junction or symbolic link is not possible in this run.");
                return;
            }

            // Lexically contained; only checking the ancestors' reparse state catches that the
            // content it names is not actually inside the root.
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => HostSourceWalker.EnsureNoReparseAncestor(
                    root, TestPaths.Under(root, "linked", SecretName)));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    /// <summary>A path outside the root is refused outright.</summary>
    [TestMethod]
    public void EnsureNoReparseAncestor_RefusesAPathOutsideTheRoot()
    {
        var root = TestPaths.TempRoot(nameof(EnsureNoReparseAncestor_RefusesAPathOutsideTheRoot));
        Directory.CreateDirectory(root);

        try
        {
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(
                () => HostSourceWalker.EnsureNoReparseAncestor(root, Path.Join(root + "-sibling", "app.dll")));

            Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void PrepareSourceAndOutside(string root, string outside)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        File.WriteAllText(TestPaths.Under(root, "app.dll"), "real");
        File.WriteAllText(TestPaths.Under(outside, SecretName), "not yours");
    }

    /// <summary>
    /// Creates a directory link, preferring a junction.
    /// </summary>
    /// <remarks>
    /// A junction is a reparse point that any user can create, so this exercises the real defect on
    /// an ordinary developer machine. A symbolic link needs Developer Mode or elevation and is only
    /// the fallback. Returning false — rather than throwing or quietly succeeding — is what lets the
    /// caller report inconclusive instead of passing without having tested anything.
    /// </remarks>
    private static bool TryCreateDirectoryLink(string linkPath, string target) =>
        TryCreateJunction(linkPath, target) || TryCreateSymbolicLink(linkPath, target);

    private static bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 30_000);

            return Directory.Exists(linkPath)
                && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryCreateSymbolicLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            // Links are removed first: deleting recursively through one would delete the target's
            // contents, which in these tests is the "outside" folder the test is asserting about.
            var links = SafeDirectories(path)
                .Where(directory => new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint));

            foreach (var link in links)
            {
                Directory.Delete(link);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp cleanup is not worth failing a test over.
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(
                path,
                "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
