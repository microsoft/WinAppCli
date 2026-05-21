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
//
// The native CLI owns a tiny hand-rolled YAML subset (just `packages:`)
// because pulling in YamlDotNet would balloon the NativeAOT trim surface.
// That makes parser/renderer drift the most likely failure mode, so this
// suite pins:
//   * SanitizeScalar (inline `#` strip, quoted-scalar peel, apostrophe
//     escape, plain-vs-quoted comment handling)
//   * QuoteScalar (drive-letter paths, leading dash, reserved YAML
//     booleans, numeric / boolean-looking versions)
//   * Parse / Render round-trips that survive unknown top-level keys and
//     inline comments on the `packages:` header
[TestClass]
public class WinappConfigDocumentTests
{
    private static WinappConfig RoundTrip(WinappConfig cfg)
    {
        var yaml = new WinappConfigDocument(cfg).Render();
        return WinappConfigDocument.Parse(yaml).Config;
    }

    // ---------------------------------------------------------------------
    // SanitizeScalar — inline `#` comment stripping
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Parse_PackageVersionWithInlineComment_StripsComment()
    {
        // A plain (unquoted) `# comment` must be dropped from the version
        // scalar — otherwise the comment text gets baked into the stored
        // value and lockfile + restore reproduce the wrong pin on the next
        // run.
        var yaml = string.Join('\n', new[]
        {
            "packages:",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39 # pinned for compat",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.AreEqual(1, cfg.Packages.Count);
        Assert.AreEqual("Microsoft.WindowsAppSDK", cfg.Packages[0].Name);
        Assert.AreEqual("1.8.39", cfg.Packages[0].Version);
    }

    [TestMethod]
    public void Parse_PackagesHeaderWithInlineComment_StillCollectsEntries()
    {
        // `packages: # SDK pins` must still open the packages section.
        // Pre-fix the parser required an exact-string match and silently
        // dropped every subsequent `- name:` / `version:` line.
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

    [TestMethod]
    public void SanitizeScalar_QuotedHashInValue_PreservesHash()
    {
        // A `#` *inside* a quoted scalar is literal — it is NOT a comment
        // boundary. The unit-level guard pins this so we don't reintroduce
        // a "strip after first #" regression.
        Assert.AreEqual("weird # name",
            WinappConfigDocument.SanitizeScalar(" \"weird # name\""));
        Assert.AreEqual("note # foo",
            WinappConfigDocument.SanitizeScalar(" 'note # foo'"));
    }

    [TestMethod]
    public void SanitizeScalar_PlainApostropheValue_StripsInlineComment()
    {
        // A plain (unquoted) `foo's-bar # comment` must drop the comment
        // — the apostrophe is NOT a quote opener and must not suppress
        // the `# comment` boundary detector.
        Assert.AreEqual("foo's-bar",
            WinappConfigDocument.SanitizeScalar(" foo's-bar # this is a comment"));
    }

    // ---------------------------------------------------------------------
    // SanitizeScalar — quoted-scalar peeling (incl. single-quote escape)
    // ---------------------------------------------------------------------

    [TestMethod]
    public void SanitizeScalar_SingleQuotedWithDoubledApostrophe_Unescapes()
    {
        // YAML single-quoted scalars use `''` as the literal-`'` escape.
        // QuoteScalar emits `'O''Brien'`; SanitizeScalar must reverse it
        // so round-trip is stable.
        Assert.AreEqual("O'Brien",
            WinappConfigDocument.SanitizeScalar(" 'O''Brien'"));
    }

    [TestMethod]
    public void SanitizeScalar_DoubleQuotedSimple_Peels()
    {
        Assert.AreEqual("hello",
            WinappConfigDocument.SanitizeScalar(" \"hello\""));
    }

    [TestMethod]
    public void SanitizeScalar_AsymmetricQuotes_DoesNotPeel()
    {
        // `it's` (plain) must NOT become `it`s` — the outer-quote peel only
        // runs when both ends match the opener.
        Assert.AreEqual("it's",
            WinappConfigDocument.SanitizeScalar(" it's"));
    }

    // ---------------------------------------------------------------------
    // QuoteScalar — values the renderer MUST quote
    // ---------------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_PackageWithDriveLetterColon_PreservedAsString()
    {
        // `C:\winmds\extra` contains `:` so the renderer must quote;
        // otherwise the next load re-parses it as a mapping and drops
        // the value. We cover this via the packages: list because that
        // is the only field today; it exercises the same QuoteScalar /
        // SanitizeScalar pipeline as any other scalar.
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = @"C:\vendor\Foo", Version = "1.0.0" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual(1, rt.Packages.Count);
        Assert.AreEqual(@"C:\vendor\Foo", rt.Packages[0].Name);
        Assert.AreEqual("1.0.0", rt.Packages[0].Version);
    }

    [TestMethod]
    public void RoundTrip_PackageNameContainingApostrophe_PreservesValue()
    {
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "Some.Vendor's.Package", Version = "1.0.0" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("Some.Vendor's.Package", rt.Packages[0].Name);
    }

    [TestMethod]
    public void RoundTrip_PackageNameContainingHashChar_PreservesValue()
    {
        // An unquoted `#` introduces a comment; the renderer must quote
        // and the parser must NOT strip the `#` from the quoted value.
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "Vendor.C#-Package", Version = "1.0.0" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("Vendor.C#-Package", rt.Packages[0].Name);
    }

    [TestMethod]
    public void RoundTrip_NumericLookingVersion_PreservedAsString()
    {
        // A version like `1.0` would otherwise re-parse as the double
        // `1.0` and lose its string identity. NeedsQuoting must catch
        // numeric-looking values and quote them.
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "Vendor.Pkg", Version = "1.0" },
                new PackagePin { Name = "Vendor.IntPkg", Version = "42" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("1.0", rt.Packages[0].Version);
        Assert.AreEqual("42", rt.Packages[1].Version);
    }

    [TestMethod]
    public void RoundTrip_ReservedYamlBooleanLikeValue_PreservedAsString()
    {
        // A version like `no` (unusual but legal) would be re-parsed as
        // the YAML 1.1 boolean false; the renderer must quote.
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "no", Version = "yes" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("no", rt.Packages[0].Name);
        Assert.AreEqual("yes", rt.Packages[0].Version);
    }

    [TestMethod]
    public void RoundTrip_ValueLeadingWithDash_PreservedAsString()
    {
        // A leading `-` would otherwise be parsed as a YAML list marker.
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "-leading-dash-pkg", Version = "1.0.0" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual("-leading-dash-pkg", rt.Packages[0].Name);
    }

    // ---------------------------------------------------------------------
    // Parse — unknown top-level keys must not leak into known sections
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Parse_UnknownTopLevelKey_DoesNotAbsorbItsChildren()
    {
        // A future / unknown top-level key (e.g. `jsBindings:` which now
        // lives in package.json) must NOT push its children into the
        // packages: section.
        var yaml = string.Join('\n', new[]
        {
            "jsBindings:",
            "  packages:",
            "    - Microsoft.WindowsAppSDK",
            "packages:",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.AreEqual(1, cfg.Packages.Count,
            "unknown top-level key must not pollute packages");
        Assert.AreEqual("Microsoft.WindowsAppSDK", cfg.Packages[0].Name);
        Assert.AreEqual("1.8.39", cfg.Packages[0].Version);
    }

    [TestMethod]
    public void Parse_EmptyDocument_ProducesEmptyConfig()
    {
        var cfg = WinappConfigDocument.Parse(string.Empty).Config;
        Assert.AreEqual(0, cfg.Packages.Count);
    }

    [TestMethod]
    public void Parse_OnlyCommentsAndBlankLines_ProducesEmptyConfig()
    {
        var yaml = string.Join('\n', new[]
        {
            "# this is a comment",
            "",
            "  # indented comment",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;
        Assert.AreEqual(0, cfg.Packages.Count);
    }

    // ---------------------------------------------------------------------
    // Render — output must round-trip identically through Parse
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Render_MultiplePackages_ParsesBackToSameConfig()
    {
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.8.39" },
                new PackagePin { Name = "Microsoft.Web.WebView2", Version = "1.0.2592.51" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual(2, rt.Packages.Count);
        Assert.AreEqual("Microsoft.WindowsAppSDK", rt.Packages[0].Name);
        Assert.AreEqual("1.8.39", rt.Packages[0].Version);
        Assert.AreEqual("Microsoft.Web.WebView2", rt.Packages[1].Name);
        Assert.AreEqual("1.0.2592.51", rt.Packages[1].Version);
    }

    [TestMethod]
    public void Render_EmptyConfig_StillEmitsPackagesHeader()
    {
        // `packages:` (with no entries) is the canonical empty form.
        // Render must always emit it so a subsequent Parse round-trips
        // to the same config.
        var doc = new WinappConfigDocument(new WinappConfig());
        var yaml = doc.Render();

        StringAssert.Contains(yaml, "packages:");
        var rt = WinappConfigDocument.Parse(yaml).Config;
        Assert.AreEqual(0, rt.Packages.Count);
    }

    [TestMethod]
    public void Render_OutputEndsWithNewline_StableUnderRepeatedRoundTrip()
    {
        // Rendering must be idempotent: Parse → Render → Parse → Render
        // produces the same bytes the second time. Trailing-newline drift
        // is a common source of "diff churn on every save".
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.8.39" },
            },
        };

        var first = new WinappConfigDocument(cfg).Render();
        var second = new WinappConfigDocument(WinappConfigDocument.Parse(first).Config).Render();

        Assert.AreEqual(first, second, "Render must be idempotent under Parse → Render");
    }

    // ---------------------------------------------------------------------
    // TryParseBool — small public helper, easy regression target
    // ---------------------------------------------------------------------

    [TestMethod]
    public void TryParseBool_AcceptsYamlBooleanLiterals()
    {
        Assert.IsTrue(WinappConfigDocument.TryParseBool("true", out var v) && v);
        Assert.IsTrue(WinappConfigDocument.TryParseBool("YES", out v) && v);
        Assert.IsTrue(WinappConfigDocument.TryParseBool(" on ", out v) && v);
        Assert.IsTrue(WinappConfigDocument.TryParseBool("1", out v) && v);

        Assert.IsTrue(WinappConfigDocument.TryParseBool("false", out v) && !v);
        Assert.IsTrue(WinappConfigDocument.TryParseBool("No", out v) && !v);
        Assert.IsTrue(WinappConfigDocument.TryParseBool("off", out v) && !v);
        Assert.IsTrue(WinappConfigDocument.TryParseBool("0", out v) && !v);

        Assert.IsFalse(WinappConfigDocument.TryParseBool("maybe", out _));
        Assert.IsFalse(WinappConfigDocument.TryParseBool(string.Empty, out _));
    }

    [TestMethod]
    public void IsTopLevelKey_AcceptsExactAndCommentedHeader()
    {
        Assert.IsTrue(WinappConfigDocument.IsTopLevelKey("packages:", "packages:"));
        Assert.IsTrue(WinappConfigDocument.IsTopLevelKey("packages:   ", "packages:"));
        Assert.IsTrue(WinappConfigDocument.IsTopLevelKey("packages: # sdk pins", "packages:"));
        Assert.IsFalse(WinappConfigDocument.IsTopLevelKey("packageses:", "packages:"));
        Assert.IsFalse(WinappConfigDocument.IsTopLevelKey("- packages:", "packages:"));
    }

    // ---------------------------------------------------------------------
    // Parse — additional coverage: duplicate keys, full-grammar round-trip
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Parse_DuplicatePackagesKey_AppendsEntriesFromBothBlocks()
    {
        // A pathological (or hand-edited) `winapp.yaml` may contain the
        // `packages:` key more than once. Pin the documented behavior —
        // the parser re-enters the section and ACCUMULATES entries — so a
        // refactor doesn't accidentally drop the second block silently.
        var yaml = string.Join('\n', new[]
        {
            "packages:",
            "  - name: Microsoft.WindowsAppSDK",
            "    version: 1.8.39",
            "packages:",
            "  - name: Microsoft.Web.WebView2",
            "    version: 1.0.2592.51",
            "",
        });

        var cfg = WinappConfigDocument.Parse(yaml).Config;

        Assert.AreEqual(2, cfg.Packages.Count,
            "duplicate top-level packages: keys must accumulate entries (not drop the second block)");
        Assert.AreEqual("Microsoft.WindowsAppSDK", cfg.Packages[0].Name);
        Assert.AreEqual("Microsoft.Web.WebView2", cfg.Packages[1].Name);
    }

    [TestMethod]
    public void RoundTrip_FullGrammarSurface_StableUnderAllQuotingRules()
    {
        // One round-trip that exercises EVERY QuoteScalar branch at once
        // (drive-letter colon, apostrophe, hash, numeric-looking version,
        // boolean-like version, leading-dash name) plus a plain entry.
        // A regression in any single rule will surface here as a
        // mis-ordered or mangled pair — single-rule tests above pin the
        // exact failure mode, this one pins the COMBINED render-parse
        // contract.
        var cfg = new WinappConfig
        {
            Packages =
            {
                new PackagePin { Name = "Plain.Vendor.Package", Version = "1.2.3" },
                new PackagePin { Name = @"C:\vendor\Local", Version = "0.1.0" },
                new PackagePin { Name = "Some.Vendor's.Package", Version = "2.0" },
                new PackagePin { Name = "Vendor.C#-Package", Version = "42" },
                new PackagePin { Name = "-leading-dash-pkg", Version = "yes" },
                new PackagePin { Name = "no", Version = "off" },
            },
        };

        var rt = RoundTrip(cfg);

        Assert.AreEqual(6, rt.Packages.Count);
        Assert.AreEqual("Plain.Vendor.Package", rt.Packages[0].Name);
        Assert.AreEqual("1.2.3", rt.Packages[0].Version);
        Assert.AreEqual(@"C:\vendor\Local", rt.Packages[1].Name);
        Assert.AreEqual("0.1.0", rt.Packages[1].Version);
        Assert.AreEqual("Some.Vendor's.Package", rt.Packages[2].Name);
        Assert.AreEqual("2.0", rt.Packages[2].Version);
        Assert.AreEqual("Vendor.C#-Package", rt.Packages[3].Name);
        Assert.AreEqual("42", rt.Packages[3].Version);
        Assert.AreEqual("-leading-dash-pkg", rt.Packages[4].Name);
        Assert.AreEqual("yes", rt.Packages[4].Version);
        Assert.AreEqual("no", rt.Packages[5].Name);
        Assert.AreEqual("off", rt.Packages[5].Version);

        // Second-round serialization must equal the first (idempotency
        // already covered for one entry; pin it for the full-grammar case).
        var firstYaml = new WinappConfigDocument(cfg).Render();
        var secondYaml = new WinappConfigDocument(WinappConfigDocument.Parse(firstYaml).Config).Render();
        Assert.AreEqual(firstYaml, secondYaml, "Render must remain idempotent across the full quoting surface");
    }

    [TestMethod]
    public void Parse_InputWithoutTrailingNewline_RoundTripsStablyAndAppendsNewline()
    {
        // A hand-edited winapp.yaml may not end with a newline. Verify:
        //   * Parse succeeds (no off-by-one on the missing terminator)
        //   * Render appends a single trailing newline
        //   * A subsequent Parse → Render is byte-identical (idempotent)
        // This is the splice-into-existing-content edge case the Render
        // idempotency test (which starts from a Render'd string) does not
        // exercise — Render already terminates with \n, so re-parsing
        // never sees the "missing trailing newline" surface.
        var noTrailingNewline = "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39";
        Assert.IsFalse(noTrailingNewline.EndsWith('\n'),
            "fixture sanity check: input must not end with a newline");

        var firstRender = new WinappConfigDocument(WinappConfigDocument.Parse(noTrailingNewline).Config).Render();
        var secondRender = new WinappConfigDocument(WinappConfigDocument.Parse(firstRender).Config).Render();

        Assert.IsTrue(firstRender.EndsWith('\n'),
            "Render must always emit a trailing newline regardless of the source document");
        Assert.AreEqual(firstRender, secondRender,
            "Parse → Render must be byte-stable across a second round-trip");

        var rt = WinappConfigDocument.Parse(firstRender).Config;
        Assert.AreEqual(1, rt.Packages.Count);
        Assert.AreEqual("Microsoft.WindowsAppSDK", rt.Packages[0].Name);
        Assert.AreEqual("1.8.39", rt.Packages[0].Version);
    }

    // ---------------------------------------------------------------------
    // File-IO splice tests — exercise the exact byte sequence
    // ConfigService.Save uses (Render → PathSafety.AtomicWriteAllText with
    // UTF8-no-BOM), then read the on-disk bytes back and re-parse. The
    // in-memory Render() tests above can't catch BOM injection, mid-write
    // truncation, or encoding drift between the writer and reader.
    // ---------------------------------------------------------------------

    private static readonly System.Text.UTF8Encoding SaveEncoding =
        new(encoderShouldEmitUTF8Identifier: false);

    private static string WriteWithSavePath(string tempPath, WinappConfig cfg)
    {
        var yaml = new WinappConfigDocument(cfg).Render();
        WinApp.Cli.Helpers.PathSafety.AtomicWriteAllText(tempPath, yaml, SaveEncoding);
        return tempPath;
    }

    [TestMethod]
    public void Splice_IntoEmptyFile_ProducesValidYaml()
    {
        // A user (or a stale process) may have left an empty winapp.yaml on
        // disk. ConfigService.Save must overwrite it with a complete document
        // that round-trips through Parse — the empty file must not poison
        // anything (header missing, BOM injected, etc).
        var tempPath = Path.Combine(Path.GetTempPath(), $"winapp-splice-empty-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllBytes(tempPath, Array.Empty<byte>());
            Assert.AreEqual(0, new FileInfo(tempPath).Length, "fixture: file must start empty");

            var cfg = new WinappConfig();
            cfg.Packages.Add(new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.8.39" });
            WriteWithSavePath(tempPath, cfg);

            var bytes = File.ReadAllBytes(tempPath);
            Assert.IsTrue(bytes.Length > 0, "Save must overwrite the empty file with rendered content");
            // No UTF-8 BOM: ConfigService.Utf8NoBom mirror.
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "Save must not emit a UTF-8 BOM");

            var saved = File.ReadAllText(tempPath, SaveEncoding);
            var rt = WinappConfigDocument.Parse(saved).Config;
            Assert.AreEqual(1, rt.Packages.Count);
            Assert.AreEqual("Microsoft.WindowsAppSDK", rt.Packages[0].Name);
            Assert.AreEqual("1.8.39", rt.Packages[0].Version);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void Splice_PreservesTrailingNewline()
    {
        // Save → on-disk bytes must end with '\n'. A second Save against the
        // same config must produce byte-identical bytes (file-level
        // idempotency, not just in-memory Render idempotency).
        var tempPath = Path.Combine(Path.GetTempPath(), $"winapp-splice-newline-{Guid.NewGuid():N}.yaml");
        try
        {
            var cfg = new WinappConfig();
            cfg.Packages.Add(new PackagePin { Name = "Microsoft.WindowsAppSDK", Version = "1.8.39" });
            cfg.Packages.Add(new PackagePin { Name = "Microsoft.UI.Xaml", Version = "2.8.6" });

            var firstBytes = File.ReadAllBytes(WriteWithSavePath(tempPath, cfg));
            Assert.IsTrue(firstBytes.Length > 0, "Save must produce a non-empty file");
            Assert.AreEqual((byte)'\n', firstBytes[^1], "Save must terminate the file with a trailing newline");

            var secondBytes = File.ReadAllBytes(WriteWithSavePath(tempPath, cfg));
            CollectionAssert.AreEqual(firstBytes, secondBytes,
                "Re-saving the same config must produce byte-identical on-disk content");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void Splice_NoTrailingNewline_AddsOneAndStaysIdempotent()
    {
        // A hand-edited winapp.yaml saved without a trailing newline must
        // get one back after the next Save, and a second Save must remain
        // byte-stable. This is the disk-side analogue of the
        // Parse_InputWithoutTrailingNewline_RoundTripsStablyAndAppendsNewline
        // in-memory test — only the file path can detect a writer/encoder
        // bug that injects extra bytes during persistence.
        var tempPath = Path.Combine(Path.GetTempPath(), $"winapp-splice-nonl-{Guid.NewGuid():N}.yaml");
        try
        {
            var seed = "packages:\n  - name: Legacy.Pkg\n    version: 0.1.0";
            Assert.IsFalse(seed.EndsWith('\n'), "fixture: seed must not end with a newline");
            File.WriteAllText(tempPath, seed, SaveEncoding);
            Assert.AreNotEqual((byte)'\n', File.ReadAllBytes(tempPath)[^1],
                "fixture: on-disk seed must not end with a newline");

            // Re-parse the on-disk bytes and save through the production
            // sequence; the new file must end with '\n' and stay stable.
            var loaded = WinappConfigDocument.Parse(File.ReadAllText(tempPath, SaveEncoding)).Config;
            var firstBytes = File.ReadAllBytes(WriteWithSavePath(tempPath, loaded));
            Assert.AreEqual((byte)'\n', firstBytes[^1],
                "Save must append a trailing newline when the source document lacked one");

            var secondBytes = File.ReadAllBytes(WriteWithSavePath(tempPath, loaded));
            CollectionAssert.AreEqual(firstBytes, secondBytes,
                "A second Save against the now-newline-terminated file must be byte-stable");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }
}
