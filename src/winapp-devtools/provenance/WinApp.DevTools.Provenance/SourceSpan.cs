namespace WinApp.DevTools.Provenance;

/// <summary>A resolved source location: a document URI plus an optional line/column.</summary>
/// <param name="Uri">Source document URI (e.g. an <c>ms-appx:///</c> XAML path).</param>
/// <param name="Line">1-based line, or <c>0</c> when unknown.</param>
/// <param name="Column">1-based column, or <c>0</c> when unknown.</param>
public sealed record SourceSpan(string Uri, int Line, int Column);
