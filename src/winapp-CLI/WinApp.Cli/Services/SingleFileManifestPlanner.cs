// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// The manifest metadata inferred for a .NET file-based app, ready to hand to
/// <see cref="IManifestTemplateService.GenerateCompleteManifestAsync"/>.
/// </summary>
/// <param name="PackageName">Sanitized <c>Identity/@Name</c>.</param>
/// <param name="DisplayName">Human-readable name for <c>Properties/DisplayName</c> and <c>uap:VisualElements/@DisplayName</c>.</param>
/// <param name="PublisherDN">Normalized X.500 <c>Identity/@Publisher</c>.</param>
/// <param name="Version">Four-part, in-range <c>Identity/@Version</c>.</param>
/// <param name="Description">Text for <c>uap:VisualElements/@Description</c>.</param>
internal sealed record SingleFileManifestInfo(
    string PackageName,
    string DisplayName,
    string PublisherDN,
    string Version,
    string Description);

/// <summary>
/// Maps a .NET file-based app's evaluated MSBuild properties onto manifest metadata.
/// <para>
/// Deliberately mirrors <c>winapp manifest generate</c>'s options 1:1 and adds nothing — there is no
/// manifest DSL here. Anything beyond these five values is served by the existing
/// <c>WinAppManifestPath</c> escape hatch, which lets the app point at a hand-authored manifest.
/// </para>
/// <para>
/// <b>Capabilities are deliberately NOT modeled.</b> A WinUI 3 desktop app is full-trust by default:
/// <c>EntryPoint="Windows.FullTrustApplication"</c> plus <c>runFullTrust</c> already unlocks
/// notifications, on-device AI, protocol handlers and shell integration, none of which need a declared
/// capability. <c>runFullTrust</c> stays fixed template boilerplate rather than user input. (Capabilities
/// also span four different manifest elements — <c>Capability</c>, <c>uap:Capability</c>,
/// <c>rescap:Capability</c>, <c>DeviceCapability</c> — so a flat name list would produce invalid
/// manifests.)
/// </para>
/// Pure and side-effect free.
/// </summary>
internal static partial class SingleFileManifestPlanner
{    /// <summary>MSBuild property naming the package identity; falls back to the file stem.</summary>
    internal const string PackageNameProperty = "WinAppPackageName";

    /// <summary>MSBuild property naming the app's display name; falls back to the file stem.</summary>
    internal const string DisplayNameProperty = "WinAppDisplayName";

    /// <summary>MSBuild property naming the publisher; falls back to <c>CN=&lt;current user&gt;</c>.</summary>
    internal const string PublisherProperty = "WinAppPublisher";

    /// <summary>MSBuild property naming the package version; falls back to <c>$(Version)</c>.</summary>
    internal const string VersionProperty = "WinAppVersion";

    /// <summary>MSBuild property naming the app description; falls back to the display name.</summary>
    internal const string DescriptionProperty = "WinAppDescription";

    /// <summary>
    /// The <c>Application/@Id</c> written for a file-based app. Fixed rather than derived from the package
    /// name: <c>Application/@Id</c> has stricter character rules than <c>Identity/@Name</c>, so deriving it
    /// from a dotted reverse-DNS package name (<c>com.contoso.counter</c>) invites sanitization bugs for no
    /// user-visible benefit. The resulting AUMID is the conventional <c>&lt;pkgfamily&gt;!App</c>.
    /// <c>winapp manifest generate</c> keeps its own package-name-derived Id; this divergence is scoped to
    /// single-file mode.
    /// </summary>
    internal const string ApplicationId = "App";

    /// <summary>The MSIX version used when neither <c>WinAppVersion</c> nor <c>$(Version)</c> yields one.</summary>
    internal const string DefaultVersion = "1.0.0.0";

    /// <summary>
    /// Infers the manifest metadata for <paramref name="singleFile"/> from its evaluated properties.
    /// </summary>
    /// <param name="singleFile">The <c>.cs</c> file-based app (its stem supplies the defaults).</param>
    /// <param name="properties">The evaluated MSBuild properties. Undeclared names evaluate to an empty string, so every lookup is a null-or-empty check.</param>
    /// <param name="defaultPublisher">The publisher used when <c>WinAppPublisher</c> is unset; defaults to <c>CN=&lt;current user&gt;</c>.</param>
    /// <exception cref="ProjectRunException">Thrown when a declared version cannot be represented as a valid MSIX <c>Identity/@Version</c>.</exception>
    public static SingleFileManifestInfo Plan(
        FileInfo singleFile,
        IReadOnlyDictionary<string, string> properties,
        string? defaultPublisher = null)
    {
        var stem = Path.GetFileNameWithoutExtension(singleFile.Name);

        // Identity/@Name must match [-.A-Za-z0-9]+, so a stem like "my counter" needs sanitizing. A declared
        // WinAppPackageName is sanitized identically, so a value that can't be an Identity name (rather than
        // silently producing an unpackable manifest) is corrected the same way `manifest generate` does.
        var packageName = ManifestService.CleanPackageName(Read(properties, PackageNameProperty) ?? stem);

        // The display name is free text, so the RAW stem is the better default than the sanitized identity.
        var displayName = Read(properties, DisplayNameProperty) ?? stem;

        // Bare names auto-wrap as CN=<name>, exactly as `manifest generate --publisher-name` does.
        var publisher = Read(properties, PublisherProperty);
        var publisherDN = publisher is not null
            ? PublisherDnHelper.Normalize(publisher)
            : defaultPublisher ?? SystemDefaultsHelper.GetDefaultPublisherCN();

        var version = ResolveVersion(singleFile, properties);

        // Falling back to the display name (rather than a generic "My Application") keeps the Settings and
        // installer text meaningful for an app whose whole configuration is a handful of directives.
        var description = Read(properties, DescriptionProperty) ?? displayName;

        return new SingleFileManifestInfo(packageName, displayName, publisherDN, version, description);
    }

    /// <summary>
    /// Resolves <c>Identity/@Version</c> from <c>WinAppVersion</c>, else the standard MSBuild
    /// <c>$(Version)</c> so <c>#:property Version=2.1.0</c> sets the assembly and package versions together.
    /// </summary>
    /// <remarks>
    /// <c>$(Version)</c> cannot be used raw. <c>Identity/@Version</c> is schema-constrained to exactly four
    /// components, each 0-65535, while MSBuild's default <c>$(Version)</c> is the three-part <c>1.0.0</c>
    /// and a real one legitimately carries a semver suffix (<c>1.2.3-preview.4</c>).
    /// <para>
    /// <c>$(VersionPrefix)</c> is NOT a safe shortcut for stripping that suffix: setting <c>Version</c>
    /// explicitly leaves <c>VersionPrefix</c> EMPTY, so reading it first silently discards the user's
    /// version.
    /// </para>
    /// So: cut at the first <c>-</c>, then pad to four components. An out-of-range or over-long value is
    /// REJECTED rather than truncated, because silently shipping a different version than the one the user
    /// wrote is worse than an actionable error.
    /// </remarks>
    private static string ResolveVersion(FileInfo singleFile, IReadOnlyDictionary<string, string> properties)
    {
        var declaredBy = VersionProperty;
        var raw = Read(properties, VersionProperty);
        if (raw is null)
        {
            declaredBy = "Version";
            raw = Read(properties, "Version");
        }

        if (raw is null)
        {
            return DefaultVersion;
        }

        // Cut the semver pre-release/build suffix: 1.2.3-preview.4 → 1.2.3, 2.0.0+build.55 → 2.0.0.
        var core = raw.Split('-', 2)[0].Split('+', 2)[0].Trim();

        // System.Version needs at least Major.Minor, so a single-component version (a legal, if unusual,
        // $(Version) of "7") would otherwise be rejected instead of padded to 7.0.0.0. Padding here rather
        // than in the shared normalizer keeps `manifest generate`'s behavior untouched.
        if (core.Length > 0 && !core.Contains('.'))
        {
            core += ".0";
        }

        // Require the whole remaining value to be numeric components before normalizing. The shared
        // normalizer deliberately parses only a LEADING numeric token, because it also handles decorated
        // file metadata like "10.0.26100.1 (WinBuild.160101.0800)" — but that leniency is wrong for a
        // hand-written directive: it would turn '1.2.3oops' into 1.2.3.0 and ship a version the user never
        // wrote, contradicting the documented promise that unusable input is rejected.
        var normalized = StrictVersionRegex().IsMatch(core)
            ? ManifestService.NormalizeManifestVersion(core)
            : null;
        if (normalized is null)
        {
            throw new ProjectRunException(
                $"'{singleFile.Name}' declares {declaredBy}='{raw}', which cannot be used as a package version. " +
                "An MSIX Identity version needs up to four components, each between 0 and 65535 (for example 1.2.3.0). " +
                $"Set '#:property {VersionProperty}=<Major.Minor.Build.Revision>' to give the package its own version.");
        }

        return normalized;
    }

    /// <summary>
    /// Reads a property, mapping both "absent" and the empty string an undeclared MSBuild property
    /// evaluates to onto <see langword="null"/>, so callers express defaults with <c>??</c>.
    /// </summary>
    private static string? Read(IReadOnlyDictionary<string, string> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            return null;
        }

        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>One to four dot-separated numeric components, and nothing else.</summary>
    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex StrictVersionRegex();
}
