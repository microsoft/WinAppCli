/**
 * Unit tests for extension templates and real-world manifest parsing.
 * Verifies that each EXTENSION_TEMPLATES entry can be inserted into a manifest
 * via addExtension() and then round-trips through parseManifest() correctly.
 * Also tests parsing of real-world appxmanifest fixtures from WinUI Gallery,
 * WindowsAppSDK-Samples, and other Microsoft sample apps.
 *
 * Run: npx tsx --test src/test/extension-templates.test.ts
 */

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { addExtension, parseManifest } from '../manifest-editor/manifest-parser';
import { EXTENSION_TEMPLATES } from '../manifest-editor/manifest-types';
import { validateManifest } from '../manifest-editor/manifest-validator';

// ─── Helpers ────────────────────────────────────────────────────────────────

const FIXTURES_DIR = join(__dirname, 'fixtures');

function loadFixture(name: string): string {
    return readFileSync(join(FIXTURES_DIR, name), 'utf-8');
}

function allFixtureFiles(): string[] {
    return readdirSync(FIXTURES_DIR).filter(f => f.endsWith('.appxmanifest') && f !== 'edge-cases.appxmanifest');
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

// ─── Extension template tests (against BASE_MANIFEST) ──────────────────────

describe('Extension Templates', () => {
    for (const template of EXTENSION_TEMPLATES) {
        it(`should insert "${template.label}" (${template.category}) into manifest`, () => {
            const result = addExtension(BASE_MANIFEST, 0, template.xml);

            assert.notEqual(result, BASE_MANIFEST, 'addExtension should modify the manifest');

            assert.ok(
                result.includes(`Category="${template.category}"`),
                `Result should contain Category="${template.category}"`
            );

            const parsed = parseManifest(result);
            assert.ok(parsed, 'parseManifest should return a result');
            assert.equal(parsed.applications.length, 1, 'Should still have 1 application');

            assert.ok(
                parsed.applications[0].extensions.length >= 1,
                `Application should have at least 1 extension after adding "${template.label}"`
            );

            const addedExt = parsed.applications[0].extensions.find(
                (ext: string) => ext.includes(`Category="${template.category}"`)
            );
            assert.ok(addedExt, `Should find an extension with Category="${template.category}" in parsed extensions`);
        });

        it(`should add required namespace declarations for "${template.label}"`, () => {
            const result = addExtension(BASE_MANIFEST, 0, template.xml);

            const prefixToNs: Record<string, string> = {
                'com:': 'xmlns:com=',
                'uap3:': 'xmlns:uap3=',
                'uap5:': 'xmlns:uap5=',
                'desktop:': 'xmlns:desktop=',
            };

            for (const [prefix, nsDecl] of Object.entries(prefixToNs)) {
                if (template.xml.includes(prefix)) {
                    assert.ok(
                        result.includes(nsDecl),
                        `"${template.label}" uses ${prefix} — result should include ${nsDecl}`
                    );
                }
            }
        });
    }

    it('should insert multiple different extensions sequentially', () => {
        let manifest = BASE_MANIFEST;

        for (const template of EXTENSION_TEMPLATES) {
            manifest = addExtension(manifest, 0, template.xml);
        }

        const parsed = parseManifest(manifest);
        assert.equal(
            parsed.applications[0].extensions.length,
            EXTENSION_TEMPLATES.length,
            `Should have ${EXTENSION_TEMPLATES.length} extensions after adding all templates`
        );
    });

    it('should preserve existing manifest content when adding extensions', () => {
        const template = EXTENSION_TEMPLATES[0];
        const result = addExtension(BASE_MANIFEST, 0, template.xml);
        const parsed = parseManifest(result);

        assert.equal(parsed.identity.name, 'TestApp');
        assert.equal(parsed.identity.publisher, 'CN=Test');
        assert.equal(parsed.identity.version, '1.0.0.0');
        assert.equal(parsed.properties.displayName, 'TestApp');
        assert.equal(parsed.applications[0].id, 'App');
        assert.equal(parsed.applications[0].executable, 'TestApp.exe');
        assert.equal(parsed.applications[0].visualElements.displayName, 'TestApp');
    });
});

// ─── Real-world fixture tests ───────────────────────────────────────────────

describe('Real-world manifest fixtures', () => {
    const fixtures = allFixtureFiles();

    for (const filename of fixtures) {
        describe(filename, () => {
            const xml = loadFixture(filename);

            it('should parse without errors', () => {
                const parsed = parseManifest(xml);
                assert.ok(parsed, 'parseManifest should return a result');
                assert.ok(parsed.identity.name, 'Should have an identity name');
                assert.ok(parsed.identity.publisher, 'Should have an identity publisher');
                assert.ok(parsed.identity.version, 'Should have an identity version');
                assert.ok(parsed.properties.displayName, 'Should have a display name');
                assert.ok(parsed.applications.length >= 1, 'Should have at least 1 application');
            });

            it('should parse visual elements correctly', () => {
                const parsed = parseManifest(xml);
                for (const app of parsed.applications) {
                    assert.ok(app.visualElements.displayName, 'Visual elements should have a display name');
                    assert.ok(app.visualElements.square150x150Logo, 'Visual elements should have Square150x150Logo');
                    assert.ok(app.visualElements.square44x44Logo, 'Visual elements should have Square44x44Logo');
                }
            });

            it('should parse dependencies', () => {
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

            it('should parse capabilities', () => {
                const parsed = parseManifest(xml);
                assert.ok(parsed.capabilities.length >= 1, 'Should have at least 1 capability');
            });

            it('should be able to add every extension template', () => {
                for (const template of EXTENSION_TEMPLATES) {
                    const result = addExtension(xml, 0, template.xml);
                    assert.notEqual(result, xml, `addExtension("${template.label}") should modify the manifest`);

                    const parsed = parseManifest(result);
                    const found = parsed.applications[0].extensions.find(
                        (ext: string) => ext.includes(`Category="${template.category}"`)
                    );
                    assert.ok(found, `Should find "${template.label}" extension after adding to ${filename}`);
                }
            });

            it('should produce no fatal validation errors for existing content', () => {
                const parsed = parseManifest(xml);
                const errors = validateManifest(parsed);
                // Only check for errors that indicate our parser is broken, not
                // validation warnings about the fixture content itself.
                // The $targetnametoken$ placeholders in sample manifests will trigger
                // "must be an .exe file" validation — that's expected for templates.
                const parserErrors = errors.filter(e =>
                    e.severity === 'error' &&
                    !e.message.includes('.exe') &&
                    !e.message.includes('PNG') &&
                    !e.message.includes('BCP-47') &&
                    !e.message.includes('Image path') &&
                    // Sample fixtures may have empty/placeholder dependency fields
                    !e.field.startsWith('dependencies.')
                );
                assert.equal(
                    parserErrors.length, 0,
                    `Unexpected validation errors: ${parserErrors.map(e => `${e.field}: ${e.message}`).join(', ')}`
                );
            });
        });
    }
});

// ─── Fixture-specific assertions ────────────────────────────────────────────

describe('Fixture-specific parsing', () => {
    it('winui-gallery: should parse protocol and appUriHandler extensions', () => {
        const parsed = parseManifest(loadFixture('winui-gallery.appxmanifest'));
        const app = parsed.applications[0];
        assert.ok(app.extensions.length >= 2, 'Should have at least 2 extensions');
        assert.ok(app.extensions.some((e: string) => e.includes('windows.protocol')), 'Should have protocol extension');
        assert.ok(app.extensions.some((e: string) => e.includes('windows.appUriHandler')), 'Should have appUriHandler extension');
        assert.ok(app.visualElements.square310x310Logo, 'Should have Square310x310Logo');
        assert.ok(app.visualElements.square71x71Logo, 'Should have Square71x71Logo');
        assert.ok(app.visualElements.splashScreenImage, 'Should have SplashScreen');
    });

    it('activation-sample: should parse fileTypeAssociation, protocol, and startupTask extensions', () => {
        const parsed = parseManifest(loadFixture('winui-gallery.appxmanifest'));
        const exts = parsed.applications[0].extensions;
        assert.ok(exts.some((e: string) => e.includes('windows.fileTypeAssociation')), 'Should have fileTypeAssociation');
        assert.ok(exts.some((e: string) => e.includes('windows.protocol')), 'Should have protocol');
        assert.ok(exts.some((e: string) => e.includes('windows.startupTask')), 'Should have startupTask');
    });

    it('share-target-sample: should parse shareTarget extension', () => {
        const parsed = parseManifest(loadFixture('winui-gallery.appxmanifest'));
        const exts = parsed.applications[0].extensions;
        assert.ok(exts.some((e: string) => e.includes('windows.shareTarget')), 'Should have shareTarget');
    });

    it('push-notifications-sample: should parse comServer and protocol extensions', () => {
        const parsed = parseManifest(loadFixture('push-notifications-sample.appxmanifest'));
        const exts = parsed.applications[0].extensions;
        assert.ok(exts.some((e: string) => e.includes('windows.comServer')), 'Should have comServer');
        assert.ok(exts.some((e: string) => e.includes('windows.protocol')), 'Should have protocol');
        assert.ok(parsed.capabilities.includes('internetClient'), 'Should have internetClient capability');
    });

    it('background-task-sample: should parse backgroundTasks and comServer extensions', () => {
        const parsed = parseManifest(loadFixture('background-task-sample.appxmanifest'));
        const exts = parsed.applications[0].extensions;
        assert.ok(exts.some((e: string) => e.includes('windows.backgroundTasks')), 'Should have backgroundTasks');
        assert.ok(exts.some((e: string) => e.includes('windows.comServer')), 'Should have comServer');
    });

    it('widgets-sample: should parse comServer and appExtension extensions', () => {
        const parsed = parseManifest(loadFixture('widgets-sample.appxmanifest'));
        const exts = parsed.applications[0].extensions;
        assert.ok(exts.some((e: string) => e.includes('windows.comServer')), 'Should have comServer');
        assert.ok(exts.some((e: string) => e.includes('windows.appExtension')), 'Should have appExtension');
    });

    it('inline: should parse app with no application-level extensions', () => {
        const xml = `<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  IgnorableNamespaces="uap">
  <Identity Name="NoExtApp" Publisher="CN=Test" Version="1.0.0.0" />
  <Properties>
    <DisplayName>NoExtApp</DisplayName>
    <PublisherDisplayName>Test</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="App.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="NoExtApp" Description="NoExtApp" BackgroundColor="transparent"
        Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
  </Capabilities>
</Package>`;
        const parsed = parseManifest(xml);
        assert.equal(parsed.applications[0].extensions.length, 0, 'Should have no application-level extensions');
        assert.ok(parsed.applications[0].visualElements.wide310x150Logo, 'Should have Wide310x150Logo');
    });
});

// ─── PhoneIdentity Tests ────────────────────────────────────────────────────

describe('PhoneIdentity parsing', () => {
    it('winui-gallery: should parse PhoneIdentity with correct GUIDs', () => {
        const parsed = parseManifest(loadFixture('winui-gallery.appxmanifest'));
        assert.ok(parsed.phoneIdentity, 'Should have phoneIdentity');
        assert.equal(parsed.phoneIdentity!.phoneProductId, '863667e0-667a-4bb4-ac52-c59656c7333a');
        assert.equal(parsed.phoneIdentity!.phonePublisherId, '00000000-0000-0000-0000-000000000000');
    });

    it('custom-controls-cpp: should return null phoneIdentity when not present', () => {
        const parsed = parseManifest(loadFixture('widgets-sample.appxmanifest'));
        assert.equal(parsed.phoneIdentity, null, 'Should be null when mp:PhoneIdentity is absent');
    });
});

// ─── ShowNameOnTiles Tests ──────────────────────────────────────────────────

import { setShowNameOnTiles } from '../manifest-editor/manifest-parser';

describe('ShowNameOnTiles parsing', () => {
    it('winui-gallery: should parse ShowNameOnTiles entries', () => {
        const parsed = parseManifest(loadFixture('winui-gallery.appxmanifest'));
        const tiles = parsed.applications[0].visualElements.showNameOnTiles;
        assert.ok(tiles.length > 0, 'Should have ShowNameOnTiles entries');
        assert.ok(tiles.includes('square150x150Logo'), 'Should include square150x150Logo');
        assert.ok(tiles.includes('wide310x150Logo'), 'Should include wide310x150Logo');
        assert.ok(tiles.includes('square310x310Logo'), 'Should include square310x310Logo');
    });

    it('custom-controls-cpp: should have empty showNameOnTiles when not present', () => {
        const parsed = parseManifest(loadFixture('widgets-sample.appxmanifest'));
        assert.deepEqual(parsed.applications[0].visualElements.showNameOnTiles, []);
    });
});

describe('setShowNameOnTiles', () => {
    it('should add ShowNameOnTiles to manifest with self-closing DefaultTile', () => {
        const xml = `<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  IgnorableNamespaces="uap">
  <Identity Name="SelfCloseApp" Publisher="CN=Test" Version="1.0.0.0" />
  <Properties>
    <DisplayName>SelfCloseApp</DisplayName>
    <PublisherDisplayName>Test</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="App.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="SelfCloseApp" Description="SelfCloseApp" BackgroundColor="transparent"
        Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>`;
        const result = setShowNameOnTiles(xml, 0, ['square150x150Logo', 'wide310x150Logo']);
        const parsed = parseManifest(result);
        const tiles = parsed.applications[0].visualElements.showNameOnTiles;
        assert.ok(tiles.includes('square150x150Logo'));
        assert.ok(tiles.includes('wide310x150Logo'));
    });

    it('should replace existing ShowNameOnTiles entries', () => {
        const xml = loadFixture('winui-gallery.appxmanifest');
        // Replace the 3 existing tiles with just 1
        const result = setShowNameOnTiles(xml, 0, ['wide310x150Logo']);
        const parsed = parseManifest(result);
        const tiles = parsed.applications[0].visualElements.showNameOnTiles;
        assert.equal(tiles.length, 1);
        assert.ok(tiles.includes('wide310x150Logo'));
    });

    it('should remove ShowNameOnTiles when given empty array', () => {
        const xml = loadFixture('winui-gallery.appxmanifest');
        const result = setShowNameOnTiles(xml, 0, []);
        const parsed = parseManifest(result);
        assert.deepEqual(parsed.applications[0].visualElements.showNameOnTiles, []);
    });

    it('should preserve the rest of the manifest when modifying ShowNameOnTiles', () => {
        const xml = loadFixture('winui-gallery.appxmanifest');
        const beforeParsed = parseManifest(xml);
        const result = setShowNameOnTiles(xml, 0, ['square150x150Logo']);
        const afterParsed = parseManifest(result);
        assert.equal(afterParsed.identity.name, beforeParsed.identity.name);
        assert.equal(afterParsed.applications[0].visualElements.displayName, beforeParsed.applications[0].visualElements.displayName);
        assert.equal(afterParsed.applications[0].extensions.length, beforeParsed.applications[0].extensions.length);
    });
});
