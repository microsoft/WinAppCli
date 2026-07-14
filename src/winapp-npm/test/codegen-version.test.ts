// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { supportsPackageImports } from '../src/jsbindings/codegen-runner';

test('package imports require dynwinrt-codegen preview.8 or newer', () => {
  assert.equal(supportsPackageImports('0.1.0-preview.6'), false);
  assert.equal(supportsPackageImports('0.1.0-preview.7'), false);
  assert.equal(supportsPackageImports('0.1.0-preview.8'), true);
  assert.equal(supportsPackageImports('0.1.0-preview.10'), true);
  assert.equal(supportsPackageImports('0.1.0'), true);
  assert.equal(supportsPackageImports('0.1.0+build'), true);
  assert.equal(supportsPackageImports('0.1.1-preview.1'), true);
  assert.equal(supportsPackageImports('0.2.0-preview.1'), true);
  assert.equal(supportsPackageImports('1.0.0'), true);
});

test('package imports reject unknown or unrelated prerelease versions', () => {
  assert.equal(supportsPackageImports(null), false);
  assert.equal(supportsPackageImports('invalid'), false);
  assert.equal(supportsPackageImports('0.0.9'), false);
  assert.equal(supportsPackageImports('0.1.0-beta.1'), false);
});
