// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Emits structured JSON error responses for `winapp ui` commands when --json is set.
/// In --json mode the logger is silenced (LogLevel.None), so error paths must call
/// <see cref="Emit"/> explicitly to give consumers something to parse.
/// </summary>
internal static class UiJsonError
{
    public const string CodeMissingApp = "missing_app";
    public const string CodeMissingSelector = "missing_selector";
    public const string CodeElementNotFound = "element_not_found";
    public const string CodeStaleElement = "stale_element";
    public const string CodeInvalidArguments = "invalid_arguments";
    public const string CodeInternalError = "internal_error";
    public const string CodeZeroSize = "zero_size_element";
    public const string CodeForegroundNotTarget = "foreground_not_target";

    /// <summary>Write a JSON error envelope to stderr. No-op when <paramref name="json"/> is false.</summary>
    public static void Emit(bool json, string code, string message,
                            string? selector = null, string? details = null)
    {
        if (!json) { return; }

        var result = new UiErrorResult
        {
            Error = new UiErrorInfo
            {
                Code = code,
                Message = message,
                Selector = selector,
                Details = details,
            },
        };
        var payload = JsonSerializer.Serialize(result, UiJsonContext.Default.UiErrorResult);
        // Errors go to stderr so consumers can separate them from successful stdout payloads.
        Console.Error.WriteLine(payload);
    }
}
