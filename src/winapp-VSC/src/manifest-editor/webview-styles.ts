/**
 * CSS styles for the AppxManifest editor webview.
 * Extracted from webview-content.ts for maintainability.
 */

export function getEditorStyles(nonce: string): string {
    return `    <style nonce="${nonce}">
        /* ─── Reset & base ─────────────────────────────────── */
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        html, body {
            height: 100%;
            font-family: var(--vscode-font-family, "Segoe UI", sans-serif);
            font-size: var(--vscode-font-size, 13px);
            color: var(--vscode-editor-foreground);
            background: var(--vscode-editor-background);
        }
        body { padding: 0; margin: 0; display: flex; flex-direction: column; overflow: hidden; }

        /* ─── Tab bar ──────────────────────────────────────── */
        .tab-bar {
            display: flex;
            border-bottom: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
            background: var(--vscode-editor-background);
            flex-shrink: 0;
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
        .tab-btn, .app-sub-tab {
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
        .tab-btn { padding: 8px 16px; }
        .app-sub-tab { padding: 6px 14px; }
        .tab-btn:hover, .app-sub-tab:hover { opacity: 1; }
        .tab-btn.active, .app-sub-tab.active {
            opacity: 1;
            border-bottom-color: var(--vscode-focusBorder, #007acc);
            color: var(--vscode-foreground);
        }
        .tab-btn:focus-visible {
            outline: 1px solid var(--vscode-focusBorder);
            outline-offset: -1px;
        }

        /* ─── Tab content ──────────────────────────────────── */
        .tab-content { display: none; padding: 20px 24px 120px; max-width: 720px; flex: 1; overflow-y: auto; }
        .tab-content.active { display: block; }

        /* ─── Section header ───────────────────────────────── */
        .section-header {
            font-size: 20px;
            font-weight: 600;
            color: var(--vscode-settings-headerForeground, var(--vscode-foreground));
            margin-bottom: 16px;
            padding-bottom: 6px;
            border-bottom: 1px solid var(--vscode-panel-border, var(--vscode-editorGroup-border));
            display: flex;
            align-items: center;
            justify-content: space-between;
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
        .form-group textarea,
        .custom-cap-row input {
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
        .form-group textarea:focus,
        .custom-cap-row input:focus {
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
        .custom-select-option.focused { background:var(--vscode-list-focusBackground, rgba(255,255,255,0.08)); outline:1px solid var(--vscode-focusBorder); outline-offset:-1px; }
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
        .copy-to-assets-link {
            color: var(--vscode-textLink-foreground, #3794ff);
            text-decoration: underline;
            cursor: pointer;
        }
        .copy-to-assets-link:hover {
            color: var(--vscode-textLink-activeForeground, #3794ff);
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
            margin-bottom: 12px;
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
        .btn-remove-field, .btn-remove-section {
            width: 24px;
            height: 24px;
            padding: 0;
            background: rgba(128, 128, 128, 0.3);
            color: var(--vscode-editor-foreground, #ffffff);
            font-size: 14px;
            align-items: center;
            justify-content: center;
        }
        .btn-remove-field {
            flex-shrink: 0;
            display: flex;
        }
        .btn-remove-section {
            display: inline-flex;
            vertical-align: middle;
            margin-left: 8px;
        }
        .btn-remove-field:hover, .btn-remove-section:hover {
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
        .btn, .btn-sm, .btn-remove-field, .btn-remove-section, .btn-add-field, .custom-dropdown-btn {
            cursor: pointer;
            border: none;
            border-radius: 2px;
            font-family: inherit;
        }
        .btn, .btn-add-field, .custom-dropdown-btn {
            font-size: 12px;
            color: var(--vscode-button-foreground);
            background: var(--vscode-button-background);
        }
        .btn { padding: 4px 12px; }
        .btn:hover, .btn-add-field:hover, .custom-dropdown-btn:hover {
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
            width: auto;
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
        .app-sub-content { display:none; }
        .app-sub-content.active { display:block; }

        .capabilities-columns { display:flex; gap:24px; }
        .capabilities-left { flex:1; min-width:0; }
        .capabilities-right { width:260px; flex-shrink:0; }
        .cap-description-panel { padding:12px; border-radius:4px; background:var(--vscode-textBlockQuote-background,transparent); border-left:3px solid var(--vscode-focusBorder,#007acc); font-size:13px; color:var(--vscode-descriptionForeground); min-height:40px; position:sticky; top:60px; }
        .cap-description-name { font-weight:600; margin-bottom:4px; color:var(--vscode-foreground); }

        .custom-dropdown { position:relative; display:inline-block; }
        .custom-dropdown-btn { padding:4px 12px; }
        .custom-dropdown-menu { display:none; position:absolute; top:100%; left:0; margin-top:4px; min-width:180px; background:var(--vscode-menu-background, var(--vscode-editor-background)); border:1px solid var(--vscode-panel-border); border-radius:6px; box-shadow:0 2px 8px rgba(0,0,0,0.2); z-index:20; padding:4px; }
        .custom-dropdown-menu.open { display:block; }
        .custom-dropdown-item { padding:6px 12px; cursor:pointer; font-size:12px; color:var(--vscode-foreground); border-radius:4px; }
        .custom-dropdown-item:hover { background:var(--vscode-list-hoverBackground, rgba(255,255,255,0.05)); }
    </style>`;
}

/** CSS styles for the parse-error page shown when the manifest XML is invalid. */
export function getErrorPageStyles(nonce: string): string {
    return `    <style nonce="${nonce}">
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        html, body {
            height: 100%;
            font-family: var(--vscode-font-family, "Segoe UI", sans-serif);
            font-size: var(--vscode-font-size, 13px);
            color: var(--vscode-editor-foreground);
            background: var(--vscode-editor-background);
            display: flex; align-items: center; justify-content: center;
        }
        .error-container { max-width: 520px; text-align: center; padding: 40px; }
        .error-icon {
            font-size: 48px; margin-bottom: 16px;
            color: var(--vscode-errorForeground, #f44747);
        }
        .error-title { font-size: 18px; font-weight: 600; margin-bottom: 12px; }
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
    </style>`;
}
