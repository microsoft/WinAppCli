// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// The guest's own no-follow walk, asserted directly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GuestFileService.ListAsync"/> is what the host reconciles against, so a link the walk
/// followed would make files outside the managed root look like managed content: the host would
/// treat them as already deployed, and a pull would copy them out. The host has its own walk in
/// <see cref="HostSourceWalker"/> and its own tests; this is the guest half of the same rule.
/// </para>
/// <para>
/// The two walks are deliberately <em>not</em> shared. The guest reports a link as absent so
/// reconciliation can delete and repair it, while the host refuses the operation outright — and the
/// host refuses even a linked root under its skip policy, where the guest enumerates through it.
/// Folding one into the other would turn a repairable guest state into a hard failure.
/// <see cref="ScopeRootThatIsALink_IsEnumeratedThrough"/> pins that divergence so it stays a
/// decision rather than a drift.
/// </para>
/// </remarks>
[TestClass]
public class GuestFileServiceWalkTests
{
    private const string SecretName = "id_rsa";

    private string _root = null!;
    private string _outside = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(GuestFileServiceWalkTests));
        _outside = TestPaths.TempRoot("guest-walk-outside");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);

        File.WriteAllText(TestPaths.Under(_outside, SecretName), "not yours");
    }

    [TestCleanup]
    public void Cleanup()
    {
        LinkTestHelpers.TryDeleteDirectory(_root);
        LinkTestHelpers.TryDeleteDirectory(_outside);
    }

    /// <summary>A junction standing in for a directory is never descended into.</summary>
    [TestMethod]
    public async Task IntermediateDirectoryLink_IsNeverDescendedInto()
    {
        var scope = WorkScope();
        var scopeDirectory = CreateScopeDirectory(scope);

        await File.WriteAllTextAsync(
            TestPaths.Under(scopeDirectory, "real.txt"), "real", TestContext.CancellationToken);

        if (!LinkTestHelpers.TryCreateDirectoryLink(TestPaths.Under(scopeDirectory, "logs"), _outside))
        {
            Assert.Inconclusive("Creating a directory link is not possible in this run.");
            return;
        }

        var listed = await new GuestFileService(_root).ListAsync(scope, TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            (string[])["real.txt"],
            listed.Select(f => f.RelativePath).ToArray(),
            "A file behind a directory link must never be reported as managed content.");
    }

    /// <summary>
    /// A file swapped for a link is reported as absent, not hashed through to its target.
    /// </summary>
    /// <remarks>
    /// The leaf matters on its own: an ordinary open follows a symbolic link, so a leaf that passed
    /// only a directory-level check would be read straight out of the tree.
    /// </remarks>
    [TestMethod]
    public async Task LeafSwappedForALink_IsReportedAsAbsent()
    {
        var scope = WorkScope();
        var scopeDirectory = CreateScopeDirectory(scope);

        var victim = TestPaths.Under(scopeDirectory, "payload.bin");
        await File.WriteAllTextAsync(victim, "real", TestContext.CancellationToken);

        if (!LinkTestHelpers.TryReplaceWithLink(victim, TestPaths.Under(_outside, SecretName), _outside))
        {
            Assert.Inconclusive("Replacing a file with a link is not possible in this run.");
            return;
        }

        var listed = await new GuestFileService(_root).ListAsync(scope, TestContext.CancellationToken);

        Assert.IsEmpty(listed, "A leaf that became a link must not be reported, and must not be hashed.");
    }

    /// <summary>
    /// A junction pointing at its own ancestor terminates the walk rather than recursing until the
    /// path length or the stack gives out.
    /// </summary>
    [TestMethod]
    public async Task SelfReferencingLink_TerminatesPromptly()
    {
        var scope = WorkScope();
        var scopeDirectory = CreateScopeDirectory(scope);

        await File.WriteAllTextAsync(
            TestPaths.Under(scopeDirectory, "real.txt"), "real", TestContext.CancellationToken);

        if (!LinkTestHelpers.TryCreateDirectoryLink(TestPaths.Under(scopeDirectory, "loop"), scopeDirectory))
        {
            Assert.Inconclusive("Creating a directory link is not possible in this run.");
            return;
        }

        // The loop edge is itself a reparse point, so it is refused where it would have been
        // followed. Without that this call does not return.
        var listed = await new GuestFileService(_root)
            .ListAsync(scope, TestContext.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        CollectionAssert.AreEqual((string[])["real.txt"], listed.Select(f => f.RelativePath).ToArray());
    }

    /// <summary>
    /// A scope root that is itself a link is enumerated through, and that is a deliberate gap.
    /// </summary>
    /// <remarks>
    /// The host walk throws for a linked root under either policy, because a host source folder is
    /// something a developer named and silently reading elsewhere would be a wrong answer. The guest
    /// walk starts by enumerating the scope folder's contents, so it never looks at that folder
    /// itself, and a junction planted there is followed. That is bounded rather than closed: the
    /// guest is a disposable Sandbox under the same mutual-trust model as the rest of the guest
    /// surface, and a guest process able to plant the junction can already read what it redirects
    /// to. This test pins the real behaviour so the gap stays recorded rather than assumed shut —
    /// see "What path containment does and does not guarantee" in docs/sandbox-execution.md.
    /// </remarks>
    [TestMethod]
    public async Task ScopeRootThatIsALink_IsEnumeratedThrough()
    {
        var scope = WorkScope();

        // The scope directory is not created; a link is planted where it would be.
        var scopeDirectory = TestPaths.Under(_root, "work", scope.Scope!);
        Directory.CreateDirectory(Path.GetDirectoryName(scopeDirectory)!);

        if (!LinkTestHelpers.TryCreateDirectoryLink(scopeDirectory, _outside))
        {
            Assert.Inconclusive("Creating a directory link is not possible in this run.");
            return;
        }

        var listed = await new GuestFileService(_root).ListAsync(scope, TestContext.CancellationToken);

        // The host walk would have thrown. The guest reports the link target's contents instead,
        // under names relative to the scope root.
        CollectionAssert.AreEqual(
            (string[])[SecretName],
            listed.Select(f => f.RelativePath).ToArray(),
            "The linked-root gap has changed; update the containment section of docs/sandbox-execution.md with it.");
    }

    private static GuestPathScope WorkScope() =>
        new(GuestRootNames.Work, Guid.NewGuid().ToString("n"));

    private string CreateScopeDirectory(GuestPathScope scope)
    {
        var directory = TestPaths.Under(_root, "work", scope.Scope!);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
