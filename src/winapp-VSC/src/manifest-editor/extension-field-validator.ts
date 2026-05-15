/**
 * Validates extension field values for the manifest editor.
 * Extracted to a standalone module so both the webview inline script and
 * unit tests can share the same validation logic.
 *
 * NOTE: This module is consumed in two ways:
 * 1. Imported directly in unit tests (Node.js)
 * 2. Its function body is inlined into the webview script template
 *    via getValidateExtFieldSource() — see webview-script.ts
 */

export interface ExtFieldValidation {
    level: 'error' | 'warning';
    message: string;
}

const GUID_REGEX = /^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$/;

export function validateExtField(fieldLabel: string, value: string, isRequired: boolean): ExtFieldValidation | null {
    // Required check first
    if (isRequired && !value) {
        return { level: 'error', message: 'This field is required.' };
    }
    if (!value) { return null; }

    switch (fieldLabel) {
        case 'Class.Id':
        case 'ToastNotificationActivation.ToastActivatorCLSID':
            if (!GUID_REGEX.test(value)) {
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
        case 'Task.Type': {
            const validTypes = ['timer', 'pushNotification', 'systemEvent', 'general', 'audio', 'controlChannel', 'bluetooth', 'location', 'deviceUse', 'deviceServicing', 'deviceConnectionChange'];
            if (!validTypes.includes(value)) {
                return { level: 'warning', message: 'Common values: ' + validTypes.slice(0, 5).join(', ') + ', ...' };
            }
            break;
        }
        case 'AppService.Name':
            if (!/^[a-zA-Z][a-zA-Z0-9._]*$/.test(value)) {
                return { level: 'warning', message: 'Recommended format: reverse-domain style (e.g., "com.contoso.myservice").' };
            }
            break;
    }
    return null;
}
