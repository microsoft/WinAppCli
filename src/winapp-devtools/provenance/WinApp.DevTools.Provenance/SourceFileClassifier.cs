namespace WinApp.DevTools.Provenance;

/// <summary>
/// Pure classification of a resolved source-file URI into provenance categories. Shared by the
/// grader (to assign <see cref="SourceKind"/>) and the census audit (to independently detect a
/// false-confident grade), so both agree on what "framework", "template/style dictionary" and
/// "the app's own authored markup" mean.
/// </summary>
public static class SourceFileClassifier
{
    /// <summary>No source file was resolved at all (runtime-only origin).</summary>
    public static bool IsEmpty(string? file) => string.IsNullOrWhiteSpace(file);

    /// <summary>A framework-owned source file (WinUI theme dictionaries / control templates).</summary>
    public static bool IsFramework(string? file) =>
        !IsEmpty(file) && file!.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>A theme/style resource dictionary (styles), as opposed to a control-template dictionary.</summary>
    public static bool IsThemeResource(string? file) =>
        !IsEmpty(file) && file!.Contains("themeresources", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A well-known template/style resource dictionary (generic.xaml / themeresources.xaml), whether
    /// framework- or app-owned. Matched by leaf name so ordinary pages are never caught.
    /// </summary>
    public static bool IsTemplateOrStyleDictionary(string? file)
    {
        if (IsEmpty(file))
        {
            return false;
        }

        string leaf = LeafName(file!);
        return leaf.Equals("generic.xaml", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("themeresources.xaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The app's own directly-authored markup (a page / UserControl) — a resolved file that is
    /// neither framework-owned nor a template/style resource dictionary. This is the ONLY category
    /// permitted to earn <see cref="Confidence.Exact"/>.
    /// </summary>
    public static bool IsAuthoredMarkup(string? file) =>
        !IsEmpty(file) && !IsFramework(file) && !IsTemplateOrStyleDictionary(file);

    /// <summary>The file name without any directory prefix.</summary>
    public static string LeafName(string file)
    {
        int slash = file.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < file.Length - 1 ? file[(slash + 1)..] : file;
    }
}
