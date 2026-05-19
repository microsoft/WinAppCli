/**
 * Capabilities chunk for the AppxManifest editor webview script.
 * Returns raw JavaScript to be concatenated into the webview IIFE.
 */
export function getCapabilitiesScript(): string {
    return `
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
`;
}
