// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Preflight checks for `node generate-bindings`, kept pure for unit tests.

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

/** Return the first missing prerequisite for passive binding generation. */
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
