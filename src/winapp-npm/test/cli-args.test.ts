// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as path from 'path';

import {
  resolveWorkspaceDir,
  firstPositional,
  isVerbose,
  isQuiet,
  hasConfigOnly,
  hasNoInstall,
  hasUseDefaults,
  resolveYamlPath,
} from '../src/cli-args';

test('firstPositional returns the first non-option token', () => {
  assert.equal(firstPositional(['init', '--use-defaults']), 'init');
  assert.equal(firstPositional(['--quiet', 'myproj']), 'myproj');
});

test('firstPositional skips the value of a value-taking option (space form)', () => {
  // `--config-dir somedir` — `somedir` is the option value, not a positional.
  assert.equal(firstPositional(['--config-dir', 'somedir', 'realpos']), 'realpos');
});

test('firstPositional does not skip a token after an `--opt=value` form', () => {
  assert.equal(firstPositional(['--config-dir=somedir', 'realpos']), 'realpos');
});

test('firstPositional returns undefined when only options are present', () => {
  assert.equal(firstPositional(['--quiet', '--verbose']), undefined);
});

test('resolveWorkspaceDir resolves the first positional to an absolute path', () => {
  const got = resolveWorkspaceDir(['sub/dir']);
  assert.equal(got, path.resolve('sub/dir'));
});

test('resolveWorkspaceDir falls back to cwd when there is no positional', () => {
  assert.equal(resolveWorkspaceDir(['--quiet']), process.cwd());
});

test('boolean flag helpers detect their flags anywhere in argv', () => {
  assert.equal(isVerbose(['a', '--verbose']), true);
  assert.equal(isVerbose(['a', '-v']), true);
  assert.equal(isVerbose(['a']), false);

  assert.equal(isQuiet(['--quiet']), true);
  assert.equal(isQuiet(['-q']), true);
  assert.equal(isQuiet([]), false);

  assert.equal(hasConfigOnly(['--config-only']), true);
  assert.equal(hasConfigOnly([]), false);

  assert.equal(hasNoInstall(['init', '--no-install']), true);
  assert.equal(hasNoInstall(['init']), false);
});

test('hasUseDefaults recognises every accepted spelling', () => {
  for (const flag of ['--use-defaults', '--no-prompt', '-y', '--yes']) {
    assert.equal(hasUseDefaults(['init', flag]), true, `expected ${flag} to count as use-defaults`);
  }
  assert.equal(hasUseDefaults(['init']), false);
});

test('resolveYamlPath honours --config-dir (space and = forms)', () => {
  assert.equal(resolveYamlPath(['--config-dir', 'cfg']), path.join(path.resolve('cfg'), 'winapp.yaml'));
  assert.equal(resolveYamlPath(['--config-dir=cfg']), path.join(path.resolve('cfg'), 'winapp.yaml'));
});

test('resolveYamlPath defaults to <cwd>/winapp.yaml without --config-dir', () => {
  // A positional base-dir must NOT change where the yaml is read from.
  assert.equal(resolveYamlPath(['init', 'someBaseDir']), path.join(process.cwd(), 'winapp.yaml'));
});
