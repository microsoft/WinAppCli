// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Telemetry;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="CIEnvironmentDetectorForTelemetry.IsCIEnvironment"/>, which classifies
/// the host as CI (used to gate update notifications and stamp telemetry). Every detection group is
/// exercised by setting exactly the variables that group requires and asserting the boolean outcome.
///
/// The suite runs sequentially and saves/clears/restores the full set of CI variables the detector
/// reads, so it is deterministic even when the real host (e.g. GitHub Actions) already exports
/// <c>CI</c>/<c>GITHUB_ACTIONS</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CIEnvironmentDetectorForTelemetryTests
{
    // Every variable the detector inspects — must stay in sync with CIEnvironmentDetectorForTelemetry.
    private static readonly string[] AllCiVars =
    [
        // BooleanVariables
        "TF_BUILD", "GITHUB_ACTIONS", "APPVEYOR", "CI", "TRAVIS", "CIRCLECI",
        // AllNotNullVariables (AWS CodeBuild / Jenkins / Google Cloud Build)
        "CODEBUILD_BUILD_ID", "AWS_REGION", "BUILD_ID", "BUILD_URL", "PROJECT_ID",
        // IfNonNullVariables (TeamCity / JetBrains Space)
        "TEAMCITY_VERSION", "JB_SPACE_API_URL",
    ];

    private Dictionary<string, string?> _saved = [];

    [TestInitialize]
    public void ClearCiEnvironment()
    {
        _saved = AllCiVars.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var name in AllCiVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestCleanup]
    public void RestoreCiEnvironment()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [TestMethod]
    public void IsCIEnvironment_NoVariablesSet_ReturnsFalse()
    {
        Assert.IsFalse(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "With no CI signals present the detector must report a non-CI (developer) host.");
    }

    [TestMethod]
    [DataRow("TF_BUILD")]
    [DataRow("GITHUB_ACTIONS")]
    [DataRow("APPVEYOR")]
    [DataRow("CI")]
    [DataRow("TRAVIS")]
    [DataRow("CIRCLECI")]
    public void IsCIEnvironment_BooleanVariableTrue_ReturnsTrue(string variable)
    {
        Environment.SetEnvironmentVariable(variable, "true");
        Assert.IsTrue(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            $"A truthy {variable} must be recognized as a CI environment.");
    }

    [TestMethod]
    public void IsCIEnvironment_BooleanVariableFalse_ReturnsFalse()
    {
        // The boolean providers must be *parsed*, not merely present: CI=false is not CI.
        Environment.SetEnvironmentVariable("CI", "false");
        Assert.IsFalse(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "A boolean CI variable set to false must not be treated as CI.");
    }

    [TestMethod]
    public void IsCIEnvironment_BooleanVariableNonBoolean_ReturnsFalse()
    {
        // A non-parseable value (bool.TryParse fails) must not trip detection.
        Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "yes-please");
        Assert.IsFalse(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "A non-boolean value must fail bool.TryParse and not be treated as CI.");
    }

    [TestMethod]
    public void IsCIEnvironment_AwsCodeBuild_RequiresAllVariables()
    {
        // Only one of the pair present -> not detected.
        Environment.SetEnvironmentVariable("CODEBUILD_BUILD_ID", "build-42");
        Assert.IsFalse(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "A partial AWS CodeBuild signal (missing AWS_REGION) must not be treated as CI.");

        // Both present -> detected (exercises the all-not-null group returning true).
        Environment.SetEnvironmentVariable("AWS_REGION", "us-east-1");
        Assert.IsTrue(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "AWS CodeBuild sets both CODEBUILD_BUILD_ID and AWS_REGION, which must be detected as CI.");
    }

    [TestMethod]
    public void IsCIEnvironment_Jenkins_RequiresBuildIdAndUrl()
    {
        Environment.SetEnvironmentVariable("BUILD_ID", "1234");
        Environment.SetEnvironmentVariable("BUILD_URL", "https://ci.example/job/1234/");
        Assert.IsTrue(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "Jenkins exports both BUILD_ID and BUILD_URL, which must be detected as CI.");
    }

    [TestMethod]
    public void IsCIEnvironment_GoogleCloudBuild_RequiresBuildIdAndProjectId()
    {
        Environment.SetEnvironmentVariable("BUILD_ID", "abc-123");
        Environment.SetEnvironmentVariable("PROJECT_ID", "my-gcp-project");
        Assert.IsTrue(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "Google Cloud Build exports both BUILD_ID and PROJECT_ID, which must be detected as CI.");
    }

    [TestMethod]
    public void IsCIEnvironment_BuildIdAlone_ReturnsFalse()
    {
        // BUILD_ID is shared by Jenkins and GCB; on its own it satisfies neither group.
        Environment.SetEnvironmentVariable("BUILD_ID", "1234");
        Assert.IsFalse(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "BUILD_ID without BUILD_URL or PROJECT_ID does not complete any provider group.");
    }

    [TestMethod]
    public void IsCIEnvironment_TeamCity_DetectedFromVersionAlone()
    {
        Environment.SetEnvironmentVariable("TEAMCITY_VERSION", "2024.03");
        Assert.IsTrue(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "A present TEAMCITY_VERSION must be detected as CI (if-non-null group).");
    }

    [TestMethod]
    public void IsCIEnvironment_JetBrainsSpace_DetectedFromApiUrlAlone()
    {
        Environment.SetEnvironmentVariable("JB_SPACE_API_URL", "https://space.example");
        Assert.IsTrue(CIEnvironmentDetectorForTelemetry.IsCIEnvironment(),
            "A present JB_SPACE_API_URL must be detected as CI (if-non-null group).");
    }
}
