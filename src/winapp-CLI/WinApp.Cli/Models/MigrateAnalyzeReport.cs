// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

/// <summary>
/// Deserialization models for the `winapp migrate analyze` v1.0 JSON contract emitted by the
/// bundled winui-analyze driver. Consumed by `migrate validate`.
/// </summary>
internal sealed class MigrateAnalyzeReport
{
    [JsonPropertyName("schemaVersion")] public string? SchemaVersion { get; set; }
    [JsonPropertyName("source")] public MigrateAnalyzeSource? Source { get; set; }
    [JsonPropertyName("summary")] public MigrateAnalyzeSummary? Summary { get; set; }
    [JsonPropertyName("files")] public List<MigrateAnalyzeFile> Files { get; set; } = [];
}

internal sealed class MigrateAnalyzeSource
{
    [JsonPropertyName("root")] public string? Root { get; set; }
    [JsonPropertyName("projectFile")] public string? ProjectFile { get; set; }
}

internal sealed class MigrateAnalyzeSummary
{
    [JsonPropertyName("filesAnalyzed")] public int FilesAnalyzed { get; set; }
    [JsonPropertyName("findings")] public int Findings { get; set; }
    [JsonPropertyName("startupCrashFindings")] public int StartupCrashFindings { get; set; }
}

internal sealed class MigrateAnalyzeFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("disposition")] public string? Disposition { get; set; }
    [JsonPropertyName("featureArea")] public string? FeatureArea { get; set; }
    [JsonPropertyName("findings")] public List<MigrateAnalyzeFinding> Findings { get; set; } = [];
}

internal sealed class MigrateAnalyzeFinding
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("detected")] public string? Detected { get; set; }
    [JsonPropertyName("location")] public MigrateAnalyzeLocation? Location { get; set; }
    [JsonPropertyName("fix")] public MigrateAnalyzeFix? Fix { get; set; }
}

internal sealed class MigrateAnalyzeLocation
{
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("column")] public int Column { get; set; }
}

internal sealed class MigrateAnalyzeFix
{
    [JsonPropertyName("ref")] public string? Ref { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
}
