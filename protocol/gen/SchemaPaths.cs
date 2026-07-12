// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
namespace Wdxp.Gen;

/// <summary>Locates the protocol source of truth by walking up from the current directory and the app
/// base directory, checking both <c>&lt;dir&gt;/protocol/&lt;file&gt;</c> and <c>&lt;dir&gt;/&lt;file&gt;</c>. Shared by
/// the generator and the conformance suite so the lookup logic can never drift between them.</summary>
public static class SchemaPaths
{
    public static string? FindUp(string fileName)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                foreach (var candidate in new[] { Path.Combine(dir.FullName, "protocol", fileName), Path.Combine(dir.FullName, fileName) })
                    if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}
