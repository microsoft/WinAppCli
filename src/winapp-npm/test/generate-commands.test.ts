// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';

const npmRoot = process.cwd();
const generator = fs.readFileSync(path.join(npmRoot, 'scripts', 'generate-commands.mjs'), 'utf8');
const generated = fs.readFileSync(path.join(npmRoot, 'src', 'winapp-commands.ts'), 'utf8');

test('target command generation derives target selection from schema metadata', () => {
  assert.doesNotMatch(generator, /TARGET_AWARE_COMMANDS/);
  assert.match(generator, /cmd\.targetAware === true/);
  assert.doesNotMatch(generator, /rootSelectorOption|dropUnsupportedSelector\(cmdPath/);
  assert.match(generated, /export interface RunOptions extends CommonOptions \{[\s\S]*?\n  on\?: string;/);
  assert.doesNotMatch(
    generated.match(/export interface CertGenerateOptions extends CommonOptions \{[\s\S]*?\n\}/)?.[0] ?? '',
    /\n  on\?: string;/,
  );
});

test('target exec generated API snapshots its OneOrMore command contract', () => {
  assert.match(generated, /export interface TargetExecOptions extends CommonOptions \{[\s\S]*command: string \| \[string, \.\.\.string\[\]\];/);
  assert.match(generated, /throw new Error\('targetExec requires a non-empty command\.'\)/);
  assert.match(generated, /args\.push\('--', \.\.\.commandArr\);/);
});
