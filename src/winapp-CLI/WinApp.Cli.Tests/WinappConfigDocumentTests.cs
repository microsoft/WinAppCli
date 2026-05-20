// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

// CA1861 ("avoid constant arrays as arguments") is real perf advice in hot
// paths, but these tests use one-shot literal arrays as fixture data and
// extracting each to a `static readonly` field would make the round-trip
// cases noticeably harder to read. Suppress at file scope.
#pragma warning disable CA1861

namespace WinApp.Cli.Tests;

// Direct round-trip tests for the WinappConfigDocument YAML grammar.
// Pre-r3 the document type was only exercised indirectly through
// ConfigService, which made it easy for the parser and the renderer to
// drift apart silently. These tests pin Render → Parse fidelity on the
// awkward inputs (single-quote escaping, `#` inside values, extraTypes
// key ordering) that the round-3 review surfaced as silent data loss.
[TestClass]
public class WinappConfigDocumentTests
{
    private static WinappConfig RoundTrip(WinappConfig cfg)
    {
        var yaml = new WinappConfigDocument(cfg).Render();
        return WinappConfigDocument.Parse(yaml).Config;
    }

    // ---------------------------------------------------------------------
    // M10 — single-quoted scalar escaping
    // ---------------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_OutputContainingApostrophe_PreservesValue()
    {
        // YAML single-quoted scalars escape a literal `'` as `''`. The
        // renderer used to emit the doubled form correctly, but the parser
        // didn't un-double on read — every save grew the apostrophe count.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/O'Brien",
            },
        };

        var rt = RoundTrip(cfg);

        Assert.IsNotNull(rt.JsBindings);
        Assert.AreEqual("bindings/O'Brien", rt.JsBindings!.Output);
    }

    [TestMethod]
    public void RoundTrip_OutputContainingHashChar_PreservesValue()
    {
        // An unquoted `#` introduces a comment; the renderer must quote and
        // the parser must NOT strip the `#` from the quoted value.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/c#-output",
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("bindings/c#-output", rt.JsBindings!.Output);
    }

    [TestMethod]
    public void RoundTrip_PackageNameContainingApostrophe_PreservesValue()
    {
        // packages: list items go through the same QuoteScalar/SanitizeScalar
        // pipeline; cover that path explicitly.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/winrt",
                Packages = { "Some.Vendor's.Package" },
            },
        };

        var rt = RoundTrip(cfg);

        CollectionAssert.AreEqual(
            new[] { "Some.Vendor's.Package" },
            rt.JsBindings!.Packages);
    }

    // ---------------------------------------------------------------------
    // M8 — extraTypes parser must be key-order-independent
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Parse_ExtraTypesWithClassesBeforeNamespace_DoesNotDropEntry()
    {
        // A user (or a YAML formatter that alphabetises keys) may write
        // `classes:` before `namespace:`. Pre-r3 the parser required the
        // dash line to be `- namespace:` exactly and silently dropped
        // entries that led with `- classes:`.
        var yaml = string.Join('\n', new[]
        {
            "packages: []",
            "jsBindings:",
            "  lang: js",
            "  output: bindings/winrt",
            "  extraTypes:",
            "    - classes:",
            "        - SomeClass",
            "      namespace: My.Namespace",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(1, cfg.JsBindings!.ExtraTypes.Count, "classes-first entry must NOT be dropped");
        var entry = cfg.JsBindings.ExtraTypes[0];
        Assert.AreEqual("My.Namespace", entry.Namespace);
        CollectionAssert.AreEqual(new[] { "SomeClass" }, entry.Classes);
    }

    [TestMethod]
    public void Parse_ExtraTypesMultipleEntries_BothOrderingsCoexist()
    {
        var yaml = string.Join('\n', new[]
        {
            "packages: []",
            "jsBindings:",
            "  lang: js",
            "  output: bindings/winrt",
            "  extraTypes:",
            "    - namespace: First.Ns",
            "      classes:",
            "        - First.A",
            "        - First.B",
            "    - classes:",
            "        - Second.X",
            "      namespace: Second.Ns",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.AreEqual(2, cfg.JsBindings!.ExtraTypes.Count);
        Assert.AreEqual("First.Ns", cfg.JsBindings.ExtraTypes[0].Namespace);
        CollectionAssert.AreEqual(
            new[] { "First.A", "First.B" },
            cfg.JsBindings.ExtraTypes[0].Classes);
        Assert.AreEqual("Second.Ns", cfg.JsBindings.ExtraTypes[1].Namespace);
        CollectionAssert.AreEqual(
            new[] { "Second.X" },
            cfg.JsBindings.ExtraTypes[1].Classes);
    }

    [TestMethod]
    public void RoundTrip_ExtraTypesWithApostropheInClassName_Preserved()
    {
        // Cover the QuoteScalar path for extraTypes entries too — both
        // namespace and classes go through it.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/winrt",
                ExtraTypes =
                {
                    new JsBindingsExtraType
                    {
                        Namespace = "Vendor's.Namespace",
                        Classes = { "Vendor's.Class" },
                    },
                },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual(1, rt.JsBindings!.ExtraTypes.Count);
        Assert.AreEqual("Vendor's.Namespace", rt.JsBindings.ExtraTypes[0].Namespace);
        CollectionAssert.AreEqual(
            new[] { "Vendor's.Class" },
            rt.JsBindings.ExtraTypes[0].Classes);
    }

    // ---------------------------------------------------------------------
    // QuoteScalar coverage — values the renderer MUST quote
    // ---------------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_WindowsPathWithDriveColon_PreservedAsString()
    {
        // `C:\foo` contains a `:` so the renderer must quote — otherwise
        // the next load would re-parse it as a mapping.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/winrt",
                AdditionalWinmds = { @"C:\winmds\extra.winmd" },
            },
        };

        var rt = RoundTrip(cfg);

        CollectionAssert.AreEqual(
            new[] { @"C:\winmds\extra.winmd" },
            rt.JsBindings!.AdditionalWinmds);
    }

    [TestMethod]
    public void RoundTrip_ReservedYamlBooleanLikeValue_PreservedAsString()
    {
        // A package id like `no` (unlikely but legal) would be re-parsed
        // as the YAML 1.1 boolean false; the renderer must quote.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/winrt",
                Packages = { "no" },
            },
        };

        var rt = RoundTrip(cfg);

        CollectionAssert.AreEqual(new[] { "no" }, rt.JsBindings!.Packages);
    }

    [TestMethod]
    public void RoundTrip_ValueLeadingWithDash_PreservedAsString()
    {
        // A leading `-` would otherwise be parsed as a YAML list marker.
        var cfg = new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "-leading-dash-dir/winrt",
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("-leading-dash-dir/winrt", rt.JsBindings!.Output);
    }

    // ---------------------------------------------------------------------
    // M7 — `packages:` must accept inline comments / trailing whitespace
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Parse_PackagesHeaderWithInlineComment_StillCollectsEntries()
    {
        // Pre-r4 the parser required `t.Equals("packages:")` exactly, so
        // a comment on the header line (`packages: # SDK pins`) silently
        // reset the section to None and every subsequent `- name:` /
        // `version:` line was dropped. `restore` then loaded zero
        // packages and did nothing. Fixed by routing through IsTopLevelKey
        // (the same comment-tolerant detector that `jsBindings:` uses).
        var yaml = string.Join('\n', new[]
        {
            "packages: # SDK pins",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.AreEqual(1, cfg.Packages.Count,
            "packages: with inline comment must still collect entries");
        Assert.AreEqual("Microsoft.WindowsAppSDK", cfg.Packages[0].Name);
        Assert.AreEqual("1.8.39", cfg.Packages[0].Version);
    }

    // r5-F1 regression — nested jsBindings sub-keys had the SAME exact-string
    // equality bug as the top-level packages: header. An inline comment on
    // any of additionalWinmds: / packages: / extraTypes: / classes: silently
    // mis-routed the following list items into the previously-active list.
    [TestMethod]
    public void Parse_NestedAdditionalWinmdsHeaderWithInlineComment_DoesNotMisrouteEntries()
    {
        var yaml = string.Join('\n', new[]
        {
            "jsBindings:",
            "  lang: js",
            "  packages:",
            "    - Microsoft.WindowsAppSDK",
            "  additionalWinmds: # vendor SDKs go here",
            "    - vendor/Foo.winmd",
            "  additionalRefs: # ref-only WinMDs",
            "    - vendor/Bar.winmd",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.IsNotNull(cfg.JsBindings);
        // Pre-fix: `vendor/Foo.winmd` would have stayed appended to
        // js.Packages because additionalWinmds: with inline comment was
        // missed; listMode stayed Packages.
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsAppSDK" },
            cfg.JsBindings!.Packages.ToList(),
            "Packages must not absorb additionalWinmds entries after inline-comment header.");
        CollectionAssert.AreEqual(
            new[] { "vendor/Foo.winmd" },
            cfg.JsBindings.AdditionalWinmds.ToList(),
            "additionalWinmds: header with inline comment must open the AdditionalWinmds list.");
        CollectionAssert.AreEqual(
            new[] { "vendor/Bar.winmd" },
            cfg.JsBindings.AdditionalRefs.ToList(),
            "additionalRefs: header with inline comment must open the AdditionalRefs list.");
    }

    [TestMethod]
    public void Parse_NestedClassesHeaderWithInlineComment_OpensClassesListNotInlineScalar()
    {
        // For extraTypes[].classes:, the parser had two branches:
        //   1. `t.Equals("classes:")` → open the classes list
        //   2. `t.StartsWith("classes:")` → try to parse inline `[X,Y]` form
        // With `classes: # comment` only branch 2 matched pre-fix; rest was
        // `# comment` which is not `[…]`, so it silently fell through and
        // the subsequent `- ClassName` lines were dropped.
        var yaml = string.Join('\n', new[]
        {
            "jsBindings:",
            "  extraTypes:",
            "    - namespace: Windows.Foundation",
            "      classes: # only these types are emitted",
            "        - Uri",
            "        - PropertyValue",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual(1, cfg.JsBindings!.ExtraTypes.Count);
        CollectionAssert.AreEqual(
            new[] { "Uri", "PropertyValue" },
            cfg.JsBindings.ExtraTypes[0].Classes.ToList(),
            "classes: header with inline comment must open the classes list.");
    }

    // ---------------------------------------------------------------------
    // L1 — plain scalar with apostrophe + inline comment round-trip
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Parse_PlainScalarApostropheWithInlineComment_StripsCommentNotApostrophe()
    {
        // A plain (unquoted) scalar like `output: foo's-dir # comment`
        // must drop the ` # comment` suffix. Pre-r4 SanitizeScalar
        // toggled inSingle on the apostrophe and then treated the `#`
        // as "inside a single-quoted scalar", so the comment leaked into
        // the value and a subsequent save re-quoted the whole thing.
        var yaml = string.Join('\n', new[]
        {
            "jsBindings:",
            "  lang: js",
            "  output: foo's-dir # this is a comment, drop me",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.IsNotNull(cfg.JsBindings);
        Assert.AreEqual("foo's-dir", cfg.JsBindings!.Output,
            "plain-scalar apostrophe must NOT suppress inline-comment stripping");
    }

    // ---------------------------------------------------------------------
    // M8 — SpliceJsBindingsInto contract (preserve comments / unknowns;
    // honor null = remove; append when missing)
    // ---------------------------------------------------------------------

    [TestMethod]
    public void SpliceJsBindingsInto_PreservesLeadingCommentAndTrailingSections()
    {
        // The splice must rewrite only the jsBindings: block in place
        // and leave every other byte of the existing yaml verbatim —
        // including a leading comment line and the trailing packages:
        // section. ConfigService.SaveJsBindingsOnly is the production
        // caller; if this drifts, user-authored YAML loses comments
        // every time the JS bindings step runs.
        var existing = string.Join('\n', new[]
        {
            "# user-managed file — do not edit jsBindings by hand",
            "",
            "jsBindings:",
            "  lang: ts",
            "  output: bindings/old",
            "",
            "packages:",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39",
            "",
        });

        var doc = new WinappConfigDocument(new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/new",
            },
        });

        var spliced = doc.SpliceJsBindingsInto(existing);

        // Leading comment + the entire packages section must survive.
        StringAssert.Contains(spliced, "# user-managed file");
        StringAssert.Contains(spliced, "packages:");
        StringAssert.Contains(spliced, "Microsoft.WindowsAppSDK");
        StringAssert.Contains(spliced, "1.8.39");
        // The jsBindings block must reflect the new values.
        StringAssert.Contains(spliced, "lang: js");
        StringAssert.Contains(spliced, "bindings/new");
        Assert.IsFalse(spliced.Contains("bindings/old"),
            "old jsBindings.output must be gone after splice");
    }

    [TestMethod]
    public void SpliceJsBindingsInto_NullJsBindings_RemovesBlockButKeepsRest()
    {
        // `JsBindings = null` means "remove the block". The rest of the
        // file (other sections, comments) must remain untouched so a user
        // can revert by hand-deleting their bindings declaration.
        var existing = string.Join('\n', new[]
        {
            "packages:",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39",
            "",
            "jsBindings:",
            "  lang: js",
            "  output: bindings/winrt",
            "",
        });

        var doc = new WinappConfigDocument(new WinappConfig { JsBindings = null });

        var spliced = doc.SpliceJsBindingsInto(existing);

        Assert.IsFalse(spliced.Contains("jsBindings:"),
            "jsBindings: header must be gone after splice with null JsBindings");
        Assert.IsFalse(spliced.Contains("bindings/winrt"),
            "old jsBindings body must be gone");
        StringAssert.Contains(spliced, "packages:");
        StringAssert.Contains(spliced, "Microsoft.WindowsAppSDK");
    }

    [TestMethod]
    public void SpliceJsBindingsInto_NoExistingBlock_AppendsOneAndRoundTrips()
    {
        // When the user's yaml has no jsBindings: yet, splice must
        // append one in a way that parses cleanly on the next load.
        var existing = string.Join('\n', new[]
        {
            "packages:",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39",
            "",
        });

        var doc = new WinappConfigDocument(new WinappConfig
        {
            JsBindings = new JsBindingsConfig
            {
                Lang = "js",
                Output = "bindings/winrt",
            },
        });

        var spliced = doc.SpliceJsBindingsInto(existing);

        // Existing section is preserved AND the new block parses back.
        StringAssert.Contains(spliced, "packages:");
        StringAssert.Contains(spliced, "jsBindings:");
        var roundTripped = WinappConfigDocument.Parse(spliced).Config;
        Assert.AreEqual(1, roundTripped.Packages.Count);
        Assert.IsNotNull(roundTripped.JsBindings);
        Assert.AreEqual("js", roundTripped.JsBindings!.Lang);
        Assert.AreEqual("bindings/winrt", roundTripped.JsBindings.Output);
    }
}
