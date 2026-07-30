// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { buildWindowsCmdLine } from '../src/jsbindings/runtime-installer';

test('buildWindowsCmdLine wraps the whole command in an outer quote pair', () => {
  // cmd.exe /s strips the first & last quote of the command string, so the
  // outer pair must be present for the inner exe-path quotes (needed for spaces
  // in "C:\Program Files\...") to survive.
  const line = buildWindowsCmdLine('C:\\Program Files\\nodejs\\npm.cmd', ['install', 'pkg@1.2.3', '--save-exact']);
  assert.equal(line, '""C:\\Program Files\\nodejs\\npm.cmd" install pkg@1.2.3 --save-exact"');
});

test('buildWindowsCmdLine quotes args containing whitespace or cmd metacharacters', () => {
  const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', ['install', 'pkg@1.0.0 beta']);
  assert.equal(line, '""C:\\tools\\npm.cmd" install "pkg@1.0.0 beta""');
});

test('buildWindowsCmdLine quotes args containing &, |, <, >, (, ), %, ! (cmd.exe metacharacters)', () => {
  // Each of these characters triggers cmd.exe parsing outside of a quoted region.
  // The implementation must wrap them so they are treated as literal text by the
  // child program rather than as shell operators.
  for (const meta of ['&', '|', '<', '>', '(', ')', '%', '!', '^']) {
    const arg = `pkg${meta}name`;
    const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', ['install', arg]);
    assert.equal(line, `""C:\\tools\\npm.cmd" install "${arg}""`, `metacharacter '${meta}' should trigger quoting`);
  }
});

test('buildWindowsCmdLine doubles embedded double quotes so cmd.exe sees them as literal', () => {
  // The closing quote of the wrap must not collide with a quote embedded in the arg.
  // Input arg `name="x"` → quoteForCmd doubles each `"` → `name=""x""` → wraps → `"name=""x"""`.
  // buildWindowsCmdLine then wraps the inner command in its outermost pair.
  const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', ['install', 'name="x"']);
  assert.equal(line, '""C:\\tools\\npm.cmd" install "name=""x""""');
});

test('buildWindowsCmdLine quotes a literal "foo & echo INJECTED" attempt as a single arg', () => {
  // Regression for the injection primitive review: an attacker-controlled
  // arg with `&` must not break out of the argv slot.
  const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', ['install', 'foo & echo INJECTED']);
  // The whole string is wrapped in quotes, so cmd.exe treats `&` as text.
  assert.equal(line, '""C:\\tools\\npm.cmd" install "foo & echo INJECTED""');
});

test('buildWindowsCmdLine leaves args without whitespace or metachars unquoted (smoke against over-quoting)', () => {
  const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', [
    'install',
    '@scope/package-name',
    '--save-exact',
    '--no-fund',
  ]);
  assert.equal(line, '""C:\\tools\\npm.cmd" install @scope/package-name --save-exact --no-fund"');
});

test('buildWindowsCmdLine wraps args containing %VAR% so the literal token survives cmd.exe parsing', () => {
  // Note: wrapping does NOT stop cmd.exe %VAR% expansion — the real defense is
  // upstream input policy. This test pins the wrap so any future caller passing
  // `%USERNAME%`-like text still gets a quoted, single-arg payload.
  const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', ['install', '%USERNAME%']);
  assert.match(line, /install "%USERNAME%""$/);
});
