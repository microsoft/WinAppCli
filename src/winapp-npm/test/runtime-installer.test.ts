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

test('buildWindowsCmdLine leaves simple args unquoted', () => {
  const line = buildWindowsCmdLine('C:\\tools\\npm.cmd', ['install', 'pkg@1.0.0', '--save-exact']);
  assert.equal(line, '""C:\\tools\\npm.cmd" install pkg@1.0.0 --save-exact"');
});
