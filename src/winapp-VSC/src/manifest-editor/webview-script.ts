/**
 * Client-side JavaScript for the AppxManifest editor webview.
 * Extracted from webview-content.ts for maintainability.
 */

import { CAPABILITY_DESCRIPTIONS, EXTENSION_TEMPLATES, OPTIONAL_VISUAL_ASSETS, SHOW_NAME_ON_TILES_OPTIONS } from './manifest-types';
import { getIdentityScript } from './webview-script-identity';
import { getCapabilitiesScript } from './webview-script-capabilities';
import { getDependenciesScript } from './webview-script-dependencies';
import { getApplicationsScript } from './webview-script-applications';
import { getResourcesScript } from './webview-script-resources';

export function getEditorScript(nonce: string, manifestDirUri: string): string {
    const safeManifestDirUri = JSON.stringify(manifestDirUri);
    return `    <script nonce="${nonce}">
    (function() {
        const vscode = acquireVsCodeApi();
        const manifestDirUri = ${safeManifestDirUri};
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
            document.querySelectorAll('.tab-content').forEach(c => {
                c.classList.remove('active');
                c.setAttribute('aria-hidden', 'true');
            });
            btn.classList.add('active');
            btn.setAttribute('aria-selected', 'true');
            btn.setAttribute('tabindex', '0');
            const tab = btn.getAttribute('data-tab');
            const panel = document.getElementById('tab-' + tab);
            panel.classList.add('active');
            panel.setAttribute('aria-hidden', 'false');
            // Move focus into the tab panel's first focusable element
            const focusable = panel.querySelector('input, select, button, textarea, [tabindex="0"]');
            if (focusable) { focusable.focus(); } else { btn.focus(); }
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
` + getIdentityScript() + getCapabilitiesScript() + getDependenciesScript() + getApplicationsScript() + getResourcesScript() + `

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
            checkImagePathWarning(
                document.getElementById('props-logo')?.closest('.logo-input-col'),
                data.properties.logo,
                'logo',
                undefined
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
                if (info.selectionStart !== null &&
                    typeof target.setSelectionRange === 'function') {
                    try { target.setSelectionRange(info.selectionStart, info.selectionEnd); } catch(e) {}
                }
            }
        }

        // ─── Validation display ─────────────────────────────
        function showValidationErrors(errors) {
            // Clear only manifest-level validation errors (those with data-field), not extension field errors
            document.querySelectorAll('.form-group[data-field]').forEach(fg => {
                fg.classList.remove('has-warning');
                setGroupValidation(fg, 'clear');
            });
            // Clear extension field validation errors
            document.querySelectorAll('input[data-ext-field]').forEach(inp => {
                const fg = inp.closest('.form-group');
                if (fg) {
                    fg.classList.remove('has-warning');
                    setGroupValidation(fg, 'clear');
                }
            });

            // Show new errors
            errors.forEach(err => {
                // Check if this is an extension field error (applications.N.extensions.M.FieldLabel)
                const extMatch = err.field.match(/^applications\\.(\d+)\\.extensions\\.(\d+)\\.(.+)$/);
                if (extMatch) {
                    const appIdx = extMatch[1];
                    const extIdx = extMatch[2];
                    const fieldLabel = extMatch[3];
                    const inp = document.querySelector('input[data-app-index="' + appIdx + '"][data-ext-index="' + extIdx + '"][data-ext-field="' + fieldLabel + '"]');
                    if (inp) {
                        const fg = inp.closest('.form-group');
                        if (fg) {
                            if (err.severity === 'warning') { fg.classList.add('has-warning'); }
                            setGroupValidation(fg, err.severity, err.message);
                        }
                    }
                    return;
                }

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
                case 'imagePathStatus': {
                    // Find the form-group for this field
                    let fg = null;
                    if (msg.field === 'logo') {
                        fg = document.getElementById('props-logo')?.closest('.logo-input-col');
                    } else {
                        const selector = msg.index !== undefined
                            ? '.form-group[data-field="applications.' + msg.index + '.' + msg.field + '"]'
                            : '.form-group[data-field*="' + msg.field + '"]';
                        fg = document.querySelector(selector);
                    }
                    if (!fg) break;
                    const vmsg = fg.querySelector('.validation-msg');
                    if (!vmsg || vmsg.classList.contains('error')) break;

                    if (msg.status === 'found') {
                        if (msg.aspectWarning) {
                            fg.classList.add('has-warning');
                            vmsg.classList.add('warning');
                            vmsg.textContent = msg.aspectWarning;
                        } else {
                            fg.classList.remove('has-warning');
                            vmsg.textContent = ''; vmsg.classList.remove('warning'); vmsg.innerHTML = '';
                        }
                    } else if (msg.status === 'external') {
                        fg.classList.add('has-warning');
                        vmsg.classList.add('warning');
                        vmsg.innerHTML = 'Image not in package directory. <a href="#" class="copy-to-assets-link" data-source="' + escapeHtml(msg.sourcePath) + '" data-field="' + escapeHtml(msg.field) + '" data-index="' + (msg.index !== undefined ? msg.index : '') + '">Copy to Assets folder?</a>';
                    } else {
                        fg.classList.add('has-warning');
                        vmsg.classList.add('warning');
                        vmsg.textContent = '⚠ Image not found in package directory';
                    }
                    break;
                }
            }
        });

        // ─── Helpers ────────────────────────────────────────
        function escapeHtml(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        function isImagePath(path) {
            return /\.(png|jpg|jpeg|gif|bmp|ico|svg|tiff?|webp)$/i.test(path);
        }

        function updateLogoPreview(imgEl, logoPath, captionEl) {
            if (logoPath && manifestDirUri && imgEl) {
                imgEl.classList.remove('loaded');
                imgEl.removeAttribute('alt');
                if (captionEl) { captionEl.textContent = ''; }
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
                if (captionEl) { captionEl.textContent = ''; }
            }
        }

        function checkImagePathWarning(formGroup, logoPath, fieldName, fieldIndex) {
            if (!formGroup) return;
            const msg = formGroup.querySelector('.validation-msg');
            if (!msg) return;
            if (!logoPath || !isImagePath(logoPath)) {
                formGroup.classList.remove('has-warning');
                if (!msg.classList.contains('error')) { msg.textContent = ''; msg.classList.remove('warning'); msg.innerHTML = ''; }
                return;
            }
            vscode.postMessage({ type: 'checkImagePath', imagePath: logoPath, field: fieldName || '', index: fieldIndex });
        }

        function toColorValue(str) {
            if (!str || str === 'transparent') return '#000000';
            if (/^#[0-9a-fA-F]{6}$/.test(str)) return str;
            return '#000000';
        }

        // Delegated click handler for "Copy to Assets" links
        document.addEventListener('click', (e) => {
            const link = e.target.closest('.copy-to-assets-link');
            if (!link) return;
            e.preventDefault();
            const sourcePath = link.getAttribute('data-source');
            const field = link.getAttribute('data-field');
            const idx = link.getAttribute('data-index');
            const section = field === 'logo' ? 'properties' : 'applications';
            vscode.postMessage({
                type: 'copyToAssets',
                sourcePath: sourcePath,
                section: section,
                field: field,
                index: idx !== '' ? parseInt(idx, 10) : undefined
            });
        });

        // Signal ready
        vscode.postMessage({ type: 'ready' });
    })();
    </script>`;
}
