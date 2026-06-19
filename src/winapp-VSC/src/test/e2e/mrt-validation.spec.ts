/**
 * E2E tests: MRT resource key validation for image/logo fields.
 * Verifies that the manifest editor correctly accepts MRT resource references
 * (ms-resource: prefix, extensionless keys, qualifier patterns) and only
 * warns on unrecognized extensions.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import {
    switchTab,
    setInputValue,
    hasErrorClass,
    hasWarningClass,
    getValidationMessage,
    waitForDebounce,
    type VSCodeTestContext,
} from './helpers';
import { ensureEditor, resetManifest } from './shared-context';

let ctx: VSCodeTestContext;
let frame: FrameLocator;

test.beforeAll(async () => {
    const shared = await ensureEditor();
    ctx = shared.ctx;
    frame = await resetManifest(ctx);
    await switchTab(frame, 'properties');
});

// ─── ms-resource: prefix ────────────────────────────────

test('ms-resource: prefixed value is accepted without error or warning', async () => {
    await setInputValue(frame, 'props-logo', 'ms-resource:StoreLogo');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    expect(await hasErrorClass(frame, 'properties.logo')).toBe(false);
    expect(await hasWarningClass(frame, 'properties.logo')).toBe(false);
});

// ─── Extensionless MRT key ──────────────────────────────

test('extensionless MRT key is accepted without error or warning', async () => {
    await setInputValue(frame, 'props-logo', 'StoreLogo');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    expect(await hasErrorClass(frame, 'properties.logo')).toBe(false);
    expect(await hasWarningClass(frame, 'properties.logo')).toBe(false);
});

// ─── Standard image path ────────────────────────────────

test('standard .png path is accepted without error or warning', async () => {
    await setInputValue(frame, 'props-logo', 'Assets\\StoreLogo.png');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    expect(await hasErrorClass(frame, 'properties.logo')).toBe(false);
    expect(await hasWarningClass(frame, 'properties.logo')).toBe(false);
});

// ─── MRT qualifier patterns ─────────────────────────────

test('scale qualifier pattern is accepted without error or warning', async () => {
    await setInputValue(frame, 'props-logo', 'StoreLogo.scale-200');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    expect(await hasErrorClass(frame, 'properties.logo')).toBe(false);
    expect(await hasWarningClass(frame, 'properties.logo')).toBe(false);
});

test('contrast qualifier pattern is accepted without error or warning', async () => {
    await setInputValue(frame, 'props-logo', 'Logo.contrast-high');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    expect(await hasErrorClass(frame, 'properties.logo')).toBe(false);
    expect(await hasWarningClass(frame, 'properties.logo')).toBe(false);
});

// ─── Unrecognized extension shows warning ───────────────

test('unrecognized extension shows warning (not error)', async () => {
    await setInputValue(frame, 'props-logo', 'Assets\\logo.txt');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    expect(await hasErrorClass(frame, 'properties.logo')).toBe(false);
    expect(await hasWarningClass(frame, 'properties.logo')).toBe(true);
    const msg = await getValidationMessage(frame, 'properties.logo');
    expect(msg).toContain('ms-resource:');
});
