// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Scans a raw argv array to detect whether a global boolean option (e.g.,
/// <c>--verbose</c>, <c>--quiet</c>, <c>--json</c>) was specified, before a real
/// System.CommandLine parse has been performed.
/// </summary>
/// <remarks>
/// The CLI configures the logger from these flags before parsing so that early
/// telemetry / first-run notices respect the level. A naïve <c>args.Contains(...)</c>
/// would treat tokens after a standalone <c>--</c> separator as winapp options even
/// though they are passthrough arguments for a subcommand
/// (e.g., <c>winapp run . -- --json</c> would erroneously enable JSON mode). This
/// helper stops scanning at the first <c>--</c>.
/// </remarks>
internal static class GlobalOptionPreScan
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="args"/> contains a token equal
    /// to <paramref name="name"/> or any of its <paramref name="aliases"/>, located
    /// before the first standalone <c>--</c> separator.
    /// </summary>
    public static bool IsFlagPresent(string[] args, string name, IEnumerable<string> aliases)
    {
        // Materialise aliases once so multi-pass `Contains` is cheap and behaves
        // identically across collection types (Option.Aliases is ICollection).
        var aliasSet = aliases as ICollection<string> ?? aliases.ToList();

        foreach (var token in args)
        {
            // Stop scanning at the first standalone '--' separator: anything after it
            // is passthrough payload for the subcommand, not a winapp global option.
            if (token == "--")
            {
                return false;
            }

            if (token == name || aliasSet.Contains(token))
            {
                return true;
            }
        }

        return false;
    }
}
