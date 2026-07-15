// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Versioning;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the floating-range-aware version math that winapp's dependency resolver relies on to tell a
/// genuine diamond conflict apart from its documented keep-first-selected behavior:
/// <see cref="NugetService.RangesHaveCommonVersion"/> (does any single version satisfy every accumulated range?)
/// and <see cref="NugetService.RangeSatisfiesWithFloat"/> (does a concrete version fall inside a range, honoring
/// a float's implied ceiling?). These are exercised directly because a floating range (<c>1.*</c>) is lost when
/// a package is authored through <c>PackageBuilder</c> (its nuspec is written as <c>[1.0.0, )</c>), so a
/// feed-authored end-to-end test could not reproduce the floating case the checks guard against.
/// </summary>
[TestClass]
public class NugetServiceVersionRangeTests
{
    // A floating range declares no explicit upper bound, so a bounds-only intersection treats 1.* and 2.* as
    // mutually satisfiable and silently accepts an invalid diamond. Each row is "range|range|..." => expected.
    [TestMethod]
    [DataRow("1.*|2.*", false, DisplayName = "Disjoint minor floats (1.* and 2.*) are a conflict")]
    [DataRow("1.*|1.2.*", true, DisplayName = "Overlapping floats (1.* contains 1.2.*) are satisfiable")]
    [DataRow("1.2.*|1.3.*", false, DisplayName = "Disjoint patch floats (1.2.* and 1.3.*) are a conflict")]
    [DataRow("2.*|[1.0.0, 1.9.0)", false, DisplayName = "Float disjoint from an explicit range is a conflict")]
    [DataRow("1.*|[1.5.0, )", true, DisplayName = "Float overlapping a higher open range is satisfiable")]
    [DataRow("*|2.*", true, DisplayName = "Unbounded major float (*) overlaps any float")]
    [DataRow("[1.0.0, )|[2.0.0, )", true, DisplayName = "Differing lower bounds only are satisfiable (no false conflict)")]
    [DataRow("[1.0.0]|[2.0.0]", false, DisplayName = "Conflicting exact pins are a conflict")]
    [DataRow("1.*", true, DisplayName = "A single float is trivially satisfiable")]
    public void RangesHaveCommonVersion_AccountsForFloatingBands(string pipeSeparatedRanges, bool expected)
    {
        var ranges = pipeSeparatedRanges.Split('|').Select(VersionRange.Parse).ToList();

        Assert.AreEqual(expected, NugetService.RangesHaveCommonVersion(ranges));
    }

    // VersionRange.Satisfies ignores a float's ceiling (1.* reports satisfying 2.0.0), so a diamond where the
    // 2.* branch fixes the shared dependency to 2.0.0 first would be silently accepted when the 1.* branch is
    // later checked. RangeSatisfiesWithFloat must reject a version above the floated band.
    [TestMethod]
    [DataRow("1.*", "2.0.0", false, DisplayName = "1.* does NOT satisfy 2.0.0 (above the floated ceiling)")]
    [DataRow("1.*", "1.5.0", true, DisplayName = "1.* satisfies an in-band 1.5.0")]
    [DataRow("1.*", "1.0.0", true, DisplayName = "1.* satisfies its floor 1.0.0")]
    [DataRow("2.*", "1.0.0", false, DisplayName = "2.* does NOT satisfy a below-floor 1.0.0")]
    [DataRow("1.2.*", "1.3.0", false, DisplayName = "1.2.* does NOT satisfy 1.3.0 (above the patch band)")]
    [DataRow("1.2.*", "1.2.9", true, DisplayName = "1.2.* satisfies an in-band 1.2.9")]
    [DataRow("[1.0.0, )", "2.0.0", true, DisplayName = "A non-floating open range is unaffected")]
    [DataRow("*", "999.0.0", true, DisplayName = "Unbounded major float (*) has no ceiling")]
    public void RangeSatisfiesWithFloat_EnforcesFloatedCeiling(string range, string version, bool expected)
    {
        Assert.AreEqual(
            expected,
            NugetService.RangeSatisfiesWithFloat(VersionRange.Parse(range), NuGetVersion.Parse(version)));
    }
}
