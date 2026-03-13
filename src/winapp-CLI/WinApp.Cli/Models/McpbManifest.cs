// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

/// <summary>
/// Represents the parsed manifest.json from an MCP Bundle (.mcpb) file.
/// See: https://github.com/modelcontextprotocol/mcpb/blob/main/MANIFEST.md
/// </summary>
internal sealed class McpbManifest
{
    [JsonPropertyName("manifest_version")]
    public string? ManifestVersion { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("author")]
    public McpbAuthor? Author { get; set; }

    [JsonPropertyName("server")]
    public McpbServer? Server { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("icons")]
    public McpbIcon[]? Icons { get; set; }

    [JsonPropertyName("tools")]
    public JsonElement[]? Tools { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; set; }

    /// <summary>
    /// Gets the Windows-specific metadata section from _meta, if present.
    /// </summary>
    public McpbWindowsMeta? GetWindowsMeta()
    {
        if (Meta is not { } meta || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!meta.TryGetProperty("com.microsoft.windows", out var winMeta))
        {
            return null;
        }

        return JsonSerializer.Deserialize<McpbWindowsMeta>(winMeta.GetRawText(), McpbJsonContext.Default.McpbWindowsMeta);
    }
}

internal sealed class McpbAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class McpbServer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("entry_point")]
    public string? EntryPoint { get; set; }

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("mcp_config")]
    public JsonElement? McpConfig { get; set; }
}

internal sealed class McpbIcon
{
    [JsonPropertyName("src")]
    public string? Src { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }
}

internal sealed class McpbWindowsMeta
{
    [JsonPropertyName("static_responses")]
    public McpbStaticResponses? StaticResponses { get; set; }

    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; set; }
}

internal sealed class McpbStaticResponses
{
    [JsonPropertyName("initialize")]
    public JsonElement? Initialize { get; set; }

    [JsonPropertyName("tools/list")]
    public JsonElement? ToolsList { get; set; }
}

/// <summary>
/// Source-generated JSON serialization context for NativeAOT compatibility.
/// </summary>
[JsonSerializable(typeof(McpbManifest))]
[JsonSerializable(typeof(McpbWindowsMeta))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class McpbJsonContext : JsonSerializerContext
{
}
