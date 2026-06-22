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
  hasAddJsBindings,
  hasUseDefaults,
  resolveYamlPath,
  stripWrapperOnlyFlags,
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

  assert.equal(hasAddJsBindings(['init', '--add-js-bindings']), true);
  assert.equal(hasAddJsBindings(['init']), false);
});

test('hasUseDefaults recognises every accepted spelling', () => {
  for (const flag of ['--use-defaults', '--no-prompt']) {
    assert.equal(hasUseDefaults(['init', flag]), true, `expected ${flag} to count as use-defaults`);
  }
  assert.equal(hasUseDefaults(['init', '-y']), false);
  assert.equal(hasUseDefaults(['init', '--yes']), false);
  assert.equal(hasUseDefaults(['init']), false);
});

test('stripWrapperOnlyFlags removes wrapper-only init flags', () => {
  assert.deepEqual(stripWrapperOnlyFlags(['.', '--add-js-bindings']), ['.']);
});

test('resolveYamlPath honours --config-dir (space and = forms)', () => {
  assert.equal(resolveYamlPath(['--config-dir', 'cfg']), path.join(path.resolve('cfg'), 'winapp.yaml'));
  assert.equal(resolveYamlPath(['--config-dir=cfg']), path.join(path.resolve('cfg'), 'winapp.yaml'));
});

test('resolveYamlPath defaults to <cwd>/winapp.yaml without --config-dir', () => {
  // restore / generate-bindings default the config dir to cwd; a positional
  // base-dir must NOT change where the yaml is read from for those commands.
  assert.equal(resolveYamlPath(['init', 'someBaseDir']), path.join(process.cwd(), 'winapp.yaml'));
});

test('resolveYamlPath uses the supplied defaultConfigDir when no --config-dir', () => {
  // init passes workspaceDir so `winapp init <base-dir>` hashes the yaml native
  // actually wrote (remapped to the selected directory).
  const baseDir = path.resolve('someBaseDir');
  assert.equal(resolveYamlPath(['init', 'someBaseDir'], baseDir), path.join(baseDir, 'winapp.yaml'));
});

test('resolveYamlPath: explicit --config-dir overrides the defaultConfigDir', () => {
  assert.equal(resolveYamlPath(['init', 'base'], path.resolve('base')), path.join(path.resolve('base'), 'winapp.yaml'));
  assert.equal(
    resolveYamlPath(['--config-dir', 'cfg'], path.resolve('base')),
    path.join(path.resolve('cfg'), 'winapp.yaml')
  );
});
