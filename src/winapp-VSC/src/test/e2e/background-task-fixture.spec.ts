/**
 * E2E tests: Background Task sample fixture.
 * Verifies the editor handles a manifest with PhoneIdentity, multiple
 * device families, various dependency types, custom capabilities, and
 * background task extensions.
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

const FIXTURE = 'background-task-sample.appxmanifest';

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
    expect(await getInputValue(frame, 'identity-name')).toBe('6b1ec254-6909-4115-a6f6-1133733eb38e');
    expect(await getInputValue(frame, 'identity-publisher')).toBe(
        'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
    );
    expect(await getInputValue(frame, 'identity-version')).toBe('1.0.0.0');
});

test('PhoneIdentity section is visible', async () => {
    await expect(frame.locator('#phone-identity-section')).toBeVisible();
});

test('PhoneIdentity fields are populated', async () => {
    const productId = await getInputValue(frame, 'phone-product-id');
    expect(productId).toBe('bb526a1f-8e02-4523-b45e-e2ee91c4c65b');
    const publisherId = await getInputValue(frame, 'phone-publisher-id');
    expect(publisherId).toBe('00000000-0000-0000-0000-000000000000');
});

// ─── Properties ─────────────────────────────────────────

test('properties fields are populated correctly', async () => {
    await switchTab(frame, 'properties');
    expect(await getInputValue(frame, 'props-displayname')).toBe('BackgroundTaskBuilder');
    expect(await getInputValue(frame, 'props-pubdisplayname')).toBe('Microsoft');
    expect(await getInputValue(frame, 'props-logo')).toBe('Assets\\StoreLogo');
});

// ─── Dependencies ───────────────────────────────────────

test('has two target device families', async () => {
    await switchTab(frame, 'dependencies');
    const count = await countListItems(frame, 'target-device-families');
    expect(count).toBe(2);
});

test('first device family is Windows.Universal', async () => {
    const title = await frame.locator('#target-device-families .list-item').first().locator('.item-title').textContent();
    expect(title).toContain('Windows.Universal');
});

test('second device family is Windows.Desktop', async () => {
    const title = await frame.locator('#target-device-families .list-item').nth(1).locator('.item-title').textContent();
    expect(title).toContain('Windows.Desktop');
});

test('main package dependencies are present', async () => {
    const count = await countListItems(frame, 'main-package-dependencies');
    expect(count).toBeGreaterThanOrEqual(1);
});

test('driver constraints are present', async () => {
    const count = await countListItems(frame, 'driver-constraints');
    expect(count).toBeGreaterThanOrEqual(1);
});

test('OS package dependencies are present', async () => {
    const count = await countListItems(frame, 'os-package-dependencies');
    expect(count).toBeGreaterThanOrEqual(1);
});

test('host runtime dependencies are present', async () => {
    const count = await countListItems(frame, 'host-runtime-dependencies');
    expect(count).toBeGreaterThanOrEqual(1);
});

test('external dependencies are present', async () => {
    const count = await countListItems(frame, 'external-dependencies');
    expect(count).toBeGreaterThanOrEqual(1);
});

// ─── Resources ──────────────────────────────────────────

test('resources section has no items (empty Resources element)', async () => {
    await switchTab(frame, 'resources');
    const count = await countListItems(frame, 'resources-list');
    expect(count).toBe(0);
});

// ─── Applications ───────────────────────────────────────

test('application card is present', async () => {
    await switchTab(frame, 'applications');
    expect(await frame.locator('.app-card').count()).toBe(1);
    const idInput = frame.locator('.app-card').first().locator('input[data-field-name="id"]');
    expect(await idInput.inputValue()).toBe('App');
});

// ─── Capabilities ───────────────────────────────────────

test('runFullTrust restricted capability is checked', async () => {
    await switchTab(frame, 'capabilities');
    expect(await isCapabilityChecked(frame, 'rescap:runFullTrust')).toBe(true);
});

test('custom capability is listed', async () => {
    const customList = frame.locator('#custom-caps-list .custom-cap-entry');
    const count = await customList.count();
    expect(count).toBeGreaterThanOrEqual(1);
});

// ─── Edit round-trip ────────────────────────────────────

test('editing display name persists to XML', async () => {
    await switchTab(frame, 'properties');
    await setInputValue(frame, 'props-displayname', 'BackgroundTaskEdited');
    await waitForDebounce(ctx.page);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('<DisplayName>BackgroundTaskEdited</DisplayName>');
});
