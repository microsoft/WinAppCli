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

test('askBindingsKind preserves existing config when --add-js-bindings is set (no Overwrite? prompt)', async () => {
  // Regression for the prompt that fired on `--add-js-bindings` + existingJsBindings
  // in interactive contexts. Opt-in means "I want JS bindings" — not "reset my
  // settings" — so preserve without prompting regardless of --use-defaults / TTY.
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: [], // no --use-defaults, simulates CI run that passes the cli.ts gate via env.CI
      isInit: true,
      existingJsBindings: true,
      sdksReady: true,
      addJsBindings: true,
    });

    assert.equal(result.kind, 'yes');
    assert.equal(result.overwriteExistingConfig, false);
  });
});

test('askBindingsKind skips JS bindings under --json (nonInteractive=true) when no existing config', async () => {
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: [],
      isInit: true,
      existingJsBindings: false,
      sdksReady: true,
      nonInteractive: true,
    });

    assert.equal(result.kind, 'no');
    assert.match(result.silentReason ?? '', /--json/);
  });
});

test('askBindingsKind preserves existing JS bindings under --json (no overwrite prompt)', async () => {
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: [],
      isInit: true,
      existingJsBindings: true,
      sdksReady: true,
      nonInteractive: true,
    });

    assert.equal(result.kind, 'yes');
    assert.equal(result.overwriteExistingConfig, false);
    assert.match(result.silentReason ?? '', /--json/);
  });
});

test('askBindingsKind skips silently when SDKs were not set up and opt-in was not requested', async () => {
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: [],
      isInit: true,
      existingJsBindings: false,
      sdksReady: false,
      nonInteractive: true,
    });

    assert.equal(result.kind, 'no');
    assert.equal(result.silentReason, undefined);
  });
});

test('askBindingsKind explains the SDK gap when --add-js-bindings was requested but SDKs were skipped', async () => {
  await withWorkspace(async (dir) => {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: [],
      isInit: true,
      existingJsBindings: false,
      sdksReady: false,
      addJsBindings: true,
      nonInteractive: true,
    });

    assert.equal(result.kind, 'no');
    assert.match(result.silentReason ?? '', /--add-js-bindings/);
  });
});

test('askBindingsKind throws when --add-js-bindings is set but package.json is missing', async () => {
  // No package.json in the temp dir — explicit opt-in must surface as an error
  // so CI/automation distinguishes "could not honor request" from "skipped".
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-init-prompt-nopkg-'));
  try {
    await assert.rejects(
      () =>
        askBindingsKind({
          workspaceDir: dir,
          argv: [],
          isInit: true,
          existingJsBindings: false,
          sdksReady: true,
          addJsBindings: true,
          nonInteractive: true,
        }),
      /no package\.json/i
    );
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('askBindingsKind silently skips when package.json is missing and --add-js-bindings was not requested', async () => {
  // Without opt-in, missing package.json is a normal "this is not an npm project" skip.
  // Stay fully silent — the user didn't ask for JS bindings, so reminding them
  // "this only applies to npm projects" is noise during non-Node init flows.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-init-prompt-nopkg-silent-'));
  try {
    const result = await askBindingsKind({
      workspaceDir: dir,
      argv: [],
      isInit: true,
      existingJsBindings: false,
      sdksReady: true,
      nonInteractive: true,
    });

    assert.equal(result.kind, 'no');
    assert.equal(result.silentReason, undefined);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
