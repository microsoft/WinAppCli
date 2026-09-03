// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsAppModelConformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppLauncherService _launcher = new(
        new Microsoft.Extensions.Logging.Abstractions.NullLogger<AppLauncherService>());

    [TestMethod]
    public void PackageIdentityVectors_MatchWindowsPublisherIds()
    {
        foreach (var vector in LoadVectors())
        {
            var expected = vector.Expected;
            var packageFamilyName = _launcher.ComputePackageFamilyName(expected.PackageName, expected.Publisher);

            Assert.AreEqual(expected.PackageFamilyName, packageFamilyName, vector.Name);
            Assert.AreEqual(
                expected.PublisherId,
                packageFamilyName[(expected.PackageName.Length + 1)..],
                vector.Name);
        }
    }

    [TestMethod]
    public void ManifestVectors_MatchIdentityAndApplicationFields()
    {
        foreach (var vector in LoadVectors())
        {
            var manifest = AppxManifestDocument.Parse(string.Join(Environment.NewLine, vector.ManifestLines));
            var expected = vector.Expected;

            Assert.HasCount(1, expected.Applications, vector.Name);
            var expectedApplication = expected.Applications[0];

            Assert.AreEqual(expected.PackageName, manifest.IdentityName, vector.Name);
            Assert.AreEqual(expected.Publisher, manifest.IdentityPublisher, vector.Name);
            Assert.AreEqual(expectedApplication.Id, manifest.ApplicationId, vector.Name);
            Assert.AreEqual(expectedApplication.Executable, manifest.ApplicationExecutable, vector.Name);
            Assert.AreEqual(
                expectedApplication.AppUserModelId,
                $"{expected.PackageFamilyName}!{manifest.ApplicationId}",
                vector.Name);

            // WinAppCli does not consume these semantics yet, but the typed model keeps the
            // shared fields visible so future activation work can use the same vectors.
            Assert.IsNotNull(expectedApplication.UsesLaunchActivationArguments, vector.Name);
            Assert.IsNotNull(expectedApplication.RunsInAppContainer, vector.Name);
        }
    }

    private static IReadOnlyList<ConformanceVector> LoadVectors()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "WindowsAppModel",
            "appx-manifest-conformance-vectors.json");
        var document = JsonSerializer.Deserialize<ConformanceVectorSet>(File.ReadAllText(path), JsonOptions);

        Assert.IsNotNull(document);
        Assert.AreEqual(1, document.SchemaVersion);
        Assert.HasCount(4, document.Vectors);
        return document.Vectors;
    }

    private sealed class ConformanceVectorSet
    {
        public int SchemaVersion { get; init; }

        public required string Description { get; init; }

        public required IReadOnlyList<string> References { get; init; }

        public required IReadOnlyList<ConformanceVector> Vectors { get; init; }
    }

    private sealed class ConformanceVector
    {
        public required string Name { get; init; }

        public required IReadOnlyList<string> ManifestLines { get; init; }

        public required ExpectedPackage Expected { get; init; }
    }

    private sealed class ExpectedPackage
    {
        public required string PackageName { get; init; }

        public required string Publisher { get; init; }

        public required string PublisherId { get; init; }

        public required string PackageFamilyName { get; init; }

        public required IReadOnlyList<ExpectedApplication> Applications { get; init; }
    }

    private sealed class ExpectedApplication
    {
        public required string Id { get; init; }

        public required string Executable { get; init; }

        public required string AppUserModelId { get; init; }

        public bool? UsesLaunchActivationArguments { get; init; }

        public bool? RunsInAppContainer { get; init; }
    }
}
