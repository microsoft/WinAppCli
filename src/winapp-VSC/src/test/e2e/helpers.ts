/**
 * Shared helpers for Playwright E2E tests of the VS Code manifest editor.
 *
 * Provides launch/teardown of VS Code, webview frame acquisition, and
 * reusable actions (tab switching, field edits, button clicks, etc.).
 */

import { _electron as electron, type ElectronApplication, type Page, type FrameLocator } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';

// ──────────────────────────────────────────────────────
// Paths
// ──────────────────────────────────────────────────────

const VSCODE_EXE =
    process.env.VSCODE_PATH ??
    path.join(os.homedir(), 'AppData', 'Local', 'Programs', 'Microsoft VS Code', 'Code.exe');

const EXTENSION_ROOT = path.resolve(__dirname, '..', '..', '..');

const FIXTURES_DIR = path.resolve(__dirname, '..', 'fixtures');

// ──────────────────────────────────────────────────────
// Launch helpers
// ──────────────────────────────────────────────────────

export interface VSCodeTestContext {
    app: ElectronApplication;
    page: Page;
    workspacePath: string;
}

/**
 * Prepares a temporary workspace with a copy of the given fixture manifest.
 * Returns the path to the workspace directory.
 */
export function createTempWorkspace(fixtureName: string): string {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'manifest-e2e-'));
    const src = path.join(FIXTURES_DIR, fixtureName);
    const dest = path.join(tmpDir, 'AppxManifest.xml');
    fs.copyFileSync(src, dest);
    return tmpDir;
}

/**
 * Launches VS Code with the extension under test, opens the given workspace,
 * and returns the Electron app + main window page.
 */
export async function launchVSCode(workspacePath: string): Promise<VSCodeTestContext> {
    const manifestPath = path.join(workspacePath, 'AppxManifest.xml');
    const app = await electron.launch({
        executablePath: VSCODE_EXE,
        args: [
            workspacePath,
            manifestPath,
            '--new-window',
            `--extensionDevelopmentPath=${EXTENSION_ROOT}`,
            '--disable-telemetry',
            '--skip-release-notes',
            '--disable-workspace-trust',
        ],
        timeout: 30_000,
    });

    const page = await app.firstWindow();
    // Wait for VS Code to settle
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(5_000);

    return { app, page, workspacePath };
}

/**
 * Opens the AppxManifest.xml file in the workspace, triggering the
 * custom manifest editor. Then locates and returns the webview FrameLocator.
 */
export async function openManifestEditor(page: Page): Promise<FrameLocator> {
    // The file is already open from launch args.
    // Reopen with the custom editor via Command Palette.
    await page.keyboard.press('Control+Shift+P');
    await page.waitForTimeout(1_000);
    await page.keyboard.type('View: Reopen Editor With...', { delay: 30 });
    await page.waitForTimeout(1_500);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(2_000);

    // Now the editor picker appears — select "AppxManifest Editor"
    await page.keyboard.type('AppxManifest Editor', { delay: 30 });
    await page.waitForTimeout(1_000);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(5_000);

    return getWebviewFrame(page);
}

/**
 * Locates and returns the webview FrameLocator for the manifest editor.
 * Useful for re-acquiring the frame after the webview reloads (e.g., fixture swap).
 */
export async function getWebviewFrame(page: Page): Promise<FrameLocator> {
    // The custom editor renders inside VS Code webview frames.
    // VS Code uses a named iframe for the webview container, and a nested
    // iframe with name="pending-frame" (or "active-frame") for the actual content.
    // We navigate the frame tree to find the inner content frame.
    const webviewOuterFrame = page.frames().find(f => f.url().includes('vscode-webview://') && !f.url().includes('fake.html'));
    if (!webviewOuterFrame) {
        throw new Error('Could not find webview outer frame');
    }
    const innerFrame = webviewOuterFrame.frameLocator('#active-frame');

    // Wait for the editor to render (the tab bar should appear)
    await innerFrame.locator('.tab-bar').waitFor({ state: 'visible', timeout: 15_000 });

    return innerFrame;
}

/**
 * Cleans up: closes VS Code and removes the temporary workspace.
 */
export async function teardown(ctx: VSCodeTestContext): Promise<void> {
    try {
        await ctx.app.close();
    } catch { /* already closed */ }
    try {
        fs.rmSync(ctx.workspacePath, { recursive: true, force: true });
    } catch { /* best-effort */ }
}

// ──────────────────────────────────────────────────────
// Webview interaction helpers
// ──────────────────────────────────────────────────────

/** Click a top-level tab by name (Identity, Properties, etc.). */
export async function switchTab(frame: FrameLocator, tabName: string): Promise<void> {
    await frame.locator(`.tab-btn[data-tab="${tabName.toLowerCase()}"]`).click();
    await frame.locator(`#tab-${tabName.toLowerCase()}`).waitFor({ state: 'visible' });
}

/** Get the value of a text input by its HTML id. */
export async function getInputValue(frame: FrameLocator, inputId: string): Promise<string> {
    return await frame.locator(`#${inputId}`).inputValue();
}

/** Set the value of a text input by its HTML id, clearing it first. */
export async function setInputValue(frame: FrameLocator, inputId: string, value: string): Promise<void> {
    const input = frame.locator(`#${inputId}`);
    await input.click();
    await input.fill(value);
    // Trigger the debounced change handler
    await input.dispatchEvent('input');
}

/** Click a button by its HTML id. */
export async function clickButton(frame: FrameLocator, buttonId: string): Promise<void> {
    await frame.locator(`#${buttonId}`).click();
}

/** Check whether a validation error message is shown for a given field group. */
export async function getValidationMessage(frame: FrameLocator, fieldDataAttr: string): Promise<string> {
    const group = frame.locator(`.form-group[data-field="${fieldDataAttr}"]`);
    const msg = group.locator('.validation-msg');
    const text = await msg.textContent();
    return text?.trim() ?? '';
}

/** Check whether a form group has the error styling class. */
export async function hasErrorClass(frame: FrameLocator, fieldDataAttr: string): Promise<boolean> {
    const group = frame.locator(`.form-group[data-field="${fieldDataAttr}"]`);
    const cls = await group.getAttribute('class') ?? '';
    return cls.includes('has-error');
}

/** Check whether a form group has the warning styling class. */
export async function hasWarningClass(frame: FrameLocator, fieldDataAttr: string): Promise<boolean> {
    const group = frame.locator(`.form-group[data-field="${fieldDataAttr}"]`);
    const cls = await group.getAttribute('class') ?? '';
    return cls.includes('has-warning');
}

/** Select a value from a custom-select dropdown. */
export async function selectCustomValue(frame: FrameLocator, selectId: string, value: string): Promise<void> {
    // Open the dropdown
    await frame.locator(`#${selectId} .custom-select-trigger`).click();
    await frame.locator(`#${selectId} .custom-select-options`).waitFor({ state: 'visible' });
    // Click the option
    await frame.locator(`#${selectId} .custom-select-option[data-value="${value}"]`).click();
}

/** Get the currently displayed value of a custom-select. */
export async function getCustomSelectValue(frame: FrameLocator, selectId: string): Promise<string> {
    const trigger = frame.locator(`#${selectId} .custom-select-trigger`);
    return (await trigger.textContent())?.trim() ?? '';
}

/** Toggle a capability checkbox (by data-capability attribute value). */
export async function toggleCapability(frame: FrameLocator, capability: string): Promise<void> {
    await frame.locator(`input[data-capability="${capability}"]`).click();
}

/** Check whether a capability checkbox is checked. */
export async function isCapabilityChecked(frame: FrameLocator, capability: string): Promise<boolean> {
    return await frame.locator(`input[data-capability="${capability}"]`).isChecked();
}

/** Read the file content from the workspace (for verifying XML changes). Saves the file first with Ctrl+S. */
export async function readManifestXml(page: Page, workspacePath: string): Promise<string> {
    await page.keyboard.press('Control+S');
    await page.waitForTimeout(1_000);
    return fs.readFileSync(path.join(workspacePath, 'AppxManifest.xml'), 'utf-8');
}

/** Wait a short time for debounced edits to propagate to the document. */
export async function waitForDebounce(page: Page, ms = 500): Promise<void> {
    await page.waitForTimeout(ms);
}

/** Count list items in a container by id. */
export async function countListItems(frame: FrameLocator, containerId: string): Promise<number> {
    return await frame.locator(`#${containerId} .list-item`).count();
}

/** Click the Nth remove button inside a list container. */
export async function removeListItem(frame: FrameLocator, containerId: string, index: number): Promise<void> {
    await frame.locator(`#${containerId} .list-item`).nth(index).locator('.btn-remove-field, .btn-remove-section, .btn-remove').first().click();
}

/** Click the move-up button on the Nth list item. */
export async function moveListItemUp(frame: FrameLocator, containerId: string, index: number): Promise<void> {
    await frame.locator(`#${containerId} .list-item`).nth(index).locator('button:has-text("▲"), button:has-text("Move Up")').first().click();
}

/** Click the move-down button on the Nth list item. */
export async function moveListItemDown(frame: FrameLocator, containerId: string, index: number): Promise<void> {
    await frame.locator(`#${containerId} .list-item`).nth(index).locator('button:has-text("▼"), button:has-text("Move Down")').first().click();
}

/** Switch an application card sub-tab (Info, Extensions, Visual Assets). */
export async function switchAppSubTab(frame: FrameLocator, appIndex: number, subTabName: string): Promise<void> {
    const card = frame.locator('.app-card').nth(appIndex);
    await card.locator(`.app-sub-tab[data-subtab="${subTabName.toLowerCase().replace(/ /g, '-')}"]`).click();
}
