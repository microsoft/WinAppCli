/**
 * Generates the HTML content for the AppxManifest editor webview.
 * Uses VS Code CSS variables for native theming.
 */

import * as vscode from 'vscode';
import { KNOWN_CAPABILITIES, ARCHITECTURE_OPTIONS, DEVICE_FAMILY_OPTIONS } from './manifest-types';
import { getEditorStyles } from './webview-styles';
import { getEditorScript } from './webview-script';

function buildCapabilityCheckboxList(
    capabilities: Array<{ name: string; label: string; namespace?: string }>,
    prefix: string
): string {
    return capabilities.map(c => {
        const capKey = prefix ? `${prefix}:${c.name}` : (c.namespace ? `${c.namespace}:${c.name}` : c.name);
        return `<label class="cap-item" data-cap="${capKey}">
            <input type="checkbox" data-capability="${capKey}" /><span>${c.label}</span>
        </label>`;
    }).join('');
}

function buildSelectOptions(options: Array<{ value: string; label: string; selected?: boolean }>): string {
    return options.map(o =>
        `<div class="custom-select-option${o.selected ? ' selected' : ''}" data-value="${o.value}">${o.label}</div>`
    ).join('\n                    ');
}

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
    const archOptionItems = buildSelectOptions(ARCHITECTURE_OPTIONS.map(a => ({ value: a, label: a })));

    const generalCaps = buildCapabilityCheckboxList(KNOWN_CAPABILITIES.general, '');
    const restrictedCaps = buildCapabilityCheckboxList(KNOWN_CAPABILITIES.restricted, 'rescap');
    const deviceCaps = buildCapabilityCheckboxList(KNOWN_CAPABILITIES.device, 'device');

    return /*html*/`<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>AppxManifest Editor</title>
${getEditorStyles(nonce)}
</head>
<body>
    <div class="tab-bar" role="tablist">
        <button class="tab-btn active" role="tab" data-tab="identity" aria-selected="true" tabindex="0">Identity</button>
        <button class="tab-btn" role="tab" data-tab="properties" aria-selected="false" tabindex="-1">Properties</button>
        <button class="tab-btn" role="tab" data-tab="dependencies" aria-selected="false" tabindex="-1">Dependencies</button>
        <button class="tab-btn" role="tab" data-tab="resources" aria-selected="false" tabindex="-1">Resources</button>
        <button class="tab-btn" role="tab" data-tab="applications" aria-selected="false" tabindex="-1">Applications</button>
        <button class="tab-btn" role="tab" data-tab="capabilities" aria-selected="false" tabindex="-1">Capabilities</button>
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
                <button class="btn-remove-field" type="button" data-target="identity-resourceid-group" data-section="identity" data-field-name="resourceId" title="Remove Resource ID">✕</button>
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
            <div class="form-group optional-field" data-field="phoneIdentity.phonePublisherId" id="phone-publisherid-group">
                <label for="phone-publisher-id">Phone Publisher ID:</label>
                <div class="optional-field-content">
                    <input type="text" id="phone-publisher-id" data-section="phoneIdentity" data-field-name="phonePublisherId" placeholder="00000000-0000-0000-0000-000000000000" />
                    <button class="btn-remove-field" type="button" data-target="phone-publisherid-group" data-section="phoneIdentity" data-field-name="phonePublisherId" title="Remove Phone Publisher ID">✕</button>
                </div>
                <div class="description">GUID that identifies the publisher, typically all zeros for desktop apps</div>
                <div class="validation-msg"></div>
            </div>
            <button class="btn-add-field" type="button" id="add-phone-publisherid" data-target="phone-publisherid-group" data-section="phoneIdentity" data-field-name="phonePublisherId" data-default="00000000-0000-0000-0000-000000000000" title="Add Phone Publisher ID attribute">+ Phone Publisher ID</button>
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
                    <div class="description">Package-relative path or key in resources.pri for the image displayed in the Microsoft Store and app installer</div>
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
                    ${buildSelectOptions([
                        { value: 'application', label: 'Application (default)', selected: true },
                        { value: 'framework', label: 'Framework' },
                        { value: 'resource', label: 'Resource' },
                        { value: 'modification', label: 'Modification' },
                    ])}
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
                    ${buildSelectOptions([
                        { value: '', label: '(omit)', selected: true },
                        { value: 'multiple', label: 'multiple' },
                        { value: 'single', label: 'single' },
                    ])}
                </div>
            </div>
            <div class="description">Whether the app supports multiple user sessions or only a single user</div>
        </div>
        <div class="form-group" data-field="properties.allowExecution">
            <label>Allow Execution:</label>
            <div class="custom-select" id="props-allowExecution">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="allowExecution">(omit)</button>
                <div class="custom-select-options">
                    ${buildSelectOptions([
                        { value: '', label: '(omit)', selected: true },
                        { value: 'true', label: 'true' },
                        { value: 'false', label: 'false' },
                    ])}
                </div>
            </div>
            <div class="description">Whether executables in the package can be launched (set to false for content-only packages)</div>
        </div>
        <div class="form-group" data-field="properties.allowExternalContent">
            <label>Allow External Content:</label>
            <div class="custom-select" id="props-allowExternalContent">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="allowExternalContent">(omit)</button>
                <div class="custom-select-options">
                    ${buildSelectOptions([
                        { value: '', label: '(omit)', selected: true },
                        { value: 'true', label: 'true' },
                        { value: 'false', label: 'false' },
                    ])}
                </div>
            </div>
            <div class="description">Whether the package allows content outside its install directory to be treated as package content</div>
        </div>
        <div class="form-group" data-field="properties.fileSystemWriteVirtualization">
            <label>File System Write Virtualization:</label>
            <div class="custom-select" id="props-fsWriteVirt">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="fileSystemWriteVirtualization">(omit)</button>
                <div class="custom-select-options">
                    ${buildSelectOptions([
                        { value: '', label: '(omit)', selected: true },
                        { value: 'enabled', label: 'enabled' },
                        { value: 'disabled', label: 'disabled' },
                    ])}
                </div>
            </div>
            <div class="description">Controls whether file system write operations are virtualized or written to the real file system</div>
        </div>
        <div class="form-group" data-field="properties.registryWriteVirtualization">
            <label>Registry Write Virtualization:</label>
            <div class="custom-select" id="props-regWriteVirt">
                <button class="custom-select-trigger" type="button" data-section="properties" data-field-name="registryWriteVirtualization">(omit)</button>
                <div class="custom-select-options">
                    ${buildSelectOptions([
                        { value: '', label: '(omit)', selected: true },
                        { value: 'enabled', label: 'enabled' },
                        { value: 'disabled', label: 'disabled' },
                    ])}
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
                        ${buildSelectOptions([
                            { value: 'on', label: 'on', selected: true },
                            { value: 'off', label: 'off' },
                            { value: 'default', label: 'default' },
                        ])}
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
                        ${buildSelectOptions([
                            { value: 'allow', label: 'allow', selected: true },
                            { value: 'defer', label: 'defer' },
                        ])}
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


${getEditorScript(nonce, manifestDirUri)}
</body>
</html>`;
}