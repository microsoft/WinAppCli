// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Categorizes NuGet packages into how their .winmd files should be fed to
// dynwinrt-codegen: `emit` (--winmd), `refOnly` (--ref for type resolution
// but no generated bindings), or `skip` (dropped entirely).
//
// Ported from C# `JsBindingsPresets.cs`. The classification used to live in
// the native CLI and was applied as it wrote the lockfile; now the native
// only writes the raw NuGet inventory and the npm wrapper applies policy
// at codegen time.

export type WinmdPackageCategory = 'emit' | 'refOnly' | 'skip';

// Built-in denylists. User `winapp.jsBindings` overrides layer on top.
const DEFAULT_REF_ONLY_PACKAGES = new Set<string>(
  ['Microsoft.WindowsAppSDK.InteractiveExperiences'].map((p) => p.toLowerCase())
);

// Packages whose .winmd files are dropped entirely from the codegen input.
// These are pulled in transitively by Microsoft.WindowsAppSDK but expose UI /
// HWND / Composition surfaces that dynwinrt can't usefully drive from a
// headless Node process:
//   - Microsoft.WindowsAppSDK.WinUI : XAML composables (Button, Page, ...)
//   - Microsoft.Web.WebView2        : HWND / Composition-hosted browser
// Users who need a denylisted package back can list it under
// `winapp.jsBindings.emitPackages`.
const DEFAULT_SKIPPED_PACKAGES = new Set<string>(
  ['Microsoft.WindowsAppSDK.WinUI', 'Microsoft.Web.WebView2'].map((p) => p.toLowerCase())
);

// Categorize a single package ID. Precedence:
//   skip > refOnly > emit (default)
export function classifyPackage(packageId: string): WinmdPackageCategory {
  if (!packageId || !packageId.trim()) {
    return 'emit';
  }
  const id = packageId.toLowerCase();
  if (DEFAULT_SKIPPED_PACKAGES.has(id)) {
    return 'skip';
  }
  if (DEFAULT_REF_ONLY_PACKAGES.has(id)) {
    return 'refOnly';
  }
  return 'emit';
}

export interface WinmdPartition {
  emit: string[];
  refOnly: string[];
  skipped: string[];
}

/** Tuple shape: one entry per NuGet package, with its name and the winmds inside it. */
export interface PackageWinmds {
  name: string;
  winmds: readonly string[];
}

/**
 * Partition a list of `{name, winmds[]}` tuples by category, using the
 * package name directly (no path extraction needed — the lockfile already
 * groups winmds by package on the writer side).
 */
export function partitionPackageWinmds(packages: readonly PackageWinmds[]): WinmdPartition {
  const emit: string[] = [];
  const refOnly: string[] = [];
  const skipped: string[] = [];

  for (const pkg of packages) {
    if (!pkg || !pkg.name || !pkg.winmds || pkg.winmds.length === 0) {
      continue;
    }
    const cat: WinmdPackageCategory = classifyPackage(pkg.name);
    const bucket = cat === 'skip' ? skipped : cat === 'refOnly' ? refOnly : emit;
    for (const w of pkg.winmds) {
      bucket.push(w);
    }
  }

  return { emit, refOnly, skipped };
}
