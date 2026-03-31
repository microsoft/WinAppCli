/**
 * Generates the HTML content for the AppxManifest editor webview.
 * Uses VS Code CSS variables for native theming.
 */

import * as vscode from 'vscode';
import { KNOWN_CAPABILITIES, ARCHITECTURE_OPTIONS, DEVICE_FAMILY_OPTIONS, EXTENSION_TEMPLATES, CAPABILITY_DESCRIPTIONS } from './manifest-types';

export function getWebviewContent(webview: vscode.Webview, nonce: string): string {
    const archOptions = ARCHITECTURE_OPTIONS.map(a => `<option value="${a}">${a}</option>`).join('');

    const generalCaps= KNOWN_CAPABILITIES.general.map(c =>
        `<label class="cap-item" data-cap="${c.name}">
            <input type="checkbox" data-capability="${c.name}" /><span>${c.label}</span>
        </label>`
    ).join('');

    const restrictedCaps = KNOWN_CAPABILITIES.restricted.map(c =>
        `<label class="cap-item" data-cap="rescap:${c.name}">
            <input type="checkbox" data-capability="rescap:${c.name}" /><span>${c.label}</span>
        </label>`
    ).join('');

    const deviceCaps = KNOWN_CAPABILITIES.device.map(c =>
        `<label class="cap-item" data-cap="device:${c.name}">
            <input type="checkbox" data-capability="device:${c.name}" /><span>${c.label}</span>
        </label>`
    ).join('');

    return /*html*/`<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>AppxManifest Editor</title>
    <style nonce="${nonce}">
        /* ─── Reset & base ─────────────────────────────────── */
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        html, body {
            height: 100%;
            font-family: var(--vscode-font-family, "Segoe UI", sans-serif);
            font-size: var(--vscode-font-size, 13px);
            color: var(--vscode-editor-foreground);
            background: var(--vscode-editor-background);
        }
        body { padding: 0; overflow-y: auto; }

        /* ─── Tab bar ──────────────────────────────────────── */
        .tab-bar {
            display: flex;
            border-bottom: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
            background: var(--vscode-editor-background);
            position: sticky;
            top: 0;
            z-index: 10;
        }
        .tab-btn {
            padding: 8px 16px;
            border: none;
            background: transparent;
            color: var(--vscode-foreground);
            cursor: pointer;
            font-size: var(--vscode-font-size, 13px);
            font-family: inherit;
            border-bottom: 2px solid transparent;
            opacity: 0.7;
            transition: opacity 0.1s;
        }
        .tab-btn:hover { opacity: 1; }
        .tab-btn.active {
            opacity: 1;
            border-bottom-color: var(--vscode-focusBorder, #007acc);
            color: var(--vscode-foreground);
        }
        .tab-btn:focus-visible {
            outline: 1px solid var(--vscode-focusBorder);
            outline-offset: -1px;
        }

        /* ─── Tab content ──────────────────────────────────── */
        .tab-content { display: none; padding: 20px 24px; max-width: 720px; }
        .tab-content.active { display: block; }

        /* ─── Section header ───────────────────────────────── */
        .section-header {
            font-size: 20px;
            font-weight: 600;
            color: var(--vscode-settings-headerForeground, var(--vscode-foreground));
            margin-bottom: 16px;
            padding-bottom: 6px;
            border-bottom: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
        }

        /* ─── Form groups ──────────────────────────────────── */
        .form-group {
            margin-bottom: 16px;
        }
        .form-group label {
            display: block;
            margin-bottom: 4px;
            font-weight: 600;
            color: var(--vscode-foreground);
            font-size: 12px;
        }
        .form-group input[type="text"],
        .form-group input[type="color"],
        .form-group select,
        .form-group textarea {
            width: 100%;
            padding: 4px 8px;
            font-family: inherit;
            font-size: var(--vscode-font-size, 13px);
            color: var(--vscode-input-foreground);
            background: var(--vscode-input-background);
            border: 1px solid var(--vscode-input-border, transparent);
            border-radius: 2px;
            outline: none;
        }
        .form-group input:focus,
        .form-group select:focus,
        .form-group textarea:focus {
            border-color: var(--vscode-focusBorder, #007acc);
        }
        .form-group textarea {
            min-height: 60px;
            resize: vertical;
        }
        .form-group select {
            appearance: auto;
        }
        .form-group .description {
            font-size: 12px;
            color: var(--vscode-descriptionForeground);
            margin-top: 2px;
        }

        /* ─── Inline validation ────────────────────────────── */
        .validation-msg {
            font-size: 12px;
            margin-top: 2px;
            display: none;
        }
        .validation-msg.error {
            color: var(--vscode-errorForeground, #f44747);
            display: block;
        }
        .validation-msg.warning {
            color: var(--vscode-editorWarning-foreground, #cca700);
            display: block;
        }
        .form-group.has-error input,
        .form-group.has-error select,
        .form-group.has-error textarea {
            border-color: var(--vscode-inputValidation-errorBorder, #f44747);
        }

        /* ─── Color picker row ─────────────────────────────── */
        .color-row {
            display: flex;
            gap: 8px;
            align-items: center;
        }
        .color-row input[type="color"] {
            width: 32px;
            height: 28px;
            padding: 2px;
            cursor: pointer;
            flex-shrink: 0;
        }
        .color-row input[type="text"] {
            flex: 1;
        }

        /* ─── List items (dependencies, etc.) ──────────────── */
        .list-container { margin-bottom: 16px; }
        .list-item {
            padding: 12px;
            margin-bottom: 8px;
            border: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
            border-radius: 4px;
            background: var(--vscode-editor-background);
            position: relative;
        }
        .list-item .item-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 8px;
        }
        .list-item .item-title {
            font-weight: 600;
            font-size: 13px;
        }
        .list-item .form-group {
            margin-bottom: 10px;
        }
        .list-item .form-group:last-child {
            margin-bottom: 0;
        }

        /* ─── Buttons ──────────────────────────────────────── */
        .btn {
            padding: 4px 12px;
            font-size: 12px;
            font-family: inherit;
            cursor: pointer;
            border: none;
            border-radius: 2px;
            color: var(--vscode-button-foreground);
            background: var(--vscode-button-background);
        }
        .btn:hover {
            background: var(--vscode-button-hoverBackground);
        }
        .btn:focus-visible {
            outline: 1px solid var(--vscode-focusBorder);
            outline-offset: 1px;
        }
        .btn-danger {
            color: var(--vscode-errorForeground, #f44747);
            background: transparent;
            border: 1px solid var(--vscode-errorForeground, #f44747);
        }
        .btn-danger:hover {
            background: var(--vscode-errorForeground, #f44747);
            color: var(--vscode-editor-background);
        }
        .btn-sm {
            padding: 2px 8px;
            font-size: 11px;
        }

        /* ─── Capabilities checklist ───────────────────────── */
        .cap-category {
            margin-bottom: 16px;
        }
        .cap-category-title {
            font-weight: 600;
            font-size: 12px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            color: var(--vscode-foreground);
            margin-bottom: 8px;
        }
        .cap-list {
            display: flex;
            flex-direction: column;
            gap: 6px;
        }
        .cap-item {
            display: flex;
            align-items: center;
            gap: 8px;
            cursor: pointer;
            font-size: 13px;
            color: var(--vscode-foreground);
        }
        .cap-item input[type="checkbox"] {
            accent-color: var(--vscode-focusBorder, #007acc);
            width: 14px;
            height: 14px;
        }
        .custom-cap-row {
            display: flex;
            gap: 8px;
            margin-top: 12px;
        }
        .custom-cap-row input {
            flex: 1;
            padding: 4px 8px;
            font-family: inherit;
            font-size: var(--vscode-font-size, 13px);
            color: var(--vscode-input-foreground);
            background: var(--vscode-input-background);
            border: 1px solid var(--vscode-input-border, transparent);
            border-radius: 2px;
        }
        .custom-cap-row input:focus {
            border-color: var(--vscode-focusBorder);
            outline: none;
        }

        /* ─── Application cards ────────────────────────────── */
        .app-card {
            border: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
            border-radius: 4px;
            padding: 16px;
            margin-bottom: 16px;
        }
        .app-card-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 12px;
        }
        .app-card-title {
            font-weight: 600;
            font-size: 18px;
            color: var(--vscode-foreground);
        }
        .extensions-note {
            font-size: 12px;
            color: var(--vscode-descriptionForeground);
            font-style: italic;
            margin-top: 12px;
            padding: 8px;
            background: var(--vscode-textBlockQuote-background, transparent);
            border-left: 3px solid var(--vscode-textBlockQuote-border, var(--vscode-focusBorder));
        }
        .ext-list {
            margin-top: 8px;
            padding-left: 16px;
            color: var(--vscode-descriptionForeground);
            font-size: 12px;
        }
        .ext-list li { margin-bottom: 2px; }

        /* ─── Visual Elements sub-section ──────────────────── */
        .subsection {
            margin-top: 16px;
            padding-top: 12px;
            border-top: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
        }
        .subsection-title {
            font-weight: 600;
            font-size: 15px;
            margin-bottom: 12px;
            color: var(--vscode-settings-headerForeground, var(--vscode-foreground));
        }

        .logo-preview { max-width:100px; max-height:100px; border-radius:4px; border:1px solid var(--vscode-panel-border); }
        .logo-side-by-side { display:flex; gap:16px; align-items:flex-start; }
        .logo-input-col { flex:1; }
        .logo-preview-col { flex-shrink:0; text-align:center; }
        .logo-caption { font-size:11px; font-style:italic; color:var(--vscode-descriptionForeground); margin-top:4px; text-align:center; }

        .app-sub-tabs { display:flex; border-bottom:1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border)); margin-bottom:16px; }
        .app-sub-tab { padding:6px 14px; border:none; background:transparent; color:var(--vscode-foreground); cursor:pointer; font-size:13px; font-family:inherit; border-bottom:2px solid transparent; opacity:0.7; }
        .app-sub-tab:hover { opacity:1; }
        .app-sub-tab.active { opacity:1; border-bottom-color:var(--vscode-focusBorder, #007acc); }
        .app-sub-content { display:none; }
        .app-sub-content.active { display:block; }

        .capabilities-columns { display:flex; gap:24px; }
        .capabilities-left { flex:1; min-width:0; }
        .capabilities-right { width:260px; flex-shrink:0; }
        .cap-description-panel { padding:12px; border-radius:4px; background:var(--vscode-textBlockQuote-background,transparent); border-left:3px solid var(--vscode-focusBorder,#007acc); font-size:13px; color:var(--vscode-descriptionForeground); min-height:40px; position:sticky; top:60px; }
        .cap-description-name { font-weight:600; margin-bottom:4px; color:var(--vscode-foreground); }

        .custom-dropdown { position:relative; display:inline-block; }
        .custom-dropdown-btn { padding:4px 12px; font-size:12px; font-family:inherit; cursor:pointer; border:none; border-radius:2px; color:var(--vscode-button-foreground); background:var(--vscode-button-background); }
        .custom-dropdown-btn:hover { background:var(--vscode-button-hoverBackground); }
        .custom-dropdown-menu { display:none; position:absolute; top:100%; left:0; margin-top:4px; min-width:180px; background:var(--vscode-menu-background, var(--vscode-editor-background)); border:1px solid var(--vscode-panel-border); border-radius:6px; box-shadow:0 2px 8px rgba(0,0,0,0.2); z-index:20; overflow:hidden; }
        .custom-dropdown-menu.open { display:block; }
        .custom-dropdown-item { padding:6px 12px; cursor:pointer; font-size:12px; color:var(--vscode-foreground); }
        .custom-dropdown-item:hover { background:var(--vscode-list-hoverBackground, rgba(255,255,255,0.05)); border-radius:4px; }
    </style>
</head>
<body>
    <div class="tab-bar" role="tablist">
        <button class="tab-btn active" role="tab" data-tab="identity" aria-selected="true">Identity</button>
        <button class="tab-btn" role="tab" data-tab="properties" aria-selected="false">Properties</button>
        <button class="tab-btn" role="tab" data-tab="dependencies" aria-selected="false">Dependencies</button>
        <button class="tab-btn" role="tab" data-tab="applications" aria-selected="false">Applications</button>
        <button class="tab-btn" role="tab" data-tab="capabilities" aria-selected="false">Capabilities</button>
    </div>

    <!-- ───── Identity ───── -->
    <div class="tab-content active" id="tab-identity" role="tabpanel">
        <div class="section-header">Package Identity</div>
        <p class="description" style="margin-bottom:16px;">Uniquely identifies your app package in the Microsoft Store and on devices.</p>
        <div class="form-group" data-field="identity.name">
            <label for="identity-name">Package Name:</label>
            <input type="text" id="identity-name" data-section="identity" data-field-name="name" placeholder="com.company.app" />
            <div class="description">Reverse-domain style, e.g. com.company.app</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="identity.publisher">
            <label for="identity-publisher">Publisher:</label>
            <input type="text" id="identity-publisher" data-section="identity" data-field-name="publisher" placeholder="CN=Contoso, O=Contoso Ltd" />
            <div class="description">X.500 distinguished name, e.g. CN=Contoso, O=Contoso Ltd</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="identity.version">
            <label for="identity-version">Version:</label>
            <input type="text" id="identity-version" data-section="identity" data-field-name="version" placeholder="1.0.0.0" />
            <div class="description">Format: Major.Minor.Build.Revision (e.g. 1.0.0.0). The revision (last segment) must be 0 for Store submissions.</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="identity.processorArchitecture">
            <label for="identity-arch">Processor Architecture:</label>
            <select id="identity-arch" data-section="identity" data-field-name="processorArchitecture">
                ${archOptions}
            </select>
            <div class="validation-msg"></div>
        </div>
    </div>

    <!-- ───── Properties ───── -->
    <div class="tab-content" id="tab-properties" role="tabpanel">
        <div class="section-header">Package Properties</div>
        <p class="description" style="margin-bottom:16px;">Display information shown to users in the Microsoft Store and on the device.</p>
        <div class="form-group" data-field="properties.displayName">
            <label for="props-displayname">Display Name:</label>
            <input type="text" id="props-displayname" data-section="properties" data-field-name="displayName" placeholder="My Application" />
            <div class="description">Max 256 characters</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.publisherDisplayName">
            <label for="props-pubdisplayname">Publisher Display Name:</label>
            <input type="text" id="props-pubdisplayname" data-section="properties" data-field-name="publisherDisplayName" placeholder="Contoso" />
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.description">
            <label for="props-description">Description:</label>
            <textarea id="props-description" data-section="properties" data-field-name="description" placeholder="A short description of the app (optional, max 2048 chars)"></textarea>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.logo">
            <div class="logo-side-by-side">
                <div class="logo-input-col">
                    <label for="props-logo">Store Logo:</label>
                    <input type="text" id="props-logo" data-section="properties" data-field-name="logo" placeholder="Assets\\StoreLogo.png" />
                    <div class="description">Relative path to the store logo image</div>
                    <div class="validation-msg"></div>
                </div>
                <div class="logo-preview-col">
                    <img id="store-logo-preview" class="logo-preview" style="display:none;" alt="Store Logo preview" />
                    <div id="store-logo-caption" class="logo-caption"></div>
                </div>
            </div>
        </div>
        <div style="margin-top:12px;">
            <button class="btn" id="btn-update-assets">Regenerate Assets</button>
            <div class="description" style="margin-top:4px;">Generate scaled assets from a source image</div>
        </div>
    </div>

    <!-- ───── Dependencies ───── -->
    <div class="tab-content" id="tab-dependencies" role="tabpanel">
        <div class="section-header">Target Device Families</div>
        <p class="description" style="margin-bottom:16px;">Specifies which Windows device types your app targets.</p>
        <div id="target-device-families" class="list-container"></div>
        <div class="custom-dropdown" id="add-family-dropdown">
            <button class="custom-dropdown-btn" id="add-target-family">+ Add Target Device Family</button>
            <div class="custom-dropdown-menu" id="add-family-menu">
                ${DEVICE_FAMILY_OPTIONS.map(f => `<div class="custom-dropdown-item" data-family="${f}">${f}</div>`).join('')}
            </div>
        </div>

        <div class="section-header" style="margin-top:48px;">Package Dependencies</div>
        <div id="package-dependencies" class="list-container"></div>
        <button class="btn" id="add-package-dep">+ Add Package Dependency</button>
    </div>

    <!-- ───── Applications ───── -->
    <div class="tab-content" id="tab-applications" role="tabpanel">
        <div class="section-header">Applications</div>
        <p class="description" style="margin-bottom:16px;">Each application entry defines an executable, its visual assets, and extensions.</p>
        <div id="applications-list"></div>
    </div>

    <!-- ───── Capabilities ───── -->
    <div class="tab-content" id="tab-capabilities" role="tabpanel">
        <div class="section-header">Capabilities</div>
        <p class="description" style="margin-bottom:16px;">Hover over a capability to see its description. Capabilities declare what system resources or devices your app can access.</p>
        <div class="capabilities-columns">
            <div class="capabilities-left">
                <div class="cap-category">
                    <div class="cap-category-title">General</div>
                    <div class="cap-list">${generalCaps}</div>
                </div>
                <div class="cap-category">
                    <div class="cap-category-title">Restricted (rescap)</div>
                    <div class="cap-list">${restrictedCaps}</div>
                </div>
                <div class="cap-category">
                    <div class="cap-category-title">Device</div>
                    <div class="cap-list">${deviceCaps}</div>
                </div>
                <div class="cap-category">
                    <div class="cap-category-title">Custom Capability</div>
                    <div class="custom-cap-row">
                        <input type="text" id="custom-cap-input" placeholder="e.g. rescap:broadFileSystemAccess" />
                        <button class="btn" id="add-custom-cap">Add</button>
                    </div>
                    <div id="custom-caps-list" class="cap-list" style="margin-top:8px;"></div>
                </div>
            </div>
            <div class="capabilities-right">
                <div class="cap-description-panel" id="cap-description-panel">
                    <div class="cap-description-name" id="cap-description-name"></div>
                    <div class="cap-description-text" id="cap-description-text">Hover over a capability to see its description.</div>
                </div>
            </div>
        </div>
    </div>

    <script nonce="${nonce}">
    (function() {
        const vscode = acquireVsCodeApi();
        let currentData = null;
        const capabilityDescriptions = ${JSON.stringify(CAPABILITY_DESCRIPTIONS)};
        const extensionTemplates = ${JSON.stringify(EXTENSION_TEMPLATES)};
        const activeAppSubTabs = {};

        // ─── Tab switching ──────────────────────────────────
        document.querySelectorAll('.tab-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                document.querySelectorAll('.tab-btn').forEach(b => {
                    b.classList.remove('active');
                    b.setAttribute('aria-selected', 'false');
                });
                document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
                btn.classList.add('active');
                btn.setAttribute('aria-selected', 'true');
                const tab = btn.getAttribute('data-tab');
                document.getElementById('tab-' + tab).classList.add('active');
            });
        });

        // ─── Field change handler ───────────────────────────
        function onFieldChange(el) {
            const section = el.getAttribute('data-section');
            const field = el.getAttribute('data-field-name');
            const index = parseInt(el.getAttribute('data-index') || '0', 10);
            const value = el.value;
            vscode.postMessage({ type: 'fieldChanged', section, field, value, index });
        }

        // Debounce helper for text inputs
        let debounceTimers = {};
        function debouncedFieldChange(el) {
            const key = el.id || el.getAttribute('data-field-name');
            clearTimeout(debounceTimers[key]);
            debounceTimers[key] = setTimeout(() => onFieldChange(el), 300);
        }

        // Bind change events to static inputs
        document.querySelectorAll('input[data-section], textarea[data-section], select[data-section]').forEach(el => {
            if (el.tagName === 'SELECT') {
                el.addEventListener('change', () => onFieldChange(el));
            } else {
                el.addEventListener('input', () => debouncedFieldChange(el));
            }
        });

        // ─── Capability toggles ─────────────────────────────
        document.querySelectorAll('.cap-item input[type="checkbox"]').forEach(cb => {
            cb.addEventListener('change', () => {
                const cap = cb.getAttribute('data-capability');
                if (cb.checked) {
                    vscode.postMessage({ type: 'addCapability', capability: cap });
                } else {
                    vscode.postMessage({ type: 'removeCapability', capability: cap });
                }
            });
        });

        // Custom capability
        document.getElementById('add-custom-cap').addEventListener('click', () => {
            const input = document.getElementById('custom-cap-input');
            const cap = input.value.trim();
            if (cap) {
                vscode.postMessage({ type: 'addCapability', capability: cap });
                input.value = '';
            }
        });
        document.getElementById('custom-cap-input').addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                document.getElementById('add-custom-cap').click();
            }
        });

        // ─── Capability hover descriptions ──────────────────
        document.querySelectorAll('.cap-item').forEach(item => {
            item.addEventListener('mouseenter', () => {
                const cap = item.getAttribute('data-cap') || '';
                const rawName = cap.replace(/^(rescap:|device:)/, '');
                const desc = capabilityDescriptions[rawName] || 'No description available.';
                const nameEl = document.getElementById('cap-description-name');
                const textEl = document.getElementById('cap-description-text');
                if (nameEl) nameEl.textContent = item.querySelector('span')?.textContent || rawName;
                if (textEl) textEl.textContent = desc;
            });
        });

        // ─── Add/Remove target device family (dropdown) ─────
        document.getElementById('add-target-family').addEventListener('click', (e) => {
            e.stopPropagation();
            document.getElementById('add-family-menu').classList.toggle('open');
        });
        document.querySelectorAll('#add-family-menu .custom-dropdown-item').forEach(item => {
            item.addEventListener('click', () => {
                const name = item.getAttribute('data-family');
                vscode.postMessage({
                    type: 'addTargetDeviceFamily',
                    family: { name, minVersion: '10.0.17763.0', maxVersionTested: '10.0.26100.0' }
                });
                document.getElementById('add-family-menu').classList.remove('open');
            });
        });
        document.addEventListener('click', () => {
            document.getElementById('add-family-menu').classList.remove('open');
            document.querySelectorAll('.add-ext-menu').forEach(m => m.classList.remove('open'));
        });

        // ─── Add/Remove package dependency ──────────────────
        document.getElementById('add-package-dep').addEventListener('click', () => {
            vscode.postMessage({
                type: 'addPackageDependency',
                dependency: { name: '', minVersion: '', publisher: '' }
            });
        });

        document.getElementById('btn-update-assets').addEventListener('click', () => {
            vscode.postMessage({ type: 'updateAssets' });
        });

        // ─── Populate form from data ────────────────────────
        function populateForm(data) {
            currentData = data;

            // Identity
            document.getElementById('identity-name').value = data.identity.name;
            document.getElementById('identity-publisher').value = data.identity.publisher;
            document.getElementById('identity-version').value = data.identity.version;
            document.getElementById('identity-arch').value = data.identity.processorArchitecture;

            // Properties
            document.getElementById('props-displayname').value = data.properties.displayName;
            document.getElementById('props-pubdisplayname').value = data.properties.publisherDisplayName;
            document.getElementById('props-description').value = data.properties.description;
            document.getElementById('props-logo').value = data.properties.logo;

            updateLogoPreview(
                document.getElementById('store-logo-preview'),
                data.properties.logo,
                document.getElementById('store-logo-caption')
            );

            // Dependencies - Target Device Families
            renderTargetDeviceFamilies(data.dependencies.targetDeviceFamilies);
            renderPackageDependencies(data.dependencies.packageDependencies);

            // Applications
            renderApplications(data.applications);

            // Capabilities
            updateCapabilityCheckboxes(data.capabilities);
        }

        function renderTargetDeviceFamilies(families) {
            const container = document.getElementById('target-device-families');
            container.innerHTML = '';
            families.forEach((fam, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Target Device: \${escapeHtml(fam.name)}</span>
                        <button class="btn btn-danger btn-sm remove-family" data-index="\${idx}">Remove</button>
                    </div>
                    <div class="form-group" data-field="dependencies.targetDeviceFamily.\${idx}.minVersion">
                        <label>Min Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="targetDeviceFamily.minVersion" data-index="\${idx}" value="\${escapeHtml(fam.minVersion)}" placeholder="10.0.17763.0" />
                        <div class="description">10.0.XXXXX.0 format</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.targetDeviceFamily.\${idx}.maxVersionTested">
                        <label>Max Version Tested:</label>
                        <input type="text" data-section="dependencies" data-field-name="targetDeviceFamily.maxVersionTested" data-index="\${idx}" value="\${escapeHtml(fam.maxVersionTested)}" placeholder="10.0.26100.0" />
                        <div class="description">10.0.XXXXX.0 format</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);

                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                item.querySelector('.remove-family').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeTargetDeviceFamily', index: idx });
                });
            });
        }

        function renderPackageDependencies(deps) {
            const container = document.getElementById('package-dependencies');
            container.innerHTML = '';
            deps.forEach((dep, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Package Dependency: \${escapeHtml(dep.name) || '(unnamed)'}</span>
                        <button class="btn btn-danger btn-sm remove-pkg-dep" data-index="\${idx}">Remove</button>
                    </div>
                    <div class="form-group" data-field="dependencies.packageDependency.\${idx}.minVersion">
                        <label>Min Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="packageDependency.minVersion" data-index="\${idx}" value="\${escapeHtml(dep.minVersion)}" placeholder="14.0.0.0" />
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.packageDependency.\${idx}.publisher">
                        <label>Publisher:</label>
                        <input type="text" data-section="dependencies" data-field-name="packageDependency.publisher" data-index="\${idx}" value="\${escapeHtml(dep.publisher)}" placeholder="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);

                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                item.querySelector('.remove-pkg-dep').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removePackageDependency', index: idx });
                });
            });
        }

        function renderApplications(apps) {
            const container = document.getElementById('applications-list');
            container.innerHTML = '';
            apps.forEach((app, idx) => {
                const card = document.createElement('div');
                card.className = 'app-card';

                const activeTab = activeAppSubTabs[idx] || 'info';

                // Build extensions HTML
                let extListHtml = '';
                if (app.extensions && app.extensions.length > 0) {
                    app.extensions.forEach((extXml, eidx) => {
                        const fields = parseExtensionFields(extXml);
                        let fieldsHtml = fields.map(f =>
                            '<div class="form-group"><label>' + escapeHtml(f.label) + ':</label>' +
                            (f.editable
                                ? '<input type="text" value="' + escapeHtml(f.value) + '" readonly style="opacity:0.8;" />'
                                : '<input type="text" value="' + escapeHtml(f.value) + '" readonly style="opacity:0.6;font-style:italic;" />'
                            ) + '</div>'
                        ).join('');
                        extListHtml += '<div class="list-item"><div class="item-header"><span class="item-title">Extension #' + (eidx + 1) + '</span><button class="btn btn-danger btn-sm remove-ext" data-app-index="' + idx + '" data-ext-index="' + eidx + '">Remove</button></div>' + fieldsHtml + '</div>';
                    });
                }

                // Build add extension dropdown
                let addExtDropdown = '<div class="custom-dropdown add-ext-dropdown">' +
                    '<button class="custom-dropdown-btn add-ext-btn">+ Add Extension</button>' +
                    '<div class="custom-dropdown-menu add-ext-menu">';
                extensionTemplates.forEach(t => {
                    addExtDropdown += '<div class="custom-dropdown-item add-ext-item" data-app-index="' + idx + '" data-xml="' + escapeHtml(t.xml) + '">' + escapeHtml(t.label) + '</div>';
                });
                addExtDropdown += '</div></div>';

                card.innerHTML = \`
                    <div class="app-card-header">
                        <span class="app-card-title">Application: \${escapeHtml(app.id || '(unnamed)')}</span>
                    </div>
                    <div class="app-sub-tabs">
                        <button class="app-sub-tab \${activeTab === 'info' ? 'active' : ''}" data-subtab="info" data-app-idx="\${idx}">Info</button>
                        <button class="app-sub-tab \${activeTab === 'extensions' ? 'active' : ''}" data-subtab="extensions" data-app-idx="\${idx}">Extensions</button>
                        <button class="app-sub-tab \${activeTab === 'visual' ? 'active' : ''}" data-subtab="visual" data-app-idx="\${idx}">Visual Assets</button>
                    </div>
                    <div class="app-sub-content \${activeTab === 'info' ? 'active' : ''}" data-subcontent="info" data-app-idx="\${idx}">
                        <div class="form-group" data-field="applications.\${idx}.id">
                            <label>Id:</label>
                            <input type="text" data-section="applications" data-field-name="id" data-index="\${idx}" value="\${escapeHtml(app.id)}" />
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.executable">
                            <label>Executable:</label>
                            <input type="text" data-section="applications" data-field-name="executable" data-index="\${idx}" value="\${escapeHtml(app.executable)}" />
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.entryPoint">
                            <label>Entry Point:</label>
                            <input type="text" data-section="applications" data-field-name="entryPoint" data-index="\${idx}" value="\${escapeHtml(app.entryPoint)}" />
                            <div class="validation-msg"></div>
                        </div>
                    </div>
                    <div class="app-sub-content \${activeTab === 'extensions' ? 'active' : ''}" data-subcontent="extensions" data-app-idx="\${idx}">
                        <p class="description" style="margin-bottom:12px;">App extensions register handlers, protocols, and other system integration points.</p>
                        \${extListHtml}
                        \${addExtDropdown}
                    </div>
                    <div class="app-sub-content \${activeTab === 'visual' ? 'active' : ''}" data-subcontent="visual" data-app-idx="\${idx}">
                        <p class="description" style="margin-bottom:12px;">Visual assets control how your app appears in the Start menu, taskbar, and Store.</p>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.displayName">
                            <label>Display Name:</label>
                            <input type="text" data-section="applications" data-field-name="visualElements.displayName" data-index="\${idx}" value="\${escapeHtml(app.visualElements.displayName)}" />
                            <div class="description">Max 256 characters</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.description">
                            <label>Description:</label>
                            <input type="text" data-section="applications" data-field-name="visualElements.description" data-index="\${idx}" value="\${escapeHtml(app.visualElements.description)}" />
                            <div class="description">Max 2048 characters</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.backgroundColor">
                            <label>Background Color:</label>
                            <div class="color-row">
                                <input type="color" data-section="applications" data-field-name="visualElements.backgroundColor" data-index="\${idx}" value="\${toColorValue(app.visualElements.backgroundColor)}" />
                                <input type="text" data-section="applications" data-field-name="visualElements.backgroundColor" data-index="\${idx}" value="\${escapeHtml(app.visualElements.backgroundColor)}" placeholder="#FFFFFF or transparent" />
                            </div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="logo-side-by-side" style="margin-top:12px;">
                            <div class="logo-input-col">
                                <div class="form-group" data-field="applications.\${idx}.visualElements.square150x150Logo">
                                    <label>Square 150x150 Logo:</label>
                                    <input type="text" data-section="applications" data-field-name="visualElements.square150x150Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.square150x150Logo)}" placeholder="Assets\\\\Square150x150Logo.png" />
                                    <div class="validation-msg"></div>
                                </div>
                                <div class="form-group" data-field="applications.\${idx}.visualElements.square44x44Logo">
                                    <label>Square 44x44 Logo:</label>
                                    <input type="text" data-section="applications" data-field-name="visualElements.square44x44Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.square44x44Logo)}" placeholder="Assets\\\\Square44x44Logo.png" />
                                    <div class="validation-msg"></div>
                                </div>
                            </div>
                            <div class="logo-preview-col">
                                <img class="logo-preview app-logo-preview" data-app-idx="\${idx}" style="display:none;" alt="Logo preview" />
                                <div class="logo-caption app-logo-caption" data-app-idx="\${idx}"></div>
                            </div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.wide310x150Logo">
                            <label>Wide 310x150 Logo:</label>
                            <input type="text" data-section="applications" data-field-name="visualElements.wide310x150Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.wide310x150Logo)}" placeholder="Assets\\\\Wide310x150Logo.png" />
                            <div class="description">Optional, in DefaultTile child element</div>
                            <div class="validation-msg"></div>
                        </div>
                    </div>
                \`;
                container.appendChild(card);

                // Bind sub-tab switching
                card.querySelectorAll('.app-sub-tab').forEach(tab => {
                    tab.addEventListener('click', () => {
                        const subtab = tab.getAttribute('data-subtab');
                        const appIdx = tab.getAttribute('data-app-idx');
                        activeAppSubTabs[appIdx] = subtab;
                        card.querySelectorAll('.app-sub-tab').forEach(t => t.classList.remove('active'));
                        card.querySelectorAll('.app-sub-content').forEach(c => c.classList.remove('active'));
                        tab.classList.add('active');
                        card.querySelector('.app-sub-content[data-subcontent="' + subtab + '"]').classList.add('active');
                    });
                });

                // Bind field events
                card.querySelectorAll('input[data-section], select[data-section]').forEach(inp => {
                    if (inp.type === 'color') {
                        inp.addEventListener('input', () => {
                            const textInput = card.querySelector('input[type="text"][data-field-name="' + inp.getAttribute('data-field-name') + '"]');
                            if (textInput) textInput.value = inp.value;
                            debouncedFieldChange(inp);
                        });
                    } else {
                        inp.addEventListener('input', () => debouncedFieldChange(inp));
                    }
                });

                // Bind extension remove buttons
                card.querySelectorAll('.remove-ext').forEach(btn => {
                    btn.addEventListener('click', () => {
                        vscode.postMessage({
                            type: 'removeExtension',
                            appIndex: parseInt(btn.getAttribute('data-app-index'), 10),
                            extIndex: parseInt(btn.getAttribute('data-ext-index'), 10)
                        });
                    });
                });

                // Bind add extension dropdown
                const addExtBtn = card.querySelector('.add-ext-btn');
                const addExtMenu = card.querySelector('.add-ext-menu');
                if (addExtBtn && addExtMenu) {
                    addExtBtn.addEventListener('click', (e) => {
                        e.stopPropagation();
                        addExtMenu.classList.toggle('open');
                    });
                    card.querySelectorAll('.add-ext-item').forEach(item => {
                        item.addEventListener('click', () => {
                            vscode.postMessage({
                                type: 'addExtension',
                                index: parseInt(item.getAttribute('data-app-index'), 10),
                                xml: item.getAttribute('data-xml')
                            });
                            addExtMenu.classList.remove('open');
                        });
                    });
                }

                // Update logo previews
                const logoPreview = card.querySelector('.app-logo-preview');
                const logoCaption = card.querySelector('.app-logo-caption');
                updateLogoPreview(logoPreview, app.visualElements.square150x150Logo, logoCaption);
            });
        }

        function updateCapabilityCheckboxes(capabilities) {
            // Uncheck all first
            document.querySelectorAll('.cap-item input[type="checkbox"]').forEach(cb => {
                cb.checked = false;
            });

            // Check matching known capabilities
            const knownCapNames = new Set();
            document.querySelectorAll('.cap-item input[type="checkbox"]').forEach(cb => {
                const cap = cb.getAttribute('data-capability');
                knownCapNames.add(cap);
                if (capabilities.includes(cap)) {
                    cb.checked = true;
                }
            });

            // Render custom capabilities (not in known list)
            const customCaps = capabilities.filter(c => !knownCapNames.has(c));
            const customList = document.getElementById('custom-caps-list');
            customList.innerHTML = '';
            customCaps.forEach(cap => {
                const label = document.createElement('label');
                label.className = 'cap-item';
                label.innerHTML = \`<input type="checkbox" checked data-custom-cap="\${escapeHtml(cap)}" /><span>\${escapeHtml(cap)}</span>\`;
                customList.appendChild(label);
                label.querySelector('input').addEventListener('change', (e) => {
                    if (!e.target.checked) {
                        vscode.postMessage({ type: 'removeCapability', capability: cap });
                    }
                });
            });
        }

        // ─── Validation display ─────────────────────────────
        function showValidationErrors(errors) {
            // Clear all existing errors
            document.querySelectorAll('.form-group').forEach(fg => {
                fg.classList.remove('has-error');
                const msg = fg.querySelector('.validation-msg');
                if (msg) { msg.className = 'validation-msg'; msg.textContent = ''; }
            });

            // Show new errors
            errors.forEach(err => {
                const fg = document.querySelector('.form-group[data-field="' + err.field + '"]');
                if (fg) {
                    fg.classList.add('has-error');
                    const msg = fg.querySelector('.validation-msg');
                    if (msg) {
                        msg.className = 'validation-msg ' + err.severity;
                        msg.textContent = err.message;
                    }
                }
            });
        }

        // ─── Message handler ────────────────────────────────
        window.addEventListener('message', event => {
            const msg = event.data;
            switch (msg.type) {
                case 'update':
                    populateForm(msg.data);
                    showValidationErrors(msg.errors || []);
                    break;
                case 'validationErrors':
                    showValidationErrors(msg.errors || []);
                    break;
                case 'refreshImages':
                    document.querySelectorAll('.logo-preview').forEach(img => {
                        if (img.src) img.src = img.src.split('?')[0] + '?t=' + Date.now();
                    });
                    break;
            }
        });

        // ─── Helpers ────────────────────────────────────────
        function escapeHtml(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        function updateLogoPreview(imgEl, logoPath, captionEl) {
            if (logoPath && imgEl) {
                imgEl.src = logoPath + '?t=' + Date.now();
                imgEl.style.display = 'block';
                imgEl.onerror = function() { imgEl.style.display = 'none'; if (captionEl) captionEl.textContent = ''; };
                if (captionEl) {
                    const parts = logoPath.replace(/\\\\/g, '/').split('/');
                    captionEl.textContent = parts[parts.length - 1];
                }
            } else if (imgEl) {
                imgEl.style.display = 'none';
                if (captionEl) captionEl.textContent = '';
            }
        }

        function parseExtensionFields(xml) {
            const parser = new DOMParser();
            const doc = parser.parseFromString(xml, 'application/xml');
            const root = doc.documentElement;
            if (!root) return [{ label: 'Raw XML', value: xml, editable: false }];
            const fields = [];
            const category = root.getAttribute('Category');
            if (category) fields.push({ label: 'Category', value: category, editable: false });
            function walk(el, depth) {
                for (let i = 0; i < el.attributes.length; i++) {
                    const attr = el.attributes[i];
                    if (attr.name === 'Category' && el === root) continue;
                    if (attr.name.startsWith('xmlns')) continue;
                    fields.push({ label: (el.localName || el.nodeName) + '.' + attr.name, value: attr.value, editable: true });
                }
                const children = el.childNodes;
                for (let j = 0; j < children.length; j++) {
                    if (children[j].nodeType === 1) walk(children[j], depth + 1);
                }
            }
            walk(root, 0);
            return fields;
        }

        function toColorValue(str) {
            if (!str || str === 'transparent') return '#000000';
            if (/^#[0-9a-fA-F]{6}$/.test(str)) return str;
            return '#000000';
        }

        // Signal ready
        vscode.postMessage({ type: 'ready' });
    })();
    </script>
</body>
</html>`;
}
