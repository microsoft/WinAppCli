namespace WinApp.DevTools.Provenance.Tests;

/// <summary>Locates the committed reference-corpus TSVs copied next to the test binaries.</summary>
internal static class Fixtures
{
    /// <summary>Absolute path to the census reference-corpus directory.</summary>
    public static string CensusDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "census");

    /// <summary>The number of TSVs expected in the reference corpus.</summary>
    public const int ExpectedTsvCount = 15;
}
