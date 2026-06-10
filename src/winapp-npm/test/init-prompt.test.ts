// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import { askBindingsKind } from '../src/jsbindings/init-prompt';

function withWorkspace(fn: (dir: string) => Promise<void> | void): Promise<void> | void {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-init-prompt-'));
  fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify({ name: 'app', version: '1.0.0' }));
  try {
    return fn(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test('askBindingsKind skips new JS bindings when init uses defaults', async () => {
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: ['--use-defaults'],
      isInit: true,
      existingJsBindings: false,
      sdksReady: true,
    });

    assert.equal(result.kind, 'no');
  });
});

test('askBindingsKind opts in when --add-js-bindings is explicit', async () => {
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: ['--use-defaults'],
      isInit: true,
      existingJsBindings: false,
      sdksReady: true,
      addJsBindings: true,
    });

    assert.equal(result.kind, 'yes');
  });
});
