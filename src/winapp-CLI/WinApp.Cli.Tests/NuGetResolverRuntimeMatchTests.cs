// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

[TestClass]
public class NuGetResolverRuntimeMatchTests
{
    [TestMethod]
    public void RuntimeMatchesRelease_SameRelease_Matches()
    {
        // The installed runtime is labelled by release ("1.8"); the project references a
        // full package version ("1.8.260222000"). They are the same release.
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("1.8", "1.8.260222000"));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_DifferentRelease_DoesNotMatch()
    {
        // Runtime detection picks the newest runtime on the machine, but a project
        // compiles against the release it references. Indexing a 2.4 runtime for a 1.8
        // project makes 'find-api' confirm types that release does not have, and an agent
        // then writes code that does not build.
        Assert.IsFalse(NuGetResolver.RuntimeMatchesRelease("2.4", "1.8.260222000"));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_NoProjectRequirement_TakesTheRuntimeAsIs()
    {
        // The machine-wide SDK scope is not tied to any project.
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("2.4", null));
    }

    [TestMethod]
    public void RuntimeMatchesRelease_UnparseableVersion_TakesTheRuntimeAsIs()
    {
        // Dropping metadata a project may need is worse than including it, so an
        // unrecognizable label on either side is not treated as a mismatch.
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("experimental", "1.8.260222000"));
        Assert.IsTrue(NuGetResolver.RuntimeMatchesRelease("1.8", "not-a-version"));
    }
}
