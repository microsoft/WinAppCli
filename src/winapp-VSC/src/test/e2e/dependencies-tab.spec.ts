/**
 * E2E tests: Dependencies tab – target device families, package dependencies,
 * and other dependency types (add, remove, move, edit).
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
    await switchTab(frame, 'dependencies');
});

// ─── Target Device Families ─────────────────────────────

test('shows existing target device family from fixture', async () => {
    const items = await countListItems(frame, 'target-device-families');
    expect(items).toBeGreaterThanOrEqual(1);
});

test('target device family fields are populated', async () => {
    const firstItem = frame.locator('#target-device-families .list-item').first();
    // The name is displayed in the header title, not as an input field
    const title = firstItem.locator('.item-title');
    const titleText = await title.textContent();
    expect(titleText).toContain('Windows.Desktop');
});

test('add target device family dropdown is visible', async () => {
    await expect(frame.locator('#add-target-family')).toBeVisible();
});

test('can add a target device family via dropdown', async () => {
    // Open the dropdown
    await frame.locator('#add-target-family').click();
    await frame.locator('#add-family-menu').waitFor({ state: 'visible' });

    // Click "Windows.Universal" or the first available option
    const firstOption = frame.locator('#add-family-menu .custom-dropdown-item').first();
    await firstOption.click();
    await ctx.page.waitForTimeout(1_000);

    const items = await countListItems(frame, 'target-device-families');
    expect(items).toBeGreaterThanOrEqual(2);
});

test('can edit target device family minVersion', async () => {
    const secondItem = frame.locator('#target-device-families .list-item').nth(1);
    const minVersionInput = secondItem.locator('input[data-field-name="targetDeviceFamily.minVersion"]');
    await minVersionInput.fill('10.0.19041.0');
    await minVersionInput.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('MinVersion="10.0.19041.0"');
});

test('can remove a target device family', async () => {
    const initialCount = await countListItems(frame, 'target-device-families');
    const lastItem = frame.locator('#target-device-families .list-item').last();
    await lastItem.locator('.btn-remove-field, button:has-text("✕")').first().click();
    await ctx.page.waitForTimeout(1_000);

    const newCount = await countListItems(frame, 'target-device-families');
    expect(newCount).toBe(initialCount - 1);
});

// ─── Package Dependencies ───────────────────────────────

test('add package dependency button is visible', async () => {
    await expect(frame.locator('#add-package-dep')).toBeVisible();
});

test('can add a package dependency', async () => {
    const initialCount = await countListItems(frame, 'package-dependencies');
    await clickButton(frame, 'add-package-dep');
    await ctx.page.waitForTimeout(1_000);

    const newCount = await countListItems(frame, 'package-dependencies');
    expect(newCount).toBe(initialCount + 1);
});

test('can edit package dependency name', async () => {
    const lastItem = frame.locator('#package-dependencies .list-item').last();
    const nameInput = lastItem.locator('input[data-field-name="packageDependency.name"]');
    await nameInput.fill('Microsoft.VCLibs.140.00');
    await nameInput.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Microsoft.VCLibs.140.00');
});

test('can remove a package dependency', async () => {
    const initialCount = await countListItems(frame, 'package-dependencies');
    const lastItem = frame.locator('#package-dependencies .list-item').last();
    await lastItem.locator('.btn-remove-field, button:has-text("✕")').first().click();
    await ctx.page.waitForTimeout(1_000);

    const newCount = await countListItems(frame, 'package-dependencies');
    expect(newCount).toBe(initialCount - 1);
});

// ─── Other dependency types ─────────────────────────────

test('add main package dependency button is visible', async () => {
    await expect(frame.locator('#add-main-pkg-dep')).toBeVisible();
});

test('add driver constraint button is visible', async () => {
    await expect(frame.locator('#add-driver-constraint')).toBeVisible();
});

test('add OS package dependency button is visible', async () => {
    await expect(frame.locator('#add-os-pkg-dep')).toBeVisible();
});

test('add host runtime dependency button is visible', async () => {
    await expect(frame.locator('#add-host-runtime-dep')).toBeVisible();
});

test('add external dependency button is visible', async () => {
    await expect(frame.locator('#add-external-dep')).toBeVisible();
});

test('can add and remove a main package dependency', async () => {
    await clickButton(frame, 'add-main-pkg-dep');
    await ctx.page.waitForTimeout(1_000);

    let count = await countListItems(frame, 'main-package-dependencies');
    expect(count).toBeGreaterThanOrEqual(1);

    const lastItem = frame.locator('#main-package-dependencies .list-item').last();
    await lastItem.locator('button:has-text("✕"), .btn-remove-section').first().click();
    await ctx.page.waitForTimeout(1_000);

    count = await countListItems(frame, 'main-package-dependencies');
    expect(count).toBe(0);
});

test('can add and remove an external dependency', async () => {
    await clickButton(frame, 'add-external-dep');
    await ctx.page.waitForTimeout(1_000);

    let count = await countListItems(frame, 'external-dependencies');
    expect(count).toBeGreaterThanOrEqual(1);

    const lastItem = frame.locator('#external-dependencies .list-item').last();
    await lastItem.locator('button:has-text("✕"), .btn-remove-section').first().click();
    await ctx.page.waitForTimeout(1_000);

    count = await countListItems(frame, 'external-dependencies');
    expect(count).toBe(0);
});
