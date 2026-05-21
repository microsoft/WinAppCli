// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

internal class CertGenerateJsonOutput
{
    public required string CertificatePath { get; set; }
    public required string Password { get; set; }
    public required string Publisher { get; set; }
    public required string SubjectName { get; set; }
    public string? PublicCertificatePath { get; set; }
}

internal class CertInfoJsonOutput
{
    public required string Subject { get; set; }
    public required string Issuer { get; set; }
    public required string Thumbprint { get; set; }
    public required string SerialNumber { get; set; }
    public required string NotBefore { get; set; }
    public required string NotAfter { get; set; }
    public required bool HasPrivateKey { get; set; }
}

internal class JsonErrorOutput
{
    public required string Error { get; set; }

    /// <summary>
    /// Writes a JSON error object to stdout and returns the given exit code.
    /// Use this from command handlers when --json is active and an error occurs.
    /// </summary>
    public static int Write(IAnsiConsole console, string message, int exitCode = 1)
    {
        var output = new JsonErrorOutput { Error = message };
        console.Profile.Out.Writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, WinAppJsonContext.Default.JsonErrorOutput));
        return exitCode;
    }
}

/// <summary>
/// Source-generated JSON serializer context for all CLI JSON output models.
/// Add new [JsonSerializable(typeof(...))] attributes here when adding --json output to more commands.
/// </summary>
[JsonSerializable(typeof(CertGenerateJsonOutput))]
[JsonSerializable(typeof(CertInfoJsonOutput))]
[JsonSerializable(typeof(JsonErrorOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class WinAppJsonContext : JsonSerializerContext
{
}
