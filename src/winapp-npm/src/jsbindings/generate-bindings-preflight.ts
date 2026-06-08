// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Preflight checks for `node generate-bindings`. Extracted from the CLI
// dispatcher so the three failure branches (missing package.json, missing
// `winapp.jsBindings`, missing winmd lockfile) can be unit-tested without
// spawning the CLI or stubbing `process.exit`. The dispatcher maps a non-`ok`
// result to stderr output + exit code 1.

import * as fs from 'fs';
import * as path from 'path';
import { hasJsBindings } from './package-json-config';
import { getLockfilePath, LOCKFILE_NAME } from './lockfile-reader';
import { assertSafeWorkspaceFile } from './path-safety';

export type GenerateBindingsPreflightKind = 'ok' | 'noPackageJson' | 'noJsBindings' | 'noLockfile';

export interface GenerateBindingsPreflightResult {
  kind: GenerateBindingsPreflightKind;
  /** Actionable lines to print to stderr; empty when `kind === 'ok'`. */
  messageLines: string[];
}

/**
 * Evaluate, in order, the prerequisites `node generate-bindings` needs:
 *   1. package.json exists (winapp.jsBindings lives there).
 *   2. The `winapp.jsBindings` namespace is already declared (added by `init`;
 *      this command never writes it).
 *   3. The `.winapp/<lockfile>` from a prior `winapp restore` is present.
 *
 * Pure aside from filesystem reads — no process exit, no stdout/stderr — so
 * callers own how failures surface.
 */
export function evaluateGenerateBindingsPreflight(workspaceDir: string): GenerateBindingsPreflightResult {
  const pkgJsonPath = path.join(workspaceDir, 'package.json');
  assertSafeWorkspaceFile(workspaceDir, pkgJsonPath, 'package.json');
  if (!fs.existsSync(pkgJsonPath)) {
    return {
      kind: 'noPackageJson',
      messageLines: [
        '❌ No package.json found in this directory.',
        '   This command only applies to npm/Node projects.',
        '   Run `npm init -y` first, then re-run this command.',
      ],
    };
  }

  if (!hasJsBindings(workspaceDir)) {
    return {
      kind: 'noJsBindings',
      messageLines: [
        '❌ No "winapp.jsBindings" namespace in package.json.',
        '   Run `winapp init` to opt into JS bindings (it adds the block and the',
        '   @microsoft/dynwinrt dependency), then re-run this command to regenerate.',
      ],
    };
  }

  const lockfilePath = getLockfilePath(workspaceDir);
  assertSafeWorkspaceFile(workspaceDir, lockfilePath, LOCKFILE_NAME);
  if (!fs.existsSync(lockfilePath)) {
    return {
      kind: 'noLockfile',
      messageLines: [
        `❌ No .winapp/${LOCKFILE_NAME} found.`,
        '   This file is written by `winapp restore`. If you cloned a fresh repo,',
        '   or upgraded from an older winapp that did not write this lockfile,',
        '   run `winapp restore` once to build the winmd inventory, then re-run this command.',
      ],
    };
  }

  return { kind: 'ok', messageLines: [] };
}
