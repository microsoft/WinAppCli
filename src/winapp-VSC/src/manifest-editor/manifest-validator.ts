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
const GUID_REGEX = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
// BCP-47: language[-script][-region][-variant] (simplified for common MSIX usage)
// Also accepts private-use tags like "x-generate" used by MSIX tooling
const BCP47_REGEX = /^(?:x(?:-[a-zA-Z0-9]{1,8})+|[a-zA-Z]{2,3}(-[a-zA-Z]{4})?(-[a-zA-Z]{2}|\d{3})?(-[a-zA-Z0-9]{5,8})*)$/;

/** Returns true if a path has a non-.png file extension (i.e. an unsupported image format). */
function hasNonPngExtension(path: string): boolean {
    const filename = path.split(/[\\/]/).pop() || '';
    const dotIdx = filename.lastIndexOf('.');
    if (dotIdx < 0) { return false; } // no extension — valid (could be scale-qualified)
    return filename.substring(dotIdx).toLowerCase() !== '.png';
}

const PNG_ERROR = 'Visual assets must be PNG files (.png).';

/** Validate an image field: error if blank (but present in manifest) or non-.png extension. */
function validateImageField(errors: ValidationError[], field: string, value: string | null | undefined): void {
    if (value === '') {
        errors.push({ field, message: 'Image path cannot be empty.', severity: 'error' });
    } else if (value && hasNonPngExtension(value)) {
        errors.push({ field, message: PNG_ERROR, severity: 'error' });
    }
}

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

    // Phone Identity validation
    if (data.phoneIdentity) {
        if (data.phoneIdentity.phoneProductId && !GUID_REGEX.test(data.phoneIdentity.phoneProductId)) {
            errors.push({ field: 'phoneIdentity.phoneProductId', message: 'Phone Product ID must be a valid GUID (e.g. 00000000-0000-0000-0000-000000000000).', severity: 'error' });
        }
        if (data.phoneIdentity.phonePublisherId && !GUID_REGEX.test(data.phoneIdentity.phonePublisherId)) {
            errors.push({ field: 'phoneIdentity.phonePublisherId', message: 'Phone Publisher ID must be a valid GUID (e.g. 00000000-0000-0000-0000-000000000000).', severity: 'error' });
        }
    }

    // Properties validation
    if (!data.properties.displayName) {
        errors.push({ field: 'properties.displayName', message: 'Display name is required.', severity: 'error' });
    } else if (data.properties.displayName.length > 256) {
        errors.push({ field: 'properties.displayName', message: 'Display name must be 256 characters or fewer.', severity: 'error' });
    }

    if (!data.properties.publisherDisplayName) {
        errors.push({ field: 'properties.publisherDisplayName', message: 'Publisher display name is required.', severity: 'error' });
    }

    if (!data.properties.logo) {
        errors.push({ field: 'properties.logo', message: 'Store logo path is required.', severity: 'error' });
    }
    validateImageField(errors, 'properties.logo', data.properties.logo);

    if (data.properties.description && data.properties.description.length > 2048) {
        errors.push({ field: 'properties.description', message: 'Description must be 2048 characters or fewer.', severity: 'error' });
    }

    // Dependencies validation
    for (let i = 0; i < data.dependencies.targetDeviceFamilies.length; i++) {
        const family = data.dependencies.targetDeviceFamilies[i];
        const prefix = `dependencies.targetDeviceFamily.${i}`;

        if (!family.minVersion) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion is required.', severity: 'error' });
        } else if (!WINDOWS_VERSION_REGEX.test(family.minVersion)) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion must be in 10.0.XXXXX.0 format.', severity: 'error' });
        }

        if (!family.maxVersionTested) {
            errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested is required.', severity: 'error' });
        } else if (!WINDOWS_VERSION_REGEX.test(family.maxVersionTested)) {
            errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested must be in 10.0.XXXXX.0 format.', severity: 'error' });
        }

        if (family.minVersion && family.maxVersionTested &&
            WINDOWS_VERSION_REGEX.test(family.minVersion) && WINDOWS_VERSION_REGEX.test(family.maxVersionTested)) {
            if (compareVersions(family.maxVersionTested, family.minVersion) < 0) {
                errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested must be greater than or equal to MinVersion.', severity: 'error' });
            }
        }
    }

    // Package dependencies validation
    for (let i = 0; i < data.dependencies.packageDependencies.length; i++) {
        const dep = data.dependencies.packageDependencies[i];
        const prefix = `dependencies.packageDependency.${i}`;

        if (!dep.name) {
            errors.push({ field: `${prefix}.name`, message: 'Package dependency name is required.', severity: 'error' });
        }

        if (!dep.minVersion) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion is required.', severity: 'error' });
        }

        if (!dep.publisher) {
            errors.push({ field: `${prefix}.publisher`, message: 'Publisher is required.', severity: 'error' });
        }
    }

    // Resources validation
    for (let i = 0; i < data.resources.length; i++) {
        const res = data.resources[i];
        if (!res.language) {
            errors.push({ field: `resources.${i}.language`, message: 'Language is required.', severity: 'error' });
        } else if (!BCP47_REGEX.test(res.language)) {
            errors.push({ field: `resources.${i}.language`, message: 'Language must be a valid BCP-47 tag (e.g. en, en-US, zh-Hans-CN) or x-generate.', severity: 'error' });
        }
    }

    // Applications validation
    for (let i = 0; i < data.applications.length; i++) {
        const app = data.applications[i];
        const prefix = `applications.${i}`;

        if (!app.id) {
            errors.push({ field: `${prefix}.id`, message: 'Application Id is required.', severity: 'error' });
        }

        if (!app.executable) {
            errors.push({ field: `${prefix}.executable`, message: 'Executable path is required.', severity: 'error' });
        } else if (!app.executable.toLowerCase().endsWith('.exe')) {
            errors.push({ field: `${prefix}.executable`, message: 'Executable must be an .exe file.', severity: 'error' });
        }

        if (!app.entryPoint) {
            errors.push({ field: `${prefix}.entryPoint`, message: 'Entry point is required.', severity: 'error' });
        }

        if (!app.visualElements.displayName) {
            errors.push({ field: `${prefix}.visualElements.displayName`, message: 'Display name is required.', severity: 'error' });
        } else if (app.visualElements.displayName.length > 256) {
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

        // Visual asset PNG validation
        const ve = app.visualElements;
        const vePrefix = `${prefix}.visualElements`;
        validateImageField(errors, `${vePrefix}.square150x150Logo`, ve.square150x150Logo);
        validateImageField(errors, `${vePrefix}.square44x44Logo`, ve.square44x44Logo);
        validateImageField(errors, `${vePrefix}.wide310x150Logo`, ve.wide310x150Logo);
        validateImageField(errors, `${vePrefix}.square71x71Logo`, ve.square71x71Logo);
        validateImageField(errors, `${vePrefix}.square310x310Logo`, ve.square310x310Logo);
        validateImageField(errors, `${vePrefix}.badgeLogo`, ve.badgeLogo);
        validateImageField(errors, `${vePrefix}.splashScreenImage`, ve.splashScreenImage);
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
