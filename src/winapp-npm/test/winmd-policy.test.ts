// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { classifyPackage, partitionPackageWinmds } from '../src/jsbindings/winmd-policy';

test('classifyPackage defaults unknown packages to emit', () => {
  assert.equal(classifyPackage('Microsoft.Windows.SDK.NET'), 'emit');
  assert.equal(classifyPackage('Some.Vendor.Package'), 'emit');
});

test('classifyPackage marks SDK.CPP and InteractiveExperiences as refOnly', () => {
  assert.equal(classifyPackage('Microsoft.Windows.SDK.CPP'), 'refOnly');
  assert.equal(classifyPackage('Microsoft.WindowsAppSDK.InteractiveExperiences'), 'refOnly');
});

test('classifyPackage skips WinUI and WebView2 packages', () => {
  assert.equal(classifyPackage('Microsoft.WindowsAppSDK.WinUI'), 'skip');
  assert.equal(classifyPackage('Microsoft.Web.WebView2'), 'skip');
});

test('classifyPackage is case-insensitive', () => {
  assert.equal(classifyPackage('microsoft.windows.sdk.cpp'), 'refOnly');
  assert.equal(classifyPackage('microsoft.windowsappsdk.winui'), 'skip');
  assert.equal(classifyPackage('MICROSOFT.WEB.WEBVIEW2'), 'skip');
  assert.equal(classifyPackage('microsoft.windowsappsdk.interactiveexperiences'), 'refOnly');
});

test('classifyPackage treats empty / whitespace IDs as emit', () => {
  assert.equal(classifyPackage(''), 'emit');
  assert.equal(classifyPackage('   '), 'emit');
});

test('partitionPackageWinmds buckets winmds by package category', () => {
  const result = partitionPackageWinmds([
    { name: 'Microsoft.WindowsAppSDK', winmds: ['a.winmd', 'b.winmd'] },
    { name: 'Microsoft.WindowsAppSDK.InteractiveExperiences', winmds: ['ie.winmd'] },
    { name: 'Microsoft.Windows.SDK.CPP', winmds: ['sdk.winmd'] },
    { name: 'Microsoft.WindowsAppSDK.WinUI', winmds: ['winui.winmd'] },
    { name: 'Microsoft.Web.WebView2', winmds: ['wv2.winmd'] },
  ]);

  assert.deepEqual(result.emit, ['a.winmd', 'b.winmd']);
  assert.deepEqual(result.refOnly, ['ie.winmd', 'sdk.winmd']);
  assert.deepEqual(result.skipped, ['winui.winmd', 'wv2.winmd']);
});

test('partitionPackageWinmds ignores packages with no name or no winmds', () => {
  const result = partitionPackageWinmds([
    { name: '', winmds: ['orphan.winmd'] },
    { name: 'Microsoft.WindowsAppSDK', winmds: [] },
    { name: 'Microsoft.Windows.SDK.NET', winmds: ['kept.winmd'] },
  ]);

  assert.deepEqual(result.emit, ['kept.winmd']);
  assert.deepEqual(result.refOnly, []);
  assert.deepEqual(result.skipped, []);
});

test('partitionPackageWinmds returns empty buckets for empty input', () => {
  const result = partitionPackageWinmds([]);
  assert.deepEqual(result, { emit: [], refOnly: [], skipped: [] });
});
