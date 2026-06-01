// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { isNetworkPath } from '../src/jsbindings/path-safety';

test('isNetworkPath returns false for empty and local drive-letter paths', () => {
  assert.equal(isNetworkPath(''), false);
  assert.equal(isNetworkPath('C:\\Users\\me\\proj'), false);
  assert.equal(isNetworkPath('relative\\path'), false);
});

test('isNetworkPath flags plain UNC paths', () => {
  assert.equal(isNetworkPath('\\\\server\\share'), true);
  assert.equal(isNetworkPath('\\\\server\\share\\sub'), true);
});

test('isNetworkPath normalizes forward slashes before classifying', () => {
  assert.equal(isNetworkPath('//server/share'), true);
});

test('isNetworkPath treats local DOS device paths as non-network', () => {
  assert.equal(isNetworkPath('\\\\?\\C:\\foo'), false);
  assert.equal(isNetworkPath('\\\\.\\C:\\foo'), false);
});

test('isNetworkPath flags DOS-device UNC paths (\\\\?\\UNC\\ and \\\\.\\UNC\\)', () => {
  assert.equal(isNetworkPath('\\\\?\\UNC\\server\\share'), true);
  assert.equal(isNetworkPath('\\\\.\\UNC\\server\\share'), true);
  // Case-insensitive on the UNC token.
  assert.equal(isNetworkPath('\\\\?\\unc\\server\\share'), true);
});
