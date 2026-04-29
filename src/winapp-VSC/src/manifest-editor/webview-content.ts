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

    const generalCaps= KNOWN_CAPABILITIES.general.map(c => {
        const capKey = c.namespace ? `${c.namespace}:${c.name}` : c.name;
        return `<label class="cap-item" data-cap="${capKey}">
            <input type="checkbox" data-capability="${capKey}" /><span>${c.label}</span>
        </label>`;
    }).join('');

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
        .tab-content { display: none; padding: 20px 24px 120px; max-width: 720px; }
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
        .subsection-header {
            font-size: 16px;
            font-weight: 600;
            color: var(--vscode-settings-headerForeground, var(--vscode-foreground));
            margin-bottom: 12px;
            padding-bottom: 4px;
            border-bottom: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
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
        .form-group.has-error textarea,
        .form-group.has-error .custom-select-trigger {
            border-color: var(--vscode-inputValidation-errorBorder, #f44747);
        }
        .form-group.has-warning input,
        .form-group.has-warning select,
        .form-group.has-warning textarea,
        .form-group.has-warning .custom-select-trigger {
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
        .item-actions {
            display: flex;
            gap: 4px;
            align-items: center;
            margin-left: auto;
        }
        .hidden-tab {
            display: none !important;
        }
        .optional-field.hidden-optional {
            display: none !important;
        }
        .btn-add-field.hidden-optional {
            display: none !important;
        }
        .btn-add-field {
            display: inline-block;
            padding: 4px 10px;
            font-size: 12px;
            cursor: pointer;
            color: var(--vscode-button-foreground);
            background: var(--vscode-button-background);
            border: none;
            border-radius: 2px;
            margin-bottom: 12px;
        }
        .btn-add-field:hover {
            background: var(--vscode-button-hoverBackground);
        }
        .optional-fields-group {
            display: flex;
            flex-direction: column;
        }
        .optional-fields-group > .optional-field { order: 0; }
        .optional-fields-group > .btn-add-buttons-row { order: 1; }
        .btn-add-buttons-row {
            display: flex;
            flex-direction: row;
            flex-wrap: wrap;
            gap: 8px;
        }
        .optional-field-content {
            display: flex;
            gap: 6px;
            align-items: center;
        }
        .optional-field-content input,
        .optional-field-content select {
            flex: 1;
        }
        .btn-remove-field {
            flex-shrink: 0;
            width: 24px;
            height: 24px;
            padding: 0;
            border: none;
            border-radius: 2px;
            background: rgba(128, 128, 128, 0.3);
            color: var(--vscode-editor-foreground, #ffffff);
            font-size: 14px;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .btn-remove-field:hover {
            background: rgba(128, 128, 128, 0.5);
        }
        .btn-remove-section {
            width: 24px;
            height: 24px;
            padding: 0;
            border: none;
            border-radius: 2px;
            background: rgba(128, 128, 128, 0.3);
            color: var(--vscode-editor-foreground, #ffffff);
            font-size: 14px;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            vertical-align: middle;
            margin-left: 8px;
        }
        .btn-remove-section:hover {
            background: rgba(128, 128, 128, 0.5);
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
            height: 24px;
            background: rgba(128, 128, 128, 0.35);
            color: var(--vscode-foreground);
        }
        .btn-sm:hover {
            background: rgba(128, 128, 128, 0.55);
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
        <p class="page-description">Use this section to define the unique identity of your package. These values determine how Windows and the Microsoft Store distinguish your package from all others. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-identity">Learn more</a></p>
        <div class="form-group" data-field="identity.name">
            <label for="identity-name">Name:</label>
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
                <button class="custom-select-trigger" id="arch-select-trigger" type="button" data-section="identity" data-field-name="processorArchitecture">(select)</button>
                <div class="custom-select-options" id="arch-select-options">
                    ${archOptionItems}
                </div>
            </div>
            <div class="description">CPU architecture this package targets</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group optional-field" data-field="identity.resourceId" id="identity-resourceid-group">
            <label for="identity-resourceid">Resource ID:</label>
            <div class="optional-field-content">
                <input type="text" id="identity-resourceid" data-section="identity" data-field-name="resourceId" placeholder="e.g. SplitConfig" />
                <button class="btn-remove-field" type="button" data-target="identity-resourceid-group" title="Remove Resource ID">✕</button>
            </div>
            <div class="description">Optional string used to differentiate packages that are part of a resource bundle or bundle optional packages (max 30 chars, alphanumeric/period/dash only)</div>
            <div class="validation-msg"></div>
        </div>
        <button class="btn-add-field" type="button" id="add-identity-resourceid" data-target="identity-resourceid-group" data-section="identity" data-field-name="resourceId" data-default="" title="Add Resource ID attribute">+ Add Resource ID</button>
        <button class="btn-add-field" type="button" id="add-phone-identity-btn" title="Add Phone Identity element">+ Phone Identity</button>
        <div id="phone-identity-section" class="section-header-spaced" style="display:none;">
            <div class="section-header">Phone Identity <button class="btn-remove-section" type="button" id="remove-phone-identity-btn" title="Remove Phone Identity element">✕</button></div>
            <p class="page-description">Use this section to configure legacy phone identity fields. These are commonly included in WinUI 3 app manifests for backward compatibility.</p>
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
        <p class="page-description">Use this section to configure the user-facing display information for your package. These values appear in the Microsoft Store listing, package details, and the Windows shell. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-properties">Learn more</a></p>
        <div class="form-group" data-field="properties.displayName">
            <label for="props-displayname">Display Name:</label>
            <input type="text" id="props-displayname" data-section="properties" data-field-name="displayName" placeholder="My Application" />
            <div class="description">Package name shown in Settings (Installed apps), the Microsoft Store, and other system surfaces, max 256 characters</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.publisherDisplayName">
            <label for="props-pubdisplayname">Publisher Display Name:</label>
            <input type="text" id="props-pubdisplayname" data-section="properties" data-field-name="publisherDisplayName" placeholder="Contoso" />
            <div class="description">Publisher name shown in Settings (Installed apps), the Microsoft Store, and package details, max 256 characters</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group" data-field="properties.description">
            <label for="props-description">Description:</label>
            <textarea id="props-description" data-section="properties" data-field-name="description" placeholder="A short description of your package"></textarea>
            <div class="description">Short summary of your package used in Store listings and package details, max 2048 characters (Optional)</div>
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
                    <div class="description">Package-relative path or key in resources.pri for the image displayed in the Microsoft Store and app installer, should be a PNG file</div>
                    <div class="validation-msg"></div>
                </div>
                <div class="logo-preview-col">
                    <img id="store-logo-preview" class="logo-preview" />
                    <div id="store-logo-caption" class="logo-caption"></div>
                </div>
            </div>
        </div>

        <div class="section-header section-header-spaced">Package Type</div>
        <p class="page-description">Use this section to control what type of package this is. Most packages are Application packages. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-properties">Learn more</a></p>
        <div class="form-group" data-field="properties.packageType">
            <label>Package Type:</label>
            <div class="custom-select" id="pkg-type-select">
                <button class="custom-select-trigger" id="pkg-type-select-trigger" type="button">Application (default)</button>
                <div class="custom-select-options" id="pkg-type-select-options">
                    <div class="custom-select-option selected" data-value="application">Application (default)</div>
                    <div class="custom-select-option" data-value="framework">Framework</div>
                    <div class="custom-select-option" data-value="resource">Resource</div>
                    <div class="custom-select-option" data-value="modification">Modification</div>
                </div>
            </div>
            <div class="description">Application packages contain executable code and UI. Framework packages provide shared runtime libraries. Resource packages contain only language/scale assets. Modification packages customize a main package.</div>
        </div>

        <div class="section-header section-header-spaced">Advanced Properties</div>
        <p class="page-description">Use this section to configure optional advanced package properties such as user scope, automatic updates, integrity enforcement, and update behavior.</p>
        <div class="form-group" data-field="properties.supportedUsers">
            <label>Supported Users:</label>
            <div class="custom-select" id="props-supportedUsers">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="supportedUsers">(omit)</button>
                <div class="custom-select-options">
                    <div class="custom-select-option selected" data-value="">(omit)</div>
                    <div class="custom-select-option" data-value="multiple">multiple</div>
                    <div class="custom-select-option" data-value="single">single</div>
                </div>
            </div>
            <div class="description">Whether the app supports multiple user sessions or only a single user</div>
        </div>
        <div class="form-group" data-field="properties.allowExecution">
            <label>Allow Execution:</label>
            <div class="custom-select" id="props-allowExecution">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="allowExecution">(omit)</button>
                <div class="custom-select-options">
                    <div class="custom-select-option selected" data-value="">(omit)</div>
                    <div class="custom-select-option" data-value="true">true</div>
                    <div class="custom-select-option" data-value="false">false</div>
                </div>
            </div>
            <div class="description">Whether executables in the package can be launched (set to false for content-only packages)</div>
        </div>
        <div class="form-group" data-field="properties.allowExternalContent">
            <label>Allow External Content:</label>
            <div class="custom-select" id="props-allowExternalContent">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="allowExternalContent">(omit)</button>
                <div class="custom-select-options">
                    <div class="custom-select-option selected" data-value="">(omit)</div>
                    <div class="custom-select-option" data-value="true">true</div>
                    <div class="custom-select-option" data-value="false">false</div>
                </div>
            </div>
            <div class="description">Whether the package allows content outside its install directory to be treated as package content</div>
        </div>
        <div class="form-group" data-field="properties.fileSystemWriteVirtualization">
            <label>File System Write Virtualization:</label>
            <div class="custom-select" id="props-fsWriteVirt">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="fileSystemWriteVirtualization">(omit)</button>
                <div class="custom-select-options">
                    <div class="custom-select-option selected" data-value="">(omit)</div>
                    <div class="custom-select-option" data-value="enabled">enabled</div>
                    <div class="custom-select-option" data-value="disabled">disabled</div>
                </div>
            </div>
            <div class="description">Controls whether file system write operations are virtualized or written to the real file system</div>
        </div>
        <div class="form-group" data-field="properties.registryWriteVirtualization">
            <label>Registry Write Virtualization:</label>
            <div class="custom-select" id="props-regWriteVirt">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="registryWriteVirtualization">(omit)</button>
                <div class="custom-select-options">
                    <div class="custom-select-option selected" data-value="">(omit)</div>
                    <div class="custom-select-option" data-value="enabled">enabled</div>
                    <div class="custom-select-option" data-value="disabled">disabled</div>
                </div>
            </div>
            <div class="description">Controls whether registry write operations are virtualized or written to the real registry</div>
        </div>
        <div class="section-header section-header-spaced">Update &amp; Integrity</div>
        <p class="page-description">Use this section to configure automatic update behavior and content integrity enforcement for your package.</p>
        <div class="optional-fields-group">
        <div class="form-group optional-field" data-field="properties.autoUpdateUri" id="props-autoupdate-group">
            <label>Auto Update App Installer URI:</label>
            <div class="optional-field-content">
                <input type="text" id="props-autoUpdateUri" data-section="properties" data-field-name="autoUpdateUri" placeholder="https://example.com/install/MyApp.appinstaller" />
                <button class="btn-remove-field" type="button" data-target="props-autoupdate-group" data-section="properties" data-field-name="autoUpdateUri" title="Remove Auto Update App Installer URI">✕</button>
            </div>
            <div class="description">URI to an .appinstaller file that enables automatic updates for sideloaded apps</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group optional-field" data-field="properties.packageIntegrityEnforcement" id="props-pkgintegrity-group">
            <label>Package Integrity Content Enforcement:</label>
            <div class="optional-field-content">
                <div class="custom-select" id="props-packageIntegrityEnforcement">
                    <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="packageIntegrityEnforcement">on</button>
                    <div class="custom-select-options">
                        <div class="custom-select-option selected" data-value="on">on</div>
                        <div class="custom-select-option" data-value="off">off</div>
                        <div class="custom-select-option" data-value="default">default</div>
                    </div>
                </div>
                <button class="btn-remove-field" type="button" data-target="props-pkgintegrity-group" data-section="properties" data-field-name="packageIntegrityEnforcement" title="Remove Package Integrity Content Enforcement">✕</button>
            </div>
            <div class="description">Controls whether Windows enforces content integrity checks for the package — "on", "off", or "default"</div>
            <div class="validation-msg"></div>
        </div>
        <div class="form-group optional-field" data-field="properties.updateWhileInUse" id="props-updatewhileinuse-group">
            <label>Update While In Use:</label>
            <div class="optional-field-content">
                <div class="custom-select" id="props-updateWhileInUse">
                    <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="updateWhileInUse">allow</button>
                    <div class="custom-select-options">
                        <div class="custom-select-option selected" data-value="allow">allow</div>
                        <div class="custom-select-option" data-value="defer">defer</div>
                    </div>
                </div>
                <button class="btn-remove-field" type="button" data-target="props-updatewhileinuse-group" data-section="properties" data-field-name="updateWhileInUse" title="Remove Update While In Use">✕</button>
            </div>
            <div class="description">Whether the package can be updated while it is running — "allow" applies updates immediately, "defer" waits until the app closes</div>
            <div class="validation-msg"></div>
        </div>
        <div class="btn-add-buttons-row">
            <button class="btn-add-field" type="button" id="add-props-autoupdate" data-target="props-autoupdate-group" data-section="properties" data-field-name="autoUpdateUri" data-default="" title="Add Auto Update App Installer URI">+ Add Auto Update App Installer URI</button>
            <button class="btn-add-field" type="button" id="add-props-pkgintegrity" data-target="props-pkgintegrity-group" data-section="properties" data-field-name="packageIntegrityEnforcement" data-default="default" title="Add Package Integrity Content Enforcement">+ Add Package Integrity Content Enforcement</button>
            <button class="btn-add-field" type="button" id="add-props-updatewhileinuse" data-target="props-updatewhileinuse-group" data-section="properties" data-field-name="updateWhileInUse" data-default="defer" title="Add Update While In Use">+ Add Update While In Use</button>
        </div>
        </div>
    </div>

    <!-- ───── Dependencies ───── -->
    <div class="tab-content" id="tab-dependencies" role="tabpanel">
        <div class="section-header">Target Device Families</div>
        <p class="page-description">Use this section to declare the Windows versions and framework packages your package requires. Target device families determine which devices can install your package. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-dependencies">Learn more</a></p>
        <div id="target-device-families" class="list-container"></div>
        <div class="custom-dropdown" id="add-family-dropdown">
            <button class="custom-dropdown-btn" id="add-target-family">+ Add Target Device Family</button>
            <div class="custom-dropdown-menu" id="add-family-menu">
                ${DEVICE_FAMILY_OPTIONS.map(f => `<div class="custom-dropdown-item" data-family="${f}">${f}</div>`).join('')}
            </div>
        </div>

        <div class="section-header section-header-spaced">Package Dependencies</div>
        <p class="page-description">Use this section to declare framework and library package dependencies required by your package. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-packagedependency">Learn more</a></p>
        <div id="package-dependencies" class="list-container"></div>
        <button class="btn" id="add-package-dep">+ Add Package Dependency</button>

        <div class="section-header section-header-spaced">Main Package Dependencies (uap3)</div>
        <p class="page-description">Use this section to declare a dependency on a main package for optional packages. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap3-mainpackagedependency2">Learn more</a></p>
        <div id="main-package-dependencies" class="list-container"></div>
        <button class="btn" id="add-main-pkg-dep">+ Add Main Package Dependency</button>

        <div class="section-header section-header-spaced">Driver Constraints (uap5)</div>
        <p class="page-description">Use this section to declare driver constraints that your package depends on. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-driverdependency">Learn more</a></p>
        <div id="driver-constraints" class="list-container"></div>
        <button class="btn" id="add-driver-constraint">+ Add Driver Constraint</button>

        <div class="section-header section-header-spaced">OS Package Dependencies (uap7)</div>
        <p class="page-description">Use this section to declare a dependency on an OS package. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap7-ospackagedependency">Learn more</a></p>
        <div id="os-package-dependencies" class="list-container"></div>
        <button class="btn" id="add-os-pkg-dep">+ Add OS Package Dependency</button>

        <div class="section-header section-header-spaced">Host Runtime Dependencies (uap10)</div>
        <p class="page-description">Use this section to declare a dependency on a host runtime. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap10-hostruntimedependency">Learn more</a></p>
        <div id="host-runtime-dependencies" class="list-container"></div>
        <button class="btn" id="add-host-runtime-dep">+ Add Host Runtime Dependency</button>

        <div class="section-header section-header-spaced">External Dependencies (win32dependencies)</div>
        <p class="page-description">Use this section to declare a dependency on an external Win32 component. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-win32dependencies-externaldependency">Learn more</a></p>
        <div id="external-dependencies" class="list-container"></div>
        <button class="btn" id="add-external-dep">+ Add External Dependency</button>
    </div>

    <!-- ───── Resources ───── -->
    <div class="tab-content" id="tab-resources" role="tabpanel">
        <div class="section-header">Resources</div>
        <p class="page-description">Use this section to declare the language resources your package supports. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-resources">Learn more</a></p>
        <div id="resources-list" class="list-container"></div>
        <button class="btn" id="add-resource-btn">+ Add Resource</button>
    </div>

    <!-- ───── Applications ───── -->
    <div class="tab-content" id="tab-applications" role="tabpanel">
        <div class="section-header">Applications</div>
        <p class="page-description">Use this section to configure the entry points and visual presentation of your applications. Each Application element represents a separate executable that can be launched from the package. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-application">Learn more</a></p>
        <div id="applications-list"></div>
        <button class="btn mt-12" id="add-application-btn">+ Add Application</button>
    </div>

    <!-- ───── Capabilities ───── -->
    <div class="tab-content" id="tab-capabilities" role="tabpanel">
        <div class="section-header">Capabilities</div>
        <p class="page-description">Use this section to declare the system resources and devices your package needs access to. Users will be prompted to grant restricted capabilities at install time. Only request capabilities your package actually uses. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-capabilities">Learn more</a></p>
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
                    <p class="field-description">Custom capabilities must follow the format <code>company.capabilityname_publisherId</code> where publisherId is a 13-character base32 identifier. <a href="https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap4-customcapability">Learn more</a></p>
                    <div class="custom-cap-row">
                        <input type="text" id="custom-cap-input" placeholder="e.g. Contoso.Devices.SerialCommunication_0wer1ey63g7b4" />
                        <button class="btn" id="add-custom-cap">Add</button>
                    </div>
                    <div id="custom-cap-error" class="validation-msg error" style="display:none;"></div>
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
        // Track optional fields the user has explicitly opened (to prevent re-parse from hiding them)
        const userOpenedOptionalFields = new Set();

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
            const value = el.value;
            const index = parseInt(el.getAttribute('data-index') || '0', 10);
            vscode.postMessage({ type: 'fieldChanged', section, field, value, index });
        }

        // Debounce helper for text inputs
        let debounceTimers = {};
        function debouncedFieldChange(el) {
            const field = el.getAttribute('data-field-name') || '';
            const idx = el.getAttribute('data-index') || '';
            const key = el.id || (field + ':' + idx);
            clearTimeout(debounceTimers[key]);
            debounceTimers[key] = setTimeout(() => onFieldChange(el), 300);
        }

        // ─── Generic custom-select initialization ─────────────
        function initCustomSelects(container) {
            const root = container || document;
            root.querySelectorAll('.custom-select').forEach(cs => {
                const trigger = cs.querySelector('.custom-select-trigger');
                const optionsDiv = cs.querySelector('.custom-select-options');
                if (!trigger || !optionsDiv) return;
                // Skip if already initialized or if trigger has no data-section (special selects like pkg-type)
                if (trigger.hasAttribute('data-cs-init')) return;
                const section = trigger.getAttribute('data-section');
                if (!section) return;
                trigger.setAttribute('data-cs-init', '1');

                trigger.addEventListener('click', (e) => {
                    e.stopPropagation();
                    document.querySelectorAll('.custom-select-options.open').forEach(o => {
                        if (o !== optionsDiv) o.classList.remove('open');
                    });
                    optionsDiv.classList.toggle('open');
                });

                optionsDiv.querySelectorAll('.custom-select-option').forEach(opt => {
                    opt.addEventListener('click', () => {
                        const val = opt.getAttribute('data-value');
                        const label = opt.textContent;
                        trigger.textContent = label;
                        trigger.setAttribute('data-current-value', val);
                        optionsDiv.classList.remove('open');
                        optionsDiv.querySelectorAll('.custom-select-option').forEach(o => o.classList.remove('selected'));
                        opt.classList.add('selected');

                        const field = trigger.getAttribute('data-field-name');
                        const index = parseInt(trigger.getAttribute('data-index') || '0', 10);
                        vscode.postMessage({ type: 'fieldChanged', section, field, value: val, index });
                    });
                });
            });
        }

        // Global click to close all open custom selects
        document.addEventListener('click', () => {
            document.querySelectorAll('.custom-select-options.open').forEach(o => o.classList.remove('open'));
        });

        // Bind change events to static inputs
        document.querySelectorAll('input[data-section], textarea[data-section]').forEach(el => {
            el.addEventListener('input', () => debouncedFieldChange(el));
        });

        // Initialize all custom selects in the static DOM (arch, properties, etc.)
        initCustomSelects(document);

        // ─── Package Type custom select ─────────────────────
        const pkgTypeTrigger = document.getElementById('pkg-type-select-trigger');
        const pkgTypeOptions = document.getElementById('pkg-type-select-options');
        if (pkgTypeTrigger && pkgTypeOptions) {
            pkgTypeTrigger.addEventListener('click', (e) => {
                e.stopPropagation();
                pkgTypeOptions.classList.toggle('open');
            });
            pkgTypeOptions.querySelectorAll('.custom-select-option').forEach(opt => {
                opt.addEventListener('click', () => {
                    const val = opt.getAttribute('data-value');
                    pkgTypeTrigger.textContent = opt.textContent;
                    pkgTypeOptions.classList.remove('open');
                    pkgTypeOptions.querySelectorAll('.custom-select-option').forEach(o => o.classList.remove('selected'));
                    opt.classList.add('selected');
                    vscode.postMessage({ type: 'packageTypeChanged', value: val });
                });
            });
            document.addEventListener('click', () => { pkgTypeOptions.classList.remove('open'); });
        }

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
            const errorEl = document.getElementById('custom-cap-error');
            const cap = input.value.trim();
            if (!cap) {
                errorEl.textContent = 'Custom capability name is required.';
                errorEl.style.display = 'block';
                return;
            }
            // Validate format: company.capabilityname_publisherId (13-char base32)
            const customCapRegex = /^[a-zA-Z0-9]+(\\.[a-zA-Z0-9]+)+_[a-z0-9]{13}$/;
            if (!customCapRegex.test(cap)) {
                errorEl.textContent = 'Custom capability must follow the format company.capabilityname_publisherId (e.g. Contoso.Devices.SerialCommunication_0wer1ey63g7b4).';
                errorEl.style.display = 'block';
                return;
            }
            errorEl.style.display = 'none';
            vscode.postMessage({ type: 'addCapability', capability: cap });
            input.value = '';
        });
        document.getElementById('custom-cap-input').addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                document.getElementById('add-custom-cap').click();
            }
        });
        document.getElementById('custom-cap-input').addEventListener('input', () => {
            document.getElementById('custom-cap-error').style.display = 'none';
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
                dependency: { name: '', minVersion: '', publisher: '', optional: '' }
            });
        });

        document.getElementById('add-main-pkg-dep').addEventListener('click', () => {
            vscode.postMessage({ type: 'addMainPackageDependency', dependency: { name: '' } });
        });
        document.getElementById('add-driver-constraint').addEventListener('click', () => {
            vscode.postMessage({ type: 'addDriverConstraint', constraint: { name: '', minVersion: '', minDate: '' } });
        });
        document.getElementById('add-os-pkg-dep').addEventListener('click', () => {
            vscode.postMessage({ type: 'addOSPackageDependency', dependency: { name: '', version: '' } });
        });
        document.getElementById('add-host-runtime-dep').addEventListener('click', () => {
            vscode.postMessage({ type: 'addHostRuntimeDependency', dependency: { name: '', publisher: '', minVersion: '' } });
        });
        document.getElementById('add-external-dep').addEventListener('click', () => {
            vscode.postMessage({ type: 'addExternalDependency', dependency: { name: '', publisher: '', minVersion: '', optional: '' } });
        });

        // ─── Add application ────────────────────────────────
        document.getElementById('add-application-btn').addEventListener('click', () => {
            vscode.postMessage({ type: 'addApplication' });
        });

        // ─── Add resource ───────────────────────────────────
        document.getElementById('add-resource-btn').addEventListener('click', () => {
            vscode.postMessage({
                type: 'addResource',
                resource: { language: '', scale: '', dxFeatureLevel: '' }
            });
        });

        // ─── Phone Identity Add/Remove buttons ─────────────
        document.getElementById('add-phone-identity-btn')?.addEventListener('click', () => {
            vscode.postMessage({ type: 'addPhoneIdentity' });
        });
        document.getElementById('remove-phone-identity-btn')?.addEventListener('click', () => {
            vscode.postMessage({ type: 'removePhoneIdentity' });
        });

        // ─── Optional field Add/Remove buttons ─────────────
        document.addEventListener('click', (e) => {
            const addBtn = e.target.closest('.btn-add-field');
            if (addBtn) {
                const targetId = addBtn.getAttribute('data-target');
                const group = document.getElementById(targetId);
                if (group) {
                    group.classList.remove('hidden-optional');
                    addBtn.classList.add('hidden-optional');
                    userOpenedOptionalFields.add(targetId);
                    // Set default value and trigger change
                    const defaultVal = addBtn.getAttribute('data-default') || '';
                    const input = group.querySelector('input[data-section]');
                    const csTrigger = group.querySelector('.custom-select-trigger[data-section]');
                    if (csTrigger) {
                        // For custom selects, set value via setCustomSelectValue using the wrapper's id
                        const wrapper = csTrigger.closest('.custom-select');
                        if (wrapper && wrapper.id) {
                            setCustomSelectValue(wrapper.id, defaultVal);
                        }
                        csTrigger.focus();
                        // Trigger immediate field change for custom selects
                        const section = csTrigger.getAttribute('data-section');
                        const field = csTrigger.getAttribute('data-field-name');
                        const index = parseInt(csTrigger.getAttribute('data-index') || '0', 10);
                        vscode.postMessage({ type: 'fieldChanged', section, field, value: defaultVal, index });
                    } else if (input) {
                        input.value = defaultVal;
                        input.focus();
                        if (input.tagName === 'INPUT' && !input.value) {
                            group.classList.add('has-error');
                            const msg = group.querySelector('.validation-msg');
                            if (msg) {
                                const fieldAttr = group.getAttribute('data-field') || '';
                                const errText = fieldAttr === 'identity.resourceId'
                                    ? 'Resource ID must be at least 1 character.'
                                    : 'This field is required. Enter a value or remove the field.';
                                msg.className = 'validation-msg error';
                                msg.textContent = errText;
                            }
                        }
                    }
                }
                return;
            }

            const removeBtn = e.target.closest('.btn-remove-field');
            if (removeBtn) {
                const targetId = removeBtn.getAttribute('data-target');
                const group = document.getElementById(targetId);
                if (group) {
                    group.classList.add('hidden-optional');
                    userOpenedOptionalFields.delete(targetId);
                    // Find the corresponding add button
                    const addBtnForGroup = document.querySelector('.btn-add-field[data-target="' + targetId + '"]');
                    if (addBtnForGroup) addBtnForGroup.classList.remove('hidden-optional');
                    // Send empty value to remove the attribute
                    const section = removeBtn.getAttribute('data-section');
                    const fieldName = removeBtn.getAttribute('data-field-name');
                    const index = removeBtn.getAttribute('data-index');
                    if (section && fieldName) {
                        const msg = { type: 'fieldChanged', section: section, field: fieldName, value: '' };
                        if (index !== null && index !== undefined) { msg.index = parseInt(index, 10); }
                        vscode.postMessage(msg);
                    }
                }
                return;
            }
        });

        // ─── Populate form from data ────────────────────────
        function populateForm(data, forceAll) {
            currentData = data;

            // Save focused element info before DOM rebuild
            const focused = forceAll ? null : document.activeElement;
            let focusInfo = null;
            if (focused && (focused.tagName === 'INPUT' || focused.tagName === 'TEXTAREA' || focused.tagName === 'SELECT' || focused.classList.contains('custom-select-trigger'))) {
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

            // Identity - optional ResourceId
            toggleOptionalField('identity-resourceid-group', 'add-identity-resourceid', data.identity.resourceId);
            setValueIfNotFocused('identity-resourceid', data.identity.resourceId || '', focused);

            // Update architecture custom select
            setCustomSelectValue('arch-select', data.identity.processorArchitecture);

            // Phone Identity
            const phoneSection = document.getElementById('phone-identity-section');
            const addPhoneBtn = document.getElementById('add-phone-identity-btn');
            if (data.phoneIdentity) {
                if (phoneSection) phoneSection.style.display = '';
                if (addPhoneBtn) addPhoneBtn.style.display = 'none';
                setValueIfNotFocused('phone-product-id', data.phoneIdentity.phoneProductId, focused);
                setValueIfNotFocused('phone-publisher-id', data.phoneIdentity.phonePublisherId, focused);
            } else {
                if (phoneSection) phoneSection.style.display = 'none';
                if (addPhoneBtn) addPhoneBtn.style.display = '';
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

            // Properties - select fields
            // Package type (derived from framework/resourcePackage/modificationPackage)
            const pkgTypeTrigger = document.getElementById('pkg-type-select-trigger');
            if (pkgTypeTrigger) {
                let pkgType = 'application';
                if (data.properties.framework === 'true') pkgType = 'framework';
                else if (data.properties.resourcePackage === 'true') pkgType = 'resource';
                else if (data.properties.modificationPackage === 'true') pkgType = 'modification';
                const pkgTypeOpts = document.querySelectorAll('#pkg-type-select-options .custom-select-option');
                pkgTypeOpts.forEach(opt => {
                    const isMatch = opt.getAttribute('data-value') === pkgType;
                    opt.classList.toggle('selected', isMatch);
                    if (isMatch) pkgTypeTrigger.textContent = opt.textContent;
                });
            }
            setCustomSelectValue('props-supportedUsers', data.properties.supportedUsers);
            setCustomSelectValue('props-allowExecution', data.properties.allowExecution);
            setCustomSelectValue('props-allowExternalContent', data.properties.allowExternalContent);
            setCustomSelectValue('props-fsWriteVirt', data.properties.fileSystemWriteVirtualization);
            setCustomSelectValue('props-regWriteVirt', data.properties.registryWriteVirtualization);

            // Properties - optional new fields
            toggleOptionalField('props-autoupdate-group', 'add-props-autoupdate', data.properties.autoUpdateUri);
            setValueIfNotFocused('props-autoUpdateUri', data.properties.autoUpdateUri || '', focused);
            toggleOptionalField('props-pkgintegrity-group', 'add-props-pkgintegrity', data.properties.packageIntegrityEnforcement);
            setCustomSelectValue('props-packageIntegrityEnforcement', data.properties.packageIntegrityEnforcement);
            toggleOptionalField('props-updatewhileinuse-group', 'add-props-updatewhileinuse', data.properties.updateWhileInUse);
            setCustomSelectValue('props-updateWhileInUse', data.properties.updateWhileInUse);

            // Dependencies - Target Device Families
            renderTargetDeviceFamilies(data.dependencies.targetDeviceFamilies);
            renderPackageDependencies(data.dependencies.packageDependencies);
            renderMainPackageDependencies(data.dependencies.mainPackageDependencies);
            renderDriverConstraints(data.dependencies.driverConstraints);
            renderOSPackageDependencies(data.dependencies.osPackageDependencies);
            renderHostRuntimeDependencies(data.dependencies.hostRuntimeDependencies);
            renderExternalDependencies(data.dependencies.externalDependencies);

            // Hide tabs based on package type
            const isNonAppPackage = data.properties.framework === 'true' || data.properties.resourcePackage === 'true' || data.properties.modificationPackage === 'true';
            const isResourcePackage = data.properties.resourcePackage === 'true';

            // Applications — hide for all non-application packages
            const appsTab = document.querySelector('.tab-btn[data-tab="applications"]');
            const appsContent = document.getElementById('tab-applications');
            if (appsTab) {
                if (isNonAppPackage) { appsTab.classList.add('hidden-tab'); } else { appsTab.classList.remove('hidden-tab'); }
            }
            if (appsContent && isNonAppPackage) {
                appsContent.classList.remove('active');
            }

            // Capabilities — hide for framework, resource, and modification packages
            const capsTab = document.querySelector('.tab-btn[data-tab="capabilities"]');
            const capsContent = document.getElementById('tab-capabilities');
            if (capsTab) {
                if (isNonAppPackage) { capsTab.classList.add('hidden-tab'); } else { capsTab.classList.remove('hidden-tab'); }
            }
            if (capsContent && isNonAppPackage) {
                capsContent.classList.remove('active');
            }

            // Dependencies — hide for resource packages
            const depsTab = document.querySelector('.tab-btn[data-tab="dependencies"]');
            const depsContent = document.getElementById('tab-dependencies');
            if (depsTab) {
                if (isResourcePackage) { depsTab.classList.add('hidden-tab'); } else { depsTab.classList.remove('hidden-tab'); }
            }
            if (depsContent && isResourcePackage) {
                depsContent.classList.remove('active');
            }

            // If the active tab was hidden, switch to Identity
            if (!document.querySelector('.tab-content.active')) {
                document.getElementById('tab-identity').classList.add('active');
                const identityTabBtn = document.querySelector('.tab-btn[data-tab="identity"]');
                if (identityTabBtn) identityTabBtn.setAttribute('aria-selected', 'true');
            }
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

        function setCustomSelectValue(selectId, value) {
            const wrapper = document.getElementById(selectId);
            if (!wrapper) return;
            const trigger = wrapper.querySelector('.custom-select-trigger');
            if (!trigger) return;
            const normalizedValue = value || '';
            trigger.setAttribute('data-current-value', normalizedValue);
            const options = wrapper.querySelectorAll('.custom-select-option');
            let label = '(select)';
            options.forEach(opt => {
                const isMatch = opt.getAttribute('data-value') === normalizedValue;
                opt.classList.toggle('selected', isMatch);
                if (isMatch) label = opt.textContent;
            });
            trigger.textContent = label;
        }

        function toggleOptionalField(groupId, addBtnId, value) {
            const group = document.getElementById(groupId);
            const addBtn = document.getElementById(addBtnId);
            if (!group) return;
            if (value || userOpenedOptionalFields.has(groupId)) {
                group.classList.remove('hidden-optional');
                if (addBtn) addBtn.classList.add('hidden-optional');
            } else {
                group.classList.add('hidden-optional');
                if (addBtn) addBtn.classList.remove('hidden-optional');
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
                        <div class="item-actions">
                            <button class="btn btn-sm move-family-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-family-down" data-index="\${idx}" \${idx === families.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-family" data-index="\${idx}" title="Remove">✕</button>
                        </div>
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
                item.querySelector('.move-family-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveTargetDeviceFamily', index: idx, direction: 'up' });
                });
                item.querySelector('.move-family-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveTargetDeviceFamily', index: idx, direction: 'down' });
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
                        <span class="item-title">Name:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-pkg-dep-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-pkg-dep-down" data-index="\${idx}" \${idx === deps.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-pkg-dep" data-index="\${idx}" title="Remove">✕</button>
                        </div>
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
                    <div class="form-group" data-field="dependencies.packageDependency.\${idx}.optional">
                        <label>Optional:</label>
                        <div class="custom-select">
                            <button class="custom-select-trigger" type="button" data-section="dependencies" data-field-name="packageDependency.optional" data-index="\${idx}">\${dep.optional === 'true' ? 'true' : dep.optional === 'false' ? 'false' : '(omit)'}</button>
                            <div class="custom-select-options">
                                <div class="custom-select-option\${dep.optional === '' ? ' selected' : ''}" data-value="">(omit)</div>
                                <div class="custom-select-option\${dep.optional === 'true' ? ' selected' : ''}" data-value="true">true</div>
                                <div class="custom-select-option\${dep.optional === 'false' ? ' selected' : ''}" data-value="false">false</div>
                            </div>
                        </div>
                        <div class="description">Whether this dependency is optional (requires uap6 namespace)</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);

                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                initCustomSelects(item);
                item.querySelector('.remove-pkg-dep').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removePackageDependency', index: idx });
                });
                item.querySelector('.move-pkg-dep-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'movePackageDependency', index: idx, direction: 'up' });
                });
                item.querySelector('.move-pkg-dep-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'movePackageDependency', index: idx, direction: 'down' });
                });
            });
        }

        function renderMainPackageDependencies(deps) {
            const container = document.getElementById('main-package-dependencies');
            container.innerHTML = '';
            deps.forEach((dep, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Name:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-main-pkg-dep-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-main-pkg-dep-down" data-index="\${idx}" \${idx === deps.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-main-pkg-dep" data-index="\${idx}" title="Remove">✕</button>
                        </div>
                    </div>
                    <div class="form-group" data-field="dependencies.mainPackageDependency.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="mainPackageDependency.name" data-index="\${idx}" value="\${escapeHtml(dep.name)}" placeholder="MainPackageName" />
                        <div class="description">Package identity name of the main package</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);
                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                item.querySelector('.remove-main-pkg-dep').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeMainPackageDependency', index: idx });
                });
                item.querySelector('.move-main-pkg-dep-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveMainPackageDependency', index: idx, direction: 'up' });
                });
                item.querySelector('.move-main-pkg-dep-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveMainPackageDependency', index: idx, direction: 'down' });
                });
            });
        }

        function renderDriverConstraints(constraints) {
            const container = document.getElementById('driver-constraints');
            container.innerHTML = '';
            constraints.forEach((dc, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Name:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-driver-constraint-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-driver-constraint-down" data-index="\${idx}" \${idx === constraints.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-driver-constraint" data-index="\${idx}" title="Remove">✕</button>
                        </div>
                    </div>
                    <div class="form-group" data-field="dependencies.driverConstraint.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="driverConstraint.name" data-index="\${idx}" value="\${escapeHtml(dc.name)}" />
                        <div class="description">The driver package identity name that this constraint applies to</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.driverConstraint.\${idx}.minVersion">
                        <label>Min Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="driverConstraint.minVersion" data-index="\${idx}" value="\${escapeHtml(dc.minVersion)}" placeholder="1.0.0.0" />
                        <div class="description">Minimum driver version required, in dotted-quad format (e.g. 1.0.0.0)</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.driverConstraint.\${idx}.minDate">
                        <label>Min Date:</label>
                        <input type="text" data-section="dependencies" data-field-name="driverConstraint.minDate" data-index="\${idx}" value="\${escapeHtml(dc.minDate)}" placeholder="2020-01-01" />
                        <div class="description">Earliest driver date accepted, in YYYY-MM-DD format</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);
                item.querySelector('.remove-driver-constraint').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeDriverConstraint', index: idx });
                });
                item.querySelector('.move-driver-constraint-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveDriverConstraint', index: idx, direction: 'up' });
                });
                item.querySelector('.move-driver-constraint-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveDriverConstraint', index: idx, direction: 'down' });
                });
                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
            });
        }

        function renderOSPackageDependencies(deps) {
            const container = document.getElementById('os-package-dependencies');
            container.innerHTML = '';
            deps.forEach((dep, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Name:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-os-pkg-dep-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-os-pkg-dep-down" data-index="\${idx}" \${idx === deps.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-os-pkg-dep" data-index="\${idx}" title="Remove">✕</button>
                        </div>
                    </div>
                    <div class="form-group" data-field="dependencies.osPackageDependency.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="osPackageDependency.name" data-index="\${idx}" value="\${escapeHtml(dep.name)}" />
                        <div class="description">Package identity name of the OS package</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.osPackageDependency.\${idx}.version">
                        <label>Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="osPackageDependency.version" data-index="\${idx}" value="\${escapeHtml(dep.version)}" placeholder="10.0.0.0" />
                        <div class="description">DotQuad version number (e.g. 10.0.0.0), each part 0–65535</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);
                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                item.querySelector('.remove-os-pkg-dep').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeOSPackageDependency', index: idx });
                });
                item.querySelector('.move-os-pkg-dep-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveOSPackageDependency', index: idx, direction: 'up' });
                });
                item.querySelector('.move-os-pkg-dep-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveOSPackageDependency', index: idx, direction: 'down' });
                });
            });
        }

        function renderHostRuntimeDependencies(deps) {
            const container = document.getElementById('host-runtime-dependencies');
            container.innerHTML = '';
            deps.forEach((dep, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Name:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-host-runtime-dep-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-host-runtime-dep-down" data-index="\${idx}" \${idx === deps.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-host-runtime-dep" data-index="\${idx}" title="Remove">✕</button>
                        </div>
                    </div>
                    <div class="form-group" data-field="dependencies.hostRuntimeDependency.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="hostRuntimeDependency.name" data-index="\${idx}" value="\${escapeHtml(dep.name)}" />
                        <div class="description">Package identity name of the host runtime</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.hostRuntimeDependency.\${idx}.publisher">
                        <label>Publisher:</label>
                        <input type="text" data-section="dependencies" data-field-name="hostRuntimeDependency.publisher" data-index="\${idx}" value="\${escapeHtml(dep.publisher)}" placeholder="CN=..." />
                        <div class="description">X.500 distinguished name of the host runtime publisher</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.hostRuntimeDependency.\${idx}.minVersion">
                        <label>Min Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="hostRuntimeDependency.minVersion" data-index="\${idx}" value="\${escapeHtml(dep.minVersion)}" placeholder="1.0.0.0" />
                        <div class="description">Minimum DotQuad version required (e.g. 1.0.0.0), each part 0–65535</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);
                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                item.querySelector('.remove-host-runtime-dep').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeHostRuntimeDependency', index: idx });
                });
                item.querySelector('.move-host-runtime-dep-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveHostRuntimeDependency', index: idx, direction: 'up' });
                });
                item.querySelector('.move-host-runtime-dep-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveHostRuntimeDependency', index: idx, direction: 'down' });
                });
            });
        }

        function renderExternalDependencies(deps) {
            const container = document.getElementById('external-dependencies');
            container.innerHTML = '';
            deps.forEach((dep, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';
                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Name:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-external-dep-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-external-dep-down" data-index="\${idx}" \${idx === deps.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-external-dep" data-index="\${idx}" title="Remove">✕</button>
                        </div>
                    </div>
                    <div class="form-group" data-field="dependencies.externalDependency.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="externalDependency.name" data-index="\${idx}" value="\${escapeHtml(dep.name)}" />
                        <div class="description">Name of the external Win32 component</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.externalDependency.\${idx}.publisher">
                        <label>Publisher:</label>
                        <input type="text" data-section="dependencies" data-field-name="externalDependency.publisher" data-index="\${idx}" value="\${escapeHtml(dep.publisher)}" placeholder="CN=..." />
                        <div class="description">X.500 distinguished name of the external component publisher</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="dependencies.externalDependency.\${idx}.minVersion">
                        <label>Min Version:</label>
                        <input type="text" data-section="dependencies" data-field-name="externalDependency.minVersion" data-index="\${idx}" value="\${escapeHtml(dep.minVersion)}" placeholder="1.0.0.0" />
                        <div class="description">Minimum version required for the external component</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group">
                        <label>Optional:</label>
                        <div class="custom-select">
                            <button class="custom-select-trigger" type="button" data-section="dependencies" data-field-name="externalDependency.optional" data-index="\${idx}">\${dep.optional === 'true' ? 'true' : dep.optional === 'false' ? 'false' : '(omit)'}</button>
                            <div class="custom-select-options">
                                <div class="custom-select-option\${dep.optional === '' ? ' selected' : ''}" data-value="">(omit)</div>
                                <div class="custom-select-option\${dep.optional === 'true' ? ' selected' : ''}" data-value="true">true</div>
                                <div class="custom-select-option\${dep.optional === 'false' ? ' selected' : ''}" data-value="false">false</div>
                            </div>
                        </div>
                        <div class="description">Whether this external dependency is optional</div>
                    </div>
                \`;
                container.appendChild(item);
                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                initCustomSelects(item);
                item.querySelector('.remove-external-dep').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeExternalDependency', index: idx });
                });
                item.querySelector('.move-external-dep-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveExternalDependency', index: idx, direction: 'up' });
                });
                item.querySelector('.move-external-dep-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveExternalDependency', index: idx, direction: 'down' });
                });
            });
        }

        function renderResources(resources) {
            const container = document.getElementById('resources-list');
            container.innerHTML = '';
            const scaleOptions = ['', '80', '100', '120', '125', '140', '150', '160', '175', '180', '200', '225', '250', '300', '350', '400', '450'];
            const dxOptions = ['', 'dx9', 'dx10', 'dx11', 'dx12'];
            resources.forEach((res, idx) => {
                const item = document.createElement('div');
                item.className = 'list-item';

                const scaleOptionsHtml = scaleOptions.map(s =>
                    '<div class="custom-select-option' + (res.scale === s ? ' selected' : '') + '" data-value="' + s + '">' + (s || '(none)') + '</div>'
                ).join('');
                const dxOptionsHtml = dxOptions.map(d =>
                    '<div class="custom-select-option' + (res.dxFeatureLevel === d ? ' selected' : '') + '" data-value="' + d + '">' + (d || '(none)') + '</div>'
                ).join('');
                const scaleLabel = res.scale || '(none)';
                const dxLabel = res.dxFeatureLevel || '(none)';

                item.innerHTML = \`
                    <div class="item-header">
                        <span class="item-title">Language:</span>
                        <div class="item-actions">
                            <button class="btn btn-sm move-resource-up" data-index="\${idx}" \${idx === 0 ? 'disabled' : ''} title="Move Up">▲</button>
                            <button class="btn btn-sm move-resource-down" data-index="\${idx}" \${idx === resources.length - 1 ? 'disabled' : ''} title="Move Down">▼</button>
                            <button class="btn-remove-field remove-resource" data-index="\${idx}" title="Remove">✕</button>
                        </div>
                    </div>
                    <div class="form-group" data-field="resources.\${idx}.language">
                        <input type="text" data-section="resources" data-field-name="language" data-index="\${idx}" value="\${escapeHtml(res.language)}" placeholder="en-us" />
                        <div class="description">BCP-47 language tag (e.g. "en-us", "fr-fr", "ja-jp") or "x-generate"</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="resources.\${idx}.scale">
                        <label>Scale:</label>
                        <div class="custom-select">
                            <button class="custom-select-trigger" type="button" data-section="resources" data-field-name="scale" data-index="\${idx}">\${scaleLabel}</button>
                            <div class="custom-select-options">
                                \${scaleOptionsHtml}
                            </div>
                        </div>
                        <div class="description">Resolution scale for resource selection (e.g. 100, 200, 400)</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="resources.\${idx}.dxFeatureLevel">
                        <label>DirectX Feature Level:</label>
                        <div class="custom-select">
                            <button class="custom-select-trigger" type="button" data-section="resources" data-field-name="dxFeatureLevel" data-index="\${idx}">\${dxLabel}</button>
                            <div class="custom-select-options">
                                \${dxOptionsHtml}
                            </div>
                        </div>
                        <div class="description">DirectX feature level for resource selection</div>
                        <div class="validation-msg"></div>
                    </div>
                \`;
                container.appendChild(item);

                item.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                initCustomSelects(item);
                item.querySelector('.remove-resource').addEventListener('click', () => {
                    vscode.postMessage({ type: 'removeResource', index: idx });
                });
                item.querySelector('.move-resource-up').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveResource', index: idx, direction: 'up' });
                });
                item.querySelector('.move-resource-down').addEventListener('click', () => {
                    vscode.postMessage({ type: 'moveResource', index: idx, direction: 'down' });
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
                        extListHtml += '<div class="list-item"><div class="item-header"><span class="item-title">Extension #' + (eidx + 1) + '</span><button class="btn-remove-field remove-ext" data-app-index="' + idx + '" data-ext-index="' + eidx + '" title="Remove">✕</button></div>' + fieldsHtml + '</div>';
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
                        \${apps.length > 1 ? '<button class="btn-remove-field remove-app-btn" data-app-index="' + idx + '" title="Remove">✕</button>' : ''}
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
                        <div class="subsection-header section-header-spaced">Advanced Attributes</div>
                        <p class="description mb-12">Optional advanced attributes for this application entry. These control trust level, runtime behavior, and multi-instance support.</p>
                        <div class="optional-fields-group">
                        <div class="form-group optional-field" data-field="applications.\${idx}.trustLevel" id="app-\${idx}-trustlevel-group">
                            <label>Trust Level:</label>
                            <div class="optional-field-content">
                                <div class="custom-select">
                                    <button class="custom-select-trigger" type="button" data-section="applications" data-field-name="trustLevel" data-index="\${idx}">\${app.trustLevel || 'appContainer'}</button>
                                    <div class="custom-select-options">
                                        <div class="custom-select-option\${app.trustLevel === 'appContainer' ? ' selected' : ''}" data-value="appContainer">appContainer</div>
                                        <div class="custom-select-option\${app.trustLevel === 'mediumIL' ? ' selected' : ''}" data-value="mediumIL">mediumIL</div>
                                    </div>
                                </div>
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-trustlevel-group" data-section="applications" data-field-name="trustLevel" data-index="\${idx}" title="Remove Trust Level">✕</button>
                            </div>
                            <div class="description">App trust level — appContainer (sandboxed UWP) or mediumIL (classic desktop, requires runFullTrust capability)</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group optional-field" data-field="applications.\${idx}.runtimeBehavior" id="app-\${idx}-runtimebehavior-group">
                            <label>Runtime Behavior:</label>
                            <div class="optional-field-content">
                                <div class="custom-select">
                                    <button class="custom-select-trigger" type="button" data-section="applications" data-field-name="runtimeBehavior" data-index="\${idx}">\${app.runtimeBehavior || 'windowsApp'}</button>
                                    <div class="custom-select-options">
                                        <div class="custom-select-option\${app.runtimeBehavior === 'windowsApp' ? ' selected' : ''}" data-value="windowsApp">windowsApp</div>
                                        <div class="custom-select-option\${app.runtimeBehavior === 'packagedClassicApp' ? ' selected' : ''}" data-value="packagedClassicApp">packagedClassicApp</div>
                                        <div class="custom-select-option\${app.runtimeBehavior === 'win32App' ? ' selected' : ''}" data-value="win32App">win32App</div>
                                    </div>
                                </div>
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-runtimebehavior-group" data-section="applications" data-field-name="runtimeBehavior" data-index="\${idx}" title="Remove Runtime Behavior">✕</button>
                            </div>
                            <div class="description">Runtime model — windowsApp (UWP), packagedClassicApp (packaged desktop), or win32App (unpackaged desktop)</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group optional-field" data-field="applications.\${idx}.supportsMultipleInstances" id="app-\${idx}-multiinstance-group">
                            <label>Supports Multiple Instances:</label>
                            <div class="optional-field-content">
                                <div class="custom-select">
                                    <button class="custom-select-trigger" type="button" data-section="applications" data-field-name="supportsMultipleInstances" data-index="\${idx}">\${app.supportsMultipleInstances || 'true'}</button>
                                    <div class="custom-select-options">
                                        <div class="custom-select-option\${app.supportsMultipleInstances === 'true' ? ' selected' : ''}" data-value="true">true</div>
                                        <div class="custom-select-option\${app.supportsMultipleInstances === 'false' ? ' selected' : ''}" data-value="false">false</div>
                                    </div>
                                </div>
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-multiinstance-group" data-section="applications" data-field-name="supportsMultipleInstances" data-index="\${idx}" title="Remove Supports Multiple Instances">✕</button>
                            </div>
                            <div class="description">Whether multiple instances of this app can run simultaneously</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group optional-field" data-field="applications.\${idx}.parameters" id="app-\${idx}-parameters-group">
                            <label>Parameters:</label>
                            <div class="optional-field-content">
                                <input type="text" data-section="applications" data-field-name="parameters" data-index="\${idx}" value="\${escapeHtml(app.parameters || '')}" placeholder="e.g. --flag value" />
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-parameters-group" data-section="applications" data-field-name="parameters" data-index="\${idx}" title="Remove Parameters">✕</button>
                            </div>
                            <div class="description">Command-line parameters passed to the executable at launch</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="btn-add-buttons-row">
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-trustlevel" data-target="app-\${idx}-trustlevel-group" data-section="applications" data-field-name="trustLevel" data-index="\${idx}" data-default="appContainer" title="Add Trust Level attribute">+ Add Trust Level</button>
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-runtimebehavior" data-target="app-\${idx}-runtimebehavior-group" data-section="applications" data-field-name="runtimeBehavior" data-index="\${idx}" data-default="windowsApp" title="Add Runtime Behavior attribute">+ Add Runtime Behavior</button>
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-multiinstance" data-target="app-\${idx}-multiinstance-group" data-section="applications" data-field-name="supportsMultipleInstances" data-index="\${idx}" data-default="true" title="Add Supports Multiple Instances">+ Add Supports Multiple Instances</button>
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-parameters" data-target="app-\${idx}-parameters-group" data-section="applications" data-field-name="parameters" data-index="\${idx}" data-default="" title="Add Parameters attribute">+ Add Parameters</button>
                        </div>
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
                            <div class="description">Short description shown in package tooltips and accessibility tools, max 2048 characters</div>
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
                        <div class="subsection-header section-header-spaced">Additional Visual Properties</div>
                        <div class="optional-fields-group">
                        <div class="form-group optional-field" data-field="applications.\${idx}.visualElements.appListEntry" id="app-\${idx}-applistentry-group">
                            <label>App List Entry:</label>
                            <div class="optional-field-content">
                                <div class="custom-select">
                                    <button class="custom-select-trigger" type="button" data-section="applications" data-field-name="visualElements.appListEntry" data-index="\${idx}">\${app.visualElements.appListEntry || 'default'}</button>
                                    <div class="custom-select-options">
                                        <div class="custom-select-option\${app.visualElements.appListEntry === 'default' ? ' selected' : ''}" data-value="default">default</div>
                                        <div class="custom-select-option\${app.visualElements.appListEntry === 'none' ? ' selected' : ''}" data-value="none">none</div>
                                    </div>
                                </div>
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-applistentry-group" data-section="applications" data-field-name="visualElements.appListEntry" data-index="\${idx}" title="Remove App List Entry">✕</button>
                            </div>
                            <div class="description">Whether the app appears in the All Apps list — "default" shows it, "none" hides it (e.g. for background tasks)</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group optional-field" data-field="applications.\${idx}.visualElements.shortName" id="app-\${idx}-shortname-group">
                            <label>Short Name:</label>
                            <div class="optional-field-content">
                                <input type="text" data-section="applications" data-field-name="visualElements.shortName" data-index="\${idx}" value="\${escapeHtml(app.visualElements.shortName || '')}" placeholder="Short display name (max 40 chars)" />
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-shortname-group" data-section="applications" data-field-name="visualElements.shortName" data-index="\${idx}" title="Remove Short Name">✕</button>
                            </div>
                            <div class="description">Abbreviated name shown on the app tile when space is limited (1–40 characters, on uap:DefaultTile)</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group optional-field" data-field="applications.\${idx}.visualElements.splashScreenBackgroundColor" id="app-\${idx}-splashbgcolor-group">
                            <label>Splash Screen Background Color:</label>
                            <div class="optional-field-content">
                                <div class="color-row">
                                    <input type="color" data-section="applications" data-field-name="visualElements.splashScreenBackgroundColor" data-index="\${idx}" value="\${toColorValue(app.visualElements.splashScreenBackgroundColor || '#FFFFFF')}" />
                                    <input type="text" data-section="applications" data-field-name="visualElements.splashScreenBackgroundColor" data-index="\${idx}" value="\${escapeHtml(app.visualElements.splashScreenBackgroundColor || '')}" placeholder="#FFFFFF or transparent" />
                                </div>
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-splashbgcolor-group" data-section="applications" data-field-name="visualElements.splashScreenBackgroundColor" data-index="\${idx}" title="Remove Splash Screen Background Color">✕</button>
                            </div>
                            <div class="description">Background color for the splash screen, displayed behind the SplashScreen image</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="form-group optional-field" data-field="applications.\${idx}.visualElements.lockScreenNotification" id="app-\${idx}-lockscreennotif-group">
                            <label>Lock Screen Notification:</label>
                            <div class="optional-field-content">
                                <div class="custom-select">
                                    <button class="custom-select-trigger" type="button" data-section="applications" data-field-name="visualElements.lockScreenNotification" data-index="\${idx}">\${app.visualElements.lockScreenNotification || 'badge'}</button>
                                    <div class="custom-select-options">
                                        <div class="custom-select-option\${app.visualElements.lockScreenNotification === 'badge' ? ' selected' : ''}" data-value="badge">badge</div>
                                        <div class="custom-select-option\${app.visualElements.lockScreenNotification === 'badgeAndTileText' ? ' selected' : ''}" data-value="badgeAndTileText">badgeAndTileText</div>
                                    </div>
                                </div>
                                <button class="btn-remove-field" type="button" data-target="app-\${idx}-lockscreennotif-group" data-section="applications" data-field-name="visualElements.lockScreenNotification" data-index="\${idx}" title="Remove Lock Screen Notification">✕</button>
                            </div>
                            <div class="description">Lock screen notification style — "badge" (icon only) or "badgeAndTileText" (icon + text). Requires BadgeLogo and lock screen capability.</div>
                            <div class="validation-msg"></div>
                        </div>
                        <div class="btn-add-buttons-row">
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-applistentry" data-target="app-\${idx}-applistentry-group" data-section="applications" data-field-name="visualElements.appListEntry" data-index="\${idx}" data-default="default" title="Add App List Entry">+ Add App List Entry</button>
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-shortname" data-target="app-\${idx}-shortname-group" data-section="applications" data-field-name="visualElements.shortName" data-index="\${idx}" data-default="" title="Add Short Name">+ Add Short Name</button>
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-splashbgcolor" data-target="app-\${idx}-splashbgcolor-group" data-section="applications" data-field-name="visualElements.splashScreenBackgroundColor" data-index="\${idx}" data-default="" title="Add Splash Screen Background Color">+ Add Splash Screen Background Color</button>
                            <button class="btn-add-field" type="button" id="add-app-\${idx}-lockscreennotif" data-target="app-\${idx}-lockscreennotif-group" data-section="applications" data-field-name="visualElements.lockScreenNotification" data-index="\${idx}" data-default="badge" title="Add Lock Screen Notification">+ Add Lock Screen Notification</button>
                        </div>
                        </div>
                        <button class="btn update-assets-btn mt-12">Regenerate Assets</button>
                    </div>
                \`;
                container.appendChild(card);

                // Toggle optional fields visibility in this app card
                toggleOptionalField('app-' + idx + '-trustlevel-group', 'add-app-' + idx + '-trustlevel', app.trustLevel);
                toggleOptionalField('app-' + idx + '-runtimebehavior-group', 'add-app-' + idx + '-runtimebehavior', app.runtimeBehavior);
                toggleOptionalField('app-' + idx + '-multiinstance-group', 'add-app-' + idx + '-multiinstance', app.supportsMultipleInstances);
                toggleOptionalField('app-' + idx + '-parameters-group', 'add-app-' + idx + '-parameters', app.parameters);
                toggleOptionalField('app-' + idx + '-applistentry-group', 'add-app-' + idx + '-applistentry', app.visualElements.appListEntry);
                toggleOptionalField('app-' + idx + '-shortname-group', 'add-app-' + idx + '-shortname', app.visualElements.shortName);
                toggleOptionalField('app-' + idx + '-splashbgcolor-group', 'add-app-' + idx + '-splashbgcolor', app.visualElements.splashScreenBackgroundColor);
                toggleOptionalField('app-' + idx + '-lockscreennotif-group', 'add-app-' + idx + '-lockscreennotif', app.visualElements.lockScreenNotification);

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
                card.querySelectorAll('input[data-section]').forEach(inp => {
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
                initCustomSelects(card);

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
            const customCapRegex = /^[a-zA-Z0-9]+(\\.[a-zA-Z0-9]+)+_[a-z0-9]{13}$/;
            customCaps.forEach(cap => {
                const wrapper = document.createElement('div');
                wrapper.className = 'custom-cap-entry';
                const label = document.createElement('label');
                label.className = 'cap-item';
                label.innerHTML = \`<input type="checkbox" checked data-custom-cap="\${escapeHtml(cap)}" /><span>\${escapeHtml(cap)}</span>\`;
                wrapper.appendChild(label);
                if (!customCapRegex.test(cap)) {
                    const errSpan = document.createElement('span');
                    errSpan.className = 'validation-msg error';
                    errSpan.textContent = 'Invalid format. Expected: company.capabilityname_publisherId (e.g. Contoso.Devices.SerialCommunication_0wer1ey63g7b4)';
                    errSpan.style.display = 'block';
                    errSpan.style.marginLeft = '24px';
                    wrapper.appendChild(errSpan);
                }
                customList.appendChild(wrapper);
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

            // Re-apply required errors for user-opened optional text fields that are still empty
            userOpenedOptionalFields.forEach(groupId => {
                const group = document.getElementById(groupId);
                if (!group || group.classList.contains('hidden-optional')) return;
                if (group.classList.contains('has-error') || group.classList.contains('has-warning')) return;
                const input = group.querySelector('input[data-section]');
                if (input && !input.value) {
                    group.classList.add('has-error');
                    const msg = group.querySelector('.validation-msg');
                    if (msg) {
                        const fieldAttr = group.getAttribute('data-field') || '';
                        const errText = fieldAttr === 'identity.resourceId'
                            ? 'Resource ID must be at least 1 character.'
                            : 'This field is required. Enter a value or remove the field.';
                        msg.className = 'validation-msg error';
                        msg.textContent = errText;
                    }
                }
            });
        }

        // ─── Message handler ────────────────────────────────
        window.addEventListener('message', event => {
            const msg = event.data;
            switch (msg.type) {
                case 'update':
                    populateForm(msg.data, msg.forceAll);
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
