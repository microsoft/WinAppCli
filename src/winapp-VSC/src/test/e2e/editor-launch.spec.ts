/**
 * E2E tests: Editor launch, tab navigation, and View XML button.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import {
    switchTab,
    type VSCodeTestContext,
} from './helpers';
import { ensureEditor, resetManifest } from './shared-context';

let ctx: VSCodeTestContext;
let frame: FrameLocator;

test.beforeAll(async () => {
    const shared = await ensureEditor();
    ctx = shared.ctx;
    frame = await resetManifest(ctx);
    await switchTab(frame, 'identity');
});

// ─── Launch ─────────────────────────────────────────────

test('manifest editor opens and shows tab bar', async () => {
    await expect(frame.locator('.tab-bar')).toBeVisible();
});

test('Identity tab is active by default', async () => {
    const btn = frame.locator('.tab-btn[data-tab="identity"]');
    await expect(btn).toHaveAttribute('aria-selected', 'true');
    await expect(frame.locator('#tab-identity')).toBeVisible();
});

test('all six tabs are visible', async () => {
    const tabs = ['identity', 'properties', 'dependencies', 'resources', 'applications', 'capabilities'];
    for (const tab of tabs) {
        await expect(frame.locator(`.tab-btn[data-tab="${tab}"]`)).toBeVisible();
    }
});

// ─── Tab switching ──────────────────────────────────────

test('can switch to Properties tab', async () => {
    await switchTab(frame, 'properties');
    await expect(frame.locator('#tab-properties')).toBeVisible();
    await expect(frame.locator('#tab-identity')).not.toBeVisible();
});

test('can switch to Dependencies tab', async () => {
    await switchTab(frame, 'dependencies');
    await expect(frame.locator('#tab-dependencies')).toBeVisible();
});

test('can switch to Resources tab', async () => {
    await switchTab(frame, 'resources');
    await expect(frame.locator('#tab-resources')).toBeVisible();
});

test('can switch to Applications tab', async () => {
    await switchTab(frame, 'applications');
    await expect(frame.locator('#tab-applications')).toBeVisible();
});

test('can switch to Capabilities tab', async () => {
    await switchTab(frame, 'capabilities');
    await expect(frame.locator('#tab-capabilities')).toBeVisible();
});

test('can switch back to Identity tab', async () => {
    await switchTab(frame, 'identity');
    await expect(frame.locator('#tab-identity')).toBeVisible();
    await expect(frame.locator('.tab-btn[data-tab="identity"]')).toHaveAttribute('aria-selected', 'true');
});

// ─── View XML ───────────────────────────────────────────

test('View XML button is visible', async () => {
    await expect(frame.locator('#view-xml-btn')).toBeVisible();
    await expect(frame.locator('#view-xml-btn')).toContainText('View XML');
});

// ─── Info banner ────────────────────────────────────────

test('info banner with feedback link is visible', async () => {
    const banner = frame.locator('.info-banner');
    await expect(banner).toBeVisible();
    await expect(banner).toContainText('This editor does not support all appxmanifest customizations');
    await expect(banner.locator('a[href*="github.com"]')).toBeVisible();
});
