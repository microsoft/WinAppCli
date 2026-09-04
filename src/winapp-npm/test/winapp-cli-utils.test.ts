// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { afterEach, mock, test } from 'node:test';
import * as assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import childProcess = require('child_process');

import { callWinappCliCapture } from '../src/winapp-cli-utils';

type FakeChild = EventEmitter & {
  stdout: EventEmitter;
  stderr: EventEmitter;
  stdin: { end: () => void };
};

function createFakeChild(onEnd: (child: FakeChild) => void): FakeChild {
  const child = new EventEmitter() as FakeChild;
  child.stdout = new EventEmitter();
  child.stderr = new EventEmitter();
  child.stdin = { end: () => onEnd(child) };
  return child;
}

function mockSpawn(create: () => FakeChild): void {
  mock.method(childProcess, 'spawn', (() => create()) as unknown as typeof childProcess.spawn);
}

afterEach(() => {
  mock.restoreAll();
});

test('capture API closes stdin so a native CLI waiting for EOF completes', async () => {
  let stdinClosed = false;
  mockSpawn(() => createFakeChild((child) => {
    stdinClosed = true;
    child.stdout.emit('data', Buffer.from('complete'));
    child.emit('close', 0);
  }));

  const result = await callWinappCliCapture(['fake-waits-for-eof']);

  assert.equal(stdinClosed, true);
  assert.deepEqual(result, { exitCode: 0, stdout: 'complete', stderr: '' });
});

test('capture API preserves nonzero native exit output', async () => {
  mockSpawn(() => createFakeChild((child) => {
    child.stdout.emit('data', Buffer.from('native output'));
    child.stderr.emit('data', Buffer.from('native error'));
    child.emit('close', 70);
  }));

  await assert.rejects(callWinappCliCapture(['fake-infrastructure-error']), (error: unknown) => {
    assert.ok(error instanceof Error);
    const captureError = error as Error & { exitCode: number; stdout: string; stderr: string };
    assert.equal(captureError.exitCode, 70);
    assert.equal(captureError.stdout, 'native output');
    assert.equal(captureError.stderr, 'native error');
    return true;
  });
});

test('capture API keeps a cancelled native process nonzero', async () => {
  mockSpawn(() => createFakeChild((child) => {
    child.emit('close', null, 'SIGTERM');
  }));

  await assert.rejects(callWinappCliCapture(['fake-cancelled']), /winapp-cli exited with code 1/);
});

test('capture API reports a native process launch failure', async () => {
  mockSpawn(() => createFakeChild((child) => {
    child.emit('error', new Error('ENOENT'));
  }));

  await assert.rejects(callWinappCliCapture(['missing-native-cli']), /Failed to execute winapp-cli: ENOENT/);
});
