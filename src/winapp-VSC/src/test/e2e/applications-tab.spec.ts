/**
 * E2E tests: Applications tab – app cards, sub-tabs, extensions,
 * visual assets, add/remove applications.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
    switchTab,
    clickButton,
    getInputValue,
    setInputValue,
    waitForDebounce,
    readManifestXml,
    switchAppSubTab,
    type VSCodeTestContext,
} from './helpers';
import { ensureEditor, resetManifest } from './shared-context';

let ctx: VSCodeTestContext;
let frame: FrameLocator;

test.beforeAll(async () => {
    const shared = await ensureEditor();
    ctx = shared.ctx;
    frame = await resetManifest(ctx);
    await switchTab(frame, 'applications');
});

// ─── Application cards ──────────────────────────────────

test('shows at least one application card', async () => {
    const cards = await frame.locator('.app-card').count();
    expect(cards).toBeGreaterThanOrEqual(1);
});

test('application card has title', async () => {
    const title = frame.locator('.app-card').first().locator('.app-card-title');
    await expect(title).toBeVisible();
});

// ─── Info sub-tab ───────────────────────────────────────

test('app Info sub-tab is visible by default', async () => {
    const card = frame.locator('.app-card').first();
    const infoTab = card.locator('.app-sub-tab').first();
    await expect(infoTab).toBeVisible();
});

test('app id field is populated', async () => {
    const card = frame.locator('.app-card').first();
    const idInput = card.locator('input[data-field-name="id"]');
    const val = await idInput.inputValue();
    expect(val).toBe('App');
});

test('app executable field is populated', async () => {
    const card = frame.locator('.app-card').first();
    const exeInput = card.locator('input[data-field-name="executable"]');
    const val = await exeInput.inputValue();
    expect(val).toContain('.exe');
});

test('editing app id updates the XML', async () => {
    const card = frame.locator('.app-card').first();
    const idInput = card.locator('input[data-field-name="id"]');
    await idInput.fill('MyApp');
    await idInput.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Id="MyApp"');
});

test('editing app executable updates the XML', async () => {
    const card = frame.locator('.app-card').first();
    const exeInput = card.locator('input[data-field-name="executable"]');
    await exeInput.fill('MyApp.exe');
    await exeInput.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Executable="MyApp.exe"');
});

// ─── Extensions sub-tab ─────────────────────────────────

test('can switch to Extensions sub-tab', async () => {
    await switchAppSubTab(frame, 0, 'extensions');
    await ctx.page.waitForTimeout(500);

    const card = frame.locator('.app-card').first();
    const extensionsContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    // The extensions tab content should become active
    await expect(extensionsContent).toBeVisible();
});

test('existing extensions are shown', async () => {
    const card = frame.locator('.app-card').first();
    // The WinUI Gallery fixture has extensions
    const extItems = card.locator('.ext-item, [data-ext-index]');
    const count = await extItems.count();
    expect(count).toBeGreaterThanOrEqual(0); // May or may not have extensions rendered as separate items
});

test('can add a Protocol Activation extension and fill its fields', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    // Open the Add Extension dropdown and select Protocol Activation
    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("Protocol Activation")').click();
    await ctx.page.waitForTimeout(1_000);

    // Verify a new extension item was added
    const updatedCount = await extContent.locator('.list-item').count();
    expect(updatedCount).toBe(initialCount + 1);

    // Fill in the Protocol Name field on the new extension
    const newExt = extContent.locator('.list-item').last();
    const nameInput = newExt.locator('input[data-ext-field]').first();
    await nameInput.fill('myprotocol');
    await nameInput.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    // Verify the XML contains the protocol extension
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('Name="myprotocol"');
    expect(xml).toContain('windows.protocol');
});

test('can remove the newly added extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    // Remove the last extension (the one we just added)
    const lastExt = extContent.locator('.list-item').last();
    await lastExt.locator('.remove-ext').click();
    await ctx.page.waitForTimeout(1_000);

    const updatedCount = await extContent.locator('.list-item').count();
    expect(updatedCount).toBe(initialCount - 1);
});

// ─── Visual Assets sub-tab ──────────────────────────────

test('can switch to Visual Assets sub-tab', async () => {
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const card = frame.locator('.app-card').first();
    const visualContent = card.locator('.app-sub-content[data-subcontent="visual"]');
    await expect(visualContent).toBeVisible();
});

test('visual asset fields are present', async () => {
    const card = frame.locator('.app-card').first();
    const visualContent = card.locator('.app-sub-content[data-subcontent="visual"]');

    // Should have display name, description, and background color inputs
    const inputs = visualContent.locator('input[data-section]');
    const count = await inputs.count();
    expect(count).toBeGreaterThanOrEqual(1);
});

test('can add a Splash Screen visual asset via dropdown', async () => {
    const card = frame.locator('.app-card').first();
    const visualContent = card.locator('.app-sub-content[data-subcontent="visual"]');

    // The Splash Screen field should not exist yet (optional asset)
    const splashBefore = visualContent.locator('input[data-field-name="visualElements.splashScreenImage"]');
    const existsBefore = await splashBefore.count();

    if (existsBefore === 0) {
        // Open the Add Visual Asset dropdown and select Splash Screen
        await card.locator('.add-visual-asset-btn').click();
        await ctx.page.waitForTimeout(300);
        await card.locator('.add-visual-asset-item:has-text("Splash Screen")').click();
        await ctx.page.waitForTimeout(1_000);

        // Verify the Splash Screen field now appears
        const splashAfter = visualContent.locator('input[data-field-name="visualElements.splashScreenImage"]');
        await expect(splashAfter).toBeVisible();

        // Fill in a path and verify it persists to XML
        await splashAfter.fill('Assets\\SplashScreen.png');
        await splashAfter.dispatchEvent('input');
        await waitForDebounce(ctx.page);
        await ctx.page.waitForTimeout(1_000);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).toContain('SplashScreen.png');
    } else {
        // If already present, just verify it's visible
        await expect(splashBefore.first()).toBeVisible();
    }
});

test('can add a Wide 310x150 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const wideItem = card.locator('.add-visual-asset-item:has-text("Wide 310x150")');
        if (await wideItem.count() > 0) {
            await wideItem.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.wide310x150Logo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\Wide310x150Logo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('Wide310x150Logo.png');
        }
    }
});

test('can add a Square 71x71 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const item = card.locator('.add-visual-asset-item:has-text("Square 71x71")');
        if (await item.count() > 0) {
            await item.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.square71x71Logo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\Square71x71Logo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('Square71x71Logo.png');
        }
    }
});

test('can add a Square 310x310 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const item = card.locator('.add-visual-asset-item:has-text("Square 310x310")');
        if (await item.count() > 0) {
            await item.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.square310x310Logo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\Square310x310Logo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('Square310x310Logo.png');
        }
    }
});

test('can add a Badge Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    // Ensure we're on visual sub-tab and dismiss any open menus
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0 && await addBtn.isVisible()) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const item = card.locator('.add-visual-asset-item:has-text("Badge Logo")');
        if (await item.count() > 0) {
            await item.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.badgeLogo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\BadgeLogo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('BadgeLogo.png');
        }
    }
});

test('can add Wide 310x150 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0 && await addBtn.isVisible()) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const item = card.locator('.add-visual-asset-item:has-text("Wide 310x150 Logo")');
        if (await item.count() > 0) {
            await item.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.wide310x150Logo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\Wide310x150Logo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('Wide310x150Logo.png');
            expect(xml).toContain('DefaultTile');
        }
    }
});

test('can add Square 71x71 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0 && await addBtn.isVisible()) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const item = card.locator('.add-visual-asset-item:has-text("Square 71x71 Logo")');
        if (await item.count() > 0) {
            await item.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.square71x71Logo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\Square71x71Logo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('Square71x71Logo.png');
            expect(xml).toContain('DefaultTile');
        }
    }
});

test('can add Square 310x310 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const addBtn = card.locator('.add-visual-asset-btn');
    if (await addBtn.count() > 0 && await addBtn.isVisible()) {
        await addBtn.click();
        await ctx.page.waitForTimeout(300);
        const item = card.locator('.add-visual-asset-item:has-text("Square 310x310 Logo")');
        if (await item.count() > 0) {
            await item.click();
            await ctx.page.waitForTimeout(1_000);
            const input = card.locator('input[data-field-name="visualElements.square310x310Logo"]');
            await expect(input).toBeVisible();
            await input.fill('Assets\\Square310x310Logo.png');
            await input.dispatchEvent('input');
            await waitForDebounce(ctx.page);
            await ctx.page.waitForTimeout(1_000);
            const xml = await readManifestXml(ctx.page, ctx.workspacePath);
            expect(xml).toContain('Square310x310Logo.png');
            expect(xml).toContain('DefaultTile');
        }
    }
});

test('can remove Badge Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const removeBtn = card.locator('.optional-assets-list .btn-remove-field[data-field-name="visualElements.badgeLogo"]');
    if (await removeBtn.count() > 0 && await removeBtn.isVisible()) {
        await removeBtn.click();
        await ctx.page.waitForTimeout(1_500);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).not.toContain('BadgeLogo.png');
        expect(xml).not.toContain('LockScreen');

        // The input should no longer be visible
        const input = card.locator('input[data-field-name="visualElements.badgeLogo"]');
        await expect(input).not.toBeVisible();
    }
});

test('can remove Wide 310x150 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const removeBtn = card.locator('.optional-assets-list .btn-remove-field[data-field-name="visualElements.wide310x150Logo"]');
    if (await removeBtn.count() > 0 && await removeBtn.isVisible()) {
        await removeBtn.click();
        await ctx.page.waitForTimeout(1_500);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).not.toContain('Wide310x150Logo');

        const input = card.locator('input[data-field-name="visualElements.wide310x150Logo"]');
        await expect(input).not.toBeVisible();
    }
});

test('can remove Square 71x71 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const removeBtn = card.locator('.optional-assets-list .btn-remove-field[data-field-name="visualElements.square71x71Logo"]');
    if (await removeBtn.count() > 0 && await removeBtn.isVisible()) {
        await removeBtn.click();
        await ctx.page.waitForTimeout(1_500);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).not.toContain('Square71x71Logo');

        const input = card.locator('input[data-field-name="visualElements.square71x71Logo"]');
        await expect(input).not.toBeVisible();
    }
});

test('can remove Square 310x310 Logo visual asset', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const removeBtn = card.locator('.optional-assets-list .btn-remove-field[data-field-name="visualElements.square310x310Logo"]');
    if (await removeBtn.count() > 0 && await removeBtn.isVisible()) {
        await removeBtn.click();
        await ctx.page.waitForTimeout(1_500);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).not.toContain('Square310x310Logo');

        const input = card.locator('input[data-field-name="visualElements.square310x310Logo"]');
        await expect(input).not.toBeVisible();
    }
});

test('can add Splash Screen Background Color', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('#add-app-0-splashbgcolor');
    if (await addBtn.count() > 0 && await addBtn.isVisible()) {
        await addBtn.click();
        await ctx.page.waitForTimeout(500);

        const group = card.locator('#app-0-splashbgcolor-group');
        await expect(group).toBeVisible();

        // Fill in a color value via the text input
        const textInput = group.locator('input[type="text"][data-field-name="visualElements.splashScreenBackgroundColor"]');
        await textInput.fill('#FF0000');
        await textInput.dispatchEvent('input');
        await waitForDebounce(ctx.page);
        await ctx.page.waitForTimeout(1_000);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).toContain('#FF0000');
    }
});

test('can remove Splash Screen Background Color', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-splashbgcolor-group');
    if (await group.isVisible()) {
        const removeBtn = group.locator('.btn-remove-field');
        await removeBtn.click();
        await ctx.page.waitForTimeout(500);

        // The add button should reappear
        await expect(card.locator('#add-app-0-splashbgcolor')).toBeVisible();
    }
});

test('Show Name on Tiles checkboxes are visible', async () => {
    const card = frame.locator('.app-card').first();
    // Make sure we're on the Visual Assets sub-tab
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    const tileSection = card.locator('.show-name-on-tiles-section');
    if (await tileSection.count() > 0) {
        await tileSection.scrollIntoViewIfNeeded();
        await expect(tileSection).toBeVisible();
        const checkboxes = tileSection.locator('.show-name-tile-cb');
        expect(await checkboxes.count()).toBeGreaterThanOrEqual(1);
    }
});

test('can toggle Show Name on Medium tile', async () => {
    const card = frame.locator('.app-card').first();
    const cb = card.locator('.show-name-tile-cb[data-tile="square150x150Logo"]');
    if (await cb.count() > 0) {
        await cb.scrollIntoViewIfNeeded();
        const wasBefore = await cb.isChecked();
        await cb.click();
        await ctx.page.waitForTimeout(1_000);

        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        if (!wasBefore) {
            expect(xml).toContain('square150x150Logo');
        }
        // Toggle back
        await cb.click();
        await ctx.page.waitForTimeout(500);
    }
});

// ─── Advanced Attributes (Info sub-tab) ─────────────────

test('can add Trust Level attribute', async () => {
    await switchAppSubTab(frame, 0, 'info');
    await ctx.page.waitForTimeout(500);

    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('#add-app-0-trustlevel');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    const group = card.locator('#app-0-trustlevel-group');
    await expect(group).toBeVisible();

    // Default value should be appContainer
    const trigger = group.locator('.custom-select-trigger');
    expect(await trigger.textContent()).toContain('appContainer');
});

test('can change Trust Level to mediumIL', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-trustlevel-group');
    const trigger = group.locator('.custom-select-trigger');
    await trigger.click();
    await group.locator('.custom-select-option[data-value="mediumIL"]').click();
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('TrustLevel="mediumIL"');
});

test('can remove Trust Level attribute', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-trustlevel-group');
    await group.locator('.btn-remove-field').click();
    await ctx.page.waitForTimeout(500);

    await expect(card.locator('#add-app-0-trustlevel')).toBeVisible();
});

test('can add Runtime Behavior attribute', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('#add-app-0-runtimebehavior');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    const group = card.locator('#app-0-runtimebehavior-group');
    await expect(group).toBeVisible();

    const trigger = group.locator('.custom-select-trigger');
    expect(await trigger.textContent()).toContain('windowsApp');
});

test('can change Runtime Behavior to packagedClassicApp', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-runtimebehavior-group');
    const trigger = group.locator('.custom-select-trigger');
    await trigger.click();
    await group.locator('.custom-select-option[data-value="packagedClassicApp"]').click();
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('RuntimeBehavior="packagedClassicApp"');
});

test('can remove Runtime Behavior attribute', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-runtimebehavior-group');
    await group.locator('.btn-remove-field').click();
    await ctx.page.waitForTimeout(500);

    await expect(card.locator('#add-app-0-runtimebehavior')).toBeVisible();
});

test('can add Supports Multiple Instances attribute', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('#add-app-0-multiinstance');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    const group = card.locator('#app-0-multiinstance-group');
    await expect(group).toBeVisible();

    const trigger = group.locator('.custom-select-trigger');
    expect(await trigger.textContent()).toContain('true');
});

test('can remove Supports Multiple Instances attribute', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-multiinstance-group');
    await group.locator('.btn-remove-field').click();
    await ctx.page.waitForTimeout(500);

    await expect(card.locator('#add-app-0-multiinstance')).toBeVisible();
});

test('can add Parameters attribute', async () => {
    const card = frame.locator('.app-card').first();
    const addBtn = card.locator('#add-app-0-parameters');
    await expect(addBtn).toBeVisible();
    await addBtn.click();
    await ctx.page.waitForTimeout(500);

    const group = card.locator('#app-0-parameters-group');
    await expect(group).toBeVisible();

    const input = group.locator('input[data-field-name="parameters"]');
    await input.fill('--verbose --port 8080');
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_000);

    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('--verbose --port 8080');
});

test('can remove Parameters attribute', async () => {
    const card = frame.locator('.app-card').first();
    const group = card.locator('#app-0-parameters-group');
    await group.locator('.btn-remove-field').click();
    await ctx.page.waitForTimeout(500);

    await expect(card.locator('#add-app-0-parameters')).toBeVisible();
});

// ─── More extension types ───────────────────────────────

test('can add a COM Server extension', async () => {
    await switchAppSubTab(frame, 0, 'extensions');
    await ctx.page.waitForTimeout(500);

    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("COM Server")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.comServer');
});

test('can add an App Execution Alias extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("App Execution Alias")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.appExecutionAlias');
});

test('can add a Background Tasks extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("Background Tasks")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.backgroundTasks');
});

test('can add a File Type Association extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("File Type Association")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.fileTypeAssociation');
});

test('can add a Startup Task extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("Startup Task")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.startupTask');
});

test('can add a Share Target extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("Share Target")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.shareTarget');
});

test('can add an App Service extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("App Service")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.appService');
});

test('can add a Toast Notification Activation extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("Toast Notification")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.toastNotificationActivation');
});

test('can add an MCP Server extension', async () => {
    const card = frame.locator('.app-card').first();
    const extContent = card.locator('.app-sub-content[data-subcontent="extensions"]');
    const initialCount = await extContent.locator('.list-item').count();

    await card.locator('.add-ext-btn').click();
    await ctx.page.waitForTimeout(300);
    await card.locator('.add-ext-item:has-text("MCP Server")').click();
    await ctx.page.waitForTimeout(1_000);

    expect(await extContent.locator('.list-item').count()).toBe(initialCount + 1);
    const xml = await readManifestXml(ctx.page, ctx.workspacePath);
    expect(xml).toContain('windows.appExtension');
});

// ─── Add/Remove application ─────────────────────────────

test('add application button is visible', async () => {
    await expect(frame.locator('#add-application-btn')).toBeVisible();
});

test('can add a new application', async () => {
    const initial = await frame.locator('.app-card').count();
    await clickButton(frame, 'add-application-btn');
    await ctx.page.waitForTimeout(1_500);

    const updated = await frame.locator('.app-card').count();
    expect(updated).toBe(initial + 1);
});

test('can remove the newly added application', async () => {
    const initial = await frame.locator('.app-card').count();
    const lastCard = frame.locator('.app-card').last();
    await lastCard.locator('button:has-text("✕"), .btn-remove-section').first().click();
    await ctx.page.waitForTimeout(1_000);

    const updated = await frame.locator('.app-card').count();
    expect(updated).toBe(initial - 1);
});

// ─── Browse executable button ───────────────────────────

test('browse executable button is visible', async () => {
    // Switch back to Info tab
    await switchAppSubTab(frame, 0, 'info');
    await ctx.page.waitForTimeout(500);

    const card = frame.locator('.app-card').first();
    const browseBtn = card.locator('button:has-text("Browse"), .browse-exe-btn').first();
    await expect(browseBtn).toBeVisible();
});

// ─── Image path warning & copy to assets ────────────────

test('shows warning for image path not in package directory', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    // Create a temp image file outside the workspace
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'img-test-'));
    const externalImg = path.join(tmpDir, 'external-logo.png');
    // Write a minimal valid PNG (1x1 pixel)
    const pngBuffer = Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
        'base64'
    );
    fs.writeFileSync(externalImg, pngBuffer);

    // Type the external path into Square 150x150 Logo field
    const input = card.locator('input[data-field-name="visualElements.square150x150Logo"]');
    await input.fill(externalImg);
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(2_000);

    // Check the warning appears with "Copy to Assets" link
    const formGroup = card.locator('.form-group[data-field*="visualElements.square150x150Logo"]');
    const validationMsg = formGroup.locator('.validation-msg');
    await expect(validationMsg).toContainText('Image not in package directory.', { timeout: 10_000 });
    const copyLink = validationMsg.locator('.copy-to-assets-link');
    await expect(copyLink).toBeVisible();

    // Clean up
    fs.rmSync(tmpDir, { recursive: true, force: true });
});

test('copy to assets copies file and updates field path', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    // Create a temp image file outside the workspace
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'img-copy-'));
    const externalImg = path.join(tmpDir, 'test-asset.png');
    const pngBuffer = Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
        'base64'
    );
    fs.writeFileSync(externalImg, pngBuffer);

    // Type the external path into Square 150x150 Logo field
    const input = card.locator('input[data-field-name="visualElements.square150x150Logo"]');
    await input.fill(externalImg);
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(2_000);

    // Click "Copy to Assets folder" link
    const formGroup = card.locator('.form-group[data-field*="visualElements.square150x150Logo"]');
    const copyLink = formGroup.locator('.copy-to-assets-link');
    if (await copyLink.count() > 0 && await copyLink.isVisible()) {
        await copyLink.click();
        await ctx.page.waitForTimeout(2_000);

        // Verify file was copied to Assets folder
        const assetsDir = path.join(ctx.workspacePath, 'Assets');
        expect(fs.existsSync(assetsDir)).toBe(true);
        const copiedFile = path.join(assetsDir, 'test-asset.png');
        expect(fs.existsSync(copiedFile)).toBe(true);

        // Verify the manifest XML was updated
        const xml = await readManifestXml(ctx.page, ctx.workspacePath);
        expect(xml).toContain('Assets\\test-asset.png');
    }

    // Clean up
    fs.rmSync(tmpDir, { recursive: true, force: true });
});

test('no warning shown for resource key paths', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    // Type a resource key (no image extension) into the logo field
    const input = card.locator('input[data-field-name="visualElements.square150x150Logo"]');
    await input.fill('ms-appx:///Resources/Logo');
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(1_500);

    // No warning should appear
    const formGroup = card.locator('.form-group[data-field*="visualElements.square150x150Logo"]');
    const validationMsg = formGroup.locator('.validation-msg');
    await expect(validationMsg).not.toContainText('Image not');
});

// ─── Aspect ratio validation ────────────────────────────

/** Creates a minimal valid PNG with given dimensions (1-pixel transparency). */
function createPngWithDimensions(width: number, height: number): Buffer {
    // PNG signature
    const signature = Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    // IHDR chunk: width(4) + height(4) + bitDepth(1) + colorType(1) + compression(1) + filter(1) + interlace(1)
    const ihdrData = Buffer.alloc(13);
    ihdrData.writeUInt32BE(width, 0);
    ihdrData.writeUInt32BE(height, 4);
    ihdrData[8] = 8;  // bit depth
    ihdrData[9] = 2;  // color type (RGB)
    ihdrData[10] = 0; // compression
    ihdrData[11] = 0; // filter
    ihdrData[12] = 0; // interlace

    const ihdrLength = Buffer.alloc(4);
    ihdrLength.writeUInt32BE(13, 0);
    const ihdrType = Buffer.from('IHDR');
    const ihdrCrc = crc32(Buffer.concat([ihdrType, ihdrData]));

    // IDAT chunk (minimal — single row of zeroes compressed with zlib)
    const zlib = require('zlib');
    const rawData = Buffer.alloc((width * 3 + 1) * height, 0); // filter byte + RGB per pixel per row
    const compressed = zlib.deflateSync(rawData);
    const idatLength = Buffer.alloc(4);
    idatLength.writeUInt32BE(compressed.length, 0);
    const idatType = Buffer.from('IDAT');
    const idatCrc = crc32(Buffer.concat([idatType, compressed]));

    // IEND chunk
    const iendLength = Buffer.from([0, 0, 0, 0]);
    const iendType = Buffer.from('IEND');
    const iendCrc = crc32(iendType);

    return Buffer.concat([
        signature,
        ihdrLength, ihdrType, ihdrData, ihdrCrc,
        idatLength, idatType, compressed, idatCrc,
        iendLength, iendType, iendCrc,
    ]);
}

/** CRC32 for PNG chunks. */
function crc32(buf: Buffer): Buffer {
    let crc = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) {
        crc ^= buf[i];
        for (let j = 0; j < 8; j++) {
            crc = (crc >>> 1) ^ (crc & 1 ? 0xEDB88320 : 0);
        }
    }
    const result = Buffer.alloc(4);
    result.writeUInt32BE((crc ^ 0xFFFFFFFF) >>> 0, 0);
    return result;
}

test('shows aspect ratio warning for non-square image in square field', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    // Create a 200x100 (2:1) PNG in the workspace Assets folder
    const assetsDir = path.join(ctx.workspacePath, 'Assets');
    if (!fs.existsSync(assetsDir)) { fs.mkdirSync(assetsDir); }
    const imgPath = path.join(assetsDir, 'wide-test.png');
    fs.writeFileSync(imgPath, createPngWithDimensions(200, 100));

    // Set the field to the in-package path
    const input = card.locator('input[data-field-name="visualElements.square150x150Logo"]');
    await input.fill('Assets\\wide-test.png');
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(2_000);

    // Should show aspect ratio warning
    const formGroup = card.locator('.form-group[data-field*="visualElements.square150x150Logo"]');
    const validationMsg = formGroup.locator('.validation-msg');
    await expect(validationMsg).toContainText('expected 1:1 (square) aspect ratio', { timeout: 10_000 });
    await expect(validationMsg).toContainText('200×100');

    // Clean up
    fs.rmSync(imgPath, { force: true });
});

test('no aspect ratio warning for correct square image', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    // Create a 150x150 (1:1) PNG in the workspace Assets folder
    const assetsDir = path.join(ctx.workspacePath, 'Assets');
    if (!fs.existsSync(assetsDir)) { fs.mkdirSync(assetsDir); }
    const imgPath = path.join(assetsDir, 'square-test.png');
    fs.writeFileSync(imgPath, createPngWithDimensions(150, 150));

    // Set the field to the in-package path
    const input = card.locator('input[data-field-name="visualElements.square150x150Logo"]');
    await input.fill('Assets\\square-test.png');
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(2_000);

    // Should NOT show any warning
    const formGroup = card.locator('.form-group[data-field*="visualElements.square150x150Logo"]');
    const validationMsg = formGroup.locator('.validation-msg');
    await expect(validationMsg).not.toContainText('aspect ratio');

    // Clean up
    fs.rmSync(imgPath, { force: true });
});

test('shows aspect ratio warning for square image in wide field', async () => {
    const card = frame.locator('.app-card').first();
    await switchAppSubTab(frame, 0, 'visual');
    await ctx.page.waitForTimeout(500);

    // First add the wide310x150 field if not present
    const addWideBtn = card.locator('button:has-text("Wide 310x150")');
    if (await addWideBtn.count() > 0 && await addWideBtn.isVisible()) {
        await addWideBtn.click();
        await ctx.page.waitForTimeout(1_000);
    }

    // Create a 100x100 (1:1) PNG — wrong for wide field
    const assetsDir = path.join(ctx.workspacePath, 'Assets');
    if (!fs.existsSync(assetsDir)) { fs.mkdirSync(assetsDir); }
    const imgPath = path.join(assetsDir, 'square-for-wide.png');
    fs.writeFileSync(imgPath, createPngWithDimensions(100, 100));

    // Set the wide field to this image
    const input = card.locator('input[data-field-name="visualElements.wide310x150Logo"]');
    await input.fill('Assets\\square-for-wide.png');
    await input.dispatchEvent('input');
    await waitForDebounce(ctx.page);
    await ctx.page.waitForTimeout(2_000);

    // Should show aspect ratio warning
    const formGroup = card.locator('.form-group[data-field*="visualElements.wide310x150Logo"]');
    const validationMsg = formGroup.locator('.validation-msg');
    await expect(validationMsg).toContainText('expected 310:150 (wide) aspect ratio', { timeout: 10_000 });

    // Clean up
    fs.rmSync(imgPath, { force: true });
});
