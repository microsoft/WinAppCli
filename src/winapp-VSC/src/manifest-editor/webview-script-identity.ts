/**
 * Identity chunk for the AppxManifest editor webview script.
 * Returns raw JavaScript to be concatenated into the webview IIFE.
 */
export function getIdentityScript(): string {
    return `
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
                                if (index !== null) { msg.index = parseInt(index, 10); }
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
                        if (index !== null) { msg.index = parseInt(index, 10); }
                        vscode.postMessage(msg);
                    }
                }
                return;
            }
        });
`;
}
