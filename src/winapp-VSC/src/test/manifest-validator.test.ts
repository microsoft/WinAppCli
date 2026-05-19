/**
 * Unit tests for manifest-validator.ts — validateManifest().
 * Covers every validation rule: identity, phone identity, properties,
 * dependencies (all sub-types), resources, and applications.
 *
 * Run: npx tsx --test src/test/manifest-validator.test.ts
 */

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { validateManifest, isValidCustomCapability, validateExtensionField } from '../manifest-editor/manifest-validator';
import type { ManifestData, ValidationError } from '../manifest-editor/manifest-types';

// ─── Helpers ────────────────────────────────────────────────────────────────

/** Returns a minimal valid ManifestData. Tests mutate a clone of this. */
function makeValidManifest(): ManifestData {
    return {
        identity: { name: 'TestApp', publisher: 'CN=Test', version: '1.0.0.0', processorArchitecture: '', resourceId: '' },
        phoneIdentity: null,
        properties: {
            displayName: 'Test', publisherDisplayName: 'Test', description: '', logo: 'Assets\\StoreLogo.png',
            framework: '', resourcePackage: '', supportedUsers: '', allowExecution: '',
            fileSystemWriteVirtualization: '', registryWriteVirtualization: '', modificationPackage: '',
            allowExternalContent: '', autoUpdateUri: '', packageIntegrityEnforcement: '', updateWhileInUse: '',
        },
        dependencies: {
            targetDeviceFamilies: [{ name: 'Windows.Desktop', minVersion: '10.0.17763.0', maxVersionTested: '10.0.22621.0' }],
            packageDependencies: [], mainPackageDependencies: [], driverConstraints: [],
            osPackageDependencies: [], hostRuntimeDependencies: [], externalDependencies: [],
        },
        applications: [{
            id: 'App', executable: 'App.exe', entryPoint: 'Windows.FullTrustApplication',
            trustLevel: '', runtimeBehavior: '', supportsMultipleInstances: '', parameters: '',
            visualElements: {
                displayName: 'App', description: '', backgroundColor: 'transparent',
                square150x150Logo: 'Assets\\Square150x150Logo.png', square44x44Logo: 'Assets\\Square44x44Logo.png',
                appListEntry: '', wide310x150Logo: null, square71x71Logo: null, square310x310Logo: null,
                badgeLogo: null, splashScreenImage: null, splashScreenBackgroundColor: '', lockScreenNotification: '',
                shortName: '', showNameOnTiles: [],
            },
            extensions: [],
        }],
        capabilities: [],
        resources: [{ language: 'en-us', scale: '', dxFeatureLevel: '' }],
    };
}

/** Return errors whose field matches the given prefix. */
function errorsFor(errors: ValidationError[], field: string): ValidationError[] {
    return errors.filter(e => e.field === field);
}

/** Assert that at least one error exists for the given field. */
function expectError(errors: ValidationError[], field: string): void {
    const matches = errorsFor(errors, field);
    assert.ok(matches.length > 0, `Expected an error for field "${field}" but found none. All errors: ${JSON.stringify(errors)}`);
    assert.ok(matches[0].message.length > 0, `Error message for "${field}" should not be empty`);
}

/** Assert that no errors exist for the given field. */
function expectNoError(errors: ValidationError[], field: string): void {
    const matches = errorsFor(errors, field);
    assert.equal(matches.length, 0, `Expected no errors for "${field}" but found: ${JSON.stringify(matches)}`);
}

// ─── 1. Identity Validation ────────────────────────────────────────────────

describe('Identity Validation', () => {
    // --- name ---
    it('should error when identity name is empty', () => {
        const m = makeValidManifest();
        m.identity.name = '';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name has special characters', () => {
        const m = makeValidManifest();
        m.identity.name = 'My App!';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name has spaces', () => {
        const m = makeValidManifest();
        m.identity.name = 'My App';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name is too short (< 3 chars)', () => {
        const m = makeValidManifest();
        m.identity.name = 'Ab';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name is too long (> 50 chars)', () => {
        const m = makeValidManifest();
        m.identity.name = 'A'.repeat(51);
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name is reserved name CON', () => {
        const m = makeValidManifest();
        m.identity.name = 'CON';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name is reserved name PRN', () => {
        const m = makeValidManifest();
        m.identity.name = 'PRN';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error when identity name is reserved name COM1', () => {
        const m = makeValidManifest();
        m.identity.name = 'COM1';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should error for reserved name case-insensitively (con)', () => {
        const m = makeValidManifest();
        m.identity.name = 'con';
        expectError(validateManifest(m), 'identity.name');
    });

    it('should accept a valid identity name', () => {
        const m = makeValidManifest();
        m.identity.name = 'My.Valid-App123';
        expectNoError(validateManifest(m), 'identity.name');
    });

    it('should accept identity name with dots and hyphens', () => {
        const m = makeValidManifest();
        m.identity.name = 'Com.Example.App-1';
        expectNoError(validateManifest(m), 'identity.name');
    });

    it('should accept identity name at exactly 3 chars', () => {
        const m = makeValidManifest();
        m.identity.name = 'Abc';
        expectNoError(validateManifest(m), 'identity.name');
    });

    it('should accept identity name at exactly 50 chars', () => {
        const m = makeValidManifest();
        m.identity.name = 'A'.repeat(50);
        expectNoError(validateManifest(m), 'identity.name');
    });

    // --- publisher ---
    it('should error when publisher is empty', () => {
        const m = makeValidManifest();
        m.identity.publisher = '';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher is not a valid X.500 DN', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'Not a DN';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher is missing CN= prefix', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'Test Publisher';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should accept valid publisher CN=Test', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Test';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept full X.500 DN publisher', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with OID', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'OID.1.2.3=Value';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    // --- publisher: DN reserved characters (RFC 2253) ---
    it('should accept publisher with escaped comma in value', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Docs\\, Inc';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with escaped plus sign in value', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=A\\+B';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with escaped backslash in value', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Path\\\\Dir';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with escaped double quote in value', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Say\\"Hello\\"';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with hex-escaped character in value', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Before\\0DAfter';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with escaped angle brackets', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=\\<Test\\>';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with escaped semicolon', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=A\\;B';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with escaped equals sign', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=A\\=B';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher with quoted value containing reserved chars', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN="Quoted, Value + Special"';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher has raw unescaped comma in value', () => {
        const m = makeValidManifest();
        // Raw comma without a valid RDN attribute on the right side
        m.identity.publisher = 'CN=Bad,Value';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher has raw unescaped double quote in value', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Bad"Quote';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher has trailing unescaped backslash', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Trailing\\';
        expectError(validateManifest(m), 'identity.publisher');
    });

    // --- publisher: positional rules (RFC 2253 leading space/#, trailing space) ---
    it('should error when publisher value starts with unescaped space', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN= LeadingSpace';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher value starts with unescaped #', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=#HashStart';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher value ends with unescaped space', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=TrailingSpace ';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with escaped leading space', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=\\ LeadingSpace';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with escaped leading #', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=\\#HashStart';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with escaped trailing space', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=TrailingSpace\\ ';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with # in the middle (not at start)', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Test#Middle';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should error when second RDN value starts with space', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Good, O= BadLeading';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when second RDN value ends with space', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Good, O=BadTrailing ';
        expectError(validateManifest(m), 'identity.publisher');
    });

    // --- publisher: additional reserved characters (/, LF, CR) ---
    it('should error when publisher value contains raw forward slash', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Bad/Slash';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with escaped forward slash', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Good\\/Slash';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher value contains line feed', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Bad\nValue';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should error when publisher value contains carriage return', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Bad\rValue';
        expectError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with hex-escaped CR', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Good\\0DValue';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    it('should accept publisher value with hex-escaped LF', () => {
        const m = makeValidManifest();
        m.identity.publisher = 'CN=Good\\0AValue';
        expectNoError(validateManifest(m), 'identity.publisher');
    });

    // --- version ---
    it('should error when version is empty', () => {
        const m = makeValidManifest();
        m.identity.version = '';
        expectError(validateManifest(m), 'identity.version');
    });

    it('should error when version is 3-part', () => {
        const m = makeValidManifest();
        m.identity.version = '1.0.0';
        expectError(validateManifest(m), 'identity.version');
    });

    it('should error when version contains letters', () => {
        const m = makeValidManifest();
        m.identity.version = '1.0.0.abc';
        expectError(validateManifest(m), 'identity.version');
    });

    it('should error when version part exceeds 65535', () => {
        const m = makeValidManifest();
        m.identity.version = '1.0.0.65536';
        expectError(validateManifest(m), 'identity.version');
    });

    it('should accept a valid version', () => {
        const m = makeValidManifest();
        m.identity.version = '10.0.19041.0';
        expectNoError(validateManifest(m), 'identity.version');
    });

    it('should accept version with all zeros', () => {
        const m = makeValidManifest();
        m.identity.version = '0.0.0.0';
        expectNoError(validateManifest(m), 'identity.version');
    });

    it('should accept version with max parts 65535', () => {
        const m = makeValidManifest();
        m.identity.version = '65535.65535.65535.65535';
        expectNoError(validateManifest(m), 'identity.version');
    });

    // --- resourceId ---
    it('should not error when resourceId is empty (optional)', () => {
        const m = makeValidManifest();
        m.identity.resourceId = '';
        expectNoError(validateManifest(m), 'identity.resourceId');
    });

    it('should error when resourceId has invalid chars', () => {
        const m = makeValidManifest();
        m.identity.resourceId = 'bad chars!';
        expectError(validateManifest(m), 'identity.resourceId');
    });

    it('should error when resourceId exceeds 30 chars', () => {
        const m = makeValidManifest();
        m.identity.resourceId = 'A'.repeat(31);
        expectError(validateManifest(m), 'identity.resourceId');
    });

    it('should error when resourceId is a reserved name', () => {
        const m = makeValidManifest();
        m.identity.resourceId = 'NUL';
        expectError(validateManifest(m), 'identity.resourceId');
    });

    it('should accept valid resourceId', () => {
        const m = makeValidManifest();
        m.identity.resourceId = 'my-resource.1';
        expectNoError(validateManifest(m), 'identity.resourceId');
    });

    it('should accept resourceId at exactly 30 chars', () => {
        const m = makeValidManifest();
        m.identity.resourceId = 'A'.repeat(30);
        expectNoError(validateManifest(m), 'identity.resourceId');
    });
});

// ─── 2. PhoneIdentity Validation ───────────────────────────────────────────

describe('PhoneIdentity Validation', () => {
    it('should accept valid GUID for phoneProductId', () => {
        const m = makeValidManifest();
        m.phoneIdentity = { phoneProductId: '00000000-0000-0000-0000-000000000000', phonePublisherId: '11111111-1111-1111-1111-111111111111' };
        expectNoError(validateManifest(m), 'phoneIdentity.phoneProductId');
    });

    it('should error on invalid GUID for phoneProductId', () => {
        const m = makeValidManifest();
        m.phoneIdentity = { phoneProductId: 'not-a-guid', phonePublisherId: '11111111-1111-1111-1111-111111111111' };
        expectError(validateManifest(m), 'phoneIdentity.phoneProductId');
    });

    it('should accept valid GUID for phonePublisherId', () => {
        const m = makeValidManifest();
        m.phoneIdentity = { phoneProductId: '00000000-0000-0000-0000-000000000000', phonePublisherId: 'aabbccdd-1122-3344-5566-778899aabbcc' };
        expectNoError(validateManifest(m), 'phoneIdentity.phonePublisherId');
    });

    it('should error on invalid GUID for phonePublisherId', () => {
        const m = makeValidManifest();
        m.phoneIdentity = { phoneProductId: '00000000-0000-0000-0000-000000000000', phonePublisherId: 'xyz' };
        expectError(validateManifest(m), 'phoneIdentity.phonePublisherId');
    });

    it('should produce no phone errors when phoneIdentity is null', () => {
        const m = makeValidManifest();
        m.phoneIdentity = null;
        const errors = validateManifest(m);
        assert.equal(errors.filter(e => e.field.startsWith('phoneIdentity')).length, 0);
    });

    it('should error when phoneProductId is empty string (required within phoneIdentity)', () => {
        const m = makeValidManifest();
        m.phoneIdentity = { phoneProductId: '', phonePublisherId: '00000000-0000-0000-0000-000000000000' };
        expectError(validateManifest(m), 'phoneIdentity.phoneProductId');
    });
});

// ─── 3. Properties Validation ──────────────────────────────────────────────

describe('Properties Validation', () => {
    it('should error when displayName is empty', () => {
        const m = makeValidManifest();
        m.properties.displayName = '';
        expectError(validateManifest(m), 'properties.displayName');
    });

    it('should error when displayName exceeds 256 chars', () => {
        const m = makeValidManifest();
        m.properties.displayName = 'A'.repeat(257);
        expectError(validateManifest(m), 'properties.displayName');
    });

    it('should accept displayName at exactly 256 chars', () => {
        const m = makeValidManifest();
        m.properties.displayName = 'A'.repeat(256);
        expectNoError(validateManifest(m), 'properties.displayName');
    });

    it('should error when publisherDisplayName is empty', () => {
        const m = makeValidManifest();
        m.properties.publisherDisplayName = '';
        expectError(validateManifest(m), 'properties.publisherDisplayName');
    });

    it('should error when publisherDisplayName exceeds 256 chars', () => {
        const m = makeValidManifest();
        m.properties.publisherDisplayName = 'A'.repeat(257);
        expectError(validateManifest(m), 'properties.publisherDisplayName');
    });

    it('should accept publisherDisplayName at exactly 256 chars', () => {
        const m = makeValidManifest();
        m.properties.publisherDisplayName = 'A'.repeat(256);
        expectNoError(validateManifest(m), 'properties.publisherDisplayName');
    });

    it('should error when logo is empty', () => {
        const m = makeValidManifest();
        m.properties.logo = '';
        expectError(validateManifest(m), 'properties.logo');
    });

    it('should error when logo has unsupported extension .gif', () => {
        const m = makeValidManifest();
        m.properties.logo = 'Assets\\Logo.gif';
        expectError(validateManifest(m), 'properties.logo');
    });

    it('should error when logo has unsupported extension .bmp', () => {
        const m = makeValidManifest();
        m.properties.logo = 'Assets\\Logo.bmp';
        expectError(validateManifest(m), 'properties.logo');
    });

    it('should accept logo with .png extension', () => {
        const m = makeValidManifest();
        m.properties.logo = 'Assets\\StoreLogo.png';
        expectNoError(validateManifest(m), 'properties.logo');
    });

    it('should accept logo with .jpg extension', () => {
        const m = makeValidManifest();
        m.properties.logo = 'Assets\\StoreLogo.jpg';
        expectNoError(validateManifest(m), 'properties.logo');
    });

    it('should accept logo with .jpeg extension', () => {
        const m = makeValidManifest();
        m.properties.logo = 'Assets\\StoreLogo.jpeg';
        expectNoError(validateManifest(m), 'properties.logo');
    });

    it('should not error when description is empty (optional)', () => {
        const m = makeValidManifest();
        m.properties.description = '';
        expectNoError(validateManifest(m), 'properties.description');
    });

    it('should error when description exceeds 2048 chars', () => {
        const m = makeValidManifest();
        m.properties.description = 'A'.repeat(2049);
        expectError(validateManifest(m), 'properties.description');
    });

    it('should error when description contains a tab', () => {
        const m = makeValidManifest();
        m.properties.description = 'Hello\tWorld';
        expectError(validateManifest(m), 'properties.description');
    });

    it('should error when description contains carriage return', () => {
        const m = makeValidManifest();
        m.properties.description = 'Hello\rWorld';
        expectError(validateManifest(m), 'properties.description');
    });

    it('should error when description contains line feed', () => {
        const m = makeValidManifest();
        m.properties.description = 'Hello\nWorld';
        expectError(validateManifest(m), 'properties.description');
    });

    it('should accept description at exactly 2048 chars', () => {
        const m = makeValidManifest();
        m.properties.description = 'A'.repeat(2048);
        expectNoError(validateManifest(m), 'properties.description');
    });
});

// ─── 4. Dependencies — Target Device Families ──────────────────────────────

describe('Dependencies - Target Device Families', () => {
    it('should error when minVersion is empty', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].minVersion = '';
        expectError(validateManifest(m), 'dependencies.targetDeviceFamily.0.minVersion');
    });

    it('should error when minVersion is not DotQuadNumber', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].minVersion = '10.0';
        expectError(validateManifest(m), 'dependencies.targetDeviceFamily.0.minVersion');
    });

    it('should error when maxVersionTested is empty', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].maxVersionTested = '';
        expectError(validateManifest(m), 'dependencies.targetDeviceFamily.0.maxVersionTested');
    });

    it('should error when maxVersionTested is not DotQuadNumber', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].maxVersionTested = 'abc';
        expectError(validateManifest(m), 'dependencies.targetDeviceFamily.0.maxVersionTested');
    });

    it('should error when maxVersionTested is less than minVersion', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].minVersion = '10.0.22000.0';
        m.dependencies.targetDeviceFamilies[0].maxVersionTested = '10.0.17763.0';
        expectError(validateManifest(m), 'dependencies.targetDeviceFamily.0.maxVersionTested');
    });

    it('should accept when maxVersionTested equals minVersion', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].minVersion = '10.0.17763.0';
        m.dependencies.targetDeviceFamilies[0].maxVersionTested = '10.0.17763.0';
        expectNoError(validateManifest(m), 'dependencies.targetDeviceFamily.0.maxVersionTested');
    });

    it('should accept when maxVersionTested is greater than minVersion', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies[0].minVersion = '10.0.17763.0';
        m.dependencies.targetDeviceFamilies[0].maxVersionTested = '10.0.22621.0';
        expectNoError(validateManifest(m), 'dependencies.targetDeviceFamily.0.maxVersionTested');
    });

    it('should validate multiple target device families independently', () => {
        const m = makeValidManifest();
        m.dependencies.targetDeviceFamilies.push({ name: 'Windows.Xbox', minVersion: '', maxVersionTested: '10.0.22621.0' });
        expectError(validateManifest(m), 'dependencies.targetDeviceFamily.1.minVersion');
        expectNoError(validateManifest(m), 'dependencies.targetDeviceFamily.0.minVersion');
    });
});

// ─── 5. Dependencies — Package Dependencies ────────────────────────────────

describe('Dependencies - Package Dependencies', () => {
    function withPkgDep(m: ManifestData, overrides: Partial<{ name: string; minVersion: string; publisher: string; optional: string }>): ManifestData {
        m.dependencies.packageDependencies = [{ name: 'Valid.Dep', minVersion: '1.0.0.0', publisher: 'CN=Test', optional: '', ...overrides }];
        return m;
    }

    it('should error when name is empty', () => {
        const m = withPkgDep(makeValidManifest(), { name: '' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.name');
    });

    it('should error when name has invalid chars', () => {
        const m = withPkgDep(makeValidManifest(), { name: 'bad name!' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.name');
    });

    it('should error when name is too short (< 3)', () => {
        const m = withPkgDep(makeValidManifest(), { name: 'Ab' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.name');
    });

    it('should error when name is too long (> 50)', () => {
        const m = withPkgDep(makeValidManifest(), { name: 'A'.repeat(51) });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.name');
    });

    it('should error when minVersion is empty', () => {
        const m = withPkgDep(makeValidManifest(), { minVersion: '' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.minVersion');
    });

    it('should error when minVersion is invalid', () => {
        const m = withPkgDep(makeValidManifest(), { minVersion: '1.0' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.minVersion');
    });

    it('should error when publisher is empty', () => {
        const m = withPkgDep(makeValidManifest(), { publisher: '' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.publisher');
    });

    it('should error when publisher is invalid DN', () => {
        const m = withPkgDep(makeValidManifest(), { publisher: 'invalid' });
        expectError(validateManifest(m), 'dependencies.packageDependency.0.publisher');
    });

    it('should accept valid package dependency', () => {
        const m = withPkgDep(makeValidManifest(), {});
        const errors = validateManifest(m);
        expectNoError(errors, 'dependencies.packageDependency.0.name');
        expectNoError(errors, 'dependencies.packageDependency.0.minVersion');
        expectNoError(errors, 'dependencies.packageDependency.0.publisher');
    });
});

// ─── 6. Dependencies — Main Package Dependencies ──────────────────────────

describe('Dependencies - Main Package Dependencies', () => {
    it('should error when name is empty', () => {
        const m = makeValidManifest();
        m.dependencies.mainPackageDependencies = [{ name: '' }];
        expectError(validateManifest(m), 'dependencies.mainPackageDependency.0.name');
    });

    it('should error when name has invalid chars', () => {
        const m = makeValidManifest();
        m.dependencies.mainPackageDependencies = [{ name: 'has space' }];
        expectError(validateManifest(m), 'dependencies.mainPackageDependency.0.name');
    });

    it('should error when name is too short', () => {
        const m = makeValidManifest();
        m.dependencies.mainPackageDependencies = [{ name: 'Ab' }];
        expectError(validateManifest(m), 'dependencies.mainPackageDependency.0.name');
    });

    it('should error when name is too long', () => {
        const m = makeValidManifest();
        m.dependencies.mainPackageDependencies = [{ name: 'A'.repeat(51) }];
        expectError(validateManifest(m), 'dependencies.mainPackageDependency.0.name');
    });

    it('should accept valid main package dependency name', () => {
        const m = makeValidManifest();
        m.dependencies.mainPackageDependencies = [{ name: 'Valid.Package' }];
        expectNoError(validateManifest(m), 'dependencies.mainPackageDependency.0.name');
    });
});

// ─── 7. Dependencies — Driver Constraints ─────────────────────────────────

describe('Dependencies - Driver Constraints', () => {
    function withDriverConstraint(m: ManifestData, overrides: Partial<{ name: string; minVersion: string; minDate: string }>): ManifestData {
        m.dependencies.driverConstraints = [{ name: 'MyDriver', minVersion: '1.0.0.0', minDate: '2020-01-01', ...overrides }];
        return m;
    }

    it('should error when constraint name is empty', () => {
        const m = withDriverConstraint(makeValidManifest(), { name: '' });
        expectError(validateManifest(m), 'dependencies.driverConstraint.0.name');
    });

    it('should error when constraint minVersion is empty', () => {
        const m = withDriverConstraint(makeValidManifest(), { minVersion: '' });
        expectError(validateManifest(m), 'dependencies.driverConstraint.0.minVersion');
    });

    it('should error when constraint minVersion is not DotQuadNumber', () => {
        const m = withDriverConstraint(makeValidManifest(), { minVersion: '1.0' });
        expectError(validateManifest(m), 'dependencies.driverConstraint.0.minVersion');
    });

    it('should error when constraint minDate is empty', () => {
        const m = withDriverConstraint(makeValidManifest(), { minDate: '' });
        expectError(validateManifest(m), 'dependencies.driverConstraint.0.minDate');
    });

    it('should error when constraint minDate is not YYYY-MM-DD', () => {
        const m = withDriverConstraint(makeValidManifest(), { minDate: '01/01/2020' });
        expectError(validateManifest(m), 'dependencies.driverConstraint.0.minDate');
    });

    it('should accept valid driver constraint', () => {
        const m = withDriverConstraint(makeValidManifest(), {});
        const errors = validateManifest(m);
        expectNoError(errors, 'dependencies.driverConstraint.0.name');
        expectNoError(errors, 'dependencies.driverConstraint.0.minVersion');
        expectNoError(errors, 'dependencies.driverConstraint.0.minDate');
    });
});

// ─── 8. Dependencies — OS Package Dependencies ─────────────────────────────

describe('Dependencies - OS Package Dependencies', () => {
    function withOsDep(m: ManifestData, overrides: Partial<{ name: string; version: string }>): ManifestData {
        m.dependencies.osPackageDependencies = [{ name: 'OS.Package', version: '10.0.0.0', ...overrides }];
        return m;
    }

    it('should error when name is empty', () => {
        const m = withOsDep(makeValidManifest(), { name: '' });
        expectError(validateManifest(m), 'dependencies.osPackageDependency.0.name');
    });

    it('should error when name has invalid chars', () => {
        const m = withOsDep(makeValidManifest(), { name: 'bad name' });
        expectError(validateManifest(m), 'dependencies.osPackageDependency.0.name');
    });

    it('should error when name is too short', () => {
        const m = withOsDep(makeValidManifest(), { name: 'AB' });
        expectError(validateManifest(m), 'dependencies.osPackageDependency.0.name');
    });

    it('should error when name is too long', () => {
        const m = withOsDep(makeValidManifest(), { name: 'A'.repeat(51) });
        expectError(validateManifest(m), 'dependencies.osPackageDependency.0.name');
    });

    it('should error when version is empty', () => {
        const m = withOsDep(makeValidManifest(), { version: '' });
        expectError(validateManifest(m), 'dependencies.osPackageDependency.0.version');
    });

    it('should error when version is not DotQuadNumber', () => {
        const m = withOsDep(makeValidManifest(), { version: 'bad' });
        expectError(validateManifest(m), 'dependencies.osPackageDependency.0.version');
    });

    it('should accept valid OS package dependency', () => {
        const m = withOsDep(makeValidManifest(), {});
        const errors = validateManifest(m);
        expectNoError(errors, 'dependencies.osPackageDependency.0.name');
        expectNoError(errors, 'dependencies.osPackageDependency.0.version');
    });
});

// ─── 9. Dependencies — Host Runtime Dependencies ───────────────────────────

describe('Dependencies - Host Runtime Dependencies', () => {
    function withHostDep(m: ManifestData, overrides: Partial<{ name: string; publisher: string; minVersion: string }>): ManifestData {
        m.dependencies.hostRuntimeDependencies = [{ name: 'HostRuntime', publisher: 'CN=Test', minVersion: '1.0.0.0', ...overrides }];
        return m;
    }

    it('should error when name is empty', () => {
        const m = withHostDep(makeValidManifest(), { name: '' });
        expectError(validateManifest(m), 'dependencies.hostRuntimeDependency.0.name');
    });

    it('should error when publisher is empty', () => {
        const m = withHostDep(makeValidManifest(), { publisher: '' });
        expectError(validateManifest(m), 'dependencies.hostRuntimeDependency.0.publisher');
    });

    it('should error when publisher is invalid DN', () => {
        const m = withHostDep(makeValidManifest(), { publisher: 'invalid' });
        expectError(validateManifest(m), 'dependencies.hostRuntimeDependency.0.publisher');
    });

    it('should error when minVersion is empty', () => {
        const m = withHostDep(makeValidManifest(), { minVersion: '' });
        expectError(validateManifest(m), 'dependencies.hostRuntimeDependency.0.minVersion');
    });

    it('should error when minVersion is invalid', () => {
        const m = withHostDep(makeValidManifest(), { minVersion: 'bad' });
        expectError(validateManifest(m), 'dependencies.hostRuntimeDependency.0.minVersion');
    });

    it('should accept valid host runtime dependency', () => {
        const m = withHostDep(makeValidManifest(), {});
        const errors = validateManifest(m);
        expectNoError(errors, 'dependencies.hostRuntimeDependency.0.name');
        expectNoError(errors, 'dependencies.hostRuntimeDependency.0.publisher');
        expectNoError(errors, 'dependencies.hostRuntimeDependency.0.minVersion');
    });
});

// ─── 10. Dependencies — External Dependencies ──────────────────────────────

describe('Dependencies - External Dependencies', () => {
    function withExtDep(m: ManifestData, overrides: Partial<{ name: string; publisher: string; minVersion: string; optional: string }>): ManifestData {
        m.dependencies.externalDependencies = [{ name: 'ExternalPkg', publisher: 'CN=Test', minVersion: '1.0.0.0', optional: '', ...overrides }];
        return m;
    }

    it('should error when name is empty', () => {
        const m = withExtDep(makeValidManifest(), { name: '' });
        expectError(validateManifest(m), 'dependencies.externalDependency.0.name');
    });

    it('should error when publisher is empty', () => {
        const m = withExtDep(makeValidManifest(), { publisher: '' });
        expectError(validateManifest(m), 'dependencies.externalDependency.0.publisher');
    });

    it('should error when publisher is invalid DN', () => {
        const m = withExtDep(makeValidManifest(), { publisher: 'not-a-dn' });
        expectError(validateManifest(m), 'dependencies.externalDependency.0.publisher');
    });

    it('should error when minVersion is empty', () => {
        const m = withExtDep(makeValidManifest(), { minVersion: '' });
        expectError(validateManifest(m), 'dependencies.externalDependency.0.minVersion');
    });

    it('should error when minVersion is invalid', () => {
        const m = withExtDep(makeValidManifest(), { minVersion: '1.0' });
        expectError(validateManifest(m), 'dependencies.externalDependency.0.minVersion');
    });

    it('should accept valid external dependency', () => {
        const m = withExtDep(makeValidManifest(), {});
        const errors = validateManifest(m);
        expectNoError(errors, 'dependencies.externalDependency.0.name');
        expectNoError(errors, 'dependencies.externalDependency.0.publisher');
        expectNoError(errors, 'dependencies.externalDependency.0.minVersion');
    });
});

// ─── 11. Resources Validation ──────────────────────────────────────────────

describe('Resources Validation', () => {
    it('should not error when language is empty', () => {
        const m = makeValidManifest();
        m.resources[0].language = '';
        expectNoError(validateManifest(m), 'resources.0.language');
    });

    it('should error when language is numeric only', () => {
        const m = makeValidManifest();
        m.resources[0].language = '123';
        expectError(validateManifest(m), 'resources.0.language');
    });

    it('should accept "en" as valid BCP-47', () => {
        const m = makeValidManifest();
        m.resources[0].language = 'en';
        expectNoError(validateManifest(m), 'resources.0.language');
    });

    it('should accept "en-US" as valid BCP-47', () => {
        const m = makeValidManifest();
        m.resources[0].language = 'en-US';
        expectNoError(validateManifest(m), 'resources.0.language');
    });

    it('should accept "zh-Hans-CN" as valid BCP-47', () => {
        const m = makeValidManifest();
        m.resources[0].language = 'zh-Hans-CN';
        expectNoError(validateManifest(m), 'resources.0.language');
    });

    it('should accept "x-generate" as valid private-use tag', () => {
        const m = makeValidManifest();
        m.resources[0].language = 'x-generate';
        expectNoError(validateManifest(m), 'resources.0.language');
    });

    it('should validate multiple resources independently', () => {
        const m = makeValidManifest();
        m.resources = [
            { language: 'en', scale: '', dxFeatureLevel: '' },
            { language: '123', scale: '', dxFeatureLevel: '' },
        ];
        expectNoError(validateManifest(m), 'resources.0.language');
        expectError(validateManifest(m), 'resources.1.language');
    });

    it('should error when resource package resource defines more than one attribute type', () => {
        const m = makeValidManifest();
        m.properties.resourcePackage = 'true';
        m.resources = [{ language: 'en-us', scale: '100', dxFeatureLevel: '' }];
        expectError(validateManifest(m), 'resources.0.language');
        expectError(validateManifest(m), 'resources.0.scale');
    });

    it('should allow resource package resource with only language', () => {
        const m = makeValidManifest();
        m.properties.resourcePackage = 'true';
        m.resources = [{ language: 'en-us', scale: '', dxFeatureLevel: '' }];
        expectNoError(validateManifest(m), 'resources.0.language');
        expectNoError(validateManifest(m), 'resources.0.scale');
    });

    it('should allow resource package resource with only scale', () => {
        const m = makeValidManifest();
        m.properties.resourcePackage = 'true';
        m.resources = [{ language: '', scale: '100', dxFeatureLevel: '' }];
        expectNoError(validateManifest(m), 'resources.0.scale');
    });

    it('should allow multiple attributes on non-resource packages', () => {
        const m = makeValidManifest();
        m.properties.resourcePackage = '';
        m.resources = [{ language: 'en-us', scale: '100', dxFeatureLevel: '' }];
        expectNoError(validateManifest(m), 'resources.0.scale');
    });
});

// ─── 12. Applications Validation ───────────────────────────────────────────

describe('Applications Validation', () => {
    // --- id ---
    it('should error when application id is empty', () => {
        const m = makeValidManifest();
        m.applications[0].id = '';
        expectError(validateManifest(m), 'applications.0.id');
    });

    it('should error when application id has invalid format', () => {
        const m = makeValidManifest();
        m.applications[0].id = '1InvalidStart';
        expectError(validateManifest(m), 'applications.0.id');
    });

    it('should error when application id has spaces', () => {
        const m = makeValidManifest();
        m.applications[0].id = 'My App';
        expectError(validateManifest(m), 'applications.0.id');
    });

    it('should error when application id exceeds 64 chars', () => {
        const m = makeValidManifest();
        m.applications[0].id = 'A'.repeat(65);
        expectError(validateManifest(m), 'applications.0.id');
    });

    it('should error when application id field value is a reserved name', () => {
        const m = makeValidManifest();
        m.applications[0].id = 'App.CON.Test';
        expectError(validateManifest(m), 'applications.0.id');
    });

    it('should error when application id is itself a reserved name', () => {
        const m = makeValidManifest();
        m.applications[0].id = 'NUL';
        expectError(validateManifest(m), 'applications.0.id');
    });

    it('should accept valid multi-segment application id', () => {
        const m = makeValidManifest();
        m.applications[0].id = 'MyCompany.MyApp1';
        expectNoError(validateManifest(m), 'applications.0.id');
    });

    it('should accept simple valid application id', () => {
        const m = makeValidManifest();
        m.applications[0].id = 'App';
        expectNoError(validateManifest(m), 'applications.0.id');
    });

    it('should accept application id at exactly 64 chars', () => {
        const m = makeValidManifest();
        // Build a string of exactly 64 chars with valid format
        m.applications[0].id = 'A'.repeat(64);
        expectNoError(validateManifest(m), 'applications.0.id');
    });

    // --- executable ---
    it('should error when executable is empty', () => {
        const m = makeValidManifest();
        m.applications[0].executable = '';
        expectError(validateManifest(m), 'applications.0.executable');
    });

    it('should error when executable does not end in .exe', () => {
        const m = makeValidManifest();
        m.applications[0].executable = 'App.dll';
        expectError(validateManifest(m), 'applications.0.executable');
    });

    it('should accept valid executable ending in .exe', () => {
        const m = makeValidManifest();
        m.applications[0].executable = 'MyApp.exe';
        expectNoError(validateManifest(m), 'applications.0.executable');
    });

    it('should accept executable with path prefix', () => {
        const m = makeValidManifest();
        m.applications[0].executable = 'bin\\MyApp.exe';
        expectNoError(validateManifest(m), 'applications.0.executable');
    });

    // --- entryPoint ---
    it('should error when entryPoint is empty', () => {
        const m = makeValidManifest();
        m.applications[0].entryPoint = '';
        expectError(validateManifest(m), 'applications.0.entryPoint');
    });

    it('should accept valid entryPoint', () => {
        const m = makeValidManifest();
        m.applications[0].entryPoint = 'Windows.FullTrustApplication';
        expectNoError(validateManifest(m), 'applications.0.entryPoint');
    });

    // --- visualElements.displayName ---
    it('should error when visual displayName is empty', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.displayName = '';
        expectError(validateManifest(m), 'applications.0.visualElements.displayName');
    });

    it('should error when visual displayName exceeds 256 chars', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.displayName = 'A'.repeat(257);
        expectError(validateManifest(m), 'applications.0.visualElements.displayName');
    });

    it('should accept visual displayName at 256 chars', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.displayName = 'A'.repeat(256);
        expectNoError(validateManifest(m), 'applications.0.visualElements.displayName');
    });

    // --- visualElements.description ---
    it('should not error when visual description is empty (optional)', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.description = '';
        expectNoError(validateManifest(m), 'applications.0.visualElements.description');
    });

    it('should error when visual description exceeds 2048 chars', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.description = 'A'.repeat(2049);
        expectError(validateManifest(m), 'applications.0.visualElements.description');
    });

    it('should error when visual description contains tab', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.description = 'Hello\tWorld';
        expectError(validateManifest(m), 'applications.0.visualElements.description');
    });

    it('should error when visual description contains newline', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.description = 'Hello\nWorld';
        expectError(validateManifest(m), 'applications.0.visualElements.description');
    });

    it('should accept valid visual description', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.description = 'A valid description without special whitespace.';
        expectNoError(validateManifest(m), 'applications.0.visualElements.description');
    });

    // --- visualElements.backgroundColor ---
    it('should error when backgroundColor is invalid hex', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = '#GGG';
        expectError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    it('should error when backgroundColor is arbitrary string', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = 'notacolor';
        expectError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    it('should accept hex color #FFFFFF', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = '#FFFFFF';
        expectNoError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    it('should accept hex color #000000', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = '#000000';
        expectNoError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    it('should accept named color "transparent"', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = 'transparent';
        expectNoError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    it('should accept named color "cornflowerBlue"', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = 'cornflowerBlue';
        expectNoError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    it('should not error when backgroundColor is empty', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.backgroundColor = '';
        expectNoError(validateManifest(m), 'applications.0.visualElements.backgroundColor');
    });

    // --- visualElements image fields ---
    it('should error when square150x150Logo is empty string', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square150x150Logo = '';
        expectError(validateManifest(m), 'applications.0.visualElements.square150x150Logo');
    });

    it('should error when square150x150Logo has unsupported extension', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square150x150Logo = 'Assets\\Logo.gif';
        expectError(validateManifest(m), 'applications.0.visualElements.square150x150Logo');
    });

    it('should accept valid square150x150Logo with .png', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square150x150Logo = 'Assets\\Square150x150Logo.png';
        expectNoError(validateManifest(m), 'applications.0.visualElements.square150x150Logo');
    });

    it('should error when square44x44Logo is empty string', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square44x44Logo = '';
        expectError(validateManifest(m), 'applications.0.visualElements.square44x44Logo');
    });

    it('should error when square44x44Logo has .bmp extension', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square44x44Logo = 'Assets\\Logo.bmp';
        expectError(validateManifest(m), 'applications.0.visualElements.square44x44Logo');
    });

    it('should not error when wide310x150Logo is null (optional)', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.wide310x150Logo = null;
        expectNoError(validateManifest(m), 'applications.0.visualElements.wide310x150Logo');
    });

    it('should error when wide310x150Logo is empty string', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.wide310x150Logo = '';
        expectError(validateManifest(m), 'applications.0.visualElements.wide310x150Logo');
    });

    it('should error when wide310x150Logo has unsupported extension', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.wide310x150Logo = 'Assets\\Wide.gif';
        expectError(validateManifest(m), 'applications.0.visualElements.wide310x150Logo');
    });

    it('should accept valid wide310x150Logo with .jpg', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.wide310x150Logo = 'Assets\\Wide.jpg';
        expectNoError(validateManifest(m), 'applications.0.visualElements.wide310x150Logo');
    });

    it('should not error when square71x71Logo is null', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square71x71Logo = null;
        expectNoError(validateManifest(m), 'applications.0.visualElements.square71x71Logo');
    });

    it('should error when square71x71Logo is empty string', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square71x71Logo = '';
        expectError(validateManifest(m), 'applications.0.visualElements.square71x71Logo');
    });

    it('should not error when square310x310Logo is null', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square310x310Logo = null;
        expectNoError(validateManifest(m), 'applications.0.visualElements.square310x310Logo');
    });

    it('should error when square310x310Logo has .svg extension', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.square310x310Logo = 'Assets\\Logo.svg';
        expectError(validateManifest(m), 'applications.0.visualElements.square310x310Logo');
    });

    it('should not error when badgeLogo is null', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.badgeLogo = null;
        expectNoError(validateManifest(m), 'applications.0.visualElements.badgeLogo');
    });

    it('should error when badgeLogo is empty string', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.badgeLogo = '';
        expectError(validateManifest(m), 'applications.0.visualElements.badgeLogo');
    });

    it('should not error when splashScreenImage is null', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.splashScreenImage = null;
        expectNoError(validateManifest(m), 'applications.0.visualElements.splashScreenImage');
    });

    it('should error when splashScreenImage has .tiff extension', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.splashScreenImage = 'Assets\\Splash.tiff';
        expectError(validateManifest(m), 'applications.0.visualElements.splashScreenImage');
    });

    it('should accept splashScreenImage with .jpeg extension', () => {
        const m = makeValidManifest();
        m.applications[0].visualElements.splashScreenImage = 'Assets\\Splash.jpeg';
        expectNoError(validateManifest(m), 'applications.0.visualElements.splashScreenImage');
    });
});

// ─── 13. Cross-section: valid manifest produces no errors ──────────────────

describe('Full valid manifest', () => {
    it('should produce no errors for a fully valid minimal manifest', () => {
        const m = makeValidManifest();
        const errors = validateManifest(m);
        assert.deepEqual(errors, [], `Expected no errors but got: ${JSON.stringify(errors)}`);
    });

    it('should produce no errors for a fully populated valid manifest', () => {
        const m = makeValidManifest();
        m.phoneIdentity = { phoneProductId: 'aabbccdd-1122-3344-5566-778899aabbcc', phonePublisherId: '00000000-0000-0000-0000-000000000000' };
        m.identity.resourceId = 'my-resource';
        m.properties.description = 'A valid description';
        m.applications[0].visualElements.description = 'App description';
        m.applications[0].visualElements.backgroundColor = '#FF5733';
        m.applications[0].visualElements.wide310x150Logo = 'Assets\\Wide.png';
        m.applications[0].visualElements.square71x71Logo = 'Assets\\Small.png';
        m.applications[0].visualElements.square310x310Logo = 'Assets\\Large.png';
        m.applications[0].visualElements.badgeLogo = 'Assets\\Badge.png';
        m.applications[0].visualElements.splashScreenImage = 'Assets\\Splash.png';
        m.dependencies.packageDependencies = [{ name: 'Some.Package', minVersion: '1.0.0.0', publisher: 'CN=Pub', optional: '' }];
        m.dependencies.mainPackageDependencies = [{ name: 'Main.Pkg' }];
        m.dependencies.driverConstraints = [{ name: 'Driver1', minVersion: '2.0.0.0', minDate: '2023-06-15' }];
        m.dependencies.osPackageDependencies = [{ name: 'OS.Pkg.Dep', version: '10.0.0.0' }];
        m.dependencies.hostRuntimeDependencies = [{ name: 'HostRT', publisher: 'CN=Host', minVersion: '1.0.0.0' }];
        m.dependencies.externalDependencies = [{ name: 'ExtDep', publisher: 'CN=Ext', minVersion: '3.0.0.0', optional: '' }];
        m.resources = [{ language: 'en-US', scale: '', dxFeatureLevel: '' }, { language: 'fr', scale: '', dxFeatureLevel: '' }];
        const errors = validateManifest(m);
        assert.deepEqual(errors, [], `Expected no errors but got: ${JSON.stringify(errors)}`);
    });
});

// ═══════════════════════════════════════════════════════════════════════════════
// Custom Capability validation (isValidCustomCapability)
// ═══════════════════════════════════════════════════════════════════════════════
describe('Custom Capability validation', () => {
    // --- Valid custom capabilities ---
    it('should accept valid custom capability: Contoso.Devices.SerialCommunication_0wer1ey63g7b4', () => {
        assert.ok(isValidCustomCapability('Contoso.Devices.SerialCommunication_0wer1ey63g7b4'));
    });

    it('should accept valid custom capability with two segments: Company.Capability_abc1234567890', () => {
        assert.ok(isValidCustomCapability('Company.Capability_abc1234567890'));
    });

    it('should accept valid custom capability with many segments: A.B.C.D.E_abcdefgh01234', () => {
        assert.ok(isValidCustomCapability('A.B.C.D.E_abcdefgh01234'));
    });

    it('should accept all-lowercase publisher ID: company.cap_abcdefgh01234', () => {
        assert.ok(isValidCustomCapability('company.cap_abcdefgh01234'));
    });

    it('should accept all-digit publisher ID: company.cap_1234567890123', () => {
        assert.ok(isValidCustomCapability('company.cap_1234567890123'));
    });

    // --- Invalid custom capabilities ---
    it('should reject empty string', () => {
        assert.ok(!isValidCustomCapability(''));
    });

    it('should reject single segment with no dot: CompanyCapability_abc1234567890', () => {
        assert.ok(!isValidCustomCapability('CompanyCapability_abc1234567890'));
    });

    it('should reject missing publisher ID: Company.Capability', () => {
        assert.ok(!isValidCustomCapability('Company.Capability'));
    });

    it('should reject publisher ID too short (12 chars): Company.Capability_abc123456789', () => {
        assert.ok(!isValidCustomCapability('Company.Capability_abc123456789'));
    });

    it('should reject publisher ID too long (14 chars): Company.Capability_abc12345678901', () => {
        assert.ok(!isValidCustomCapability('Company.Capability_abc12345678901'));
    });

    it('should reject uppercase in publisher ID: Company.Capability_ABC1234567890', () => {
        assert.ok(!isValidCustomCapability('Company.Capability_ABC1234567890'));
    });

    it('should reject special characters in name: Company.Cap-ability_abc1234567890', () => {
        assert.ok(!isValidCustomCapability('Company.Cap-ability_abc1234567890'));
    });

    it('should reject spaces: Company .Capability_abc1234567890', () => {
        assert.ok(!isValidCustomCapability('Company .Capability_abc1234567890'));
    });

    it('should reject leading dot: .Company.Capability_abc1234567890', () => {
        assert.ok(!isValidCustomCapability('.Company.Capability_abc1234567890'));
    });

    it('should reject trailing dot before underscore: Company.Capability._abc1234567890', () => {
        assert.ok(!isValidCustomCapability('Company.Capability._abc1234567890'));
    });

    it('should reject empty segment: Company..Capability_abc1234567890', () => {
        assert.ok(!isValidCustomCapability('Company..Capability_abc1234567890'));
    });

    it('should reject no underscore separator: Company.Capabilityabc1234567890', () => {
        assert.ok(!isValidCustomCapability('Company.Capabilityabc1234567890'));
    });

    it('should reject prefixed capability format: rescap:broadFileSystemAccess', () => {
        assert.ok(!isValidCustomCapability('rescap:broadFileSystemAccess'));
    });

    it('should reject plain capability name: internetClient', () => {
        assert.ok(!isValidCustomCapability('internetClient'));
    });
});

// ─── Extension Field Validation via validateManifest ────────────────────────

describe('Extension Field Validation in validateManifest', () => {
    it('should report error for invalid GUID in Class.Id extension field', () => {
        const m = makeValidManifest();
        m.applications[0].extensions = [
            '<Extension Category="windows.comServer"><ComServer><ExeServer Executable="server.exe" DisplayName="MyServer"><Class Id="not-a-guid" /></ExeServer></ComServer></Extension>'
        ];
        const errors = validateManifest(m);
        const extErrors = errors.filter(e => e.field.includes('extensions.0.Class.Id'));
        assert.ok(extErrors.length > 0, 'Expected extension field error for invalid Class.Id GUID');
        assert.equal(extErrors[0].severity, 'error');
    });

    it('should not report error for valid GUID in Class.Id extension field', () => {
        const m = makeValidManifest();
        m.applications[0].extensions = [
            '<Extension Category="windows.comServer"><ComServer><ExeServer Executable="server.exe" DisplayName="MyServer"><Class Id="{12345678-1234-1234-1234-123456789012}" /></ExeServer></ComServer></Extension>'
        ];
        const errors = validateManifest(m);
        const extErrors = errors.filter(e => e.field.includes('extensions.0.Class.Id'));
        assert.equal(extErrors.length, 0, 'Expected no extension field error for valid Class.Id GUID');
    });

    it('should report error for invalid protocol name', () => {
        const m = makeValidManifest();
        m.applications[0].extensions = [
            '<Extension Category="windows.protocol"><Protocol Name="MyProtocol" /></Extension>'
        ];
        const errors = validateManifest(m);
        const extErrors = errors.filter(e => e.field.includes('extensions.0.Protocol.Name'));
        assert.ok(extErrors.length > 0, 'Expected extension field error for uppercase protocol name');
        assert.equal(extErrors[0].severity, 'error');
    });

    it('should report warning for non-exe/dll ExeServer.Executable', () => {
        const m = makeValidManifest();
        m.applications[0].extensions = [
            '<Extension Category="windows.comServer"><ComServer><ExeServer Executable="server.bat" DisplayName="MyServer"><Class Id="{12345678-1234-1234-1234-123456789012}" /></ExeServer></ComServer></Extension>'
        ];
        const errors = validateManifest(m);
        const extErrors = errors.filter(e => e.field.includes('extensions.0.ExeServer.Executable'));
        assert.ok(extErrors.length > 0, 'Expected extension field warning for .bat executable');
        assert.equal(extErrors[0].severity, 'warning');
    });

    it('should report no extension errors for valid extension XML', () => {
        const m = makeValidManifest();
        m.applications[0].extensions = [
            '<Extension Category="windows.protocol"><Protocol Name="myprotocol" /></Extension>'
        ];
        const errors = validateManifest(m);
        const extErrors = errors.filter(e => e.field.startsWith('applications.0.extensions.'));
        assert.equal(extErrors.length, 0, 'Expected no extension field errors for valid extension');
    });

    it('should validate extension text content fields like FileType', () => {
        const m = makeValidManifest();
        m.applications[0].extensions = [
            '<Extension Category="windows.fileTypeAssociation"><FileTypeAssociation Name="mytype"><SupportedFileTypes><FileType>txt</FileType></SupportedFileTypes></FileTypeAssociation></Extension>'
        ];
        const errors = validateManifest(m);
        const extErrors = errors.filter(e => e.field.includes('extensions.0.FileType'));
        assert.ok(extErrors.length > 0, 'Expected error for FileType without leading dot');
        assert.equal(extErrors[0].severity, 'error');
    });
});
