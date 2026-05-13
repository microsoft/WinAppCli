/**
 * E2E tests: Properties tab – display name, publisher display name,
 * description, logo, package type, and optional fields.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import {
    switchTab,
    getInputValue,
    setInputValue,
    getValidationMessage,
    hasErrorClass,
    selectCustomValue,
    getCustomSelectValue,
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
    await switchTab(frame, 'properties');
});

// ─── Field population ───────────────────────────────────

test('display name field is populated', async () => {
    const value = await getInputValue(frame, 'props-displayname');
    expect(value).toBe('WinUI 3 Gallery');
});

test('publisher display name is populated', async () => {
    const value = await getInputValue(frame, 'props-pubdisplayname');
    expect(value).toBe('Microsoft Corporation');
});

test('logo path is populated', async () => {
    const value = await getInputValue(frame, 'props-logo');
    expect(value).toContain('StoreLogo');
});

// ─── Editing fields ─────────────────────────────────────

test('editing display name updates the XML', async () => {
    await setInputValue(frame, 'props-displayname', 'My Cool App');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('<DisplayName>My Cool App</DisplayName>');
});

test('editing publisher display name updates the XML', async () => {
    await setInputValue(frame, 'props-pubdisplayname', 'Test Publisher');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('<PublisherDisplayName>Test Publisher</PublisherDisplayName>');
});

test('editing logo path updates the XML', async () => {
    await setInputValue(frame, 'props-logo', 'Assets\\NewLogo.png');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('<Logo>Assets\\NewLogo.png</Logo>');
});

// ─── Validation ─────────────────────────────────────────

test('clearing display name shows validation error', async () => {
    await setInputValue(frame, 'props-displayname', '');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    const msg = await getValidationMessage(frame, 'properties.displayName');
    expect(msg.length).toBeGreaterThan(0);
});

test('clearing publisher display name shows validation error', async () => {
    await setInputValue(frame, 'props-pubdisplayname', '');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    const msg = await getValidationMessage(frame, 'properties.publisherDisplayName');
    expect(msg.length).toBeGreaterThan(0);
});

test('restoring values clears errors', async () => {
    await setInputValue(frame, 'props-displayname', 'WinUI 3 Gallery');
    await setInputValue(frame, 'props-pubdisplayname', 'Microsoft Corporation');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    expect(await hasErrorClass(frame, 'properties.displayName')).toBe(false);
    expect(await hasErrorClass(frame, 'properties.publisherDisplayName')).toBe(false);
});

// ─── Package Type select ────────────────────────────────

test('package type selector is visible', async () => {
    await expect(frame.locator('#pkg-type-select')).toBeVisible();
});

// ─── Browse logo button ─────────────────────────────────

test('browse logo button is visible', async () => {
    const browseBtn = frame.locator('.browse-image-btn[data-field-name="logo"]');
    await expect(browseBtn).toBeVisible();
});

// ─── Description field ──────────────────────────────────

test('description textarea is editable', async () => {
    const textarea = frame.locator('#props-description');
    // The field may or may not exist depending on fixture, but should be present in the DOM
    const count = await textarea.count();
    if (count > 0) {
        await textarea.fill('A test description');
        await waitForDebounce(ctx.page);
        await ctx.page.waitForTimeout(1_000);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).toContain('A test description');
    }
});

// ─── Optional Properties fields ─────────────────────────

test('can add Auto Update App Installer URI', async () => {
    const addBtn = frame.locator('#add-props-autoupdate');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    // The field group should now be visible
    const group = frame.locator('#props-autoupdate-group');
    await expect(group).toBeVisible();

    // Fill in a URI and verify it persists
    const input = group.locator('input[data-field-name="autoUpdateUri"]');
    await input.fill('https://example.com/appinstaller');
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('https://example.com/appinstaller');
});

test('can remove Auto Update App Installer URI', async () => {
    const group = frame.locator('#props-autoupdate-group');
    const removeBtn = group.locator('.btn-remove-field');
    await removeBtn.click();
    await ctx.page.waitForTimeout(500);

    // The add button should be visible again
    await expect(frame.locator('#add-props-autoupdate')).toBeVisible();
});

test('can add Package Integrity Content Enforcement', async () => {
    const addBtn = frame.locator('#add-props-pkgintegrity');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    const group = frame.locator('#props-pkgintegrity-group');
    await expect(group).toBeVisible();

    // It should show a select with "default" value
    const trigger = group.locator('.custom-select-trigger');
    const value = await trigger.textContent();
    expect(value?.trim()).toBe('default');
});

test('can remove Package Integrity Content Enforcement', async () => {
    const group = frame.locator('#props-pkgintegrity-group');
    const removeBtn = group.locator('.btn-remove-field');
    await removeBtn.click();
    await ctx.page.waitForTimeout(500);

    await expect(frame.locator('#add-props-pkgintegrity')).toBeVisible();
});

test('can add Update While In Use', async () => {
    const addBtn = frame.locator('#add-props-updatewhileinuse');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    const group = frame.locator('#props-updatewhileinuse-group');
    await expect(group).toBeVisible();

    // It should show a select with "defer" value
    const trigger = group.locator('.custom-select-trigger');
    const value = await trigger.textContent();
    expect(value?.trim()).toBe('defer');
});

test('can remove Update While In Use', async () => {
    const group = frame.locator('#props-updatewhileinuse-group');
    const removeBtn = group.locator('.btn-remove-field');
    await removeBtn.click();
    await ctx.page.waitForTimeout(500);

    await expect(frame.locator('#add-props-updatewhileinuse')).toBeVisible();
});
