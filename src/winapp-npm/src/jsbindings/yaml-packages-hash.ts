// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// SHA-256 hex over canonicalized `lower(name)|version` lines from the
// workspace's `winapp.yaml` `packages:` block.
//
// Port of native `WinApp.Cli.Services.YamlPackagesHasher` — used as a
// staleness signal against the lockfile's `yaml_packages_hash`. If the user
// edits `winapp.yaml` (adds / removes / re-pins an SDK package) without
// re-running `winapp restore`, the orchestrator detects the drift and asks
// them to restore before generating bindings — otherwise we'd emit JS
// bindings for the wrong winmd set.
//
// The C# side computes the hash with `SHA256.HashData(UTF8(joined))`; the
// canonical form is the *exact* string we must reproduce here byte-for-byte:
//   * each `name` lowercased via OrdinalIgnoreCase semantics
//     (we use `.toLowerCase()` — both implementations match for ASCII, and
//      every NuGet package id we accept is ASCII per NuGet's grammar)
//   * pairs deduped (Ordinal compare)
//   * sorted Ordinal-ascending
//   * lines joined with `\n` (no trailing newline)
//   * UTF-8 encoded, SHA-256, hex (lower-case)

import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import { assertSafeWorkspaceFile } from './path-safety';

export interface PackagePin {
  name: string;
  version: string;
}

/**
 * Compute the canonical hash for a sequence of name/version pairs.
 * Whitespace-only names are skipped (matching C#'s `IsNullOrWhiteSpace`).
 * `null`/`undefined` versions canonicalize to the empty string.
 */
export function computeYamlPackagesHash(packages: Iterable<PackagePin>): string {
  const lines = new Set<string>();
  for (const p of packages) {
    if (!p || typeof p.name !== 'string' || p.name.trim().length === 0) {
      continue;
    }
    const version = typeof p.version === 'string' ? p.version : '';
    lines.add(`${p.name.toLowerCase()}|${version}`);
  }
  const sorted = [...lines].sort();
  const joined = sorted.join('\n');
  return crypto.createHash('sha256').update(joined, 'utf8').digest('hex');
}

/**
 * Read `winapp.yaml` and extract its `packages:` pins using a tiny line-based
 * scanner. Mirrors the native CLI's hand-rolled grammar in
 * {@link ../../winapp-CLI/WinApp.Cli/Services/WinappConfigDocument.cs}:
 *
 *   * top-level key with optional inline `# comment`
 *   * list entries via `- name: <scalar>` then `version: <scalar>`
 *   * unknown top-level keys reset the parser (children are ignored)
 *
 * We deliberately do NOT pull in a full YAML parser — this routine runs on
 * every restore / generate-bindings and the grammar is intentionally tiny.
 * Falls back to `null` (rather than throwing) when the file is missing or
 * malformed; the caller treats that as "can't detect drift" and proceeds.
 *
 * Path safety:
 *   * Always reparse-point-guarded. Default location (`<workspaceDir>/winapp.yaml`)
 *     uses the workspace itself as the trust boundary. Explicit `yamlPath`
 *     (e.g. user passed `--config-dir ../other`) uses the file's containing
 *     directory as the boundary — this mirrors the native CLI's
 *     `ConfigService.GuardConfigPath` (boundary = `ConfigPath.DirectoryName`),
 *     so a workspace-internal junction/symlink redirecting to an attacker
 *     path is refused regardless of whether the caller pointed at the
 *     default location or an explicit `--config-dir`.
 */
export function readWinappYamlPackages(workspaceDir: string, yamlPath?: string): PackagePin[] | null {
  const defaultPath = path.join(workspaceDir, 'winapp.yaml');
  const resolved = yamlPath ? path.resolve(yamlPath) : defaultPath;

  // Trust boundary: workspace for the default path, file's containing
  // directory for an explicit path. The latter matches the native
  // ConfigService's `ConfigPath.DirectoryName` boundary, which is also
  // the boundary `--config-dir` legitimately escapes out of.
  const safetyBoundary = resolved === defaultPath ? workspaceDir : path.dirname(resolved);
  assertSafeWorkspaceFile(safetyBoundary, resolved, 'winapp.yaml');

  if (!fs.existsSync(resolved)) {
    return null;
  }
  let raw: string;
  try {
    raw = fs.readFileSync(resolved, 'utf8');
  } catch {
    return null;
  }
  return parsePackagesFromYaml(raw);
}

/** Visible for unit tests. */
export function parsePackagesFromYaml(yaml: string): PackagePin[] {
  const lines = yaml.split(/\r?\n/);
  const packages: PackagePin[] = [];
  let inPackages = false;
  let currentName: string | null = null;
  for (const rawLine of lines) {
    const indent = leadingSpaces(rawLine);
    const t = rawLine.trim();
    if (t.length === 0 || t.startsWith('#')) {
      continue;
    }
    if (indent === 0) {
      // Top-level key boundary.
      inPackages = isTopLevelKey(t, 'packages:');
      currentName = null;
      continue;
    }
    if (!inPackages) {
      continue;
    }
    // List item or bare `name:` / `version:` row.
    const dashName = matchPrefixCaseInsensitive(t, '- name:');
    if (dashName !== null) {
      currentName = sanitizeScalar(dashName);
      continue;
    }
    const bareName = matchPrefixCaseInsensitive(t, 'name:');
    if (bareName !== null) {
      currentName = sanitizeScalar(bareName);
      continue;
    }
    const version = matchPrefixCaseInsensitive(t, 'version:');
    if (version !== null && currentName !== null) {
      packages.push({ name: currentName, version: sanitizeScalar(version) });
      currentName = null;
    }
  }
  return packages;
}

function leadingSpaces(line: string): number {
  let i = 0;
  while (i < line.length && line[i] === ' ') {
    i++;
  }
  return i;
}

function isTopLevelKey(trimmed: string, key: string): boolean {
  if (!trimmed.toLowerCase().startsWith(key.toLowerCase())) {
    return false;
  }
  if (trimmed.length === key.length) {
    return true;
  }
  const rest = trimmed.slice(key.length).trimStart();
  return rest.length === 0 || rest.startsWith('#');
}

function matchPrefixCaseInsensitive(trimmed: string, prefix: string): string | null {
  if (trimmed.toLowerCase().startsWith(prefix.toLowerCase())) {
    return trimmed.slice(prefix.length);
  }
  return null;
}

/**
 * Strip inline `# comment` from an unquoted scalar; peel a single pair of
 * matching surrounding quotes; un-double `''` inside single-quoted scalars.
 * Port of `WinappConfigDocument.SanitizeScalar` — we only handle what the
 * native renderer actually emits, not the full YAML 1.2 scalar grammar.
 */
function sanitizeScalar(raw: string): string {
  if (!raw) {
    return '';
  }
  const trimmed = raw.replace(/^\s+/, '');
  if (trimmed.length === 0) {
    return '';
  }
  const opener = trimmed[0] === '"' || trimmed[0] === "'" ? trimmed[0] : null;
  let cutoff = trimmed.length;
  let inSingle = false;
  let inDouble = false;
  const trackQuoteState = opener !== null;
  for (let i = 0; i < trimmed.length; i++) {
    const c = trimmed[i];
    if (trackQuoteState) {
      if (inDouble) {
        if (c === '\\' && i + 1 < trimmed.length) {
          i++;
          continue;
        }
        if (c === '"') {
          inDouble = false;
        }
        continue;
      }
      if (inSingle) {
        if (c === "'") {
          inSingle = false;
        }
        continue;
      }
      if (c === '"') {
        inDouble = true;
        continue;
      }
      if (c === "'") {
        inSingle = true;
        continue;
      }
    }
    if (c === '#') {
      // YAML requires whitespace before an unquoted inline comment.
      if (i === 0 || /\s/.test(trimmed[i - 1])) {
        cutoff = i;
        break;
      }
    }
  }
  const value = trimmed.slice(0, cutoff).replace(/\s+$/, '');
  if (value.length >= 2 && opener && value[0] === opener && value[value.length - 1] === opener) {
    const inner = value.slice(1, -1);
    if (opener === "'") {
      return inner.replace(/''/g, "'");
    }
    return inner;
  }
  return value;
}
