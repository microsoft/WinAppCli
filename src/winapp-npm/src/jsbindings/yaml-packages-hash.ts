// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// SHA-256 hex over canonical `lower(name)|version` lines from `winapp.yaml` packages.
// Port of native `YamlPackagesHasher`; stale hashes catch package edits before codegen
// emits against the wrong winmd set. The joined UTF-8 string must match C# byte-for-byte:
// ASCII lowercase names, Ordinal dedupe/sort, `\n` joins, no trailing newline.

import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import { assertSafeWorkspaceFile } from './path-safety';

export interface PackagePin {
  name: string;
  version: string;
}

/**
 * Compute the canonical hash. Blank names are skipped; nullish versions become empty.
 * C# golden tests pin parity; update both implementations if canonicalization changes.
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
 * Read `winapp.yaml` package pins with the native hand-rolled grammar (top-level
 * `packages`, `- name`, `version`, inline comments). No full YAML parser; missing
 * or malformed files return null so callers can proceed without drift detection.
 * Reparse-guarded: default uses workspace boundary; explicit yamlPath uses its
 * containing directory, matching native `ConfigService.GuardConfigPath`.
 */
export function readWinappYamlPackages(workspaceDir: string, yamlPath?: string): PackagePin[] | null {
  const defaultPath = path.join(workspaceDir, 'winapp.yaml');
  const resolved = yamlPath ? path.resolve(yamlPath) : defaultPath;

  // Boundary: workspace for default path; explicit paths use their containing dir like native --config-dir.
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
  // Section-scoped by design: tooling-written YAML matches native hashes, while
  // malformed top-level name/version rows aren't mistaken for package pins.
  // If malformed-input parity is required, change both parsers together.
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

/** Sanitize scalars like native output: inline comments, one quote pair, doubled single quotes. */
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
