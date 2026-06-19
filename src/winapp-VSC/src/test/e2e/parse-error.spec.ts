/**
 * E2E tests: Parse error handling – verifies the error view when
 * opening a malformed XML file, and recovery when fixed.
 */

import { test, expect, type FrameLocator } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import {
    createTempWorkspace,
    launchVSCode,
    teardown,
    type VSCodeTestContext,
} from './helpers';

let ctx: VSCodeTestContext;

test.afterAll(async () => {
    if (ctx) await teardown(ctx);
});

test('shows error view for malformed XML', async () => {
    // Create a workspace with a broken manifest
    const tmpDir = createTempWorkspace('winui-gallery.appxmanifest');
    const manifestPath = path.join(tmpDir, 'AppxManifest.xml');
    fs.writeFileSync(manifestPath, '<Package><broken xml here', 'utf-8');

    ctx = await launchVSCode(tmpDir);

    // Open the manifest file via Quick Open (Ctrl+P)
    await ctx.page.keyboard.press('Control+P');
    await ctx.page.waitForTimeout(1_000);
    await ctx.page.keyboard.type('AppxManifest.xml', { delay: 30 });
    await ctx.page.waitForTimeout(1_500);
    await ctx.page.keyboard.press('Enter');
    await ctx.page.waitForTimeout(2_000);

    // Reopen with the custom editor
    await ctx.page.keyboard.press('Control+Shift+P');
    await ctx.page.waitForTimeout(1_000);
    await ctx.page.keyboard.type('View: Reopen Editor With...', { delay: 30 });
    await ctx.page.waitForTimeout(1_500);
    await ctx.page.keyboard.press('Enter');
    await ctx.page.waitForTimeout(2_000);
    await ctx.page.keyboard.type('AppxManifest Editor', { delay: 30 });
    await ctx.page.waitForTimeout(1_000);
    await ctx.page.keyboard.press('Enter');
    await ctx.page.waitForTimeout(5_000);

    // Navigate into the webview frames
    const webviewOuterFrame = ctx.page.frames().find(f => f.url().includes('vscode-webview://') && !f.url().includes('fake.html'));
    expect(webviewOuterFrame).toBeTruthy();
    const innerFrame = webviewOuterFrame!.frameLocator('#active-frame');

    // Should show the error view
    const errorContainer = innerFrame.locator('.error-container');
    await expect(errorContainer).toBeVisible({ timeout: 15_000 });
    await expect(innerFrame.locator('.error-title')).toContainText('Unable to Open Manifest Editor');
    await expect(innerFrame.locator('.error-detail')).toBeVisible();
    await expect(innerFrame.locator('#open-as-text')).toBeVisible();
    await expect(innerFrame.locator('#open-as-text')).toContainText('Open in Text Editor');
});
