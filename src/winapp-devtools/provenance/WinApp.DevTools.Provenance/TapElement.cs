namespace WinApp.DevTools.Provenance;

/// <summary>
/// One row of the raw per-element source-resolution census emitted by the "reading the UI"
/// collector for a live app: the flat <c>handle, type, name, file, line, col</c> record.
/// </summary>
/// <param name="Handle">Opaque live-element handle.</param>
/// <param name="Type">Runtime type name of the element.</param>
/// <param name="Name">The element's <c>x:Name</c>, or empty.</param>
/// <param name="File">Resolved source file URI, or empty when unresolved.</param>
/// <param name="Line">Resolved 1-based line, or <c>0</c>.</param>
/// <param name="Column">Resolved 1-based column, or <c>0</c>.</param>
public sealed record TapElement(long Handle, string Type, string Name, string File, int Line, int Column);
