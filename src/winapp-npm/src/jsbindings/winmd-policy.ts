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

import * as path from 'path';

export type WinmdPackageCategory = 'emit' | 'refOnly' | 'skip';

// Built-in denylists. User `winapp.jsBindings` overrides layer on top.
const DEFAULT_REF_ONLY_PACKAGES = new Set<string>(
  ['Microsoft.WindowsAppSDK.InteractiveExperiences'].map((p) => p.toLowerCase())
);

const DEFAULT_SKIPPED_PACKAGES = new Set<string>(['Microsoft.WindowsAppSDK.WinUI'].map((p) => p.toLowerCase()));

export interface PackageCategoryOverrides {
  skip?: string[];
  refOnly?: string[];
  emit?: string[];
}

function lowercaseSet(values: readonly string[] | undefined): Set<string> | undefined {
  if (!values || values.length === 0) {
    return undefined;
  }
  return new Set(values.map((v) => v.toLowerCase()));
}

// Categorize a single package ID. Precedence:
//   force-emit > skip > refOnly > emit (default)
export function classifyPackage(packageId: string, overrides?: PackageCategoryOverrides): WinmdPackageCategory {
  if (!packageId || !packageId.trim()) {
    return 'emit';
  }
  const id = packageId.toLowerCase();
  const skip = lowercaseSet(overrides?.skip);
  const refOnly = lowercaseSet(overrides?.refOnly);
  const forceEmit = lowercaseSet(overrides?.emit);

  // Force-emit always wins — lets users opt back in to a denylisted package.
  if (forceEmit?.has(id)) {
    return 'emit';
  }
  if (DEFAULT_SKIPPED_PACKAGES.has(id) || skip?.has(id)) {
    return 'skip';
  }
  if (DEFAULT_REF_ONLY_PACKAGES.has(id) || refOnly?.has(id)) {
    return 'refOnly';
  }
  return 'emit';
}

// Given a winmd file path and the NuGet cache root, return the package ID
// (lowercased) by extracting the first path segment under the cache root.
// Returns null when the path is not under the cache (e.g. user winmds).
export function extractPackageIdFromPath(winmdPath: string, nugetCacheRoot?: string): string | null {
  if (!winmdPath || !winmdPath.trim()) {
    return null;
  }

  if (nugetCacheRoot && nugetCacheRoot.trim()) {
    try {
      const full = path.resolve(winmdPath);
      const root = path.resolve(nugetCacheRoot).replace(/[\\/]+$/, '');
      const rootPrefix = root + path.sep;
      // Case-insensitive compare for Windows path conventions.
      if (full.toLowerCase().startsWith(rootPrefix.toLowerCase())) {
        const rel = full.substring(rootPrefix.length);
        const firstSep = rel.search(/[\\/]/);
        return firstSep > 0 ? rel.substring(0, firstSep) : rel;
      }
    } catch {
      // Fall through to legacy heuristic.
    }
  }

  // Legacy heuristic: scan for a literal "packages" segment.
  const segs = winmdPath.split(/[\\/]/).filter((s) => s.length > 0);
  for (let i = 0; i < segs.length - 1; i++) {
    if (segs[i].toLowerCase() === 'packages') {
      return segs[i + 1];
    }
  }
  return null;
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
 *
 * `emitScope` (when provided) demotes out-of-scope emit packages to refOnly
 * so codegen still sees their metadata for cross-package type resolution.
 * Skip/refOnly classifications take precedence over scope.
 *
 * Prefer this overload over `partitionByPackageCategory(string[], …)` when
 * the source data is the lockfile — see orchestrator.ts.
 */
export function partitionPackageWinmds(
  packages: readonly PackageWinmds[],
  options?: {
    overrides?: PackageCategoryOverrides;
    emitScope?: readonly string[];
  }
): WinmdPartition {
  const overrides = options?.overrides;
  const scope = lowercaseSet(options?.emitScope);

  const emit: string[] = [];
  const refOnly: string[] = [];
  const skipped: string[] = [];

  for (const pkg of packages) {
    if (!pkg || !pkg.name || !pkg.winmds || pkg.winmds.length === 0) {
      continue;
    }
    let cat: WinmdPackageCategory = classifyPackage(pkg.name, overrides);
    if (scope && cat === 'emit' && !scope.has(pkg.name.toLowerCase())) {
      cat = 'refOnly';
    }
    const bucket = cat === 'skip' ? skipped : cat === 'refOnly' ? refOnly : emit;
    for (const w of pkg.winmds) {
      bucket.push(w);
    }
  }

  return { emit, refOnly, skipped };
}

// Partition a flat list of winmd paths by category. Falls back to
// `extractPackageIdFromPath` for each entry — needed for loose user-supplied
// `additionalWinmds` / `additionalRefs` that don't carry their package
// identity. For lockfile-sourced winmds, use `partitionPackageWinmds` instead.
//
// `emitScope` (when provided) demotes out-of-scope emit packages to refOnly
// so codegen still sees their metadata for cross-package type resolution.
// Skip/refOnly classifications take precedence over scope.
export function partitionByPackageCategory(
  winmds: readonly string[],
  options?: {
    overrides?: PackageCategoryOverrides;
    nugetCacheRoot?: string;
    emitScope?: readonly string[];
  }
): WinmdPartition {
  const overrides = options?.overrides;
  const nugetCacheRoot = options?.nugetCacheRoot;
  const scope = lowercaseSet(options?.emitScope);

  const emit: string[] = [];
  const refOnly: string[] = [];
  const skipped: string[] = [];

  for (const w of winmds) {
    const pkg = extractPackageIdFromPath(w, nugetCacheRoot);
    let cat: WinmdPackageCategory = pkg === null ? 'emit' : classifyPackage(pkg, overrides);

    if (scope && cat === 'emit' && pkg !== null && !scope.has(pkg.toLowerCase())) {
      cat = 'refOnly';
    }

    if (cat === 'skip') {
      skipped.push(w);
    } else if (cat === 'refOnly') {
      refOnly.push(w);
    } else {
      emit.push(w);
    }
  }

  return { emit, refOnly, skipped };
}
