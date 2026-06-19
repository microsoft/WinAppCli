/**
 * E2E tests: Identity tab – fields, validation, optional Resource ID, Phone Identity.
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
    clickButton,
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
    await switchTab(frame, 'identity');
});

// ─── Field population ───────────────────────────────────

test('name field is populated from manifest', async () => {
    const value = await getInputValue(frame, 'identity-name');
    expect(value).toBe('Microsoft.WinUI3ControlsGallery');
});

test('publisher field is populated from manifest', async () => {
    const value = await getInputValue(frame, 'identity-publisher');
    expect(value).toContain('CN=Microsoft Corporation');
});

test('version field is populated from manifest', async () => {
    const value = await getInputValue(frame, 'identity-version');
    expect(value).toBe('2.8.0.0');
});

// ─── Editing fields ─────────────────────────────────────

test('editing name field updates the XML document', async () => {
    await setInputValue(frame, 'identity-name', 'com.test.MyNewApp');
    await waitForDebounce(ctx.page);

    // Allow VS Code to apply the edit
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Name="com.test.MyNewApp"');
});

test('editing version field updates the XML document', async () => {
    await setInputValue(frame, 'identity-version', '3.0.0.0');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Version="3.0.0.0"');
});

// ─── Validation ─────────────────────────────────────────

test('clearing name shows validation error', async () => {
    await setInputValue(frame, 'identity-name', '');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    const msg = await getValidationMessage(frame, 'identity.name');
    expect(msg.length).toBeGreaterThan(0);
    expect(await hasErrorClass(frame, 'identity.name')).toBe(true);
});

test('entering valid name clears validation error', async () => {
    await setInputValue(frame, 'identity-name', 'com.test.ValidApp');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    expect(await hasErrorClass(frame, 'identity.name')).toBe(false);
});

test('invalid version format shows validation error', async () => {
    await setInputValue(frame, 'identity-version', 'not-a-version');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    const msg = await getValidationMessage(frame, 'identity.version');
    expect(msg.length).toBeGreaterThan(0);
    expect(await hasErrorClass(frame, 'identity.version')).toBe(true);
});

test('valid version clears error', async () => {
    await setInputValue(frame, 'identity-version', '1.0.0.0');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(500);

    expect(await hasErrorClass(frame, 'identity.version')).toBe(false);
});

// ─── Processor Architecture select ──────────────────────

test('processor architecture custom select displays current value', async () => {
    // The fixture doesn't set ProcessorArchitecture, so it should be empty or default
    const val = await getCustomSelectValue(frame, 'arch-select');
    expect(typeof val).toBe('string');
});

test('can change processor architecture', async () => {
    await selectCustomValue(frame, 'arch-select', 'x64');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('ProcessorArchitecture="x64"');
});

// ─── Optional Resource ID ───────────────────────────────

test('add Resource ID button is visible', async () => {
    await expect(frame.locator('#add-identity-resourceid')).toBeVisible();
});

test('clicking Add Resource ID shows the field', async () => {
    await clickButton(frame, 'add-identity-resourceid');
    await ctx.page.waitForTimeout(500);

    await expect(frame.locator('#identity-resourceid-group')).toBeVisible();
    await expect(frame.locator('#identity-resourceid')).toBeVisible();
});

test('entering a Resource ID updates the XML', async () => {
    await setInputValue(frame, 'identity-resourceid', 'MyResourceId');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('ResourceId="MyResourceId"');
});

// ─── Phone Identity ─────────────────────────────────────

test('Phone Identity section is visible (fixture has PhoneIdentity)', async () => {
    // The winui-gallery fixture includes mp:PhoneIdentity
    await expect(frame.locator('#phone-identity-section')).toBeVisible();
});

test('Phone Identity fields are populated', async () => {
    const productId = await getInputValue(frame, 'phone-product-id');
    expect(productId).toBe('863667e0-667a-4bb4-ac52-c59656c7333a');

    const publisherId = await getInputValue(frame, 'phone-publisher-id');
    expect(publisherId).toBe('00000000-0000-0000-0000-000000000000');
});

test('editing Phone Identity updates the XML', async () => {
    await setInputValue(frame, 'phone-product-id', '11111111-2222-3333-4444-555555555555');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('PhoneProductId="11111111-2222-3333-4444-555555555555"');
});

test('remove Phone Identity button removes the section', async () => {
    await clickButton(frame, 'remove-phone-identity-btn');
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).not.toContain('PhoneIdentity');
});

// ─── Regression: issue #426 (paste same value) ──────────

test('setting name to its current value does not corrupt the XML', async () => {
    // First set a known value
    await setInputValue(frame, 'identity-name', 'com.test.SameValue');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    // Now "paste" the same value again (simulates user selecting all + paste)
    await setInputValue(frame, 'identity-name', 'com.test.SameValue');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    // Should have exactly one Name attribute, not a duplicate
    const nameMatches = xml.match(/Name="com\.test\.SameValue"/g) || [];
    expect(nameMatches.length).toBe(1);
    // XML should still parse (no "Attribute Name redefined" error)
    expect(xml).toContain('<Identity');
    expect(xml).not.toContain('Name="com.test.SameValue" Name="');
});
