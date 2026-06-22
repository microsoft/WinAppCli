// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { computeYamlPackagesHash, parsePackagesFromYaml } from '../src/jsbindings/yaml-packages-hash';

test('computeYamlPackagesHash matches the cross-language golden fixture', () => {
  // Pinned byte-for-byte against the C# YamlPackagesHasherTests golden fixture.
  const hash = computeYamlPackagesHash([
    { name: 'Microsoft.WindowsAppSDK', version: '2.1.3' },
    { name: 'Microsoft.Windows.SDK.CPP', version: '10.0.28000.1839' },
  ]);
  assert.equal(hash, '8581abfcb53fa04056a066fc7098c5d94064cc275e20f0e547365c1b8b146e54');
});

test('computeYamlPackagesHash is order-independent (canonical sort)', () => {
  const a = computeYamlPackagesHash([
    { name: 'A.Pkg', version: '1.0' },
    { name: 'B.Pkg', version: '2.0' },
  ]);
  const b = computeYamlPackagesHash([
    { name: 'B.Pkg', version: '2.0' },
    { name: 'A.Pkg', version: '1.0' },
  ]);
  assert.equal(a, b);
});

test('computeYamlPackagesHash lowercases names and dedupes', () => {
  const mixedCase = computeYamlPackagesHash([{ name: 'Microsoft.Foo', version: '1.0' }]);
  const lower = computeYamlPackagesHash([{ name: 'microsoft.foo', version: '1.0' }]);
  assert.equal(mixedCase, lower);

  const single = computeYamlPackagesHash([{ name: 'Dup', version: '1' }]);
  const duped = computeYamlPackagesHash([
    { name: 'Dup', version: '1' },
    { name: 'dup', version: '1' },
  ]);
  assert.equal(single, duped);
});

test('computeYamlPackagesHash skips whitespace-only names and treats missing version as empty', () => {
  const withBlank = computeYamlPackagesHash([
    { name: '   ', version: '9' },
    { name: 'Real', version: '' },
  ]);
  const onlyReal = computeYamlPackagesHash([{ name: 'Real', version: '' }]);
  assert.equal(withBlank, onlyReal);
});

test('parsePackagesFromYaml extracts name/version pairs from the packages block', () => {
  const yaml = [
    'sdk: stable',
    'packages:',
    '  - name: Microsoft.WindowsAppSDK',
    '    version: 2.1.3',
    '  - name: Microsoft.Windows.SDK.CPP',
    '    version: 10.0.28000.1839',
  ].join('\n');
  assert.deepEqual(parsePackagesFromYaml(yaml), [
    { name: 'Microsoft.WindowsAppSDK', version: '2.1.3' },
    { name: 'Microsoft.Windows.SDK.CPP', version: '10.0.28000.1839' },
  ]);
});

test('parsePackagesFromYaml ignores entries outside the packages block', () => {
  const yaml = [
    'other:',
    '  - name: ShouldBeIgnored',
    '    version: 0.0.0',
    'packages:',
    '  - name: Kept',
    '    version: 1.2.3',
  ].join('\n');
  assert.deepEqual(parsePackagesFromYaml(yaml), [{ name: 'Kept', version: '1.2.3' }]);
});

test('parsePackagesFromYaml strips inline comments and surrounding quotes from scalars', () => {
  const yaml = [
    'packages:',
    '  - name: Microsoft.WindowsAppSDK # the main SDK',
    "    version: '2.1.3'",
    '  - name: "Quoted.Pkg"',
    '    version: 4.5.6   # trailing comment',
  ].join('\n');
  assert.deepEqual(parsePackagesFromYaml(yaml), [
    { name: 'Microsoft.WindowsAppSDK', version: '2.1.3' },
    { name: 'Quoted.Pkg', version: '4.5.6' },
  ]);
});

test('parsePackagesFromYaml returns an empty list when there is no packages block', () => {
  assert.deepEqual(parsePackagesFromYaml('sdk: stable\n'), []);
});

test('parsePackagesFromYaml accepts `- version:` as the first item key (cross-parser parity)', () => {
  // YAML lets the dashed item key be either field; the C# parser handles both,
  // so the TS parser must too — otherwise the yaml hash drifts and triggers a
  // false "lockfile stale" warning.
  const yaml = [
    'packages:',
    '  - version: 2.1.3',
    '    name: Microsoft.WindowsAppSDK',
    '  - version: 10.0.28000.1839',
    '    name: Microsoft.Windows.SDK.CPP',
  ].join('\n');
  assert.deepEqual(parsePackagesFromYaml(yaml), [
    { name: 'Microsoft.WindowsAppSDK', version: '2.1.3' },
    { name: 'Microsoft.Windows.SDK.CPP', version: '10.0.28000.1839' },
  ]);
});

test('parsePackagesFromYaml hash matches whether name- or version-first', () => {
  // Same packages, mirrored shape: hashes must be identical.
  const nameFirst = parsePackagesFromYaml(
    ['packages:', '  - name: Foo', '    version: 1.0', '  - name: Bar', '    version: 2.0'].join('\n')
  );
  const versionFirst = parsePackagesFromYaml(
    ['packages:', '  - version: 1.0', '    name: Foo', '  - version: 2.0', '    name: Bar'].join('\n')
  );
  assert.equal(computeYamlPackagesHash(nameFirst), computeYamlPackagesHash(versionFirst));
});
