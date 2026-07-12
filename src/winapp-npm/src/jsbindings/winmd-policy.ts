// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Categorizes NuGet .winmds for dynwinrt-codegen: emit, refOnly, or skip.

export type WinmdPackageCategory = 'emit' | 'refOnly' | 'skip';

// Built-in package policy. Ref-only winmds are loaded for type resolution
// without generating their own bindings.
const DEFAULT_REF_ONLY_PACKAGES = new Set<string>(
  ['Microsoft.Windows.SDK.CPP', 'Microsoft.WindowsAppSDK.InteractiveExperiences'].map((p) => p.toLowerCase())
);

// Dropped transitive winmds expose UI/HWND/Composition surfaces that headless Node can't drive.
// Users can force-emit a dropped winmd via `winapp.jsBindings.additionalWinmds` (explicit opt-in).
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

/** Partition package-grouped winmd tuples by policy category. */
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
