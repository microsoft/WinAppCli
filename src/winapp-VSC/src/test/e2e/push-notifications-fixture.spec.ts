/**
 * E2E tests: Push Notifications sample fixture.
 * Verifies the editor correctly loads and displays a manifest with
 * COM extensions, protocol extensions, and specific capabilities.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import {
    switchTab,
    getInputValue,
    isCapabilityChecked,
    countListItems,
    readManifestXml,
    setInputValue,
    waitForDebounce,
    type VSCodeTestContext,
} from './helpers';
import { ensureEditor, resetManifest } from './shared-context';

const FIXTURE = 'push-notifications-sample.appxmanifest';

let ctx: VSCodeTestContext;
let frame: FrameLocator;

test.beforeAll(async () => {
    const shared = await ensureEditor();
    ctx = shared.ctx;
    frame = await resetManifest(ctx, FIXTURE);
});

// ─── Identity ───────────────────────────────────────────

test('identity fields are populated correctly', async () => {
    await switchTab(frame, 'identity');
    expect(await getInputValue(frame, 'identity-name')).toBe('PushNotificationsSample');
    expect(await getInputValue(frame, 'identity-publisher')).toBe('CN=Microsoft');
    expect(await getInputValue(frame, 'identity-version')).toBe('1.0.0.0');
});

// ─── Properties ─────────────────────────────────────────

test('properties fields are populated correctly', async () => {
    await switchTab(frame, 'properties');
    expect(await getInputValue(frame, 'props-displayname')).toBe('Push Notifications Sample');
    expect(await getInputValue(frame, 'props-pubdisplayname')).toBe('Microsoft Corporation');
    expect(await getInputValue(frame, 'props-logo')).toBe('Images\\StoreLogo.png');
});

// ─── Dependencies ───────────────────────────────────────

test('has one target device family (Windows.Universal)', async () => {
    await switchTab(frame, 'dependencies');
    const count = await countListItems(frame, 'target-device-families');
    expect(count).toBe(1);
    const title = await frame.locator('#target-device-families .list-item').first().locator('.item-title').textContent();
    expect(title).toContain('Windows.Universal');
});

// ─── Applications ───────────────────────────────────────

test('application card is present with correct id', async () => {
    await switchTab(frame, 'applications');
    const cards = frame.locator('.app-card');
    expect(await cards.count()).toBe(1);
    const idInput = cards.first().locator('input[data-field-name="id"]');
    expect(await idInput.inputValue()).toBe('App');
});

test('application has extensions', async () => {
    const card = frame.locator('.app-card').first();
    // The extensions sub-tab content should list COM and protocol extensions
    const extItems = card.locator('.ext-item, [data-ext-index]');
    const count = await extItems.count();
    expect(count).toBeGreaterThan(0);
});

// ─── Capabilities ───────────────────────────────────────

test('internetClient capability is checked', async () => {
    await switchTab(frame, 'capabilities');
    expect(await isCapabilityChecked(frame, 'internetClient')).toBe(true);
});

test('runFullTrust restricted capability is checked', async () => {
    expect(await isCapabilityChecked(frame, 'rescap:runFullTrust')).toBe(true);
});

// ─── Edit round-trip ────────────────────────────────────

test('editing identity name persists to XML', async () => {
    await switchTab(frame, 'identity');
    await setInputValue(frame, 'identity-name', 'PushNotificationsEdited');
    await waitForDebounce(ctx.page);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Name="PushNotificationsEdited"');
});
