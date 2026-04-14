/**
 * Validation rules for appxmanifest.xml fields.
 * Provides real-time inline validation for the form editor.
 */

import { ManifestData, ValidationError } from './manifest-types';

const VERSION_REGEX = /^\d+\.\d+\.\d+\.\d+$/;
// Full X.500 DN pattern matching the appxmanifest schema constraint.
// Allowed RDN aliases: CN, L, O, OU, E, C, S, STREET, T, G, I, SN, DC, SERIALNUMBER,
// Description, PostalCode, POBox, Phone, X21Address, dnQualifier, or OID.x.y.z...
const PUBLISHER_DN_REGEX = /^(CN|L|O|OU|E|C|S|STREET|T|G|I|SN|DC|SERIALNUMBER|Description|PostalCode|POBox|Phone|X21Address|dnQualifier|(OID\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))+))=(([^,+="<>#;])+|".*?")(,\s*((CN|L|O|OU|E|C|S|STREET|T|G|I|SN|DC|SERIALNUMBER|Description|PostalCode|POBox|Phone|X21Address|dnQualifier|(OID\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))+))=(([^,+="<>#;])+|".*?")))*$/;
const IDENTITY_NAME_REGEX = /^[a-zA-Z0-9.\-]+$/;
const HEX_COLOR_REGEX = /^#[0-9a-fA-F]{6}$/;
const GUID_REGEX = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
// BCP-47: language[-script][-region][-variant] (simplified for common MSIX usage)
// Also accepts private-use tags like "x-generate" used by MSIX tooling
const BCP47_REGEX = /^(?:x(?:-[a-zA-Z0-9]{1,8})+|[a-zA-Z]{2,3}(-[a-zA-Z]{4})?(-[a-zA-Z]{2}|\d{3})?(-[a-zA-Z0-9]{5,8})*)$/;
// Application.Id: ASCII, alpha-numeric fields separated by periods, each field starts with a letter
const APP_ID_REGEX = /^[a-zA-Z][a-zA-Z0-9]*(\.[a-zA-Z][a-zA-Z0-9]*)*$/;

/** Reserved device names that cannot be used as Identity Name, ResourceId, or Application Id fields. */
const RESERVED_NAMES = new Set([
    'CON', 'PRN', 'AUX', 'NUL',
    'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
    'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9',
]);

/** Named colors accepted by the appxmanifest schema for BackgroundColor. */
const NAMED_COLORS = new Set([
    'aliceBlue', 'antiqueWhite', 'aqua', 'aquamarine', 'azure', 'beige', 'bisque', 'black',
    'blanchedAlmond', 'blue', 'blueViolet', 'brown', 'burlyWood', 'cadetBlue', 'chartreuse',
    'chocolate', 'coral', 'cornflowerBlue', 'cornsilk', 'crimson', 'cyan', 'darkBlue', 'darkCyan',
    'darkGoldenrod', 'darkGray', 'darkGreen', 'darkKhaki', 'darkMagenta', 'darkOliveGreen',
    'darkOrange', 'darkOrchid', 'darkRed', 'darkSalmon', 'darkSeaGreen', 'darkSlateBlue',
    'darkSlateGray', 'darkTurquoise', 'darkViolet', 'deepPink', 'deepSkyBlue', 'dimGray',
    'dodgerBlue', 'firebrick', 'floralWhite', 'forestGreen', 'fuchsia', 'gainsboro', 'ghostWhite',
    'gold', 'goldenrod', 'gray', 'green', 'greenYellow', 'honeydew', 'hotPink', 'indianRed',
    'indigo', 'ivory', 'khaki', 'lavender', 'lavenderBlush', 'lawnGreen', 'lemonChiffon',
    'lightBlue', 'lightCoral', 'lightCyan', 'lightGoldenrodYellow', 'lightGray', 'lightGreen',
    'lightPink', 'lightSalmon', 'lightSeaGreen', 'lightSkyBlue', 'lightSlateGray', 'lightSteelBlue',
    'lightYellow', 'lime', 'limeGreen', 'linen', 'magenta', 'maroon', 'mediumAquamarine',
    'mediumBlue', 'mediumOrchid', 'mediumPurple', 'mediumSeaGreen', 'mediumSlateBlue',
    'mediumSpringGreen', 'mediumTurquoise', 'mediumVioletRed', 'midnightBlue', 'mintCream',
    'mistyRose', 'moccasin', 'navajoWhite', 'navy', 'oldLace', 'olive', 'oliveDrab', 'orange',
    'orangeRed', 'orchid', 'paleGoldenrod', 'paleGreen', 'paleTurquoise', 'paleVioletRed',
    'papayaWhip', 'peachPuff', 'peru', 'pink', 'plum', 'powderBlue', 'purple', 'red', 'rosyBrown',
    'royalBlue', 'saddleBrown', 'salmon', 'sandyBrown', 'seaGreen', 'seaShell', 'sienna', 'silver',
    'skyBlue', 'slateBlue', 'slateGray', 'snow', 'springGreen', 'steelBlue', 'tan', 'teal',
    'thistle', 'tomato', 'transparent', 'turquoise', 'violet', 'wheat', 'white', 'whiteSmoke',
    'yellow', 'yellowGreen',
]);

/** Validate a DotQuadNumber: four dot-separated integers each 0–65535. */
function isValidDotQuadNumber(value: string): boolean {
    if (!VERSION_REGEX.test(value)) { return false; }
    return value.split('.').every(part => {
        const n = parseInt(part, 10);
        return n >= 0 && n <= 65535;
    });
}

/** Returns true if a path has an unsupported image file extension. Schema allows .png, .jpg, .jpeg. */
function hasUnsupportedImageExtension(path: string): boolean {
    const filename = path.split(/[\\/]/).pop() || '';
    const dotIdx = filename.lastIndexOf('.');
    if (dotIdx < 0) { return false; } // no extension — valid (could be scale-qualified)
    const ext = filename.substring(dotIdx).toLowerCase();
    return ext !== '.png' && ext !== '.jpg' && ext !== '.jpeg';
}

const IMAGE_FORMAT_ERROR = 'Visual assets must be .png, .jpg, or .jpeg files.';

/** Validate an image field: error if blank (but present in manifest) or unsupported extension. */
function validateImageField(errors: ValidationError[], field: string, value: string | null | undefined): void {
    if (value === '') {
        errors.push({ field, message: 'Image path cannot be empty.', severity: 'error' });
    } else if (value && hasUnsupportedImageExtension(value)) {
        errors.push({ field, message: IMAGE_FORMAT_ERROR, severity: 'error' });
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
    } else if (data.identity.name.length < 3) {
        errors.push({ field: 'identity.name', message: 'Package name must be at least 3 characters.', severity: 'error' });
    } else if (data.identity.name.length > 50) {
        errors.push({ field: 'identity.name', message: 'Package name must be 50 characters or fewer.', severity: 'error' });
    } else if (RESERVED_NAMES.has(data.identity.name.toUpperCase())) {
        errors.push({ field: 'identity.name', message: 'Package name cannot be a reserved device name (CON, PRN, AUX, NUL, COM1–9, LPT1–9).', severity: 'error' });
    }

    if (!data.identity.publisher) {
        errors.push({ field: 'identity.publisher', message: 'Publisher is required.', severity: 'error' });
    } else if (!PUBLISHER_DN_REGEX.test(data.identity.publisher)) {
        errors.push({ field: 'identity.publisher', message: 'Publisher must be a valid X.500 distinguished name (e.g. CN=Contoso, O=Contoso Ltd).', severity: 'error' });
    }

    if (!data.identity.version) {
        errors.push({ field: 'identity.version', message: 'Version is required.', severity: 'error' });
    } else if (!isValidDotQuadNumber(data.identity.version)) {
        errors.push({ field: 'identity.version', message: 'Version must be a DotQuadNumber in Major.Minor.Build.Revision format (e.g. 1.0.0.0), each part 0–65535.', severity: 'error' });
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
    } else if (data.properties.publisherDisplayName.length > 256) {
        errors.push({ field: 'properties.publisherDisplayName', message: 'Publisher display name must be 256 characters or fewer.', severity: 'error' });
    }

    if (!data.properties.logo) {
        errors.push({ field: 'properties.logo', message: 'Store logo path is required.', severity: 'error' });
    }
    validateImageField(errors, 'properties.logo', data.properties.logo);

    if (data.properties.description && data.properties.description.length > 2048) {
        errors.push({ field: 'properties.description', message: 'Description must be 2048 characters or fewer.', severity: 'error' });
    } else if (data.properties.description && /[\t\r\n]/.test(data.properties.description)) {
        errors.push({ field: 'properties.description', message: 'Description cannot contain tabs, carriage returns, or line feeds.', severity: 'error' });
    }

    // Dependencies validation
    for (let i = 0; i < data.dependencies.targetDeviceFamilies.length; i++) {
        const family = data.dependencies.targetDeviceFamilies[i];
        const prefix = `dependencies.targetDeviceFamily.${i}`;

        if (!family.minVersion) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion is required.', severity: 'error' });
        } else if (!isValidDotQuadNumber(family.minVersion)) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion must be a DotQuadNumber (e.g. 10.0.17763.0), each part 0–65535.', severity: 'error' });
        }

        if (!family.maxVersionTested) {
            errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested is required.', severity: 'error' });
        } else if (!isValidDotQuadNumber(family.maxVersionTested)) {
            errors.push({ field: `${prefix}.maxVersionTested`, message: 'MaxVersionTested must be a DotQuadNumber (e.g. 10.0.26100.0), each part 0–65535.', severity: 'error' });
        }

        if (family.minVersion && family.maxVersionTested &&
            isValidDotQuadNumber(family.minVersion) && isValidDotQuadNumber(family.maxVersionTested)) {
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
        } else if (!isValidDotQuadNumber(dep.minVersion)) {
            errors.push({ field: `${prefix}.minVersion`, message: 'MinVersion must be a 4-part dotted version (e.g. 14.0.0.0), each part 0–65535.', severity: 'error' });
        }

        if (!dep.publisher) {
            errors.push({ field: `${prefix}.publisher`, message: 'Publisher is required.', severity: 'error' });
        } else if (!PUBLISHER_DN_REGEX.test(dep.publisher)) {
            errors.push({ field: `${prefix}.publisher`, message: 'Publisher must be a valid X.500 distinguished name (e.g. CN=Microsoft Corporation, O=Microsoft Corporation).', severity: 'error' });
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
        } else if (!APP_ID_REGEX.test(app.id)) {
            errors.push({ field: `${prefix}.id`, message: 'Application Id must contain alpha-numeric fields separated by periods, each starting with a letter.', severity: 'error' });
        } else if (app.id.length > 64) {
            errors.push({ field: `${prefix}.id`, message: 'Application Id must be 64 characters or fewer.', severity: 'error' });
        } else {
            const idFields = app.id.split('.');
            const reservedField = idFields.find(f => RESERVED_NAMES.has(f.toUpperCase()));
            if (reservedField) {
                errors.push({ field: `${prefix}.id`, message: `Application Id cannot use reserved name "${reservedField}" as a field value.`, severity: 'error' });
            }
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
        } else if (app.visualElements.description && /[\t\r\n]/.test(app.visualElements.description)) {
            errors.push({ field: `${prefix}.visualElements.description`, message: 'Description cannot contain tabs, carriage returns, or line feeds.', severity: 'error' });
        }

        if (app.visualElements.backgroundColor &&
            !HEX_COLOR_REGEX.test(app.visualElements.backgroundColor) &&
            !NAMED_COLORS.has(app.visualElements.backgroundColor)) {
            errors.push({ field: `${prefix}.visualElements.backgroundColor`, message: 'Background color must be a hex color (e.g. #FFFFFF), "transparent", or a named color (e.g. cornflowerBlue).', severity: 'error' });
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
