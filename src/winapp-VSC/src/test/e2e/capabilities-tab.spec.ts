/**
 * E2E tests: Capabilities tab – checkbox toggles, custom capabilities,
 * hover descriptions, and validation.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import {
    switchTab,
    toggleCapability,
    isCapabilityChecked,
    clickButton,
    setInputValue,
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
    await switchTab(frame, 'capabilities');
});

// ─── Capability categories ──────────────────────────────

test('General capabilities section is visible', async () => {
    await expect(frame.locator('.cap-category-title:has-text("General")')).toBeVisible();
});

test('Restricted capabilities section is visible', async () => {
    await expect(frame.locator('.cap-category-title:has-text("Restricted")')).toBeVisible();
});

test('Device capabilities section is visible', async () => {
    await expect(frame.locator('.cap-category-title:has-text("Device")')).toBeVisible();
});

test('Custom Capability section is visible', async () => {
    await expect(frame.locator('.cap-category-title:has-text("Custom")')).toBeVisible();
});

// ─── Existing capabilities from fixture ─────────────────

test('runFullTrust capability is checked (from fixture)', async () => {
    const checked = await isCapabilityChecked(frame, 'rescap:runFullTrust');
    expect(checked).toBe(true);
});

// ─── Toggle capabilities ────────────────────────────────

test('can check internetClient capability', async () => {
    await toggleCapability(frame, 'internetClient');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const checked = await isCapabilityChecked(frame, 'internetClient');
    expect(checked).toBe(true);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('internetClient');
});

test('can uncheck internetClient capability', async () => {
    await toggleCapability(frame, 'internetClient');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const checked = await isCapabilityChecked(frame, 'internetClient');
    expect(checked).toBe(false);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).not.toContain('"internetClient"');
});

test('can toggle a device capability', async () => {
    await toggleCapability(frame, 'device:microphone');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const checked = await isCapabilityChecked(frame, 'device:microphone');
    expect(checked).toBe(true);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('microphone');

    // Toggle back off
    await toggleCapability(frame, 'device:microphone');
    await ctx.page.waitForTimeout(1_000);
});

// ─── System AI capability ────────────────────────────────

test('can add systemAIModels capability', async () => {
    await toggleCapability(frame, 'systemai:systemAIModels');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const checked = await isCapabilityChecked(frame, 'systemai:systemAIModels');
    expect(checked).toBe(true);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('systemAIModels');
    expect(xml).toContain('schemas.microsoft.com/appx/manifest/systemai/windows10');
});

test('can remove systemAIModels capability', async () => {
    await toggleCapability(frame, 'systemai:systemAIModels');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const checked = await isCapabilityChecked(frame, 'systemai:systemAIModels');
    expect(checked).toBe(false);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).not.toContain('systemAIModels');
    // xmlns:systemai should be removed when no longer used
    expect(xml).not.toContain('xmlns:systemai');
});

// ─── Namespace cleanup on capability removal ─────────────

test('removing last rescap capability removes xmlns:rescap', async () => {
    // The fixture has rescap:runFullTrust checked — uncheck it
    await toggleCapability(frame, 'rescap:runFullTrust');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).not.toContain('runFullTrust');
    expect(xml).not.toContain('xmlns:rescap');
});

test('re-adding rescap capability restores xmlns:rescap', async () => {
    await toggleCapability(frame, 'rescap:runFullTrust');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('runFullTrust');
    expect(xml).toContain('xmlns:rescap');
});

// ─── Capability description panel ───────────────────────

test('description panel exists', async () => {
    await expect(frame.locator('#cap-description-panel')).toBeVisible();
    await expect(frame.locator('#cap-description-text')).toBeVisible();
});

test('hovering a capability shows description', async () => {
    // Hover over the internetClient capability
    await frame.locator('.cap-item[data-cap="internetClient"]').hover();
    await ctx.page.waitForTimeout(500);

    const descText = await frame.locator('#cap-description-text').textContent();
    expect(descText).toBeTruthy();
    expect(descText!.length).toBeGreaterThan(10);
});

// ─── Custom capabilities ────────────────────────────────

test('custom capability input is visible', async () => {
    await expect(frame.locator('#custom-cap-input')).toBeVisible();
    await expect(frame.locator('#add-custom-cap')).toBeVisible();
});

test('adding empty custom capability shows error', async () => {
    await clickButton(frame, 'add-custom-cap');
    await ctx.page.waitForTimeout(500);

    const errorEl = frame.locator('#custom-cap-error');
    await expect(errorEl).toBeVisible();
    const errorText = await errorEl.textContent();
    expect(errorText).toContain('required');
});

test('adding invalid format custom capability shows error', async () => {
    const input = frame.locator('#custom-cap-input');
    await input.fill('invalid-capability');
    await clickButton(frame, 'add-custom-cap');
    await ctx.page.waitForTimeout(500);

    const errorEl = frame.locator('#custom-cap-error');
    await expect(errorEl).toBeVisible();
    const errorText = await errorEl.textContent();
    expect(errorText).toContain('format');
});

test('typing in input clears validation error', async () => {
    const input = frame.locator('#custom-cap-input');
    await input.fill('a');
    await ctx.page.waitForTimeout(300);

    const errorEl = frame.locator('#custom-cap-error');
    // Error should be hidden after typing
    await expect(errorEl).not.toBeVisible();
});

test('adding valid custom capability succeeds', async () => {
    const input = frame.locator('#custom-cap-input');
    await input.fill('Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
    await clickButton(frame, 'add-custom-cap');
    await ctx.page.waitForTimeout(1_000);

    // Input should be cleared
    const val = await input.inputValue();
    expect(val).toBe('');

    // The capability should appear in the XML
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
});

test('custom capability appears in the custom capabilities list', async () => {
    const customList = frame.locator('#custom-caps-list');
    await expect(customList).toContainText('Contoso.Devices.SerialCommunication');
});
