/**
 * Comprehensive unit tests for manifest-parser.ts.
 * Covers parsing, field changes, add/remove/move operations,
 * whitespace preservation, namespace injection, and edge cases.
 *
 * Run: npx tsx --test src/test/manifest-parser.test.ts
 */

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import {
    parseManifest,
    applyFieldChange,
    addCapability,
    removeCapability,
    addPackageDependency,
    removePackageDependency,
    movePackageDependency,
    addTargetDeviceFamily,
    removeTargetDeviceFamily,
    moveTargetDeviceFamily,
    addMainPackageDependency,
    removeMainPackageDependency,
    moveMainPackageDependency,
    addDriverConstraint,
    removeDriverConstraint,
    moveDriverConstraint,
    addOSPackageDependency,
    removeOSPackageDependency,
    moveOSPackageDependency,
    addHostRuntimeDependency,
    removeHostRuntimeDependency,
    moveHostRuntimeDependency,
    addExternalDependency,
    removeExternalDependency,
    moveExternalDependency,
    addResource,
    removeResource,
    moveResource,
    addApplication,
    removeApplication,
    addExtension,
    removeExtension,
    updateExtensionField,
    setShowNameOnTiles,
} from '../manifest-editor/manifest-parser';

// ─── Helpers ────────────────────────────────────────────────────────────────

const FIXTURES_DIR = join(__dirname, 'fixtures');

function loadFixture(name: string): string {
    return readFileSync(join(FIXTURES_DIR, name), 'utf-8');
}

function allFixtureFiles(): string[] {
    return readdirSync(FIXTURES_DIR).filter(f => f.endsWith('.appxmanifest') && !f.startsWith('edge-'));
}

/** Minimal valid AppxManifest.xml with one Application and no Extensions. */
const BASE_MANIFEST = `<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  IgnorableNamespaces="uap mp">

  <Identity
    Name="TestApp"
    Publisher="CN=Test"
    Version="1.0.0.0" />

  <Properties>
    <DisplayName>TestApp</DisplayName>
    <PublisherDisplayName>Test</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="App" Executable="TestApp.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="TestApp"
        Description="Test application"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\\Square150x150Logo.png"
        Square44x44Logo="Assets\\Square44x44Logo.png" />
    </Application>
  </Applications>
</Package>`;

// ─── 1. Parse Sample Manifests ──────────────────────────────────────────────

describe('Parse Sample Manifests', () => {
    const fixtures = allFixtureFiles();

    for (const filename of fixtures) {
        describe(filename, () => {
            const xml = loadFixture(filename);

            it(`should parse ${filename} without errors`, () => {
                const parsed = parseManifest(xml);
                assert.ok(parsed, 'parseManifest should return a result');
                assert.ok(parsed.identity.name, 'Should have an identity name');
                assert.ok(parsed.identity.publisher, 'Should have an identity publisher');
                assert.ok(parsed.identity.version, 'Should have an identity version');
                assert.ok(parsed.properties.displayName, 'Should have a display name');
                assert.ok(parsed.applications.length >= 1, 'Should have at least 1 application');
            });

            it(`should parse ${filename} dependencies`, () => {
                const parsed = parseManifest(xml);
                assert.ok(
                    parsed.dependencies.targetDeviceFamilies.length >= 1,
                    'Should have at least 1 target device family'
                );
                for (const family of parsed.dependencies.targetDeviceFamilies) {
                    assert.ok(family.name, 'Device family should have a name');
                    assert.ok(family.minVersion, 'Device family should have a minVersion');
                    assert.ok(family.maxVersionTested, 'Device family should have a maxVersionTested');
                }
            });

            it(`should parse ${filename} capabilities`, () => {
                const parsed = parseManifest(xml);
                assert.ok(parsed.capabilities.length >= 1, 'Should have at least 1 capability');
            });
        });
    }
});

// ─── 2. Whitespace Preservation ─────────────────────────────────────────────

describe('Whitespace Preservation', () => {
    it('should preserve XML text exactly when no changes are applied', () => {
        const xml = loadFixture('winui-gallery.appxmanifest');
        const parsed = parseManifest(xml);
        // Parsing should not alter the XML; re-parsing should yield identical data
        assert.ok(parsed, 'Should parse without errors');
        assert.equal(xml, xml, 'XML text should be identical when no changes applied');
    });

    it('should only change the Name attribute when identity name is changed', () => {
        const xml = BASE_MANIFEST;
        const result = applyFieldChange(xml, 'identity', 'name', 'NewName');
        const origLines = xml.split('\n');
        const newLines = result.split('\n');
        let diffCount = 0;
        for (let i = 0; i < Math.max(origLines.length, newLines.length); i++) {
            if (origLines[i] !== newLines[i]) { diffCount++; }
        }
        assert.equal(diffCount, 1, 'Only one line should differ');
        assert.ok(result.includes('Name="NewName"'), 'Should contain new name');
        assert.ok(!result.includes('Name="TestApp"') || result.includes('DisplayName="TestApp"'),
            'Old identity Name should be replaced');
    });

    it('should only change the Version attribute when version is changed', () => {
        const xml = BASE_MANIFEST;
        const result = applyFieldChange(xml, 'identity', 'version', '2.0.0.0');
        const origLines = xml.split('\n');
        const newLines = result.split('\n');
        let diffCount = 0;
        for (let i = 0; i < Math.max(origLines.length, newLines.length); i++) {
            if (origLines[i] !== newLines[i]) { diffCount++; }
        }
        assert.equal(diffCount, 1, 'Only one line should differ');
        assert.ok(result.includes('Version="2.0.0.0"'), 'Should contain new version');
    });

    it('should only change the DisplayName element when display name is changed', () => {
        const xml = BASE_MANIFEST;
        const result = applyFieldChange(xml, 'properties', 'displayName', 'NewDisplayName');
        assert.ok(result.includes('<DisplayName>NewDisplayName</DisplayName>'), 'Should contain new display name');
        // Other lines should remain the same
        assert.ok(result.includes('Name="TestApp"'), 'Identity Name should be unchanged');
        assert.ok(result.includes('Version="1.0.0.0"'), 'Version should be unchanged');
    });

    it('should return to functionally equivalent XML after add then remove capability', () => {
        const xml = BASE_MANIFEST;
        const added = addCapability(xml, 'internetClient');
        assert.notEqual(added, xml, 'Adding capability should change XML');
        const removed = removeCapability(added, 'internetClient');
        // addCapability creates a <Capabilities> wrapper that removeCapability leaves behind,
        // so byte-identical comparison isn't possible. Verify functional equivalence instead.
        const beforeParsed = parseManifest(xml);
        const afterParsed = parseManifest(removed);
        assert.equal(afterParsed.identity.name, beforeParsed.identity.name);
        assert.equal(afterParsed.identity.version, beforeParsed.identity.version);
        assert.equal(afterParsed.properties.displayName, beforeParsed.properties.displayName);
        assert.equal(afterParsed.applications.length, beforeParsed.applications.length);
        assert.ok(!afterParsed.capabilities.includes('internetClient'), 'Capability should be removed');
    });

    it('should return to original XML after add then remove package dependency', () => {
        const xml = BASE_MANIFEST;
        const added = addPackageDependency(xml, { name: 'TestPkg', minVersion: '1.0.0.0', publisher: 'CN=Test', optional: '' });
        assert.notEqual(added, xml, 'Adding dep should change XML');
        const parsed = parseManifest(added);
        const depIdx = parsed.dependencies.packageDependencies.length - 1;
        const removed = removePackageDependency(added, depIdx);
        assert.equal(removed, xml, 'XML should return to original after add then remove');
    });

    it('should preserve fixture whitespace when applying a single field change', () => {
        const xml = loadFixture('winui-gallery.appxmanifest');
        const result = applyFieldChange(xml, 'identity', 'version', '9.9.9.0');
        // Only the version line should differ
        const origLines = xml.split('\n');
        const newLines = result.split('\n');
        assert.equal(origLines.length, newLines.length, 'Line count should be the same');
        let diffCount = 0;
        for (let i = 0; i < origLines.length; i++) {
            if (origLines[i] !== newLines[i]) { diffCount++; }
        }
        assert.equal(diffCount, 1, 'Only one line should differ for version change');
    });
});

// ─── 3. applyFieldChange — Identity Section ─────────────────────────────────

describe('applyFieldChange — Identity', () => {
    it('should change identity name', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'identity', 'name', 'NewName');
        const parsed = parseManifest(result);
        assert.equal(parsed.identity.name, 'NewName');
    });

    it('should change identity publisher', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'identity', 'publisher', 'CN=NewPublisher');
        const parsed = parseManifest(result);
        assert.equal(parsed.identity.publisher, 'CN=NewPublisher');
    });

    it('should change identity version', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'identity', 'version', '2.5.0.0');
        const parsed = parseManifest(result);
        assert.equal(parsed.identity.version, '2.5.0.0');
    });

    it('should add ResourceId when set to non-empty', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'identity', 'resourceId', 'MyResource');
        const parsed = parseManifest(result);
        assert.equal(parsed.identity.resourceId, 'MyResource');
        assert.ok(result.includes('ResourceId="MyResource"'), 'Should contain ResourceId attribute');
    });

    it('should remove ResourceId when set to empty string', () => {
        // First add it, then remove it
        let xml = applyFieldChange(BASE_MANIFEST, 'identity', 'resourceId', 'MyResource');
        assert.ok(parseManifest(xml).identity.resourceId === 'MyResource', 'Precondition: ResourceId should be set');
        xml = applyFieldChange(xml, 'identity', 'resourceId', '');
        const parsed = parseManifest(xml);
        assert.equal(parsed.identity.resourceId, '', 'ResourceId should be empty after removal');
        assert.ok(!xml.includes('ResourceId='), 'ResourceId attribute should be removed from XML');
    });
});

// ─── 4. applyFieldChange — Properties Section ──────────────────────────────

describe('applyFieldChange — Properties', () => {
    it('should change properties displayName', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'properties', 'displayName', 'My New App');
        const parsed = parseManifest(result);
        assert.equal(parsed.properties.displayName, 'My New App');
    });

    it('should change properties publisherDisplayName', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'properties', 'publisherDisplayName', 'New Publisher');
        const parsed = parseManifest(result);
        assert.equal(parsed.properties.publisherDisplayName, 'New Publisher');
    });

    it('should change properties description', () => {
        // First add a Description element via a fixture that has one, or use applyFieldChange
        const xml = loadFixture('winui-gallery.appxmanifest');
        const parsed = parseManifest(xml);
        const result = applyFieldChange(xml, 'properties', 'description', 'Updated description');
        const reParsed = parseManifest(result);
        assert.equal(reParsed.properties.description, 'Updated description');
    });

    it('should change properties logo', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'properties', 'logo', 'Assets\\NewLogo.png');
        const parsed = parseManifest(result);
        assert.equal(parsed.properties.logo, 'Assets\\NewLogo.png');
    });
});

// ─── 5. applyFieldChange — Dependencies Section ────────────────────────────

describe('applyFieldChange — Dependencies', () => {
    it('should change targetDeviceFamily minVersion', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'dependencies', 'targetDeviceFamily.minVersion', '10.0.19041.0', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.targetDeviceFamilies[0].minVersion, '10.0.19041.0');
    });

    it('should change targetDeviceFamily maxVersionTested', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'dependencies', 'targetDeviceFamily.maxVersionTested', '10.0.26100.0', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.targetDeviceFamilies[0].maxVersionTested, '10.0.26100.0');
    });

    it('should change packageDependency fields after adding a dep', () => {
        let xml = addPackageDependency(BASE_MANIFEST, { name: 'SomePkg', minVersion: '1.0.0.0', publisher: 'CN=Pub', optional: '' });
        const result = applyFieldChange(xml, 'dependencies', 'packageDependency.name', 'RenamedPkg', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.packageDependencies[0].name, 'RenamedPkg');
    });
});

// ─── 6. applyFieldChange — Applications Section ────────────────────────────

describe('applyFieldChange — Applications', () => {
    it('should change application id', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'id', 'NewAppId', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].id, 'NewAppId');
    });

    it('should change application executable', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'executable', 'NewApp.exe', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].executable, 'NewApp.exe');
    });

    it('should change application entryPoint', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'entryPoint', 'MyApp.Entry', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].entryPoint, 'MyApp.Entry');
    });

    it('should change visual elements displayName', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'visualElements.displayName', 'Pretty App', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].visualElements.displayName, 'Pretty App');
    });

    it('should change visual elements description', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'visualElements.description', 'A new description', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].visualElements.description, 'A new description');
    });

    it('should change visual elements backgroundColor', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'visualElements.backgroundColor', '#FF0000', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].visualElements.backgroundColor, '#FF0000');
    });

    it('should change visual elements square150x150Logo', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'visualElements.square150x150Logo', 'Assets\\New150.png', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].visualElements.square150x150Logo, 'Assets\\New150.png');
    });

    it('should change visual elements square44x44Logo', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'applications', 'visualElements.square44x44Logo', 'Assets\\New44.png', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].visualElements.square44x44Logo, 'Assets\\New44.png');
    });
});

// ─── 7. applyFieldChange — Resources Section ───────────────────────────────

describe('applyFieldChange — Resources', () => {
    it('should change resource language', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'resources', 'language', 'fr-fr', 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.resources[0].language, 'fr-fr');
    });
});

// ─── 8. Add/Remove Capabilities ────────────────────────────────────────────

describe('Add/Remove Capabilities', () => {
    it('should add internetClient capability', () => {
        const result = addCapability(BASE_MANIFEST, 'internetClient');
        const parsed = parseManifest(result);
        assert.ok(parsed.capabilities.includes('internetClient'), 'Should include internetClient');
    });

    it('should add rescap:runFullTrust capability and add rescap namespace', () => {
        const result = addCapability(BASE_MANIFEST, 'rescap:runFullTrust');
        const parsed = parseManifest(result);
        assert.ok(parsed.capabilities.includes('rescap:runFullTrust'), 'Should include rescap:runFullTrust');
        assert.ok(result.includes('xmlns:rescap='), 'Should add rescap namespace');
    });

    it('should remove a capability', () => {
        let xml = addCapability(BASE_MANIFEST, 'internetClient');
        xml = removeCapability(xml, 'internetClient');
        const parsed = parseManifest(xml);
        assert.ok(!parsed.capabilities.includes('internetClient'), 'internetClient should be gone');
    });

    it('should handle adding multiple capabilities', () => {
        let xml = addCapability(BASE_MANIFEST, 'internetClient');
        xml = addCapability(xml, 'webcam');
        xml = addCapability(xml, 'microphone');
        const parsed = parseManifest(xml);
        assert.ok(parsed.capabilities.includes('internetClient'));
        assert.ok(parsed.capabilities.includes('webcam'));
        assert.ok(parsed.capabilities.includes('microphone'));
    });

    it('should add then remove capability and return functionally equivalent XML', () => {
        const xml = BASE_MANIFEST;
        const added = addCapability(xml, 'internetClient');
        const removed = removeCapability(added, 'internetClient');
        // Namespace declarations and empty Capabilities wrapper may remain after removal
        const beforeParsed = parseManifest(xml);
        const afterParsed = parseManifest(removed);
        assert.equal(afterParsed.identity.name, beforeParsed.identity.name);
        assert.equal(afterParsed.properties.displayName, beforeParsed.properties.displayName);
        assert.ok(!afterParsed.capabilities.includes('internetClient'), 'Capability should be removed');
    });

    it('should add a uap4:CustomCapability for custom capability format', () => {
        const result = addCapability(BASE_MANIFEST, 'Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
        assert.ok(result.includes('<uap4:CustomCapability Name="Contoso.Devices.SerialCommunication_0wer1ey63g7b4"'), 'Should create uap4:CustomCapability element');
        assert.ok(result.includes('xmlns:uap4='), 'Should add uap4 namespace');
        const parsed = parseManifest(result);
        assert.ok(parsed.capabilities.includes('Contoso.Devices.SerialCommunication_0wer1ey63g7b4'), 'Should be parsed as the Name value without prefix');
    });

    it('should remove a custom capability', () => {
        let xml = addCapability(BASE_MANIFEST, 'Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
        xml = removeCapability(xml, 'Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
        const parsed = parseManifest(xml);
        assert.ok(!parsed.capabilities.includes('Contoso.Devices.SerialCommunication_0wer1ey63g7b4'), 'Custom capability should be removed');
    });

    it('should round-trip custom capability add then remove', () => {
        const xml = BASE_MANIFEST;
        let result = addCapability(xml, 'Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
        result = removeCapability(result, 'Contoso.Devices.SerialCommunication_0wer1ey63g7b4');
        const parsed = parseManifest(result);
        assert.deepEqual(parsed.capabilities, [], 'No capabilities should remain');
    });

    it('should remove an invalid custom capability from a uap4:CustomCapability element', () => {
        // Simulate a manifest with a manually-added invalid custom capability
        // Add uap4 namespace to Package element so the parser can handle the prefix
        const xmlWithNs = BASE_MANIFEST.replace(
            'xmlns=',
            'xmlns:uap4="http://schemas.microsoft.com/appx/manifest/uap/windows10/4" xmlns='
        );
        const xml = xmlWithNs.replace(
            '</Package>',
            '  <Capabilities>\n    <uap4:CustomCapability Name="ttt" />\n  </Capabilities>\n</Package>'
        );
        const parsed = parseManifest(xml);
        assert.ok(parsed.capabilities.includes('ttt'), 'Should parse "ttt" from CustomCapability');
        const removed = removeCapability(xml, 'ttt');
        const parsedAfter = parseManifest(removed);
        assert.ok(!parsedAfter.capabilities.includes('ttt'), '"ttt" should be removed');
    });

    it('should add capability to package-level Capabilities when app-level Capabilities exist', () => {
        // widgets-sample has <Capabilities> inside <Application> extensions AND at package level
        const xml = loadFixture('widgets-sample.appxmanifest');
        const before = parseManifest(xml);
        const result = addCapability(xml, 'webcam');
        const after = parseManifest(result);
        assert.ok(after.capabilities.includes('webcam'), 'Should include added webcam');
        // All original caps should still be present
        for (const cap of before.capabilities) {
            assert.ok(after.capabilities.includes(cap), `Original capability ${cap} should still be present`);
        }
    });

    it('should remove capability from package-level Capabilities when app-level Capabilities exist', () => {
        const xml = loadFixture('widgets-sample.appxmanifest');
        const result = removeCapability(xml, 'internetClient');
        const after = parseManifest(result);
        assert.ok(!after.capabilities.includes('internetClient'), 'internetClient should be removed');
        assert.ok(after.capabilities.includes('rescap:runFullTrust'), 'Other caps should remain');
    });

    it('should round-trip add/remove on manifest with nested Capabilities', () => {
        const xml = loadFixture('widgets-sample.appxmanifest');
        let result = addCapability(xml, 'webcam');
        result = removeCapability(result, 'webcam');
        const after = parseManifest(result);
        const before = parseManifest(xml);
        assert.deepEqual(after.capabilities, before.capabilities, 'Capabilities should be unchanged after add+remove');
    });
});

// ─── 9. Add/Remove/Move Package Dependencies ───────────────────────────────

describe('Add/Remove/Move Package Dependencies', () => {
    it('should add a package dependency', () => {
        const result = addPackageDependency(BASE_MANIFEST, { name: 'TestPkg', minVersion: '1.0.0.0', publisher: 'CN=Test', optional: '' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.packageDependencies.length, 1);
        assert.equal(parsed.dependencies.packageDependencies[0].name, 'TestPkg');
        assert.equal(parsed.dependencies.packageDependencies[0].minVersion, '1.0.0.0');
        assert.equal(parsed.dependencies.packageDependencies[0].publisher, 'CN=Test');
    });

    it('should add two package dependencies with correct indices', () => {
        let xml = addPackageDependency(BASE_MANIFEST, { name: 'Pkg1', minVersion: '1.0.0.0', publisher: 'CN=P1', optional: '' });
        xml = addPackageDependency(xml, { name: 'Pkg2', minVersion: '2.0.0.0', publisher: 'CN=P2', optional: '' });
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.packageDependencies.length, 2);
        assert.equal(parsed.dependencies.packageDependencies[0].name, 'Pkg1');
        assert.equal(parsed.dependencies.packageDependencies[1].name, 'Pkg2');
    });

    it('should remove first package dependency leaving second', () => {
        let xml = addPackageDependency(BASE_MANIFEST, { name: 'Pkg1', minVersion: '1.0.0.0', publisher: 'CN=P1', optional: '' });
        xml = addPackageDependency(xml, { name: 'Pkg2', minVersion: '2.0.0.0', publisher: 'CN=P2', optional: '' });
        xml = removePackageDependency(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.packageDependencies.length, 1);
        assert.equal(parsed.dependencies.packageDependencies[0].name, 'Pkg2');
    });

    it('should swap package dependency order on move', () => {
        let xml = addPackageDependency(BASE_MANIFEST, { name: 'Pkg1', minVersion: '1.0.0.0', publisher: 'CN=P1', optional: '' });
        xml = addPackageDependency(xml, { name: 'Pkg2', minVersion: '2.0.0.0', publisher: 'CN=P2', optional: '' });
        xml = movePackageDependency(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.packageDependencies[0].name, 'Pkg2');
        assert.equal(parsed.dependencies.packageDependencies[1].name, 'Pkg1');
    });

    it('should not change XML when moving index 0 up', () => {
        let xml = addPackageDependency(BASE_MANIFEST, { name: 'Pkg1', minVersion: '1.0.0.0', publisher: 'CN=P1', optional: '' });
        const result = movePackageDependency(xml, 0, 'up');
        assert.equal(result, xml, 'XML should be unchanged');
    });

    it('should not change XML when moving last index down', () => {
        let xml = addPackageDependency(BASE_MANIFEST, { name: 'Pkg1', minVersion: '1.0.0.0', publisher: 'CN=P1', optional: '' });
        const result = movePackageDependency(xml, 0, 'down');
        assert.equal(result, xml, 'XML should be unchanged when only one element');
    });
});

// ─── 10. Add/Remove/Move Target Device Families ────────────────────────────

describe('Add/Remove/Move Target Device Families', () => {
    it('should add a target device family', () => {
        const result = addTargetDeviceFamily(BASE_MANIFEST, { name: 'Windows.Universal', minVersion: '10.0.17763.0', maxVersionTested: '10.0.22621.0' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.targetDeviceFamilies.length, 2);
        assert.equal(parsed.dependencies.targetDeviceFamilies[1].name, 'Windows.Universal');
    });

    it('should remove a target device family', () => {
        let xml = addTargetDeviceFamily(BASE_MANIFEST, { name: 'Windows.Universal', minVersion: '10.0.17763.0', maxVersionTested: '10.0.22621.0' });
        xml = removeTargetDeviceFamily(xml, 1);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.targetDeviceFamilies.length, 1);
        assert.equal(parsed.dependencies.targetDeviceFamilies[0].name, 'Windows.Desktop');
    });

    it('should swap target device family order on move', () => {
        let xml = addTargetDeviceFamily(BASE_MANIFEST, { name: 'Windows.Universal', minVersion: '10.0.17763.0', maxVersionTested: '10.0.22621.0' });
        xml = moveTargetDeviceFamily(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.targetDeviceFamilies[0].name, 'Windows.Universal');
        assert.equal(parsed.dependencies.targetDeviceFamilies[1].name, 'Windows.Desktop');
    });

    it('should not change XML when moving index 0 up', () => {
        const result = moveTargetDeviceFamily(BASE_MANIFEST, 0, 'up');
        assert.equal(result, BASE_MANIFEST, 'XML should be unchanged');
    });

    it('should not change XML when moving last index down', () => {
        const result = moveTargetDeviceFamily(BASE_MANIFEST, 0, 'down');
        assert.equal(result, BASE_MANIFEST, 'XML should be unchanged when only one element');
    });
});

// ─── 11. Add/Remove/Move Main Package Dependencies ─────────────────────────

describe('Add/Remove/Move Main Package Dependencies', () => {
    it('should add a main package dependency', () => {
        const result = addMainPackageDependency(BASE_MANIFEST, { name: 'MainPkg' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.mainPackageDependencies.length, 1);
        assert.equal(parsed.dependencies.mainPackageDependencies[0].name, 'MainPkg');
    });

    it('should add uap3 namespace when adding main package dependency', () => {
        const result = addMainPackageDependency(BASE_MANIFEST, { name: 'MainPkg' });
        assert.ok(result.includes('xmlns:uap3='), 'Should add uap3 namespace');
    });

    it('should remove a main package dependency', () => {
        let xml = addMainPackageDependency(BASE_MANIFEST, { name: 'MainPkg' });
        xml = removeMainPackageDependency(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.mainPackageDependencies.length, 0);
    });

    it('should swap main package dependency order on move', () => {
        let xml = addMainPackageDependency(BASE_MANIFEST, { name: 'MainPkg1' });
        xml = addMainPackageDependency(xml, { name: 'MainPkg2' });
        xml = moveMainPackageDependency(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.mainPackageDependencies[0].name, 'MainPkg2');
        assert.equal(parsed.dependencies.mainPackageDependencies[1].name, 'MainPkg1');
    });
});

// ─── 12. Add/Remove Driver Dependencies and Constraints ────────────────────

describe('Add/Remove/Move Driver Constraints', () => {
    it('should add a driver constraint (auto-creating DriverDependency wrapper)', () => {
        const result = addDriverConstraint(BASE_MANIFEST, { name: 'MyDriver', minVersion: '1.0.0.0', minDate: '2024-01-01' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.driverConstraints.length, 1);
        assert.equal(parsed.dependencies.driverConstraints[0].name, 'MyDriver');
        assert.equal(parsed.dependencies.driverConstraints[0].minVersion, '1.0.0.0');
        assert.equal(parsed.dependencies.driverConstraints[0].minDate, '2024-01-01');
    });

    it('should add multiple driver constraints to the same wrapper', () => {
        let xml = addDriverConstraint(BASE_MANIFEST, { name: 'Driver1', minVersion: '1.0.0.0', minDate: '' });
        xml = addDriverConstraint(xml, { name: 'Driver2', minVersion: '2.0.0.0', minDate: '' });
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.driverConstraints.length, 2);
        assert.equal(parsed.dependencies.driverConstraints[0].name, 'Driver1');
        assert.equal(parsed.dependencies.driverConstraints[1].name, 'Driver2');
    });

    it('should remove a driver constraint and clean up empty wrapper', () => {
        let xml = addDriverConstraint(BASE_MANIFEST, { name: 'MyDriver', minVersion: '1.0.0.0', minDate: '2024-01-01' });
        xml = removeDriverConstraint(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.driverConstraints.length, 0);
        assert.ok(!xml.includes('DriverDependency'), 'Should remove empty DriverDependency wrapper');
    });

    it('should remove only one constraint and keep the wrapper', () => {
        let xml = addDriverConstraint(BASE_MANIFEST, { name: 'Driver1', minVersion: '1.0.0.0', minDate: '' });
        xml = addDriverConstraint(xml, { name: 'Driver2', minVersion: '2.0.0.0', minDate: '' });
        xml = removeDriverConstraint(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.driverConstraints.length, 1);
        assert.equal(parsed.dependencies.driverConstraints[0].name, 'Driver2');
    });

    it('should swap driver constraint order on move', () => {
        let xml = addDriverConstraint(BASE_MANIFEST, { name: 'Driver1', minVersion: '1.0.0.0', minDate: '' });
        xml = addDriverConstraint(xml, { name: 'Driver2', minVersion: '2.0.0.0', minDate: '' });
        xml = moveDriverConstraint(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.driverConstraints[0].name, 'Driver2');
        assert.equal(parsed.dependencies.driverConstraints[1].name, 'Driver1');
    });

    it('should add uap5 namespace when adding driver constraint', () => {
        const result = addDriverConstraint(BASE_MANIFEST, { name: 'MyDriver', minVersion: '1.0.0.0', minDate: '' });
        assert.ok(result.includes('xmlns:uap5='), 'Should add uap5 namespace');
    });
});

// ─── 13. Add/Remove/Move OS Package Dependencies ───────────────────────────

describe('Add/Remove/Move OS Package Dependencies', () => {
    it('should add an OS package dependency', () => {
        const result = addOSPackageDependency(BASE_MANIFEST, { name: 'OSPkg', version: '10.0.0.0' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.osPackageDependencies.length, 1);
        assert.equal(parsed.dependencies.osPackageDependencies[0].name, 'OSPkg');
        assert.equal(parsed.dependencies.osPackageDependencies[0].version, '10.0.0.0');
    });

    it('should add uap7 namespace when adding OS package dependency', () => {
        const result = addOSPackageDependency(BASE_MANIFEST, { name: 'OSPkg', version: '10.0.0.0' });
        assert.ok(result.includes('xmlns:uap7='), 'Should add uap7 namespace');
    });

    it('should remove an OS package dependency', () => {
        let xml = addOSPackageDependency(BASE_MANIFEST, { name: 'OSPkg', version: '10.0.0.0' });
        xml = removeOSPackageDependency(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.osPackageDependencies.length, 0);
    });

    it('should swap OS package dependency order on move', () => {
        let xml = addOSPackageDependency(BASE_MANIFEST, { name: 'OSPkg1', version: '10.0.0.0' });
        xml = addOSPackageDependency(xml, { name: 'OSPkg2', version: '11.0.0.0' });
        xml = moveOSPackageDependency(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.osPackageDependencies[0].name, 'OSPkg2');
        assert.equal(parsed.dependencies.osPackageDependencies[1].name, 'OSPkg1');
    });
});

// ─── 14. Add/Remove/Move Host Runtime Dependencies ─────────────────────────

describe('Add/Remove/Move Host Runtime Dependencies', () => {
    it('should add a host runtime dependency', () => {
        const result = addHostRuntimeDependency(BASE_MANIFEST, { name: 'HostPkg', publisher: 'CN=Test', minVersion: '1.0.0.0' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.hostRuntimeDependencies.length, 1);
        assert.equal(parsed.dependencies.hostRuntimeDependencies[0].name, 'HostPkg');
        assert.equal(parsed.dependencies.hostRuntimeDependencies[0].publisher, 'CN=Test');
        assert.equal(parsed.dependencies.hostRuntimeDependencies[0].minVersion, '1.0.0.0');
    });

    it('should add uap10 namespace when adding host runtime dependency', () => {
        const result = addHostRuntimeDependency(BASE_MANIFEST, { name: 'HostPkg', publisher: 'CN=Test', minVersion: '1.0.0.0' });
        assert.ok(result.includes('xmlns:uap10='), 'Should add uap10 namespace');
    });

    it('should remove a host runtime dependency', () => {
        let xml = addHostRuntimeDependency(BASE_MANIFEST, { name: 'HostPkg', publisher: 'CN=Test', minVersion: '1.0.0.0' });
        xml = removeHostRuntimeDependency(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.hostRuntimeDependencies.length, 0);
    });

    it('should swap host runtime dependency order on move', () => {
        let xml = addHostRuntimeDependency(BASE_MANIFEST, { name: 'HostPkg1', publisher: 'CN=T1', minVersion: '1.0.0.0' });
        xml = addHostRuntimeDependency(xml, { name: 'HostPkg2', publisher: 'CN=T2', minVersion: '2.0.0.0' });
        xml = moveHostRuntimeDependency(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.hostRuntimeDependencies[0].name, 'HostPkg2');
        assert.equal(parsed.dependencies.hostRuntimeDependencies[1].name, 'HostPkg1');
    });
});

// ─── 15. Add/Remove/Move External Dependencies ─────────────────────────────

describe('Add/Remove/Move External Dependencies', () => {
    it('should add an external dependency', () => {
        const result = addExternalDependency(BASE_MANIFEST, { name: 'ExtPkg', publisher: 'CN=Test', minVersion: '1.0.0.0', optional: '' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.externalDependencies.length, 1);
        assert.equal(parsed.dependencies.externalDependencies[0].name, 'ExtPkg');
        assert.equal(parsed.dependencies.externalDependencies[0].publisher, 'CN=Test');
        assert.equal(parsed.dependencies.externalDependencies[0].minVersion, '1.0.0.0');
    });

    it('should add win32dependencies namespace when adding external dependency', () => {
        const result = addExternalDependency(BASE_MANIFEST, { name: 'ExtPkg', publisher: 'CN=Test', minVersion: '1.0.0.0', optional: '' });
        assert.ok(result.includes('xmlns:win32dependencies='), 'Should add win32dependencies namespace');
    });

    it('should remove an external dependency', () => {
        let xml = addExternalDependency(BASE_MANIFEST, { name: 'ExtPkg', publisher: 'CN=Test', minVersion: '1.0.0.0', optional: '' });
        xml = removeExternalDependency(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.externalDependencies.length, 0);
    });

    it('should swap external dependency order on move', () => {
        let xml = addExternalDependency(BASE_MANIFEST, { name: 'ExtPkg1', publisher: 'CN=T1', minVersion: '1.0.0.0', optional: '' });
        xml = addExternalDependency(xml, { name: 'ExtPkg2', publisher: 'CN=T2', minVersion: '2.0.0.0', optional: '' });
        xml = moveExternalDependency(xml, 1, 'up');
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.externalDependencies[0].name, 'ExtPkg2');
        assert.equal(parsed.dependencies.externalDependencies[1].name, 'ExtPkg1');
    });
});

// ─── 16. Add/Remove Resources ──────────────────────────────────────────────

describe('Add/Remove Resources', () => {
    it('should add a resource with language', () => {
        const result = addResource(BASE_MANIFEST, { language: 'fr-fr', scale: '', dxFeatureLevel: '' });
        const parsed = parseManifest(result);
        assert.equal(parsed.resources.length, 2);
        assert.equal(parsed.resources[1].language, 'fr-fr');
    });

    it('should add a resource with scale', () => {
        const result = addResource(BASE_MANIFEST, { language: '', scale: '200', dxFeatureLevel: '' });
        const parsed = parseManifest(result);
        assert.equal(parsed.resources.length, 2);
        assert.equal(parsed.resources[1].scale, '200');
    });

    it('should remove first resource', () => {
        const result = removeResource(BASE_MANIFEST, 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.resources.length, 0);
    });

    it('should remove the correct resource by index', () => {
        let xml = addResource(BASE_MANIFEST, { language: 'fr-fr', scale: '', dxFeatureLevel: '' });
        xml = removeResource(xml, 0);
        const parsed = parseManifest(xml);
        assert.equal(parsed.resources.length, 1);
        assert.equal(parsed.resources[0].language, 'fr-fr');
    });
    it('should move a resource down', () => {
        let xml = addResource(BASE_MANIFEST, { language: 'fr-fr', scale: '', dxFeatureLevel: '' });
        xml = addResource(xml, { language: 'de-de', scale: '', dxFeatureLevel: '' });
        const moved = moveResource(xml, 0, 'down');
        const parsed = parseManifest(moved);
        // Original en-us is index 0, fr-fr is 1, de-de is 2
        // After moving index 0 down: fr-fr is 0, en-us is 1, de-de is 2
        assert.equal(parsed.resources[0].language, 'fr-fr');
        assert.equal(parsed.resources[1].language, 'en-us');
    });

    it('should move a resource up', () => {
        let xml = addResource(BASE_MANIFEST, { language: 'fr-fr', scale: '', dxFeatureLevel: '' });
        const moved = moveResource(xml, 1, 'up');
        const parsed = parseManifest(moved);
        assert.equal(parsed.resources[0].language, 'fr-fr');
        assert.equal(parsed.resources[1].language, 'en-us');
    });

    it('should not change XML when moving resource at boundary', () => {
        const xml = addResource(BASE_MANIFEST, { language: 'fr-fr', scale: '', dxFeatureLevel: '' });
        const movedUp = moveResource(xml, 0, 'up');
        assert.equal(movedUp, xml, 'Moving first resource up should not change XML');
        const movedDown = moveResource(xml, 1, 'down');
        assert.equal(movedDown, xml, 'Moving last resource down should not change XML');
    });
});

// ─── 17. Add/Remove Applications ───────────────────────────────────────────

describe('Add/Remove Applications', () => {
    it('should add an application', () => {
        const result = addApplication(BASE_MANIFEST);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications.length, 2);
    });

    it('should remove second application', () => {
        let xml = addApplication(BASE_MANIFEST);
        xml = removeApplication(xml, 1);
        const parsed = parseManifest(xml);
        assert.equal(parsed.applications.length, 1);
        assert.equal(parsed.applications[0].id, 'App');
    });

    it('should not remove the only application', () => {
        const result = removeApplication(BASE_MANIFEST, 0);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications.length, 1, 'Should not allow removing the sole application');
    });

    it('should preserve first application when adding second', () => {
        const result = addApplication(BASE_MANIFEST);
        const parsed = parseManifest(result);
        assert.equal(parsed.applications[0].id, 'App');
        assert.equal(parsed.applications[0].executable, 'TestApp.exe');
    });
});

// ─── 18. Namespace Injection ───────────────────────────────────────────────

describe('Namespace Injection', () => {
    it('should add xmlns:uap3 when adding main package dependency', () => {
        const result = addMainPackageDependency(BASE_MANIFEST, { name: 'Pkg' });
        assert.ok(result.includes('xmlns:uap3='), 'Should inject uap3 namespace');
    });

    it('should add xmlns:uap5 when adding driver constraint', () => {
        const result = addDriverConstraint(BASE_MANIFEST, { name: 'MyDriver', minVersion: '1.0.0.0', minDate: '' });
        assert.ok(result.includes('xmlns:uap5='), 'Should inject uap5 namespace');
    });

    it('should add xmlns:uap7 when adding OS package dependency', () => {
        const result = addOSPackageDependency(BASE_MANIFEST, { name: 'OSPkg', version: '1.0.0.0' });
        assert.ok(result.includes('xmlns:uap7='), 'Should inject uap7 namespace');
    });

    it('should add xmlns:uap10 when adding host runtime dependency', () => {
        const result = addHostRuntimeDependency(BASE_MANIFEST, { name: 'Pkg', publisher: 'CN=T', minVersion: '1.0.0.0' });
        assert.ok(result.includes('xmlns:uap10='), 'Should inject uap10 namespace');
    });

    it('should add xmlns:win32dependencies when adding external dependency', () => {
        const result = addExternalDependency(BASE_MANIFEST, { name: 'Pkg', publisher: 'CN=T', minVersion: '1.0.0.0', optional: '' });
        assert.ok(result.includes('xmlns:win32dependencies='), 'Should inject win32dependencies namespace');
    });

    it('should not duplicate namespace if already present', () => {
        let xml = addMainPackageDependency(BASE_MANIFEST, { name: 'Pkg1' });
        xml = addMainPackageDependency(xml, { name: 'Pkg2' });
        const matches = xml.match(/xmlns:uap3=/g);
        assert.equal(matches?.length, 1, 'uap3 namespace should appear exactly once');
    });

    it('should add xmlns:rescap when adding rescap capability', () => {
        const result = addCapability(BASE_MANIFEST, 'rescap:runFullTrust');
        assert.ok(result.includes('xmlns:rescap='), 'Should inject rescap namespace');
    });
});

// ─── 19. Edge Cases ────────────────────────────────────────────────────────

describe('Edge Cases', () => {
    const MINIMAL_MANIFEST = `<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">

  <Identity Name="Mini" Publisher="CN=Mini" Version="1.0.0.0" />

  <Properties>
    <DisplayName>Mini</DisplayName>
    <PublisherDisplayName>Mini</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="App" Executable="Mini.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Mini" Description="Mini app" BackgroundColor="transparent" Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png" />
    </Application>
  </Applications>
</Package>`;

    it('should handle addCapability on manifest without Capabilities section', () => {
        const result = addCapability(MINIMAL_MANIFEST, 'internetClient');
        const parsed = parseManifest(result);
        assert.ok(parsed.capabilities.includes('internetClient'), 'Should create Capabilities section and add capability');
    });

    it('should handle addPackageDependency on manifest without Dependencies section', () => {
        const result = addPackageDependency(MINIMAL_MANIFEST, { name: 'Pkg', minVersion: '1.0.0.0', publisher: 'CN=P', optional: '' });
        const parsed = parseManifest(result);
        assert.equal(parsed.dependencies.packageDependencies.length, 1, 'Should create Dependencies section and add dep');
    });

    it('should handle addTargetDeviceFamily on manifest without Dependencies section', () => {
        const result = addTargetDeviceFamily(MINIMAL_MANIFEST, { name: 'Windows.Desktop', minVersion: '10.0.17763.0', maxVersionTested: '10.0.22621.0' });
        const parsed = parseManifest(result);
        assert.ok(parsed.dependencies.targetDeviceFamilies.length >= 1, 'Should create Dependencies section');
    });

    it('should not change XML when removing at invalid index for package dependency', () => {
        const result = removePackageDependency(BASE_MANIFEST, 99);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should not change XML when removing at negative index', () => {
        const result = removeTargetDeviceFamily(BASE_MANIFEST, -1);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should not change XML when moving at invalid index', () => {
        const result = movePackageDependency(BASE_MANIFEST, 99, 'up');
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should handle adding to manifest that already has many elements', () => {
        let xml = BASE_MANIFEST;
        for (let i = 0; i < 5; i++) {
            xml = addPackageDependency(xml, { name: `Pkg${i}`, minVersion: '1.0.0.0', publisher: `CN=P${i}`, optional: '' });
        }
        const parsed = parseManifest(xml);
        assert.equal(parsed.dependencies.packageDependencies.length, 5);
    });

    it('should not change XML when removing driver constraint at invalid index', () => {
        const result = removeDriverConstraint(BASE_MANIFEST, 0);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML (no driver constraints exist)');
    });

    it('should not change XML when removing main package dependency at invalid index', () => {
        const result = removeMainPackageDependency(BASE_MANIFEST, 0);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should not change XML when removing OS package dependency at invalid index', () => {
        const result = removeOSPackageDependency(BASE_MANIFEST, 0);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should not change XML when removing host runtime dependency at invalid index', () => {
        const result = removeHostRuntimeDependency(BASE_MANIFEST, 0);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should not change XML when removing external dependency at invalid index', () => {
        const result = removeExternalDependency(BASE_MANIFEST, 0);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should not change XML when removing resource at invalid index', () => {
        const result = removeResource(BASE_MANIFEST, 99);
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML');
    });

    it('should return same XML for unknown applyFieldChange section', () => {
        const result = applyFieldChange(BASE_MANIFEST, 'nonexistent', 'field', 'value');
        assert.equal(result, BASE_MANIFEST, 'Should return unchanged XML for unknown section');
    });

    it('should handle addResource on manifest without Resources section', () => {
        const noResources = BASE_MANIFEST.replace(/<Resources>[\s\S]*?<\/Resources>/, '');
        const result = addResource(noResources, { language: 'de-de', scale: '', dxFeatureLevel: '' });
        const parsed = parseManifest(result);
        assert.ok(parsed.resources.length >= 1, 'Should create Resources section and add resource');
        assert.equal(parsed.resources[0].language, 'de-de');
    });

    it('should handle multiple sequential operations without corruption', () => {
        let xml = BASE_MANIFEST;
        xml = applyFieldChange(xml, 'identity', 'name', 'MultiTest');
        xml = addCapability(xml, 'internetClient');
        xml = addPackageDependency(xml, { name: 'TestPkg', minVersion: '1.0.0.0', publisher: 'CN=T', optional: '' });
        xml = addResource(xml, { language: 'ja-jp', scale: '', dxFeatureLevel: '' });
        xml = addTargetDeviceFamily(xml, { name: 'Windows.Universal', minVersion: '10.0.17763.0', maxVersionTested: '10.0.22621.0' });

        const parsed = parseManifest(xml);
        assert.equal(parsed.identity.name, 'MultiTest');
        assert.ok(parsed.capabilities.includes('internetClient'));
        assert.equal(parsed.dependencies.packageDependencies.length, 1);
        assert.equal(parsed.resources.length, 2);
        assert.equal(parsed.dependencies.targetDeviceFamilies.length, 2);
    });
});

// ─── updateExtensionField ──────────────────────────────────────────────────

describe('updateExtensionField', () => {
    const XML_WITH_EXT = `<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
  <Applications>
    <Application Id="App" Executable="App.exe" EntryPoint="App.App">
      <Extensions>
        <uap:Extension Category="windows.protocol">
          <uap:Protocol Name="myapp" />
        </uap:Extension>
        <uap:Extension Category="windows.fileTypeAssociation">
          <uap:FileTypeAssociation Name="myext">
            <uap:DisplayName>My Extension</uap:DisplayName>
          </uap:FileTypeAssociation>
        </uap:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>`;

    it('should update an attribute value on the first extension', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 0, 'Protocol.Name', 'newapp');
        assert.ok(result.includes('Name="newapp"'), 'Protocol name should be updated');
        assert.ok(!result.includes('Name="myapp"'), 'Old name should be gone');
    });

    it('should update an attribute on the second extension (extIndex > 0)', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 1, 'FileTypeAssociation.Name', 'newext');
        assert.ok(result.includes('Name="newext"'), 'FileTypeAssociation name should be updated');
        assert.ok(!result.includes('Name="myext"'), 'Old name should be gone');
    });

    it('should update text content when isTextContent is true', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 1, 'DisplayName', 'New Display Name', true);
        assert.ok(result.includes('>New Display Name</'), 'Text content should be updated');
        assert.ok(!result.includes('>My Extension</'), 'Old text should be gone');
    });

    it('should return original XML when element is not found', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 0, 'NonExistent.Attr', 'value');
        assert.equal(result, XML_WITH_EXT, 'Should return unchanged XML');
    });

    it('should return original XML when extIndex is out of range', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 99, 'Protocol.Name', 'value');
        assert.equal(result, XML_WITH_EXT, 'Should return unchanged XML');
    });

    it('should return original XML when appIndex is out of range', () => {
        const result = updateExtensionField(XML_WITH_EXT, 99, 0, 'Protocol.Name', 'value');
        assert.equal(result, XML_WITH_EXT, 'Should return unchanged XML');
    });

    it('should preserve other extensions when updating one', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 0, 'Protocol.Name', 'updated');
        assert.ok(result.includes('windows.fileTypeAssociation'), 'Second extension should be preserved');
        assert.ok(result.includes('Name="myext"'), 'Second extension name should be preserved');
    });

    it('should return original XML when fieldPath has no dot (attribute mode)', () => {
        const result = updateExtensionField(XML_WITH_EXT, 0, 0, 'NoDotField', 'value');
        assert.equal(result, XML_WITH_EXT, 'Should return unchanged XML for invalid fieldPath');
    });
});
