/**
 * Client-side JavaScript for the AppxManifest editor webview.
 * Extracted from webview-content.ts for maintainability.
 */

import { CAPABILITY_DESCRIPTIONS, EXTENSION_TEMPLATES, OPTIONAL_VISUAL_ASSETS, SHOW_NAME_ON_TILES_OPTIONS } from './manifest-types';

export function getEditorScript(nonce: string, manifestDirUri: string): string {
    return `    <script nonce="${nonce}">
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
        function activateTab(btn) {
            document.querySelectorAll('.tab-btn').forEach(b => {
                b.classList.remove('active');
                b.setAttribute('aria-selected', 'false');
                b.setAttribute('tabindex', '-1');
            });
            document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
            btn.classList.add('active');
            btn.setAttribute('aria-selected', 'true');
            btn.setAttribute('tabindex', '0');
            btn.focus();
            const tab = btn.getAttribute('data-tab');
            document.getElementById('tab-' + tab).classList.add('active');
        }

        document.querySelectorAll('.tab-btn').forEach(btn => {
            btn.addEventListener('click', () => activateTab(btn));
        });

        // WAI-ARIA Tabs: ArrowLeft/ArrowRight to cycle visible tabs
        document.querySelector('.tab-bar').addEventListener('keydown', (e) => {
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
            const tabs = Array.from(document.querySelectorAll('.tab-btn:not(.hidden-tab)'));
            if (!tabs.length) return;
            const current = document.querySelector('.tab-btn.active');
            let idx = tabs.indexOf(current);
            if (e.key === 'ArrowRight') { idx = (idx + 1) % tabs.length; }
            else { idx = idx <= 0 ? tabs.length - 1 : idx - 1; }
            activateTab(tabs[idx]);
            e.preventDefault();
        });

        // Set initial tabindex: 0 on active, -1 on others
        document.querySelectorAll('.tab-btn').forEach(btn => {
            btn.setAttribute('tabindex', btn.classList.contains('active') ? '0' : '-1');
        });

        // ─── View XML / Open as text ────────────────────────
        document.getElementById('view-xml-btn').addEventListener('click', () => {
            vscode.postMessage({ type: 'openAsText' });
        });
        document.getElementById('open-xml-link').addEventListener('click', () => {
            vscode.postMessage({ type: 'openAsText' });
        });

        // ─── Validation helper ──────────────────────────────
        function setGroupValidation(group, level, message) {
            if (!group) return;
            const msg = group.querySelector('.validation-msg');
            if (level === 'error') {
                group.classList.add('has-error');
                if (msg) { msg.className = 'validation-msg error'; msg.textContent = message || ''; }
            } else if (level === 'warning') {
                group.classList.remove('has-error');
                if (msg) { msg.className = 'validation-msg warning'; msg.textContent = message || ''; }
            } else {
                group.classList.remove('has-error');
                if (msg) { msg.className = 'validation-msg'; msg.textContent = ''; }
            }
        }

        // ─── Field change handler ───────────────────────────
        function onFieldChange(el) {
            const section = el.getAttribute('data-section');
            const field = el.getAttribute('data-field-name');
            const value = el.value;
            const index = parseInt(el.getAttribute('data-index') || '0', 10);

            // Inline GUID validation for phoneIdentity fields
            if (section === 'phoneIdentity' && (field === 'phoneProductId' || field === 'phonePublisherId')) {
                const group = el.closest('.form-group');
                const label = field === 'phoneProductId' ? 'Phone Product ID' : 'Phone Publisher ID';
                const guidPattern = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
                if (!value || !guidPattern.test(value)) {
                    setGroupValidation(group, 'error', label + ' must be a valid GUID (e.g. 00000000-0000-0000-0000-000000000000)');
                } else {
                    setGroupValidation(group, 'clear');
                }
            }

            vscode.postMessage({ type: 'fieldChanged', section, field, value, index });
        }

        // Debounce helper for text inputs
        let debounceTimers = {};
        let pendingElements = {};
        function debouncedFieldChange(el) {
            const field = el.getAttribute('data-field-name') || '';
            const idx = el.getAttribute('data-index') || '';
            const key = el.id || (field + ':' + idx);
            clearTimeout(debounceTimers[key]);
            pendingElements[key] = el;
            debounceTimers[key] = setTimeout(() => {
                onFieldChange(el);
                delete pendingElements[key];
                delete debounceTimers[key];
            }, 300);
        }

        function flushPendingChanges() {
            const changes = [];
            for (const key in pendingElements) {
                const el = pendingElements[key];
                clearTimeout(debounceTimers[key]);
                changes.push({
                    section: el.getAttribute('data-section'),
                    field: el.getAttribute('data-field-name'),
                    value: el.value,
                    index: parseInt(el.getAttribute('data-index') || '0', 10),
                });
            }
            debounceTimers = {};
            pendingElements = {};
            return changes;
        }

        // ─── Shared custom-select wiring helper ────────────────
        function wireCustomSelect(triggerEl, optionsEl, onChange) {
            // ARIA setup
            triggerEl.setAttribute('role', 'combobox');
            triggerEl.setAttribute('aria-haspopup', 'listbox');
            triggerEl.setAttribute('aria-expanded', 'false');
            optionsEl.setAttribute('role', 'listbox');
            optionsEl.querySelectorAll('.custom-select-option').forEach(opt => {
                opt.setAttribute('role', 'option');
            });

            // Click to toggle
            triggerEl.addEventListener('click', (e) => {
                e.stopPropagation();
                const isOpen = optionsEl.classList.toggle('open');
                triggerEl.setAttribute('aria-expanded', String(isOpen));
            });

            // Keyboard navigation
            triggerEl.addEventListener('keydown', (e) => {
                const options = Array.from(optionsEl.querySelectorAll('.custom-select-option'));
                if (!options.length) return;

                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    if (!optionsEl.classList.contains('open')) {
                        optionsEl.classList.add('open');
                        triggerEl.setAttribute('aria-expanded', 'true');
                        const sel = optionsEl.querySelector('.custom-select-option.selected') || options[0];
                        if (sel) sel.classList.add('focused');
                    } else {
                        const focused = optionsEl.querySelector('.custom-select-option.focused');
                        if (focused) focused.click();
                    }
                } else if (e.key === 'Escape') {
                    optionsEl.classList.remove('open');
                    triggerEl.setAttribute('aria-expanded', 'false');
                    options.forEach(o => o.classList.remove('focused'));
                } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                    e.preventDefault();
                    if (!optionsEl.classList.contains('open')) {
                        optionsEl.classList.add('open');
                        triggerEl.setAttribute('aria-expanded', 'true');
                    }
                    const cur = optionsEl.querySelector('.custom-select-option.focused');
                    let idx = cur ? options.indexOf(cur) : -1;
                    options.forEach(o => o.classList.remove('focused'));
                    idx = e.key === 'ArrowDown' ? (idx + 1) % options.length : (idx <= 0 ? options.length - 1 : idx - 1);
                    options[idx].classList.add('focused');
                    options[idx].scrollIntoView({ block: 'nearest' });
                }
            });

            // Option click
            optionsEl.querySelectorAll('.custom-select-option').forEach(opt => {
                opt.addEventListener('click', () => {
                    const val = opt.getAttribute('data-value');
                    triggerEl.textContent = opt.textContent;
                    optionsEl.classList.remove('open');
                    triggerEl.setAttribute('aria-expanded', 'false');
                    optionsEl.querySelectorAll('.custom-select-option').forEach(o => {
                        o.classList.remove('selected');
                        o.classList.remove('focused');
                    });
                    opt.classList.add('selected');
                    onChange(val, triggerEl);
                });
            });
        }

        // ─── Generic custom-select initialization ─────────────
        function initCustomSelects(container) {
            (container || document).querySelectorAll('.custom-select').forEach(wrapper => {
                const trigger = wrapper.querySelector('.custom-select-trigger');
                const options = wrapper.querySelector('.custom-select-options');
                if (!trigger || !options) return;
                // Skip if already initialized or if trigger has no data-section (special selects like pkg-type)
                if (trigger.hasAttribute('data-cs-init')) return;
                const section = trigger.getAttribute('data-section');
                if (!section) return;
                trigger.setAttribute('data-cs-init', '1');

                wireCustomSelect(trigger, options, (val, triggerEl) => {
                    triggerEl.setAttribute('data-current-value', val);
                    const field = triggerEl.getAttribute('data-field-name');
                    const index = parseInt(triggerEl.getAttribute('data-index') || '0', 10);
                    vscode.postMessage({ type: 'fieldChanged', section, field, value: val, index });
                });
            });
        }

        // Global click to close all open custom selects
        document.addEventListener('click', () => {
            document.querySelectorAll('.custom-select-options.open').forEach(o => {
                o.classList.remove('open');
                o.querySelectorAll('.custom-select-option').forEach(opt => opt.classList.remove('focused'));
                const trigger = o.closest('.custom-select')?.querySelector('.custom-select-trigger');
                if (trigger) trigger.setAttribute('aria-expanded', 'false');
            });
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
            wireCustomSelect(pkgTypeTrigger, pkgTypeOptions, (val) => {
                vscode.postMessage({ type: 'packageTypeChanged', value: val });
            });
            // Close on outside click (the global handler covers generic selects)
            document.addEventListener('click', () => {
                pkgTypeOptions.classList.remove('open');
                pkgTypeTrigger.setAttribute('aria-expanded', 'false');
            });
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
                        if (defaultVal) {
                            // Immediately send the default value to the extension
                            const section = input.getAttribute('data-section');
                            const field = input.getAttribute('data-field-name');
                            const index = input.getAttribute('data-index');
                            if (section && field) {
                                const msg = { type: 'fieldChanged', section, field, value: defaultVal };
                                if (index !== null && index !== undefined) { msg.index = parseInt(index, 10); }
                                vscode.postMessage(msg);
                            }
                        } else if (input.tagName === 'INPUT') {
                            const fieldAttr = group.getAttribute('data-field') || '';
                            const errText = fieldAttr === 'identity.resourceId'
                                ? 'Resource ID must be at least 1 character.'
                                : 'This field is required. Enter a value or remove the field.';
                            setGroupValidation(group, 'error', errText);
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
                // PhonePublisherId is optional
                toggleOptionalField('phone-publisherid-group', 'add-phone-publisherid', data.phoneIdentity.phonePublisherId);
                setValueIfNotFocused('phone-publisher-id', data.phoneIdentity.phonePublisherId || '', focused);
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

        function renderReorderableList(containerId, items, config) {
            const container = document.getElementById(containerId);
            container.innerHTML = '';
            items.forEach((item, idx) => {
                const div = document.createElement('div');
                div.className = 'list-item';
                div.innerHTML =
                    '<div class="item-header">' +
                        '<span class="item-title">' + config.titleFn(item, idx) + '</span>' +
                        '<div class="item-actions">' +
                            '<button class="btn btn-sm move-up" data-index="' + idx + '"' + (idx === 0 ? ' disabled' : '') + ' title="Move Up">▲</button>' +
                            '<button class="btn btn-sm move-down" data-index="' + idx + '"' + (idx === items.length - 1 ? ' disabled' : '') + ' title="Move Down">▼</button>' +
                            '<button class="btn-remove-field remove-item" data-index="' + idx + '" title="Remove">✕</button>' +
                        '</div>' +
                    '</div>' +
                    config.fieldsFn(item, idx);
                container.appendChild(div);

                div.querySelectorAll('input[data-section]').forEach(inp => {
                    inp.addEventListener('input', () => debouncedFieldChange(inp));
                });
                if (config.hasCustomSelects) {
                    initCustomSelects(div);
                }
                div.querySelector('.remove-item').addEventListener('click', () => {
                    vscode.postMessage({ type: config.removeType, index: idx });
                });
                div.querySelector('.move-up').addEventListener('click', () => {
                    vscode.postMessage({ type: config.moveType, index: idx, direction: 'up' });
                });
                div.querySelector('.move-down').addEventListener('click', () => {
                    vscode.postMessage({ type: config.moveType, index: idx, direction: 'down' });
                });
            });
        }

        function renderTargetDeviceFamilies(families) {
            renderReorderableList('target-device-families', families, {
                titleFn: (fam) => 'Target Device: ' + escapeHtml(fam.name),
                removeType: 'removeTargetDeviceFamily',
                moveType: 'moveTargetDeviceFamily',
                fieldsFn: (fam, idx) => \`
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
                    </div>\`,
            });
        }

        function renderPackageDependencies(deps) {
            renderReorderableList('package-dependencies', deps, {
                titleFn: () => 'Name:',
                removeType: 'removePackageDependency',
                moveType: 'movePackageDependency',
                hasCustomSelects: true,
                fieldsFn: (dep, idx) => \`
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
                    </div>\`,
            });
        }

        function renderMainPackageDependencies(deps) {
            renderReorderableList('main-package-dependencies', deps, {
                titleFn: () => 'Name:',
                removeType: 'removeMainPackageDependency',
                moveType: 'moveMainPackageDependency',
                fieldsFn: (dep, idx) => \`
                    <div class="form-group" data-field="dependencies.mainPackageDependency.\${idx}.name">
                        <input type="text" data-section="dependencies" data-field-name="mainPackageDependency.name" data-index="\${idx}" value="\${escapeHtml(dep.name)}" placeholder="MainPackageName" />
                        <div class="description">Package identity name of the main package</div>
                        <div class="validation-msg"></div>
                    </div>\`,
            });
        }

        function renderDriverConstraints(constraints) {
            renderReorderableList('driver-constraints', constraints, {
                titleFn: () => 'Name:',
                removeType: 'removeDriverConstraint',
                moveType: 'moveDriverConstraint',
                fieldsFn: (dc, idx) => \`
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
                    </div>\`,
            });
        }

        function renderOSPackageDependencies(deps) {
            renderReorderableList('os-package-dependencies', deps, {
                titleFn: () => 'Name:',
                removeType: 'removeOSPackageDependency',
                moveType: 'moveOSPackageDependency',
                fieldsFn: (dep, idx) => \`
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
                    </div>\`,
            });
        }

        function renderHostRuntimeDependencies(deps) {
            renderReorderableList('host-runtime-dependencies', deps, {
                titleFn: () => 'Name:',
                removeType: 'removeHostRuntimeDependency',
                moveType: 'moveHostRuntimeDependency',
                fieldsFn: (dep, idx) => \`
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
                    </div>\`,
            });
        }

        function renderExternalDependencies(deps) {
            renderReorderableList('external-dependencies', deps, {
                titleFn: () => 'Name:',
                removeType: 'removeExternalDependency',
                moveType: 'moveExternalDependency',
                hasCustomSelects: true,
                fieldsFn: (dep, idx) => \`
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
                    </div>\`,
            });
        }

        function renderResources(resources) {
            const scaleOptions = ['', '80', '100', '120', '125', '140', '150', '160', '175', '180', '200', '225', '250', '300', '350', '400', '450'];
            const dxOptions = ['', 'dx9', 'dx10', 'dx11', 'dx12'];
            renderReorderableList('resources-list', resources, {
                titleFn: () => 'Language:',
                removeType: 'removeResource',
                moveType: 'moveResource',
                hasCustomSelects: true,
                fieldsFn: (res, idx) => {
                    const scaleOptionsHtml = scaleOptions.map(s =>
                        '<div class="custom-select-option' + (res.scale === s ? ' selected' : '') + '" data-value="' + s + '">' + (s || '(none)') + '</div>'
                    ).join('');
                    const dxOptionsHtml = dxOptions.map(d =>
                        '<div class="custom-select-option' + (res.dxFeatureLevel === d ? ' selected' : '') + '" data-value="' + d + '">' + (d || '(none)') + '</div>'
                    ).join('');
                    const scaleLabel = res.scale || '(none)';
                    const dxLabel = res.dxFeatureLevel || '(none)';
                    return \`
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
                    </div>\`;
                },
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
                    'Registration', 'ExecutionAlias.Alias',
                    'Extension.EntryPoint', 'Task.Type',
                    'Protocol.Name',
                    'FileTypeAssociation.Name', 'FileType',
                    'StartupTask.TaskId', 'StartupTask.DisplayName',
                    'DataFormat',
                    'AppService.Name',
                    'ToastNotificationActivation.ToastActivatorCLSID'
                ]);
                if (app.extensions && app.extensions.length > 0) {
                    app.extensions.forEach((extXml, eidx) => {
                        const fields = parseExtensionFields(extXml);
                        let fieldsHtml = fields.map(f => {
                            let descHtml = f.description ? '<div class="description">' + escapeHtml(f.description) + '</div>' : '';
                            const textContentAttr = f.isTextContent ? ' data-ext-text-content="true"' : '';
                            const isRequired = f.editable && requiredExtFields.has(f.label);
                            const validation = f.editable ? validateExtField(f.label, f.value, isRequired) : null;
                            const errorClass = validation && validation.level ? ' has-' + validation.level : '';
                            const errorMsg = validation && validation.message
                                ? '<div class="validation-msg ' + validation.level + '">' + escapeHtml(validation.message) + '</div>'
                                : '<div class="validation-msg"></div>';
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
                        // Live validation for extension fields
                        const fg = inp.closest('.form-group');
                        const fieldLabel = inp.getAttribute('data-ext-field');
                        const isReq = requiredExtFields.has(fieldLabel);
                        if (fg) {
                            const validation = validateExtField(fieldLabel, inp.value, isReq);
                            fg.classList.remove('has-warning');
                            if (validation && validation.level) {
                                setGroupValidation(fg, validation.level, validation.message);
                            } else {
                                setGroupValidation(fg, 'clear');
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
                fg.classList.remove('has-warning');
                setGroupValidation(fg, 'clear');
            });

            // Show new errors
            errors.forEach(err => {
                const fg = document.querySelector('.form-group[data-field="' + err.field + '"]');
                if (fg) {
                    if (err.severity === 'warning') { fg.classList.add('has-warning'); }
                    setGroupValidation(fg, err.severity, err.message);
                }
            });

            // Re-apply required errors for user-opened optional text fields that are still empty
            userOpenedOptionalFields.forEach(groupId => {
                const group = document.getElementById(groupId);
                if (!group || group.classList.contains('hidden-optional')) return;
                if (group.classList.contains('has-error') || group.classList.contains('has-warning')) return;
                const input = group.querySelector('input[data-section]');
                if (input && !input.value) {
                    const fieldAttr = group.getAttribute('data-field') || '';
                    const errText = fieldAttr === 'identity.resourceId'
                        ? 'Resource ID must be at least 1 character.'
                        : 'This field is required. Enter a value or remove the field.';
                    setGroupValidation(group, 'error', errText);
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
                case 'flushChanges': {
                    const changes = flushPendingChanges();
                    vscode.postMessage({ type: 'changesFlushed', changes, nonce: msg.nonce });
                    break;
                }
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

        /** Validate an extension field value and return { level, message } or null if valid.
         *  Keep in sync with extension-field-validator.ts (canonical source). */
        function validateExtField(fieldLabel, value, isRequired) {
            const guidRegex = /^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$/;

            // Required check first
            if (isRequired && !value) {
                return { level: 'error', message: 'This field is required.' };
            }
            if (!value) return null;

            switch (fieldLabel) {
                case 'Class.Id':
                case 'ToastNotificationActivation.ToastActivatorCLSID':
                    if (!guidRegex.test(value)) {
                        return { level: 'error', message: 'Must be a valid GUID, e.g., {12345678-1234-1234-1234-123456789012}' };
                    }
                    break;
                case 'ExecutionAlias.Alias':
                    if (!/\.exe$/i.test(value)) {
                        return { level: 'error', message: 'Alias must end with .exe (e.g., "myapp.exe").' };
                    }
                    if (/[\\/:*?"<>|]/.test(value)) {
                        return { level: 'error', message: 'Alias must not contain path separators or special characters.' };
                    }
                    break;
                case 'Protocol.Name':
                    if (!/^[a-z][a-z0-9.+\-]*$/.test(value)) {
                        return { level: 'error', message: 'Protocol must start with a lowercase letter and contain only lowercase letters, digits, ".", "+", or "-".' };
                    }
                    break;
                case 'FileType':
                    if (!/^\.[a-zA-Z0-9]+$/.test(value)) {
                        return { level: 'error', message: 'File extension must start with "." followed by alphanumeric characters (e.g., ".txt").' };
                    }
                    break;
                case 'FileTypeAssociation.Name':
                    if (!/^[a-zA-Z0-9.]+$/.test(value)) {
                        return { level: 'error', message: 'Name must contain only letters, digits, and periods.' };
                    }
                    break;
                case 'StartupTask.Enabled':
                    if (value !== 'true' && value !== 'false') {
                        return { level: 'error', message: 'Value must be "true" or "false".' };
                    }
                    break;
                case 'ExeServer.Executable':
                    if (!/\.(exe|dll)$/i.test(value)) {
                        return { level: 'warning', message: 'Expected a .exe or .dll path.' };
                    }
                    break;
                case 'Task.Type':
                    var validTypes = ['timer', 'pushNotification', 'systemEvent', 'general', 'audio', 'controlChannel', 'bluetooth', 'location', 'deviceUse', 'deviceServicing', 'deviceConnectionChange'];
                    if (!validTypes.includes(value)) {
                        return { level: 'warning', message: 'Common values: ' + validTypes.slice(0, 5).join(', ') + ', ...' };
                    }
                    break;
                case 'AppService.Name':
                    if (!/^[a-zA-Z][a-zA-Z0-9._]*$/.test(value)) {
                        return { level: 'warning', message: 'Recommended format: reverse-domain style (e.g., "com.contoso.myservice").' };
                    }
                    break;
            }
            return null;
        }

        function toColorValue(str) {
            if (!str || str === 'transparent') return '#000000';
            if (/^#[0-9a-fA-F]{6}$/.test(str)) return str;
            return '#000000';
        }

        // Signal ready
        vscode.postMessage({ type: 'ready' });
    })();
    </script>`;
}