/**
 * Unit tests for xml-utils.ts low-level XML manipulation utilities.
 *
 * Run: npx tsx --test src/test/xml-utils.test.ts
 */

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    escapeRegex,
    escapeXmlAttr,
    escapeXmlText,
    replaceAttribute,
    removeAttribute,
    addAttributeToElement,
    replaceElementText,
    findParentBounds,
    findDirectChildElementBounds,
    ensureNamespace,
    swapAdjacentElements,
    detectIndent,
    getCapabilityElementInfo,
    buildVisualChildElement,
} from '../manifest-editor/xml-utils';
import { insertChildBeforeClose } from '../manifest-editor/manifest-xml-ops';

// ---------------------------------------------------------------------------
// escapeRegex
// ---------------------------------------------------------------------------
describe('escapeRegex', () => {
    it('escapes special regex characters', () => {
        assert.equal(escapeRegex('a.b*c+d?e'), 'a\\.b\\*c\\+d\\?e');
        assert.equal(escapeRegex('foo[bar]'), 'foo\\[bar\\]');
        assert.equal(escapeRegex('a{1}|b$^c'), 'a\\{1\\}\\|b\\$\\^c');
    });

    it('leaves normal text unchanged', () => {
        assert.equal(escapeRegex('hello world'), 'hello world');
    });
});

// ---------------------------------------------------------------------------
// escapeXmlAttr
// ---------------------------------------------------------------------------
describe('escapeXmlAttr', () => {
    it('escapes ampersand, angle brackets, quotes', () => {
        assert.equal(escapeXmlAttr('a&b<c>d"e\'f'), 'a&amp;b&lt;c&gt;d&quot;e&apos;f');
    });

    it('leaves safe text unchanged', () => {
        assert.equal(escapeXmlAttr('hello'), 'hello');
    });
});

// ---------------------------------------------------------------------------
// escapeXmlText
// ---------------------------------------------------------------------------
describe('escapeXmlText', () => {
    it('escapes ampersand and angle brackets', () => {
        assert.equal(escapeXmlText('a&b<c>d'), 'a&amp;b&lt;c&gt;d');
    });

    it('does not escape quotes', () => {
        assert.equal(escapeXmlText('"hello"'), '"hello"');
    });
});

// ---------------------------------------------------------------------------
// replaceAttribute
// ---------------------------------------------------------------------------
describe('replaceAttribute', () => {
    const xml = '<Root><Identity Name="OldName" Version="1.0.0.0" /></Root>';
    const pattern = /(<Identity\b[^>]*>)/;

    it('replaces an attribute value', () => {
        const result = replaceAttribute(xml, pattern, 'Name', 'NewName');
        assert.equal(result, '<Root><Identity Name="NewName" Version="1.0.0.0" /></Root>');
    });

    it('returns original when element not found', () => {
        const result = replaceAttribute(xml, /(<Missing\b[^>]*>)/, 'Name', 'X');
        assert.equal(result, xml);
    });

    it('returns original when attribute not found', () => {
        const result = replaceAttribute(xml, pattern, 'NoSuchAttr', 'X');
        assert.equal(result, xml);
    });

    it('escapes special characters in the new value', () => {
        const result = replaceAttribute(xml, pattern, 'Name', 'A&B<C>"D');
        assert.ok(result.includes('Name="A&amp;B&lt;C&gt;&quot;D"'));
    });
});

// ---------------------------------------------------------------------------
// removeAttribute
// ---------------------------------------------------------------------------
describe('removeAttribute', () => {
    const xml = '<Root><Identity Name="App" Version="1.0" /></Root>';
    const pattern = /(<Identity\b[^>]*\/>)/;

    it('removes an attribute', () => {
        const result = removeAttribute(xml, pattern, 'Version');
        assert.ok(!result.includes('Version'));
        assert.ok(result.includes('Name="App"'));
    });

    it('returns original when attribute not found', () => {
        const result = removeAttribute(xml, pattern, 'Missing');
        assert.equal(result, xml);
    });
});

// ---------------------------------------------------------------------------
// addAttributeToElement
// ---------------------------------------------------------------------------
describe('addAttributeToElement', () => {
    it('adds attribute to single-line element', () => {
        const xml = '<Root><Identity Name="App" /></Root>';
        const result = addAttributeToElement(xml, /(<Identity\b[^>]*\/>)/, 'Version', '2.0');
        assert.ok(result.includes('Version="2.0"'));
        assert.ok(result.includes('Name="App"'));
    });

    it('adds attribute on new line for multi-line element', () => {
        const xml = '<Root>\n  <Identity\n    Name="App"\n  />\n</Root>';
        const result = addAttributeToElement(xml, /(<Identity\b[^>]*\/>)/s, 'Version', '3.0');
        assert.ok(result.includes('Version="3.0"'));
        // Should be on its own indented line
        assert.ok(result.includes('\n    Version="3.0"'));
    });

    it('returns original when element not found', () => {
        const xml = '<Root />';
        const result = addAttributeToElement(xml, /(<Missing\b[^>]*>)/, 'X', 'Y');
        assert.equal(result, xml);
    });
});

// ---------------------------------------------------------------------------
// replaceElementText
// ---------------------------------------------------------------------------
describe('replaceElementText', () => {
    it('replaces text content and escapes special chars', () => {
        const xml = '<Package><DisplayName>OldName</DisplayName></Package>';
        const pattern = /(<DisplayName>)(.*?)(<\/DisplayName>)/;
        const result = replaceElementText(xml, pattern, 'A&B<C>');
        assert.equal(result, '<Package><DisplayName>A&amp;B&lt;C&gt;</DisplayName></Package>');
    });

    it('returns original when tag not found', () => {
        const xml = '<Package><Other>Text</Other></Package>';
        const pattern = /(<Missing>)(.*?)(<\/Missing>)/;
        assert.equal(replaceElementText(xml, pattern, 'X'), xml);
    });
});

// ---------------------------------------------------------------------------
// findParentBounds
// ---------------------------------------------------------------------------
describe('findParentBounds', () => {
    it('finds bounds of a parent element', () => {
        const xml = '<Root><Applications><App /></Applications></Root>';
        const bounds = findParentBounds(xml, 'Applications');
        assert.ok(bounds !== null);
        assert.equal(xml.substring(bounds!.contentStart, bounds!.contentEnd), '<App />');
    });

    it('returns null when element not found', () => {
        assert.equal(findParentBounds('<Root />', 'Missing'), null);
    });

    it('returns null for self-closing element', () => {
        assert.equal(findParentBounds('<Root><Applications /></Root>', 'Applications'), null);
    });
});

// ---------------------------------------------------------------------------
// findDirectChildElementBounds
// ---------------------------------------------------------------------------
describe('findDirectChildElementBounds', () => {
    it('finds self-closing and non-self-closing children', () => {
        const xml = '<Parent><A /><B>text</B><C /></Parent>';
        const bounds = findParentBounds(xml, 'Parent')!;
        const children = findDirectChildElementBounds(xml, bounds.contentStart, bounds.contentEnd);
        assert.equal(children.length, 3);
        assert.ok(xml.substring(children[0].start, children[0].end).startsWith('<A'));
        assert.ok(xml.substring(children[1].start, children[1].end).includes('</B>'));
    });

    it('skips XML comments', () => {
        const xml = '<Parent><!-- comment --><A /></Parent>';
        const bounds = findParentBounds(xml, 'Parent')!;
        const children = findDirectChildElementBounds(xml, bounds.contentStart, bounds.contentEnd);
        assert.equal(children.length, 1);
    });

    it('skips CDATA sections', () => {
        const xml = '<Parent><![CDATA[<not an element>]]><A /></Parent>';
        const bounds = findParentBounds(xml, 'Parent')!;
        const children = findDirectChildElementBounds(xml, bounds.contentStart, bounds.contentEnd);
        assert.equal(children.length, 1);
    });
});

// ---------------------------------------------------------------------------
// ensureNamespace
// ---------------------------------------------------------------------------
describe('ensureNamespace', () => {
    it('returns unchanged when namespace already present', () => {
        const xml = '<Package xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">\n</Package>';
        const result = ensureNamespace(xml, 'uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10');
        assert.equal(result, xml);
    });

    it('adds namespace to multiline Package tag', () => {
        const xml = '<Package\n  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"\n  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"\n>\n</Package>';
        const result = ensureNamespace(xml, 'rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities');
        assert.ok(result.includes('xmlns:rescap='));
    });

    it('adds namespace to single-line Package tag', () => {
        const xml = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"></Package>';
        const result = ensureNamespace(xml, 'uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10');
        assert.ok(result.includes('xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"'));
    });
});

// ---------------------------------------------------------------------------
// swapAdjacentElements
// ---------------------------------------------------------------------------
describe('swapAdjacentElements', () => {
    it('swaps two adjacent elements', () => {
        const xml = '<Root><A /><B /></Root>';
        const a = { start: 6, end: 11 }; // <A />
        const b = { start: 11, end: 16 }; // <B />
        const result = swapAdjacentElements(xml, a, b);
        assert.equal(result, '<Root><B /><A /></Root>');
    });

    it('handles reversed order (a.start > b.start)', () => {
        const xml = '<Root><A /><B /></Root>';
        const a = { start: 11, end: 16 }; // <B />
        const b = { start: 6, end: 11 }; // <A />
        const result = swapAdjacentElements(xml, a, b);
        assert.equal(result, '<Root><B /><A /></Root>');
    });
});

// ---------------------------------------------------------------------------
// detectIndent
// ---------------------------------------------------------------------------
describe('detectIndent', () => {
    it('detects indentation of the line at the given position', () => {
        const xml = '<Root>\n    <Child />\n</Root>';
        // pos of '<Child' is index 11 (after \n and 4 spaces)
        const childPos = xml.indexOf('<Child');
        assert.equal(detectIndent(xml, childPos), '    ');
    });

    it('returns empty string when at start of file', () => {
        const xml = '<Root />';
        assert.equal(detectIndent(xml, 0), '');
    });
});

// ---------------------------------------------------------------------------
// getCapabilityElementInfo
// ---------------------------------------------------------------------------
describe('getCapabilityElementInfo', () => {
    it('handles device: prefix', () => {
        const info = getCapabilityElementInfo('device:microphone');
        assert.equal(info.elementName, 'DeviceCapability');
        assert.equal(info.attrName, 'microphone');
    });

    it('handles ns:name prefix', () => {
        const info = getCapabilityElementInfo('uap:appointments');
        assert.equal(info.elementName, 'uap:Capability');
        assert.equal(info.attrName, 'appointments');
    });

    it('handles custom capability pattern', () => {
        const info = getCapabilityElementInfo('company.name.cap_1234567890abc');
        assert.equal(info.elementName, 'uap4:CustomCapability');
        assert.equal(info.attrName, 'company.name.cap_1234567890abc');
    });

    it('handles plain capability', () => {
        const info = getCapabilityElementInfo('internetClient');
        assert.equal(info.elementName, 'Capability');
        assert.equal(info.attrName, 'internetClient');
    });
});

// ---------------------------------------------------------------------------
// buildVisualChildElement
// ---------------------------------------------------------------------------
describe('buildVisualChildElement', () => {
    it('builds wide310x150Logo element', () => {
        const result = buildVisualChildElement('wide310x150Logo', 'Assets\\Wide.png');
        assert.equal(result, '<uap:DefaultTile Wide310x150Logo="Assets\\Wide.png" />');
    });

    it('builds badgeLogo element', () => {
        const result = buildVisualChildElement('badgeLogo', 'Assets\\Badge.png');
        assert.equal(result, '<uap:LockScreen Notification="badge" BadgeLogo="Assets\\Badge.png" />');
    });

    it('builds splashScreenImage element', () => {
        const result = buildVisualChildElement('splashScreenImage', 'Assets\\Splash.png');
        assert.equal(result, '<uap:SplashScreen Image="Assets\\Splash.png" />');
    });

    it('returns null for unknown field', () => {
        assert.equal(buildVisualChildElement('unknownField', 'value'), null);
    });
});

// ---------------------------------------------------------------------------
// insertChildBeforeClose
// ---------------------------------------------------------------------------
describe('insertChildBeforeClose', () => {
    it('inserts child element with proper indentation', () => {
        const xml = '<Parent>\n  <Existing />\n  </Parent>';
        const closePos = xml.indexOf('</Parent>');
        const result = insertChildBeforeClose(xml, closePos, '<New />', '  ');
        assert.ok(result.includes('    <New />'));
        assert.ok(result.includes('</Parent>'));
        // Child should appear before the close tag
        const newIdx = result.indexOf('<New />');
        const closeIdx = result.indexOf('</Parent>');
        assert.ok(newIdx < closeIdx);
    });
});
