/**
 * Dependencies chunk for the AppxManifest editor webview script.
 * Returns raw JavaScript to be concatenated into the webview IIFE.
 */
export function getDependenciesScript(): string {
    return `
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
`;
}
