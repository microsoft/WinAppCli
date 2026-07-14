// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import { getCodegenPackageVersion } from '../src/jsbindings/codegen-runner';

function makeWorkspace(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-codegen-version-'));
}

function writeCodegenPackage(workspaceDir: string, packageJson: string | null): void {
  const packageDir = path.join(workspaceDir, 'node_modules', '@microsoft', 'dynwinrt-codegen');
  fs.mkdirSync(packageDir, { recursive: true });
  fs.writeFileSync(path.join(packageDir, 'cli.js'), '#!/usr/bin/env node\n');
  if (packageJson !== null) {
    fs.writeFileSync(path.join(packageDir, 'package.json'), packageJson);
  }
}

test('getCodegenPackageVersion returns the package.json version string', () => {
  const workspace = makeWorkspace();
  try {
    writeCodegenPackage(workspace, JSON.stringify({ name: '@microsoft/dynwinrt-codegen', version: '0.1.0-preview.8' }));
    assert.equal(getCodegenPackageVersion(workspace), '0.1.0-preview.8');
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});

test('getCodegenPackageVersion returns null when version is missing', () => {
  const workspace = makeWorkspace();
  try {
    writeCodegenPackage(workspace, JSON.stringify({ name: '@microsoft/dynwinrt-codegen' }));
    assert.equal(getCodegenPackageVersion(workspace), null);
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});

test('getCodegenPackageVersion returns null when version is not a string', () => {
  const workspace = makeWorkspace();
  try {
    writeCodegenPackage(workspace, JSON.stringify({ name: '@microsoft/dynwinrt-codegen', version: 1 }));
    assert.equal(getCodegenPackageVersion(workspace), null);
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});

test('getCodegenPackageVersion throws when package.json is malformed', () => {
  const workspace = makeWorkspace();
  try {
    writeCodegenPackage(workspace, '{');
    assert.throws(() => getCodegenPackageVersion(workspace), SyntaxError);
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});

test('getCodegenPackageVersion throws when package.json is missing', () => {
  const workspace = makeWorkspace();
  try {
    writeCodegenPackage(workspace, null);
    assert.throws(() => getCodegenPackageVersion(workspace), /package\.json/);
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});
