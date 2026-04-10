/**
 * Generates the HTML content for the AppxManifest editor webview.
 * Uses VS Code CSS variables for native theming.
 */

import * as vscode from 'vscode';
import { KNOWN_CAPABILITIES, ARCHITECTURE_OPTIONS, DEVICE_FAMILY_OPTIONS, EXTENSION_TEMPLATES, CAPABILITY_DESCRIPTIONS, OPTIONAL_VISUAL_ASSETS, SHOW_NAME_ON_TILES_OPTIONS } from './manifest-types';

/** Generates an error view shown when the manifest XML cannot be parsed. */
export function getParseErrorContent(webview: vscode.Webview, nonce: string, errorMessage: string): string {
    return /*html*/`<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>AppxManifest Editor</title>
    <style nonce="${nonce}">
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        html, body {
            height: 100%;
            font-family: var(--vscode-font-family, "Segoe UI", sans-serif);
            font-size: var(--vscode-font-size, 13px);
            color: var(--vscode-editor-foreground);
            background: var(--vscode-editor-background);
            display: flex; align-items: center; justify-content: center;
        }
        .error-container {
            max-width: 520px; text-align: center; padding: 40px;
        }
        .error-icon {
            font-size: 48px; margin-bottom: 16px;
            color: var(--vscode-errorForeground, #f44747);
        }
        .error-title {
            font-size: 18px; font-weight: 600; margin-bottom: 12px;
        }
        .error-message {
            font-size: 13px; color: var(--vscode-descriptionForeground);
            margin-bottom: 20px; line-height: 1.5;
        }
        .error-detail {
            font-family: var(--vscode-editor-font-family, monospace);
            font-size: 12px; background: var(--vscode-input-background);
            border: 1px solid var(--vscode-input-border, transparent);
            border-radius: 4px; padding: 10px; text-align: left;
            white-space: pre-wrap; word-break: break-word;
            color: var(--vscode-errorForeground, #f44747);
            margin-bottom: 20px;
        }
        .btn {
            padding: 6px 16px; font-size: 13px; font-family: inherit;
            cursor: pointer; border: none; border-radius: 2px;
            color: var(--vscode-button-foreground);
            background: var(--vscode-button-background);
        }
        .btn:hover { background: var(--vscode-button-hoverBackground); }
    </style>
</head>
<body>
    <div class="error-container">
        <div class="error-icon">⚠</div>
        <div class="error-title">Unable to Open Manifest Editor</div>
        <div class="error-message">
            The appxmanifest file contains XML syntax errors that prevent the visual editor from loading.
            Please open the file in the text editor to fix the errors, then reopen this editor.
        </div>
        <div class="error-detail">${errorMessage.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')}</div>
        <button class="btn" id="open-as-text">Open in Text Editor</button>
    </div>
    <script nonce="${nonce}">
        const vscode = acquireVsCodeApi();
        document.getElementById('open-as-text').addEventListener('click', () => {
            vscode.postMessage({ type: 'openAsText' });
        });
        // Listen for retry signal when document is fixed externally
        window.addEventListener('message', event => {
            if (event.data.type === 'retryParse') {
                vscode.postMessage({ type: 'ready' });
            }
        });
    </script>
</body>
</html>`;
}

export function getWebviewContent(webview: vscode.Webview, nonce: string, manifestDirUri: string): string {
    const archOptionItems = ARCHITECTURE_OPTIONS.map(a => `<div class="custom-select-option" data-value="${a}">${a}</div>`).join('');

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
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
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
        .tab-bar-spacer { flex: 1; }
        .view-xml-btn {
            padding: 6px 12px;
            border: none;
            background: transparent;
            color: var(--vscode-foreground);
            cursor: pointer;
            font-size: var(--vscode-font-size, 13px);
            font-family: inherit;
            opacity: 0.7;
            transition: opacity 0.1s;
            display: flex;
            align-items: center;
            gap: 4px;
            margin-right: 8px;
        }
        .view-xml-btn:hover { opacity: 1; }
        .view-xml-btn:focus-visible {
            outline: 1px solid var(--vscode-focusBorder);
            outline-offset: -1px;
        }
        .view-xml-icon {
            font-size: 14px;
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
        .section-header-spaced {
            margin-top: 64px;
        }

        /* ─── Page description ────────────────────────────── */
        .page-description {
            font-size: 12px;
            color: var(--vscode-descriptionForeground);
            margin-bottom: 36px;
        }
        .page-description a,
        .doc-link {
            color: var(--vscode-textLink-foreground, #3794ff);
            text-decoration: none;
        }
        .page-description a:hover,
        .doc-link:hover {
            color: var(--vscode-textLink-activeForeground, #3794ff);
            text-decoration: underline;
        }

        /* ─── Info banner (toast-style, bottom-right) ─────── */
        .info-banner {
            position: fixed;
            bottom: 16px;
            right: 16px;
            z-index: 100;
            display: flex;
            align-items: flex-start;
            gap: 8px;
            padding: 10px 14px;
            max-width: 340px;
            font-size: 12px;
            line-height: 1.5;
            color: var(--vscode-editorInfo-foreground, var(--vscode-foreground));
            background: var(--vscode-editorWidget-background, var(--vscode-input-background));
            border: 1px solid var(--vscode-editorWidget-border, var(--vscode-panel-border, transparent));
            border-radius: 4px;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
        }
        .info-banner-icon {
            flex-shrink: 0;
            font-size: 14px;
        }
        .info-banner-link {
            color: var(--vscode-textLink-foreground, #3794ff);
            cursor: pointer;
            text-decoration: underline;
        }
        .info-banner-link:hover {
            color: var(--vscode-textLink-activeForeground, #3794ff);
        }

        /* ─── Utility classes (avoid inline styles blocked by CSP) ─── */
        .hidden { display: none; }
        .mt-8 { margin-top: 8px; }
        .mt-12 { margin-top: 12px; }
        .mb-8 { margin-bottom: 8px; }
        .mb-12 { margin-bottom: 12px; }
        .ext-field-readonly { opacity: 0.8; }
        .ext-field-computed { opacity: 0.6; font-style: italic; }
        .browse-row { display: flex; gap: 8px; align-items: stretch; }
        .browse-row input[type="text"] { flex: 1; }
        .browse-row .btn { align-self: stretch; }
        .browse-file-btn, .browse-image-btn { white-space: nowrap; }

        /* ─── Form groups ──────────────────────────────────── */
        .form-group {
            margin-bottom: 16px;
        }
        .form-group label {
            display: block;
            margin-bottom: 4px;
            font-weight: 600;
            color: var(--vscode-foreground);
            font-size: 13px;
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

        /* ─── Custom select (styled dropdown) ─────────────── */
        .custom-select { position:relative; width:100%; }
        .custom-select-trigger {
            width:100%; padding:4px 28px 4px 8px; font-family:inherit;
            font-size:var(--vscode-font-size, 13px); color:var(--vscode-input-foreground);
            background:var(--vscode-input-background); border:1px solid var(--vscode-input-border, transparent);
            border-radius:2px; outline:none; cursor:pointer; text-align:left;
        }
        .custom-select-trigger::after {
            content:'▾'; position:absolute; right:8px; top:50%; transform:translateY(-50%);
            pointer-events:none; font-size:12px; color:var(--vscode-descriptionForeground);
        }
        .custom-select-trigger:focus { border-color:var(--vscode-focusBorder, #007acc); }
        .custom-select-options {
            display:none; position:absolute; top:100%; left:0; right:0; margin-top:2px;
            background:var(--vscode-menu-background, var(--vscode-editor-background));
            border:1px solid var(--vscode-panel-border); border-radius:6px;
            box-shadow:0 2px 8px rgba(0,0,0,0.2); z-index:20; padding:4px; max-height:200px; overflow-y:auto;
        }
        .custom-select-options.open { display:block; }
        .custom-select-option {
            padding:5px 10px; cursor:pointer; font-size:13px; color:var(--vscode-foreground); border-radius:4px;
        }
        .custom-select-option:hover { background:var(--vscode-list-hoverBackground, rgba(255,255,255,0.05)); }
        .custom-select-option.selected { background:var(--vscode-list-activeSelectionBackground, rgba(255,255,255,0.1)); color:var(--vscode-list-activeSelectionForeground, var(--vscode-foreground)); }
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
        .form-group.has-warning input,
        .form-group.has-warning select,
        .form-group.has-warning textarea {
            border-color: var(--vscode-editorWarning-foreground, #cca700);
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
        .btn-secondary {
            color: var(--vscode-button-foreground);
            background: transparent;
            border: 1px solid var(--vscode-button-foreground);
        }
        .btn-secondary:hover {
            background: var(--vscode-toolbar-hoverBackground, rgba(90, 93, 94, 0.31));
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
        .section-label {
            font-weight: 600;
            font-size: 13px;
            color: var(--vscode-foreground);
            display: block;
        }
        .tile-checkboxes {
            display: flex;
            flex-direction: column;
            gap: 8px;
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

        .logo-preview { width:64px; height:64px; object-fit:contain; border-radius:4px; display:none; }
        .logo-preview.loaded { display:block; border:1px solid var(--vscode-panel-border); }
        .logo-side-by-side { display:flex; gap:16px; align-items:flex-start; }
        .logo-input-col { flex:1; }
        .logo-preview-col { flex-shrink:0; width:140px; display:flex; flex-direction:column; align-items:center; }
        .logo-caption { font-size:11px; font-style:italic; color:var(--vscode-descriptionForeground); margin-top:4px; text-align:center; width:140px; }

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
        .custom-dropdown-menu { display:none; position:absolute; top:100%; left:0; margin-top:4px; min-width:180px; background:var(--vscode-menu-background, var(--vscode-editor-background)); border:1px solid var(--vscode-panel-border); border-radius:6px; box-shadow:0 2px 8px rgba(0,0,0,0.2); z-index:20; padding:4px; }
        .custom-dropdown-menu.open { display:block; }
        .custom-dropdown-item { padding:6px 12px; cursor:pointer; font-size:12px; color:var(--vscode-foreground); border-radius:4px; }
        .custom-dropdown-item:hover { background:var(--vscode-list-hoverBackground, rgba(255,255,255,0.05)); }
    </style>
</head>
<body>
    <div class="tab-bar" role="tablist">
        <button class="tab-btn active" role="tab" data-tab="identity" aria-selected="true">Identity</button>
        <button class="tab-btn" role="tab" data-tab="properties" aria-selected="false">Properties</button>
        <button class="tab-btn" role="tab" data-tab="dependencies" aria-selected="false">Dependencies</button>
        <button class="tab-btn" role="tab" data-tab="resources" aria-selected="false">Resources</button>
        <button class="tab-btn" role="tab" data-tab="applications" aria-selected="false">Applications</button>
        <button class="tab-btn" role="tab" data-tab="capabilities" aria-selected="false">Capabilities</button>
        <div class="tab-bar-spacer"></div>
        <button class="view-xml-btn" id="view-xml-btn" title="Open in text editor"><span class="view-xml-icon">{ }</span> View XML</button>
    </div>

    <!-- ───── Identity ───── -->
    <div class="tab-content active" id="tab-identity" role="tabpanel">
        <div class="section-header">Package Identity</div>
        <p class="page-description">Use this page to define the unique identity of your app package. These values determine how Windows and the Microsoft Store distinguish your package from all others. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-identity">Learn more</a></p>
        <div class="form-group" data-field="identity.name">
            <label for="identity-name">Package Name:</label>
            <input type="text" id="identity-name" data-section="identity" data-field-name="name" placeholder="com.company.app" />
            <div class="description">Unique identifier for your package in reverse-domain style (e.g. com.company.app), used internally by Windows and the Store</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="identity.publisher">
            <label for="identity-publisher">Publisher:</label>
            <input type="text" id="identity-publisher" data-section="identity" data-field-name="publisher" placeholder="CN=Contoso, O=Contoso Ltd" />
            <div class="description">X.500 distinguished name that identifies the publisher (e.g. CN=Contoso, O=Contoso Ltd), must match the subject name of your code-signing certificate</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="identity.version">
            <label for="identity-version">Version:</label>
            <input type="text" id="identity-version" data-section="identity" data-field-name="version" placeholder="1.0.0.0" />
            <div class="description">Version of your application, revision (last segment) must be 0 for Store submissions</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="identity.processorArchitecture">
            <label>Processor Architecture:</label>
            <div class="custom-select" id="arch-select">
                <button class="custom-select-trigger" id="arch-select-trigger" type="button"></button>
                <div class="custom-select-options" id="arch-select-options">
                    ${archOptionItems}
                </div>
            </div>
            <div class="description">CPU architecture this package targets</div>
            <div class="validation-msg"></div>
        </div>
        <div id="phone-identity-section" class="section-header-spaced" style="display:none;">
            <div class="section-header">Phone Identity</div>
            <p class="page-description">Legacy phone identity fields. These are commonly included in WinUI 3 app manifests for backward compatibility.</p>
            <div class="form-group" data-field="phoneIdentity.phoneProductId">
                <label for="phone-product-id">Phone Product ID:</label>
                <input type="text" id="phone-product-id" data-section="phoneIdentity" data-field-name="phoneProductId" placeholder="00000000-0000-0000-0000-000000000000" />
                <div class="description">GUID that identifies the product, carried over from Windows Phone 8</div>
                <div class="validation-msg"></div>
            </div>
            <div class="form-group" data-field="phoneIdentity.phonePublisherId">
                <label for="phone-publisher-id">Phone Publisher ID:</label>
                <input type="text" id="phone-publisher-id" data-section="phoneIdentity" data-field-name="phonePublisherId" placeholder="00000000-0000-0000-0000-000000000000" />
                <div class="description">GUID that identifies the publisher, typically all zeros for desktop apps</div>
                <div class="validation-msg"></div>
            </div>
        </div>
    </div>

    <!-- ───── Properties ───── -->
    <div class="tab-content" id="tab-properties" role="tabpanel">
        <div class="section-header">Package Properties</div>
        <p class="page-description">Use this page to configure the user-facing display information for your app. These values appear in the Microsoft Store listing, app info dialogs, and the Windows shell. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-properties">Learn more</a></p>
        <div class="form-group" data-field="properties.displayName">
            <label for="props-displayname">Display Name:</label>
            <input type="text" id="props-displayname" data-section="properties" data-field-name="displayName" placeholder="My Application" />
            <div class="description">App name shown to users in the Start menu and Store, max 256 characters</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.publisherDisplayName">
            <label for="props-pubdisplayname">Publisher Display Name:</label>
            <input type="text" id="props-pubdisplayname" data-section="properties" data-field-name="publisherDisplayName" placeholder="Contoso" />
            <div class="description">Publisher name shown in the Store and app info, max 256 characters</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.description">
            <label for="props-description">Description:</label>
            <textarea id="props-description" data-section="properties" data-field-name="description" placeholder="A short description of the app (optional, max 2048 chars)"></textarea>
            <div class="description">Short summary of what your app does used in Store listings and app info dialogs, max 2048 characters (Optional)</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.logo">
            <div class="logo-side-by-side">
                <div class="logo-input-col">
                    <label for="props-logo">Store Logo:</label>
                    <div class="browse-row">
                        <input type="text" id="props-logo" data-section="properties" data-field-name="logo" placeholder="Assets\\StoreLogo.png" />
                        <button class="btn btn-sm browse-image-btn" data-section="properties" data-field-name="logo">Choose file</button>
                    </div>
                    <div class="description">Relative path to the image displayed in the Microsoft Store and app installer, should be a 50×50 pixel PNG path relative to the manifest file location</div>
                    <div class="validation-msg"></div>
                </div>
                <div class="logo-preview-col">
                    <img id="store-logo-preview" class="logo-preview" />
                    <div id="store-logo-caption" class="logo-caption"></div>
                </div>
            </div>
        </div>
    </div>

    <!-- ───── Dependencies ───── -->
    <div class="tab-content" id="tab-dependencies" role="tabpanel">
        <div class="section-header">Target Device Families</div>
        <p class="page-description">Use this page to declare the Windows versions and framework packages your app requires. Target device families determine which devices can install your package. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-dependencies">Learn more</a></p>
        <div id="target-device-families" class="list-container"></div>
        <div class="custom-dropdown" id="add-family-dropdown">
            <button class="custom-dropdown-btn" id="add-target-family">+ Add Target Device Family</button>
            <div class="custom-dropdown-menu" id="add-family-menu">
                ${DEVICE_FAMILY_OPTIONS.map(f => `<div class="custom-dropdown-item" data-family="${f}">${f}</div>`).join('')}
            </div>
        </div>

        <div class="section-header section-header-spaced">Package Dependencies</div>
        <div id="package-dependencies" class="list-container"></div>
        <button class="btn" id="add-package-dep">+ Add Package Dependency</button>
    </div>

    <!-- ───── Resources ───── -->
    <div class="tab-content" id="tab-resources" role="tabpanel">
        <div class="section-header">Resources</div>
        <p class="page-description">Use this page to declare the language resources your app supports. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-resources">Learn more</a></p>
        <div id="resources-list" class="list-container"></div>
        <button class="btn" id="add-resource-btn">+ Add Resource</button>
    </div>

    <!-- ───── Applications ───── -->
    <div class="tab-content" id="tab-applications" role="tabpanel">
        <div class="section-header">Applications</div>
        <p class="page-description">Use this page to configure the entry points and visual presentation of your app. Each Application element represents a separate executable that can be launched from the package. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-application">Learn more</a></p>
        <div id="applications-list"></div>
        <button class="btn mt-12" id="add-application-btn">+ Add Application</button>
    </div>

    <!-- ───── Capabilities ───── -->
    <div class="tab-content" id="tab-capabilities" role="tabpanel">
        <div class="section-header">Capabilities</div>
        <p class="page-description">Use this page to declare the system resources and devices your app needs access to. Users will be prompted to grant restricted capabilities at install time. Only request capabilities your app actually uses. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-capabilities">Learn more</a></p>
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
                    <div id="custom-caps-list" class="cap-list mt-8"></div>
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

    <div class="info-banner">
        <span class="info-banner-icon">ℹ</span>
        <span>This editor does not support all appxmanifest customizations. For advanced scenarios, <a class="info-banner-link" id="open-xml-link">open the XML source</a>. Missing a feature? <a class="info-banner-link" href="https://github.com/microsoft/winappCli/issues">File feedback</a>.</span>
    </div>

    <script nonce="${nonce}">
    (function() {
        const vscode = acquireVsCodeApi();
        const manifestDirUri = '${manifestDirUri}';
        let currentData = null;
        const capabilityDescriptions = ${JSON.stringify(CAPABILITY_DESCRIPTIONS)};
        const extensionTemplates = ${JSON.stringify(EXTENSION_TEMPLATES)};
        const optionalVisualAssets = ${JSON.stringify(OPTIONAL_VISUAL_ASSETS)};
        const showNameOnTilesOptions = ${JSON.stringify(SHOW_NAME_ON_TILES_OPTIONS)};
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

        // ─── View XML / Open as text ────────────────────────
        document.getElementById('view-xml-btn').addEventListener('click', () => {
            vscode.postMessage({ type: 'openAsText' });
        });
        document.getElementById('open-xml-link').addEventListener('click', () => {
            vscode.postMessage({ type: 'openAsText' });
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

        // ─── Image browse buttons (static) ──────────────────
        document.querySelectorAll('.browse-image-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const msg = {
                    type: 'browseImage',
                    section: btn.getAttribute('data-section'),
                    field: btn.getAttribute('data-field-name'),
                };
                const idx = btn.getAttribute('data-index');
                if (idx !== null) { msg.index = parseInt(idx, 10); }
                vscode.postMessage(msg);
            });
        });

        // ─── Architecture custom select ──────────────────────
        const archTrigger = document.getElementById('arch-select-trigger');
        const archOptions = document.getElementById('arch-select-options');
        if (archTrigger && archOptions) {
            archTrigger.addEventListener('click', (e) => {
                e.stopPropagation();
                archOptions.classList.toggle('open');
            });
            archOptions.querySelectorAll('.custom-select-option').forEach(opt => {
                opt.addEventListener('click', () => {
                    const val = opt.getAttribute('data-value');
                    archTrigger.textContent = val;
                    archOptions.classList.remove('open');
                    archOptions.querySelectorAll('.custom-select-option').forEach(o => o.classList.remove('selected'));
                    opt.classList.add('selected');
                    vscode.postMessage({ type: 'fieldChanged', section: 'identity', field: 'processorArchitecture', value: val });
                });
            });
            document.addEventListener('click', () => { archOptions.classList.remove('open'); });
        }

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
                    family: { name, minVersion: '', maxVersionTested: '' }
                });
                document.getElementById('add-family-menu').classList.remove('open');
            });
        });
        document.addEventListener('click', () => {
            document.getElementById('add-family-menu').classList.remove('open');
            document.querySelectorAll('.add-ext-menu').forEach(m => m.classList.remove('open'));
            document.querySelectorAll('.add-visual-asset-menu').forEach(m => m.classList.remove('open'));
        });

        // ─── Add/Remove package dependency ──────────────────
        document.getElementById('add-package-dep').addEventListener('click', () => {
            vscode.postMessage({
                type: 'addPackageDependency',
                dependency: { name: '', minVersion: '', publisher: '' }
            });
        });

        // ─── Add application ────────────────────────────────
        document.getElementById('add-application-btn').addEventListener('click', () => {
            vscode.postMessage({ type: 'addApplication' });
        });

        // ─── Add resource ───────────────────────────────────
        document.getElementById('add-resource-btn').addEventListener('click', () => {
            vscode.postMessage({
                type: 'addResource',
                resource: { language: '' }
            });
        });

        // ─── Populate form from data ────────────────────────
        function populateForm(data) {
            currentData = data;

            // Save focused element info before DOM rebuild
            const focused = document.activeElement;
            let focusInfo = null;
            if (focused && (focused.tagName === 'INPUT' || focused.tagName === 'TEXTAREA' || focused.tagName === 'SELECT')) {
                focusInfo = {
                    section: focused.getAttribute('data-section'),
                    fieldName: focused.getAttribute('data-field-name'),
                    index: focused.getAttribute('data-index'),
                    id: focused.id,
                    selectionStart: focused.selectionStart,
                    selectionEnd: focused.selectionEnd,
                    type: focused.type,
                    extField: focused.getAttribute('data-ext-field'),
                    appIndex: focused.getAttribute('data-app-index'),
                    extIndex: focused.getAttribute('data-ext-index')
                };
            }

            // Identity
            setValueIfNotFocused('identity-name', data.identity.name, focused);
            setValueIfNotFocused('identity-publisher', data.identity.publisher, focused);
            setValueIfNotFocused('identity-version', data.identity.version, focused);

            // Update architecture custom select
            const archTrigger = document.getElementById('arch-select-trigger');
            if (archTrigger) {
                archTrigger.textContent = data.identity.processorArchitecture || '(select)';
                document.querySelectorAll('#arch-select-options .custom-select-option').forEach(opt => {
                    opt.classList.toggle('selected', opt.getAttribute('data-value') === data.identity.processorArchitecture);
                });
            }

            // Phone Identity
            const phoneSection = document.getElementById('phone-identity-section');
            if (data.phoneIdentity) {
                if (phoneSection) phoneSection.style.display = '';
                setValueIfNotFocused('phone-product-id', data.phoneIdentity.phoneProductId, focused);
                setValueIfNotFocused('phone-publisher-id', data.phoneIdentity.phonePublisherId, focused);
            } else {
                if (phoneSection) phoneSection.style.display = 'none';
            }

            // Properties
            setValueIfNotFocused('props-displayname', data.properties.displayName, focused);
            setValueIfNotFocused('props-pubdisplayname', data.properties.publisherDisplayName, focused);
            setValueIfNotFocused('props-description', data.properties.description, focused);
            setValueIfNotFocused('props-logo', data.properties.logo, focused);

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

            // Resources
            renderResources(data.resources);

            // Restore focus after DOM rebuild
            if (focusInfo) {
                restoreFocus(focusInfo);
            }
        }

        function setValueIfNotFocused(elementId, value, focusedEl) {
            const el = document.getElementById(elementId);
            if (el && el !== focusedEl) {
                el.value = value;
            }
        }

        function restoreFocus(info) {
            let target = null;
            // Try by ID first (for static inputs)
            if (info.id) {
                target = document.getElementById(info.id);
            }
            // Try extension field match
            if (!target && info.extField) {
                document.querySelectorAll('input[data-ext-field]').forEach(el => {
                    if (el.getAttribute('data-ext-field') === info.extField &&
                        el.getAttribute('data-app-index') === info.appIndex &&
                        el.getAttribute('data-ext-index') === info.extIndex) {
                        target = el;
                    }
                });
            }
            // Fall back to data attributes (for dynamically rendered inputs)
            if (!target && info.section && info.fieldName) {
                const selector = (info.type === 'color' ? 'input[type="color"]' : 'input:not([type="color"]), textarea, select');
                document.querySelectorAll(selector).forEach(el => {
                    if (el.getAttribute('data-section') === info.section &&
                        el.getAttribute('data-field-name') === info.fieldName &&
                        el.getAttribute('data-index') === info.index) {
                        target = el;
                    }
                });
            }
            if (target) {
                target.focus();
                // Restore cursor position for text inputs
                if (info.selectionStart !== null && info.selectionStart !== undefined &&
                    typeof target.setSelectionRange === 'function') {
                    try { target.setSelectionRange(info.selectionStart, info.selectionEnd); } catch(e) {}
                }
            }
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
                        <div class="description">Minimum Windows version required to install this package</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.targetDeviceFamily.\${idx}.maxVersionTested">
                        <label>Max Version Tested:</label>
                        <input type="text" data-section="dependencies" data-field-name="targetDeviceFamily.maxVersionTested" data-index="\${idx}" value="\${escapeHtml(fam.maxVersionTested)}" placeholder="10.0.26100.0" />
                        <div class="description">Highest Windows version app has tested against, must be ≥ Min Version, used to determine compatibility behavior</div>
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
                        <span class="item-title">Package Dependency:</span>
                        <button class="btn btn-danger btn-sm remove-pkg-dep" data-index="\${idx}">Remove</button>
                    </div>
                    <div class="form-group" data-field="dependencies.packageDependency.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="packageDependency.name" data-index="\${idx}" value="\${escapeHtml(dep.name)}" placeholder="Microsoft.VCLibs.140.00" />
                        <div class="description">Package identity name</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.packageDependency.\${idx}.minVersion">
                        <label>Min Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="packageDependency.minVersion" data-index="\${idx}" value="\${escapeHtml(dep.minVersion)}" placeholder="14.0.0.0" />
                        <div class="description">Minimum version required</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.packageDependency.\${idx}.publisher">
                        <label>Publisher:</label>
                        <input type="text" data-section="dependencies" data-field-name="packageDependency.publisher" data-index="\${idx}" value="\${escapeHtml(dep.publisher)}" placeholder="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />
                        <div class="description">X.500 distinguished name of the package publisher</div>
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

        function renderResources(resources) {
            const container = document.getElementById('resources-list');
            container.innerHTML = '';
            resources.forEach((res, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="form-group" data-field="resources.\${idx}.language">
                        <div class="item-header">
                            <label>Language:</label>
                            <button class="btn btn-danger btn-sm remove-resource" data-index="\${idx}">Remove</button>
                        </div>
                        <input type="text" data-section="resources" data-field-name="language" data-index="\${idx}" value="\${escapeHtml(res.language)}" placeholder="en-us" />
                        <div class="description">BCP-47 language tag (e.g. "en-us", "fr-fr", "ja-jp") or "x-generate"</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);

                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                item.querySelector('.remove-resource').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeResource', index: idx });
                });
            });
        }

        function buildOptionalAssetsHtml(app, idx) {
            let html = '';
            optionalVisualAssets.forEach(asset => {
                const val = app.visualElements[asset.field];
                if (val !== null && val !== undefined) {
                    html += '<div class="form-group" data-field="applications.' + idx + '.visualElements.' + asset.field + '">' +
                        '<label>' + escapeHtml(asset.label) + ':</label>' +
                        '<div class="browse-row">' +
                        '<input type="text" data-section="applications" data-field-name="visualElements.' + asset.field + '" data-index="' + idx + '" value="' + escapeHtml(val) + '" placeholder="' + escapeHtml(asset.placeholder) + '" />' +
                        '<button class="btn btn-sm browse-image-btn" data-section="applications" data-field-name="visualElements.' + asset.field + '" data-index="' + idx + '">Choose file</button>' +
                        '</div>' +
                        '<div class="description">' + escapeHtml(asset.description) + '</div>' +
                        '<div class="validation-msg"></div>' +
                        '</div>';
                }
            });
            return html;
        }

        function buildAddVisualAssetMenuHtml(app, idx) {
            let html = '';
            optionalVisualAssets.forEach(asset => {
                const val = app.visualElements[asset.field];
                if (val === null || val === undefined) {
                    html += '<div class="custom-dropdown-item add-visual-asset-item" data-app-index="' + idx + '" data-asset-field="' + asset.field + '">' + escapeHtml(asset.label) + '</div>';
                }
            });
            return html;
        }

        function hasUnspecifiedVisualAssets(app) {
            return optionalVisualAssets.some(asset => app.visualElements[asset.field] === null || app.visualElements[asset.field] === undefined);
        }

        function buildShowNameOnTilesHtml(app, idx) {
            // Only show checkboxes for tile sizes that have defined visual assets
            const ve = app.visualElements;
            const availableTiles = showNameOnTilesOptions.filter(opt => {
                // square150x150Logo is always required, so always a string
                if (opt.veField === 'square150x150Logo') return true;
                // Optional tiles: only show checkbox if the asset is defined (not null)
                return ve[opt.veField] !== null && ve[opt.veField] !== undefined;
            });
            if (availableTiles.length === 0) return '';

            const currentTiles = ve.showNameOnTiles || [];
            let html = '<div class="show-name-on-tiles-section mt-12">' +
                '<label class="section-label">Show App Name on Tiles:</label>' +
                '<div class="description mb-8">Select which tile sizes display the app name overlay.</div>' +
                '<div class="tile-checkboxes">';
            availableTiles.forEach(opt => {
                const checked = currentTiles.includes(opt.tile) ? ' checked' : '';
                html += '<label class="cap-item"><input type="checkbox" class="show-name-tile-cb" data-app-index="' + idx + '" data-tile="' + opt.tile + '"' + checked + ' /><span>' + escapeHtml(opt.label) + '</span></label>';
            });
            html += '</div></div>';
            return html;
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
                const requiredExtFields = new Set([
                    'ExeServer.Executable', 'ExeServer.DisplayName', 'Class.Id',
                    'AppExtension.Name', 'AppExtension.Id', 'AppExtension.DisplayName', 'AppExtension.PublicFolder',
                    'Registration', 'ExecutionAlias.Alias'
                ]);
                if (app.extensions && app.extensions.length > 0) {
                    app.extensions.forEach((extXml, eidx) => {
                        const fields = parseExtensionFields(extXml);
                        let fieldsHtml = fields.map(f => {
                            let descHtml = f.description ? '<div class="description">' + escapeHtml(f.description) + '</div>' : '';
                            const textContentAttr = f.isTextContent ? ' data-ext-text-content="true"' : '';
                            const isRequired = f.editable && requiredExtFields.has(f.label);
                            const isEmpty = f.editable && !f.value;
                            const errorClass = isRequired && isEmpty ? ' has-error' : '';
                            const errorMsg = isRequired && isEmpty ? '<div class="validation-msg error">This field is required.</div>' : '<div class="validation-msg"></div>';
                            if (!f.editable) {
                                return '<div class="form-group"><label>' + escapeHtml(f.label) + ':</label>' +
                                    '<input type="text" value="' + escapeHtml(f.value) + '" readonly class="ext-field-computed" />' +
                                    descHtml + '</div>';
                            }
                            // Add a browse button for Registration fields
                            const isBrowsable = f.isTextContent && f.label === 'Registration';
                            const inputHtml = '<input type="text" value="' + escapeHtml(f.value) + '" data-ext-field="' + escapeHtml(f.label) + '" data-app-index="' + idx + '" data-ext-index="' + eidx + '"' + textContentAttr + ' />';
                            if (isBrowsable) {
                                return '<div class="form-group' + errorClass + '"><label>' + escapeHtml(f.label) + ':</label>' +
                                    '<div class="browse-row">' + inputHtml +
                                    '<button class="btn btn-sm browse-file-btn" data-app-index="' + idx + '" data-ext-index="' + eidx + '" data-ext-field="' + escapeHtml(f.label) + '">Choose file</button>' +
                                    '</div>' + descHtml + errorMsg + '</div>';
                            }
                            return '<div class="form-group' + errorClass + '"><label>' + escapeHtml(f.label) + ':</label>' +
                                inputHtml + descHtml + errorMsg + '</div>';
                        }).join('');
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
                        \${apps.length > 1 ? '<button class="btn btn-danger btn-sm remove-app-btn" data-app-index="' + idx + '">Remove</button>' : ''}
                    </div>
                    <div class="app-sub-tabs">
                        <button class="app-sub-tab \${activeTab === 'info' ? 'active' : ''}" data-subtab="info" data-app-idx="\${idx}">Info</button>
                        <button class="app-sub-tab \${activeTab === 'extensions' ? 'active' : ''}" data-subtab="extensions" data-app-idx="\${idx}">Extensions</button>
                        <button class="app-sub-tab \${activeTab === 'visual' ? 'active' : ''}" data-subtab="visual" data-app-idx="\${idx}">Visual Assets</button>
                    </div>
                    <div class="app-sub-content \${activeTab === 'info' ? 'active' : ''}" data-subcontent="info" data-app-idx="\${idx}">
                        <p class="description mb-12">Configure the core identity and entry point of this application. <a class="doc-link" href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-application">Learn more</a></p>
                        <div class="form-group" data-field="applications.\${idx}.id">
                            <label>Id:</label>
                            <input type="text" data-section="applications" data-field-name="id" data-index="\${idx}" value="\${escapeHtml(app.id)}" />
                            <div class="description">Unique identifier used internally by Windows for activation</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.executable">
                            <label>Executable:</label>
                            <div class="browse-row">
                                <input type="text" data-section="applications" data-field-name="executable" data-index="\${idx}" value="\${escapeHtml(app.executable)}" />
                                <button class="btn btn-sm browse-exe-btn" data-section="applications" data-field-name="executable" data-index="\${idx}">Choose file</button>
                            </div>
                            <div class="description">Relative path to the .exe file inside the package</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.entryPoint">
                            <label>Entry Point:</label>
                            <input type="text" data-section="applications" data-field-name="entryPoint" data-index="\${idx}" value="\${escapeHtml(app.entryPoint)}" />
                            <div class="description">Activation type or runtime class, use 'Windows.FullTrustApplication' for desktop (Win32) apps</div>
                            <div class="validation-msg"></div>
                        </div>
                    </div>
                    <div class="app-sub-content \${activeTab === 'extensions' ? 'active' : ''}" data-subcontent="extensions" data-app-idx="\${idx}">
                        <p class="description mb-12">Extensions register your app for system integration points like URI protocols, file type associations, COM servers, and execution aliases. <a class="doc-link" href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-1-extension">Learn more</a></p>
                        \${extListHtml}
                        \${addExtDropdown}
                    </div>
                    <div class="app-sub-content \${activeTab === 'visual' ? 'active' : ''}" data-subcontent="visual" data-app-idx="\${idx}">
                        <p class="description mb-12">Visual assets define how your app appears in the Start menu, taskbar, and task switcher. Provide high-quality images at the correct sizes for a polished look. <a class="doc-link" href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap-visualelements">Learn more</a></p>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.displayName">
                            <label>Display Name:</label>
                            <input type="text" data-section="applications" data-field-name="visualElements.displayName" data-index="\${idx}" value="\${escapeHtml(app.visualElements.displayName)}" />
                            <div class="description">Name displayed on the app tile in the Start menu and in search results, max 256 characters</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.description">
                            <label>Description:</label>
                            <input type="text" data-section="applications" data-field-name="visualElements.description" data-index="\${idx}" value="\${escapeHtml(app.visualElements.description)}" />
                            <div class="description">Short description shown in app info tooltips and accessibility tools, max 2048 characters</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group" data-field="applications.\${idx}.visualElements.backgroundColor">
                            <label>Background Color:</label>
                            <div class="color-row">
                                <input type="color" data-section="applications" data-field-name="visualElements.backgroundColor" data-index="\${idx}" value="\${toColorValue(app.visualElements.backgroundColor)}" />
                                <input type="text" data-section="applications" data-field-name="visualElements.backgroundColor" data-index="\${idx}" value="\${escapeHtml(app.visualElements.backgroundColor)}" placeholder="#FFFFFF or transparent" />
                            </div>
                            <div class="description">Background color for the app tile, use a hex color or 'transparent'</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="logo-side-by-side mt-12">
                            <div class="logo-input-col">
                                <div class="form-group" data-field="applications.\${idx}.visualElements.square150x150Logo">
                                    <label>Square 150x150 Logo:</label>
                                    <div class="browse-row">
                                        <input type="text" data-section="applications" data-field-name="visualElements.square150x150Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.square150x150Logo)}" placeholder="Assets\\\\Square150x150Logo.png" />
                                        <button class="btn btn-sm browse-image-btn" data-section="applications" data-field-name="visualElements.square150x150Logo" data-index="\${idx}">Choose file</button>
                                    </div>
                                    <div class="description">Medium tile image shown in the Start menu, relative path to a 150×150 pixel PNG</div>
                                    <div class="validation-msg"></div>
                                </div>
                                <div class="form-group" data-field="applications.\${idx}.visualElements.square44x44Logo">
                                    <label>Square 44x44 Logo:</label>
                                    <div class="browse-row">
                                        <input type="text" data-section="applications" data-field-name="visualElements.square44x44Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.square44x44Logo)}" placeholder="Assets\\\\Square44x44Logo.png" />
                                        <button class="btn btn-sm browse-image-btn" data-section="applications" data-field-name="visualElements.square44x44Logo" data-index="\${idx}">Choose file</button>
                                    </div>
                                    <div class="description">Small app icon shown in the taskbar, task switcher, and notification area, relative path to a 44×44 pixel PNG</div>
                                    <div class="validation-msg"></div>
                                </div>
                            </div>
                            <div class="logo-preview-col">
                                <img class="logo-preview app-logo-preview" data-app-idx="\${idx}" />
                                <div class="logo-caption app-logo-caption" data-app-idx="\${idx}"></div>
                            </div>
                        </div>
                        <div class="optional-assets-list" data-app-idx="\${idx}">
                        \${buildOptionalAssetsHtml(app, idx)}
                        </div>
                        \${hasUnspecifiedVisualAssets(app) ? '<div class="custom-dropdown add-visual-asset-dropdown" data-app-idx="' + idx + '">' +
                            '<button class="custom-dropdown-btn add-visual-asset-btn">+ Add Visual Asset</button>' +
                            '<div class="custom-dropdown-menu add-visual-asset-menu">' +
                            buildAddVisualAssetMenuHtml(app, idx) +
                            '</div></div>' : ''}
                        \${buildShowNameOnTilesHtml(app, idx)}
                        <button class="btn update-assets-btn mt-12">Regenerate Assets</button>
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

                // Bind remove application button
                const removeAppBtn = card.querySelector('.remove-app-btn');
                if (removeAppBtn) {
                    removeAppBtn.addEventListener('click', () => {
                        vscode.postMessage({ type: 'removeApplication', index: parseInt(removeAppBtn.getAttribute('data-app-index'), 10) });
                    });
                }

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

                // Bind editable extension field inputs
                card.querySelectorAll('input[data-ext-field]').forEach(inp => {
                    let extDebounce = null;
                    inp.addEventListener('input', () => {
                        // Live validation for required extension fields
                        const fg = inp.closest('.form-group');
                        const fieldLabel = inp.getAttribute('data-ext-field');
                        const isReq = requiredExtFields.has(fieldLabel);
                        if (fg && isReq) {
                            if (!inp.value) {
                                fg.classList.add('has-error');
                                const vm = fg.querySelector('.validation-msg');
                                if (vm) { vm.className = 'validation-msg error'; vm.textContent = 'This field is required.'; }
                            } else {
                                fg.classList.remove('has-error');
                                const vm = fg.querySelector('.validation-msg');
                                if (vm) { vm.className = 'validation-msg'; vm.textContent = ''; }
                            }
                        }
                        clearTimeout(extDebounce);
                        extDebounce = setTimeout(() => {
                            vscode.postMessage({
                                type: 'updateExtensionField',
                                appIndex: parseInt(inp.getAttribute('data-app-index'), 10),
                                extIndex: parseInt(inp.getAttribute('data-ext-index'), 10),
                                fieldPath: inp.getAttribute('data-ext-field'),
                                value: inp.value,
                                isTextContent: inp.hasAttribute('data-ext-text-content')
                            });
                        }, 300);
                    });
                });

                // Bind browse file buttons
                card.querySelectorAll('.browse-file-btn').forEach(btn => {
                    btn.addEventListener('click', () => {
                        vscode.postMessage({
                            type: 'browseFile',
                            appIndex: parseInt(btn.getAttribute('data-app-index'), 10),
                            extIndex: parseInt(btn.getAttribute('data-ext-index'), 10),
                            fieldPath: btn.getAttribute('data-ext-field')
                        });
                    });
                });

                // Bind image browse buttons (dynamic in app cards)
                card.querySelectorAll('.browse-image-btn').forEach(btn => {
                    btn.addEventListener('click', () => {
                        const msg = {
                            type: 'browseImage',
                            section: btn.getAttribute('data-section'),
                            field: btn.getAttribute('data-field-name'),
                        };
                        const bIdx = btn.getAttribute('data-index');
                        if (bIdx !== null) { msg.index = parseInt(bIdx, 10); }
                        vscode.postMessage(msg);
                    });
                });

                // Bind exe browse buttons (dynamic in app cards)
                card.querySelectorAll('.browse-exe-btn').forEach(btn => {
                    btn.addEventListener('click', () => {
                        const msg = {
                            type: 'browseExe',
                            section: btn.getAttribute('data-section'),
                            field: btn.getAttribute('data-field-name'),
                        };
                        const bIdx = btn.getAttribute('data-index');
                        if (bIdx !== null) { msg.index = parseInt(bIdx, 10); }
                        vscode.postMessage(msg);
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

                // Bind optional visual asset inputs and browse buttons
                card.querySelectorAll('.optional-assets-list input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                card.querySelectorAll('.optional-assets-list .browse-image-btn').forEach(btn => {
                    btn.addEventListener('click', () => {
                        const msg = {
                            type: 'browseImage',
                            section: btn.getAttribute('data-section'),
                            field: btn.getAttribute('data-field-name'),
                        };
                        const bIdx = btn.getAttribute('data-index');
                        if (bIdx !== null) { msg.index = parseInt(bIdx, 10); }
                        vscode.postMessage(msg);
                    });
                });

                // Bind add visual asset dropdown
                const addVisualBtn = card.querySelector('.add-visual-asset-btn');
                const addVisualMenu = card.querySelector('.add-visual-asset-menu');
                if (addVisualBtn && addVisualMenu) {
                    addVisualBtn.addEventListener('click', (e) => {
                        e.stopPropagation();
                        addVisualMenu.classList.toggle('open');
                    });
                    card.querySelectorAll('.add-visual-asset-item').forEach(item => {
                        item.addEventListener('click', () => {
                            const appIndex = parseInt(item.getAttribute('data-app-index'), 10);
                            const assetField = item.getAttribute('data-asset-field');
                            const asset = optionalVisualAssets.find(a => a.field === assetField);
                            if (asset) {
                                vscode.postMessage({
                                    type: 'fieldChanged',
                                    section: 'applications',
                                    field: 'visualElements.' + assetField,
                                    value: '',
                                    index: appIndex
                                });
                            }
                            addVisualMenu.classList.remove('open');
                        });
                    });
                }

                // Bind ShowNameOnTiles checkboxes
                card.querySelectorAll('.show-name-tile-cb').forEach(cb => {
                    cb.addEventListener('change', () => {
                        const appIdx = parseInt(cb.getAttribute('data-app-index'), 10);
                        // Gather all checked tiles for this app
                        const tiles = [];
                        card.querySelectorAll('.show-name-tile-cb:checked').forEach(checked => {
                            tiles.push(checked.getAttribute('data-tile'));
                        });
                        vscode.postMessage({ type: 'setShowNameOnTiles', appIndex: appIdx, tiles: tiles });
                    });
                });

                // Update logo previews
                const logoPreview = card.querySelector('.app-logo-preview');
                const logoCaption = card.querySelector('.app-logo-caption');
                updateLogoPreview(logoPreview, app.visualElements.square150x150Logo, logoCaption);

                // Regenerate Assets button
                const updateAssetsBtn = card.querySelector('.update-assets-btn');
                if (updateAssetsBtn) {
                    updateAssetsBtn.addEventListener('click', () => {
                        vscode.postMessage({ type: 'updateAssets' });
                    });
                }
            });
        }

        function updateCapabilityCheckboxes(capabilities) {
            const capContainer = document.getElementById('tab-capabilities');
            // Uncheck all first (scoped to capabilities tab only)
            capContainer.querySelectorAll('.cap-item input[type="checkbox"]').forEach(cb => {
                cb.checked = false;
            });

            // Check matching known capabilities
            const knownCapNames = new Set();
            capContainer.querySelectorAll('.cap-item input[type="checkbox"]').forEach(cb => {
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
            // Clear only manifest-level validation errors (those with data-field), not extension field errors
            document.querySelectorAll('.form-group[data-field]').forEach(fg => {
                fg.classList.remove('has-error', 'has-warning');
                const msg = fg.querySelector('.validation-msg');
                if (msg) { msg.className = 'validation-msg'; msg.textContent = ''; }
            });

            // Show new errors
            errors.forEach(err => {
                const fg = document.querySelector('.form-group[data-field="' + err.field + '"]');
                if (fg) {
                    fg.classList.add(err.severity === 'warning' ? 'has-warning' : 'has-error');
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
            if (logoPath && manifestDirUri && imgEl) {
                // Start hidden; only show on successful load
                imgEl.classList.remove('loaded');
                imgEl.removeAttribute('alt');
                if (captionEl) captionEl.textContent = '';
                imgEl.onload = function() {
                    imgEl.classList.add('loaded');
                    if (captionEl) {
                        const parts = logoPath.replace(/\\\\/g, '/').split('/');
                        captionEl.textContent = parts[parts.length - 1];
                    }
                };
                imgEl.onerror = function() { imgEl.classList.remove('loaded'); if (captionEl) captionEl.textContent = ''; };
                imgEl.src = manifestDirUri + '/' + encodeURI(logoPath.replace(/\\\\/g, '/')) + '?t=' + Date.now();
            } else if (imgEl) {
                imgEl.classList.remove('loaded');
                imgEl.removeAttribute('alt');
                if (captionEl) captionEl.textContent = '';
            }
        }

        function parseExtensionFields(xml) {
            const parser = new DOMParser();
            const doc = parser.parseFromString(xml, 'application/xml');
            const root = doc.documentElement;
            if (!root) return [{ label: 'Raw XML', value: xml, editable: false, description: '' }];

            // Descriptions for known extension fields
            const fieldDescriptions = {
                'AppExtension.Name': 'Extension contract name, use "com.microsoft.windows.ai.mcpServer" to register as an MCP server',
                'AppExtension.Id': 'Unique identifier for this app extension',
                'AppExtension.DisplayName': 'Display name shown when discovering this extension',
                'AppExtension.PublicFolder': 'Folder in the package accessible to the host app, typically "Assets" or "Public"',
                'Registration': 'Path to the MCP server configuration JSON file, relative to the PublicFolder',
                'ExeServer.Executable': 'Relative path to the COM server executable',
                'ExeServer.DisplayName': 'Name for this COM server, shown in system tools',
                'Class.Id': 'CLSID (GUID) that uniquely identifies this COM class, format: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}',
                'Protocol.Name': 'URI scheme this app handles (e.g., "myapp"), users can launch your app with myapp://',
                'DisplayName': 'User-friendly display name for this extension',
                'ExecutionAlias.Alias': 'Command-line alias users type to launch your app from a terminal or Run dialog (e.g., "myapp.exe")',
            };

            const fields = [];
            const category = root.getAttribute('Category');
            if (category) fields.push({ label: 'Category', value: category, editable: false, description: 'Extension category type' });
            function walk(el, depth) {
                for (let i = 0; i < el.attributes.length; i++) {
                    const attr = el.attributes[i];
                    if (attr.name === 'Category' && el === root) continue;
                    if (attr.name.startsWith('xmlns')) continue;
                    const fieldKey = (el.localName || el.nodeName) + '.' + attr.name;
                    const desc = fieldDescriptions[fieldKey] || '';
                    fields.push({ label: fieldKey, value: attr.value, editable: true, description: desc });
                }
                // Check for text-content elements (leaf elements with only text children)
                let hasElementChildren = false;
                let textContent = '';
                const children = el.childNodes;
                for (let j = 0; j < children.length; j++) {
                    if (children[j].nodeType === 1) { hasElementChildren = true; }
                    else if (children[j].nodeType === 3) { textContent += children[j].nodeValue || ''; }
                }
                if (!hasElementChildren && textContent.trim()) {
                    const elName = el.localName || el.nodeName;
                    const desc = fieldDescriptions[elName] || '';
                    fields.push({ label: elName, value: textContent.trim(), editable: true, description: desc, isTextContent: true });
                } else if (!hasElementChildren && el !== root) {
                    // Empty leaf element (ignoring xmlns attrs) — show as editable blank field
                    let nonXmlnsAttrs = 0;
                    for (let k = 0; k < el.attributes.length; k++) {
                        if (!el.attributes[k].name.startsWith('xmlns')) nonXmlnsAttrs++;
                    }
                    if (nonXmlnsAttrs > 0) return; // has real attributes, already handled above
                    const elName = el.localName || el.nodeName;
                    const desc = fieldDescriptions[elName] || '';
                    fields.push({ label: elName, value: '', editable: true, description: desc, isTextContent: true });
                }
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
