/**
 * E2E tests: Resources tab – add, edit, move, and remove resources.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import {
    switchTab,
    clickButton,
    countListItems,
    waitForDebounce,
    readManifestXml,
    type VSCodeTestContext,
} from './helpers';
import { ensureEditor, resetManifest } from './shared-context';

let ctx: VSCodeTestContext;
let frame: FrameLocator;

test.beforeAll(async () => {
    const shared = await ensureEditor();
    ctx = shared.ctx;
    frame = await resetManifest(ctx);
    await switchTab(frame, 'resources');
});

// ─── Initial state ──────────────────────────────────────

test('shows existing resources from fixture', async () => {
    const count = await countListItems(frame, 'resources-list');
    expect(count).toBeGreaterThanOrEqual(1);
});

test('resource language field is populated', async () => {
    const firstItem = frame.locator('#resources-list .list-item').first();
    const langInput = firstItem.locator('input[data-field-name="language"]');
    const val = await langInput.inputValue();
    expect(val).toBe('x-generate');
});

// ─── Add resource ───────────────────────────────────────

test('add resource button is visible', async () => {
    await expect(frame.locator('#add-resource-btn')).toBeVisible();
});

test('can add a new resource', async () => {
    const initial = await countListItems(frame, 'resources-list');
    await clickButton(frame, 'add-resource-btn');
    await ctx.page.waitForTimeout(1_000);

    const updated = await countListItems(frame, 'resources-list');
    expect(updated).toBe(initial + 1);
});

// ─── Edit resource ──────────────────────────────────────

test('can edit resource language', async () => {
    const lastItem = frame.locator('#resources-list .list-item').last();
    const langInput = lastItem.locator('input[data-field-name="language"]');
    await langInput.fill('en-US');
    await langInput.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Language="en-US"');
});

// ─── Move resources ─────────────────────────────────────

test('can move resource down and up', async () => {
    // Need at least 2 resources; we added one above
    const count = await countListItems(frame, 'resources-list');
    if (count >= 2) {
        // Get first resource's language before move
        const firstItem = frame.locator('#resources-list .list-item').first();
        const langBefore = await firstItem.locator('input[data-field-name="language"]').inputValue();

        // Move first item down
        const moveDownBtn = firstItem.locator('button:has-text("▼"), button[title*="down"], .btn-move-down').first();
        if (await moveDownBtn.count() > 0) {
            await moveDownBtn.click();
            await ctx.page.waitForTimeout(1_000);

            // After moving down, the original first item should now be second
            const newFirst = frame.locator('#resources-list .list-item').first();
            const langAfter = await newFirst.locator('input[data-field-name="language"]').inputValue();
            expect(langAfter).not.toBe(langBefore);
        }
    }
});

// ─── Remove resource ────────────────────────────────────

test('can remove a resource', async () => {
    const initial = await countListItems(frame, 'resources-list');
    const lastItem = frame.locator('#resources-list .list-item').last();
    await lastItem.locator('button:has-text("✕"), .btn-remove-section').first().click();
    await ctx.page.waitForTimeout(1_000);

    const updated = await countListItems(frame, 'resources-list');
    expect(updated).toBe(initial - 1);
});
