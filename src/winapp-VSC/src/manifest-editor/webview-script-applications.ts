/**
 * Applications chunk for the AppxManifest editor webview script.
 * Returns raw JavaScript to be concatenated into the webview IIFE.
 */
export function getApplicationsScript(): string {
    return `
        // ─── Add application ────────────────────────────────
        document.getElementById('add-application-btn').addEventListener('click', () => {
            vscode.postMessage({ type: 'addApplication' });
        });

        function buildOptionalAssetsHtml(app, idx) {
            let html = '';
            optionalVisualAssets.forEach(asset => {
                const val = app.visualElements[asset.field];
                if (val !== null) {
                    html += '<div class="form-group" data-field="applications.' + idx + '.visualElements.' + asset.field + '">' +
                        '<label>' + escapeHtml(asset.label) + ':</label>' +
                        '<div class="browse-row">' +
                        '<input type="text" data-section="applications" data-field-name="visualElements.' + asset.field + '" data-index="' + idx + '" value="' + escapeHtml(val) + '" placeholder="' + escapeHtml(asset.placeholder) + '" />' +
                        '<button class="btn btn-sm browse-image-btn" data-section="applications" data-field-name="visualElements.' + asset.field + '" data-index="' + idx + '">Choose file</button>' +
                        '<button class="btn-remove-field" type="button" data-section="applications" data-field-name="visualElements.' + asset.field + '" data-index="' + idx + '" title="Remove ' + escapeHtml(asset.label) + '">✕</button>' +
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
                return ve[opt.veField] !== null;
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
                if (app.extensions && app.extensions.length > 0) {
                    app.extensions.forEach((extXml, eidx) => {
                        const fields = parseExtensionFields(extXml);
                        let fieldsHtml = fields.map(f => {
                            let descHtml = f.description ? '<div class="description">' + escapeHtml(f.description) + '</div>' : '';
                            const textContentAttr = f.isTextContent ? ' data-ext-text-content="true"' : '';
                            const errorClass = '';
                            const errorMsg = '<div class="validation-msg"></div>';
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
                        <button class="btn update-assets-btn mt-12">Regenerate Assets</button>
                        <div class="logo-side-by-side mt-12">
                            <div class="logo-input-col">
                                <div class="form-group" data-field="applications.\${idx}.visualElements.square150x150Logo">
                                    <label>Square 150x150 Logo:</label>
                                    <div class="browse-row">
                                        <input type="text" data-section="applications" data-field-name="visualElements.square150x150Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.square150x150Logo)}" placeholder="Assets\\\\Square150x150Logo.png" />
                                        <button class="btn btn-sm browse-image-btn" data-section="applications" data-field-name="visualElements.square150x150Logo" data-index="\${idx}">Choose file</button>
                                    </div>
                                    <div class="description">Medium tile image shown in the Start menu — package-relative path or key in resources.pri</div>
                                    <div class="validation-msg"></div>
                                </div>
                                <div class="form-group" data-field="applications.\${idx}.visualElements.square44x44Logo">
                                    <label>Square 44x44 Logo:</label>
                                    <div class="browse-row">
                                        <input type="text" data-section="applications" data-field-name="visualElements.square44x44Logo" data-index="\${idx}" value="\${escapeHtml(app.visualElements.square44x44Logo)}" placeholder="Assets\\\\Square44x44Logo.png" />
                                        <button class="btn btn-sm browse-image-btn" data-section="applications" data-field-name="visualElements.square44x44Logo" data-index="\${idx}">Choose file</button>
                                    </div>
                                    <div class="description">Small app icon shown in the taskbar, task switcher, and notification area — package-relative path or key</div>
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
                card.querySelectorAll('.optional-assets-list .btn-remove-field').forEach(btn => {
                    btn.addEventListener('click', () => {
                        const field = btn.getAttribute('data-field-name');
                        const rIdx = parseInt(btn.getAttribute('data-index'), 10);
                        vscode.postMessage({ type: 'removeVisualAsset', field: field, index: rIdx });
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

                // Check image path warnings for all visual asset fields
                card.querySelectorAll('.form-group[data-field*="visualElements."]').forEach(fg => {
                    const input = fg.querySelector('input[data-field-name]');
                    if (input) {
                        const fieldName = input.getAttribute('data-field-name');
                        const fieldIdx = parseInt(input.getAttribute('data-index'), 10);
                        checkImagePathWarning(fg, input.value, fieldName, isNaN(fieldIdx) ? undefined : fieldIdx);
                    }
                });

                // Regenerate Assets button
                const updateAssetsBtn = card.querySelector('.update-assets-btn');
                if (updateAssetsBtn) {
                    updateAssetsBtn.addEventListener('click', () => {
                        vscode.postMessage({ type: 'updateAssets' });
                    });
                }
            });
        }

        function parseExtensionFields(xml) {
            const parser = new DOMParser();
            const doc = parser.parseFromString(xml, 'application/xml');
            const root = doc.documentElement;
            if (!root) return [{ label: 'Raw XML', value: xml, editable: false, description: '' }];

            // Descriptions for known extension fields
            const fieldDescriptions = {
                // MCP Server / App Extension (windows.appExtension)
                'AppExtension.Name': 'Extension contract name, use "com.microsoft.windows.ai.mcpServer" to register as an MCP server',
                'AppExtension.Id': 'Unique identifier for this app extension instance',
                'AppExtension.DisplayName': 'Display name shown when discovering this extension',
                'AppExtension.PublicFolder': 'Folder in the package accessible to the host app, typically "Assets" or "Public"',
                'Registration': 'Path to the MCP server configuration JSON file, relative to the PublicFolder',
                // COM Server (windows.comServer)
                'ExeServer.Executable': 'Relative path to the COM server executable inside the package',
                'ExeServer.DisplayName': 'Name for this COM server, shown in system tools and diagnostics',
                'Class.Id': 'CLSID (GUID) that uniquely identifies this COM class, format: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}',
                // App Execution Alias (windows.appExecutionAlias)
                'ExecutionAlias.Alias': 'Command-line alias users type to launch your app (e.g., "myapp.exe"). Must end in .exe',
                // Background Tasks (windows.backgroundTasks)
                'Extension.EntryPoint': 'Activatable class ID for the background task (e.g., "MyApp.BackgroundTask"), or "Windows.FullTrustApplication" for Win32 apps',
                'Task.Type': 'Background task trigger type (e.g., "timer", "pushNotification", "systemEvent", "general")',
                // Protocol Activation (windows.protocol)
                'Protocol.Name': 'URI scheme this app handles (e.g., "myapp"). Users launch your app with myapp://. Lowercase letters, digits, and ".", "+", "-" only',
                // File Type Association (windows.fileTypeAssociation)
                'FileTypeAssociation.Name': 'Internal name for this file type association (letters, digits, periods only)',
                'DisplayName': 'User-friendly display name shown in the Open With dialog',
                'FileType': 'File extension to associate (must start with ".", e.g., ".txt", ".myext")',
                // Startup Task (windows.startupTask)
                'StartupTask.TaskId': 'Unique identifier for this startup task, used to enable/disable it programmatically',
                'StartupTask.Enabled': 'Whether the task runs automatically at user logon ("true" or "false")',
                'StartupTask.DisplayName': 'Name shown to the user in Task Manager Startup tab',
                // Share Target (windows.shareTarget)
                'DataFormat': 'Data format this share target accepts (e.g., "Text", "URI", "Bitmap", "Html", "StorageItems")',
                // App Service (windows.appService)
                'AppService.Name': 'Unique name for this app service that other apps use to connect (e.g., "com.contoso.myservice")',
                // Toast Notification Activation (windows.toastNotificationActivation)
                'ToastNotificationActivation.ToastActivatorCLSID': 'COM CLSID for toast activation, format: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}',
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
`;
}
