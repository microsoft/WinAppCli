// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Filesystem-safety helpers for workspace writes and winmd paths.

import * as fs from 'fs';
import * as path from 'path';

// True for UNC / network paths (`\\server\share`, `\\?\UNC\…`, `\\.\UNC\…`).
// Local DOS device paths (`\\?\C:\…`, `\\.\C:\…`) are not network.
export function isNetworkPath(p: string): boolean {
  if (!p) {
    return false;
  }
  // Normalize forward slashes — Windows accepts both.
  const norm = p.replace(/\//g, '\\');

  if (!norm.startsWith('\\\\')) {
    return false;
  }

  // Plain UNC: `\\server\share\…`
  if (!norm.startsWith('\\\\?\\') && !norm.startsWith('\\\\.\\')) {
    return true;
  }

  // DOS device namespace: `\\?\UNC\…` or `\\.\UNC\…` is network; other
  // device paths (drive letters) are not.
  const afterPrefix = norm.substring(4);
  return /^UNC\\/i.test(afterPrefix);
}

// True when target escapes boundary, either side is UNC, or any segment is reparse-backed.
// Refuses hostile symlink/junction redirects to victim locations.
export function hasReparsePointOnPath(targetPath: string, boundary: string): boolean {
  if (!targetPath || !boundary) {
    return false;
  }
  if (isNetworkPath(targetPath) || isNetworkPath(boundary)) {
    return true;
  }

  // Resolve absolute paths, then normalize for containment without collapsing drive roots.
  let absTarget: string;
  let absBoundary: string;
  try {
    absTarget = normalizeForContainment(path.resolve(targetPath));
    absBoundary = normalizeForContainment(path.resolve(boundary));
  } catch {
    // If we can't even resolve the paths, treat as unsafe — safer default.
    return true;
  }

  // String-only containment: boundary itself, or boundary + separator.
  const sameAsBoundary = absTarget.toLowerCase() === absBoundary.toLowerCase();
  const boundaryWithSep = absBoundary.endsWith(path.sep) ? absBoundary : absBoundary + path.sep;
  const insideBoundary = absTarget.toLowerCase().startsWith(boundaryWithSep.toLowerCase());
  if (!sameAsBoundary && !insideBoundary) {
    return true;
  }

  // Check boundary first; a reparse boundary would make descendants silently follow it.
  if (isReparseSegment(absBoundary)) {
    return true;
  }
  if (sameAsBoundary) {
    return false;
  }

  // Walk DOWN via lstat so symlinks/junctions aren't followed.
  const rel = absTarget.substring(absBoundary.length);
  const segments = rel.length === 0 ? [] : rel.split(/[\\/]/).filter((s) => s.length > 0);

  let probe = absBoundary;
  for (const seg of segments) {
    probe = path.join(probe, seg);
    if (isReparseSegment(probe)) {
      return true;
    }
  }
  return false;
}

// Trim trailing separators but preserve drive roots: `C:\` → `C:` would make
// `path.join` produce drive-relative `C:foo`, bypassing reparse checks.
function normalizeForContainment(p: string): string {
  const trimmed = p.replace(/[\\/]+$/, '');
  if (trimmed.length === 2 && trimmed[1] === ':') {
    return trimmed + path.sep;
  }
  return trimmed;
}

function isReparseSegment(p: string): boolean {
  let stat: fs.Stats;
  try {
    stat = fs.lstatSync(p);
  } catch (err) {
    // Missing leaf is fine; unknown/permission errors mean we cannot prove safety.
    const code = (err as NodeJS.ErrnoException).code;
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      return false;
    }
    return true;
  }
  return stat.isSymbolicLink();
}

/**
 * Throw if a workspace file or any ancestor is UNC/reparse-backed.
 * Single chokepoint mirroring native lockfile/write guards; `label` names the failing file.
 */
export function assertSafeWorkspaceFile(workspaceDir: string, filePath: string, label: string): void {
  if (isNetworkPath(workspaceDir) || isNetworkPath(filePath)) {
    throw new Error(
      `Refusing to access ${label} at '${filePath}': workspace or target path is a UNC / network path. ` +
        'Use a local drive-letter path.'
    );
  }
  if (hasReparsePointOnPath(filePath, workspaceDir)) {
    throw new Error(
      `Refusing to access ${label} at '${filePath}': the file or one of its ` +
        `ancestors below '${workspaceDir}' is a reparse point (symlink / junction) ` +
        'or the file is outside the workspace. Resolve the link and re-run.'
    );
  }
}

/**
 * Guard recursively deleted output dirs: must be strictly inside the workspace,
 * non-UNC, and reparse-free from workspace through existing output dir.
 */
export function assertSafeWorkspaceOutputDir(workspaceDir: string, outputDir: string, label: string): string {
  if (!outputDir || !outputDir.trim()) {
    throw new Error(`${label} must not be empty.`);
  }
  if (isNetworkPath(workspaceDir) || isNetworkPath(outputDir)) {
    throw new Error(
      `Refusing to use ${label} at '${outputDir}': workspace or output path is a UNC / network path. ` +
        'Use a local drive-letter path.'
    );
  }

  const resolvedOutput = path.isAbsolute(outputDir) ? path.resolve(outputDir) : path.resolve(workspaceDir, outputDir);
  const resolvedWorkspace = normalizeForContainment(path.resolve(workspaceDir));
  const prefix = resolvedWorkspace.endsWith(path.sep) ? resolvedWorkspace : resolvedWorkspace + path.sep;
  const insideWorkspace =
    resolvedOutput.length > prefix.length && resolvedOutput.toLowerCase().startsWith(prefix.toLowerCase());

  if (!insideWorkspace) {
    throw new Error(
      `${label} ('${outputDir}') resolves to '${resolvedOutput}' which is outside the workspace ` +
        `('${resolvedWorkspace}'). The directory is wiped before each run, so it must be a ` +
        "path strictly inside the workspace. Use a relative path like '.winapp/bindings' or an absolute path " +
        'that descends from the workspace root.'
    );
  }

  if (hasReparsePointOnPath(resolvedOutput, resolvedWorkspace)) {
    throw new Error(
      `${label} ('${outputDir}') resolves through a reparse point under '${resolvedWorkspace}'. ` +
        'Reparse points (symlinks / junctions) are rejected because the wipe could follow them ' +
        'outside the workspace. Move the output to a regular subdirectory.'
    );
  }

  return resolvedOutput;
}
