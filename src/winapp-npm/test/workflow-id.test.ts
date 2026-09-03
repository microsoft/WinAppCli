// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { WINAPP_UI_WORKFLOW_ID } from '../src/winapp-cli-utils';
import { uiListWindows } from '../src/winapp-commands';

// Cooperative desktop turns group commands by workflow id. The wrapper must pass that id to the
// spawned child ONLY: writing it into process.env would silently enrol every later call in this
// process — including unrelated ones — into a workflow the caller meant for one command, and would
// race across concurrent calls.

test('workflowId is not written into the parent process environment', async () => {
  const before = process.env[WINAPP_UI_WORKFLOW_ID];

  // The CLI binary is not present in a unit-test checkout, so the call fails; the assertion is about
  // what the wrapper did to this process before spawning, which happens either way.
  await uiListWindows({ workflowId: 'unit-test-workflow' }).catch(() => undefined);

  assert.equal(
    process.env[WINAPP_UI_WORKFLOW_ID],
    before,
    'the wrapper must never mutate process.env — the id belongs to the child only'
  );
});

test('omitting workflowId leaves an inherited value untouched', async () => {
  const before = process.env[WINAPP_UI_WORKFLOW_ID];
  process.env[WINAPP_UI_WORKFLOW_ID] = 'inherited-workflow';
  try {
    await uiListWindows({}).catch(() => undefined);

    assert.equal(
      process.env[WINAPP_UI_WORKFLOW_ID],
      'inherited-workflow',
      'an environment-provided workflow id must still reach the child unchanged'
    );
  } finally {
    if (before === undefined) {
      delete process.env[WINAPP_UI_WORKFLOW_ID];
    } else {
      process.env[WINAPP_UI_WORKFLOW_ID] = before;
    }
  }
});
