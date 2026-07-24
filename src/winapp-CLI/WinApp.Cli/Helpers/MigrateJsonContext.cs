// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Source-generated JSON context for the `migrate analyze` contract (NativeAOT/trim-safe
/// deserialization used by `migrate validate`).
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MigrateAnalyzeReport))]
internal partial class MigrateJsonContext : JsonSerializerContext;
