// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

[JsonConverter(typeof(JsonStringEnumConverter<IfExists>))]
public enum IfExists
{
    Error,
    Overwrite,
    Skip
}
