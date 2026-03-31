/**
 * Validation rules for appxmanifest.xml fields.
 * Provides real-time inline validation for the form editor.
 */

import { ManifestData, ValidationError } from './manifest-types';

const VERSION_REGEX = /^\d+\.\d+\.\d+\.\d+$/;
const PUBLISHER_DN_REGEX = /^CN\s*=\s*.+/i;
const IDENTITY_NAME_REGEX = /^[a-zA-Z0-9.\-]+$/;
const WINDOWS_VERSION_REGEX = /^10\.0\.\d+\.\d+$/;
const HEX_COLOR_REGEX = /^#[0-9a-fA-F]{6}$/;

/** Validate all fields and return a list of errors. */
export function validateManifest(data: ManifestData): ValidationError[] {
    const errors: ValidationError[] = [];

    // Identity validation
    if (!data.identity.name) {
        errors.push({ field: 'identity.name', message: 'Package name is required.', severity: 'error' });
    } else if (!IDENTITY_NAME_REGEX.test(data.identity.name)) {
        errors.push({ field: 'identity.name', message: 'Package name can only contain letters, numbers, dots, and hyphens.', severity: 'error' });
    }

    if (!data.identity.publisher) {
        errors.push({ field: 'identity.publisher', message: 'Publisher is required.', severity: 'error' });
    } else if (!PUBLISHER_DN_REGEX.test(data.identity.publisher)) {
        errors.push({ field: 'identity.publisher', message: 'Publisher must be a valid X.500 distinguished name starting with CN=.', severity: 'error' });
    }

    if (!data.identity.version) {
        errors.push({ field: 'identity.version', message: 'Version is required.', severity: 'error' });
    } else if (!VERSION_REGEX.test(data.identity.version)) {
        errors.push({ field: 'identity.version', message: 'Version must be in Major.Minor.Build.Revision format (e.g. 1.0.0.0).', severity: 'error' });
    }

    // Properties validation
    if (!data.properties.displayName) {
        errors.push({ field: 'properties.displayName', message: 'Display name is required.', severity: 'error' });
    } else if (data.properties.displayName.length > 256) {
        errors.push({ field: 'properties.displayName', message: 'Display name must be 256 characters or fewer.', severity: 'error' });
    }

    if (data.properties.description && data.properties.description.length > 2048) {
        errors.push({ field: 'properties.description', message: 'Description must be 2048 characters or fewer.', severity: 'error' });
    }

    // Dependencies validation
    for (let i = 0; i < data.dependencies.targetDeviceFamilies.length; i++) {
        const family = data.dependencies.targetDeviceFamilies[i];
        const prefix = `dependencies.targetDeviceFamily.${i}`;

        if (family.minVersion && !WINDOWS_VERSION_REGEX.test(family.minVersion)) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion must be in 10.0.XXXXX.0 format.', severity: 'error' });
        }

        if (family.maxVersionTested && !WINDOWS_VERSION_REGEX.test(family.maxVersionTested)) {
            errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested must be in 10.0.XXXXX.0 format.', severity: 'error' });
        }

        if (family.minVersion && family.maxVersionTested &&
            WINDOWS_VERSION_REGEX.test(family.minVersion) && WINDOWS_VERSION_REGEX.test(family.maxVersionTested)) {
            if (compareVersions(family.maxVersionTested, family.minVersion) < 0) {
                errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested must be greater than or equal to MinVersion.', severity: 'error' });
            }
        }
    }

    // Applications validation
    for (let i = 0; i < data.applications.length; i++) {
        const app = data.applications[i];
        const prefix = `applications.${i}`;

        if (app.visualElements.displayName && app.visualElements.displayName.length > 256) {
            errors.push({ field: `${prefix}.visualElements.displayName`, message: 'Display name must be 256 characters or fewer.', severity: 'error' });
        }

        if (app.visualElements.description && app.visualElements.description.length > 2048) {
            errors.push({ field: `${prefix}.visualElements.description`, message: 'Description must be 2048 characters or fewer.', severity: 'error' });
        }

        if (app.visualElements.backgroundColor &&
            app.visualElements.backgroundColor.toLowerCase() !== 'transparent' &&
            !HEX_COLOR_REGEX.test(app.visualElements.backgroundColor)) {
            errors.push({ field: `${prefix}.visualElements.backgroundColor`, message: 'Background color must be a hex color (e.g. #FFFFFF) or "transparent".', severity: 'error' });
        }
    }

    return errors;
}

/** Compare two version strings. Returns negative if a < b, 0 if equal, positive if a > b. */
function compareVersions(a: string, b: string): number {
    const pa = a.split('.').map(Number);
    const pb = b.split('.').map(Number);
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        const na = pa[i] || 0;
        const nb = pb[i] || 0;
        if (na !== nb) { return na - nb; }
    }
    return 0;
}
