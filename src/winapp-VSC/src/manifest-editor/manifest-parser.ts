/**
 * Parse and modify appxmanifest.xml using @xmldom/xmldom.
 * Reads XML into ManifestData for the form, and applies edits back to the XML text.
 */

import { DOMParser, XMLSerializer } from '@xmldom/xmldom';
import type { Element, Document } from '@xmldom/xmldom';
import {
    ManifestData,
    IdentityData,
    PhoneIdentityData,
    PropertiesData,
    DependenciesData,
    TargetDeviceFamilyData,
    PackageDependencyData,
    ApplicationData,
    VisualElementsData,
    ResourceData,
} from './manifest-types';

// Common AppxManifest namespace URIs
const NS = {
    default: 'http://schemas.microsoft.com/appx/manifest/foundation/windows10',
    uap: 'http://schemas.microsoft.com/appx/manifest/uap/windows10',
    rescap: 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities',
    desktop: 'http://schemas.microsoft.com/appx/manifest/desktop/windows10',
};

/**
 * Parse appxmanifest.xml text into a ManifestData object.
 *
 * NOTE: Package-level <Extensions> (outside <Applications>) are not yet
 * parsed or editable. They are preserved in the XML but not surfaced in the
 * editor UI. Common package-level extensions include
 * windows.activatableClass.inProcessServer and background task host DLLs.
 * See: https://github.com/microsoft/winappCli/issues
 */
export function parseManifest(xmlText: string): ManifestData {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement;
    if (!root) { throw new Error('Invalid XML: no root element'); }

    return {
        identity: parseIdentity(root),
        phoneIdentity: parsePhoneIdentity(root),
        properties: parseProperties(root),
        dependencies: parseDependencies(root),
        applications: parseApplications(root),
        capabilities: parseCapabilities(root),
        resources: parseResources(root),
    };
}

/**
 * Apply a field change to the XML text and return the updated XML string.
 * Uses surgical string replacements to preserve original formatting.
 * Falls back to DOM parse/serialize only when a new element must be created.
 */
export function applyFieldChange(
    xmlText: string,
    section: string,
    field: string,
    value: string,
    index?: number,
): string {
    const idx = index ?? 0;

    switch (section) {
        case 'identity':
            return applyIdentityChangeString(xmlText, field, value);
        case 'phoneIdentity':
            return applyPhoneIdentityChangeString(xmlText, field, value);
        case 'properties':
            return applyPropertiesChangeString(xmlText, field, value);
        case 'dependencies':
            return applyDependenciesChangeString(xmlText, field, value, idx);
        case 'applications':
            return applyApplicationChangeString(xmlText, field, value, idx);
        case 'resources':
            return applyResourcesChangeString(xmlText, field, value, idx);
        default:
            return xmlText;
    }
}

/** Add a capability element to the XML. */
export function addCapability(xmlText: string, capability: string): string {
    const { elementName, attrName } = getCapabilityElementInfo(capability);
    const childXml = `<${elementName} Name="${attrName}" />`;

    let result = xmlText;

    // Ensure rescap namespace is declared if adding a restricted capability
    if (capability.startsWith('rescap:')) {
        const rescapDecl = 'xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"';
        if (!result.includes(rescapDecl)) {
            result = result.replace(/<Package\b/, '<Package ' + rescapDecl);
        }
    }

    const bounds = findParentBounds(result, 'Capabilities');
    if (bounds) {
        const parentIndent = detectIndent(result, bounds.openStart);
        return insertChildBeforeClose(result, bounds.contentEnd, childXml, parentIndent);
    }

    // No Capabilities element — create one before </Package>
    const pkgClose = result.lastIndexOf('</Package>');
    if (pkgClose < 0) { return result; }
    const pkgIndent = detectIndent(result, pkgClose);
    const parentIndent = pkgIndent + '  ';
    const block = parentIndent + '<Capabilities>\n' +
        parentIndent + '  ' + childXml + '\n' +
        parentIndent + '</Capabilities>\n';
    let lineStart = pkgClose;
    while (lineStart > 0 && result[lineStart - 1] !== '\n') { lineStart--; }
    return result.substring(0, lineStart) + block + result.substring(lineStart);
}

/** Remove a capability element from the XML. */
export function removeCapability(xmlText: string, capability: string): string {
    const bounds = findParentBounds(xmlText, 'Capabilities');
    if (!bounds) { return xmlText; }

    const { attrName, namespace: capNs } = parseCapabilityString(capability);
    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);

    // Search backwards (last match first, same as original behavior)
    for (let i = children.length - 1; i >= 0; i--) {
        const child = children[i];
        const childXml = xmlText.substring(child.start, child.end);
        if (!matchesCapabilityTag(childXml, capNs)) { continue; }
        if (!hasNameAttribute(childXml, attrName)) { continue; }
        return removeElementWithWhitespace(xmlText, child.start, child.end, bounds.contentStart);
    }

    return xmlText;
}

/** Add a PackageDependency element. */
export function addPackageDependency(xmlText: string, dep: PackageDependencyData): string {
    let attrs = `Name="${dep.name}"`;
    if (dep.minVersion) { attrs += ` MinVersion="${dep.minVersion}"`; }
    if (dep.publisher) { attrs += ` Publisher="${dep.publisher}"`; }
    const childXml = `<PackageDependency ${attrs} />`;

    const bounds = findParentBounds(xmlText, 'Dependencies');
    if (bounds) {
        const parentIndent = detectIndent(xmlText, bounds.openStart);
        return insertChildBeforeClose(xmlText, bounds.contentEnd, childXml, parentIndent);
    }

    // No Dependencies element — create one before </Package>
    const pkgClose = xmlText.lastIndexOf('</Package>');
    if (pkgClose < 0) { return xmlText; }
    const pkgIndent = detectIndent(xmlText, pkgClose);
    const parentIndent = pkgIndent + '  ';
    const block = parentIndent + '<Dependencies>\n' +
        parentIndent + '  ' + childXml + '\n' +
        parentIndent + '</Dependencies>\n';
    let lineStart = pkgClose;
    while (lineStart > 0 && xmlText[lineStart - 1] !== '\n') { lineStart--; }
    return xmlText.substring(0, lineStart) + block + xmlText.substring(lineStart);
}

/** Remove a PackageDependency element by index. */
export function removePackageDependency(xmlText: string, index: number): string {
    const bounds = findParentBounds(xmlText, 'Dependencies');
    if (!bounds) { return xmlText; }

    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const pkgDeps = children.filter(c => /^<PackageDependency\b/.test(xmlText.substring(c.start, c.end)));
    if (index < 0 || index >= pkgDeps.length) { return xmlText; }

    return removeElementWithWhitespace(xmlText, pkgDeps[index].start, pkgDeps[index].end, bounds.contentStart);
}

/** Add a TargetDeviceFamily element. */
export function addTargetDeviceFamily(xmlText: string, family: TargetDeviceFamilyData): string {
    const childXml = `<TargetDeviceFamily Name="${family.name}" MinVersion="${family.minVersion}" MaxVersionTested="${family.maxVersionTested}" />`;

    const bounds = findParentBounds(xmlText, 'Dependencies');
    if (bounds) {
        const parentIndent = detectIndent(xmlText, bounds.openStart);
        return insertChildBeforeClose(xmlText, bounds.contentEnd, childXml, parentIndent);
    }

    // No Dependencies element — create one before </Package>
    const pkgClose = xmlText.lastIndexOf('</Package>');
    if (pkgClose < 0) { return xmlText; }
    const pkgIndent = detectIndent(xmlText, pkgClose);
    const parentIndent = pkgIndent + '  ';
    const block = parentIndent + '<Dependencies>\n' +
        parentIndent + '  ' + childXml + '\n' +
        parentIndent + '</Dependencies>\n';
    let lineStart = pkgClose;
    while (lineStart > 0 && xmlText[lineStart - 1] !== '\n') { lineStart--; }
    return xmlText.substring(0, lineStart) + block + xmlText.substring(lineStart);
}

/** Remove a TargetDeviceFamily element by index. */
export function removeTargetDeviceFamily(xmlText: string, index: number): string {
    const bounds = findParentBounds(xmlText, 'Dependencies');
    if (!bounds) { return xmlText; }

    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const families = children.filter(c => /^<TargetDeviceFamily\b/.test(xmlText.substring(c.start, c.end)));
    if (index < 0 || index >= families.length) { return xmlText; }

    return removeElementWithWhitespace(xmlText, families[index].start, families[index].end, bounds.contentStart);
}

/** Add a Resource element to the XML. */
export function addResource(xmlText: string, resource: ResourceData): string {
    let attrs = '';
    if (resource.language) { attrs = ` Language="${resource.language}"`; }
    const childXml = `<Resource${attrs} />`;

    const bounds = findParentBounds(xmlText, 'Resources');
    if (bounds) {
        const parentIndent = detectIndent(xmlText, bounds.openStart);
        return insertChildBeforeClose(xmlText, bounds.contentEnd, childXml, parentIndent);
    }

    // No Resources element — create one before </Package>
    const pkgClose = xmlText.lastIndexOf('</Package>');
    if (pkgClose < 0) { return xmlText; }
    const pkgIndent = detectIndent(xmlText, pkgClose);
    const parentIndent = pkgIndent + '  ';
    const block = parentIndent + '<Resources>\n' +
        parentIndent + '  ' + childXml + '\n' +
        parentIndent + '</Resources>\n';
    let lineStart = pkgClose;
    while (lineStart > 0 && xmlText[lineStart - 1] !== '\n') { lineStart--; }
    return xmlText.substring(0, lineStart) + block + xmlText.substring(lineStart);
}

/** Remove a Resource element by index. */
export function removeResource(xmlText: string, index: number): string {
    const bounds = findParentBounds(xmlText, 'Resources');
    if (!bounds) { return xmlText; }

    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const resources = children.filter(c => /^<Resource\b/.test(xmlText.substring(c.start, c.end)));
    if (index < 0 || index >= resources.length) { return xmlText; }

    return removeElementWithWhitespace(xmlText, resources[index].start, resources[index].end, bounds.contentStart);
}

/** Set the ShowNameOnTiles entries for an application by index.
 *  `tiles` is an array of tile values like ['square150x150Logo', 'wide310x150Logo'].
 *  An empty array removes ShowNameOnTiles entirely. */
export function setShowNameOnTiles(xmlText: string, appIndex: number, tiles: string[]): string {
    let xml = xmlText;

    // Find the nth Application's VisualElements
    const vePattern = /<[a-zA-Z0-9]*:?VisualElements\b[^>]*(?:\/>|>)/gs;
    let veMatch: RegExpExecArray | null;
    let count = 0;
    let veMatchResult: RegExpExecArray | null = null;
    while ((veMatch = vePattern.exec(xml)) !== null) {
        if (count === appIndex) { veMatchResult = veMatch; break; }
        count++;
    }
    if (!veMatchResult) { return xml; }

    // Find the DefaultTile within this Application's scope
    const veStart = veMatchResult.index;
    // Find the end of VisualElements (closing tag)
    const veClosePattern = /<\/[a-zA-Z0-9]*:?VisualElements\s*>/;
    const afterVe = xml.substring(veStart);
    const veCloseMatch = veClosePattern.exec(afterVe);
    const veEndPos = veCloseMatch ? veStart + veCloseMatch.index + veCloseMatch[0].length : xml.length;
    const veBlock = xml.substring(veStart, veEndPos);

    // Check if DefaultTile exists in this VisualElements block
    const dtPattern = /<[a-zA-Z0-9]*:?DefaultTile\b/;
    const hasDT = dtPattern.test(veBlock);

    if (!hasDT) {
        if (tiles.length === 0) { return xml; }
        // No DefaultTile yet — create one with ShowNameOnTiles inside, before </VisualElements>
        if (!veCloseMatch) { return xml; }
        const veIndentMatch = xml.substring(0, veStart).match(/([ \t]*)$/);
        const veIndent = veIndentMatch ? veIndentMatch[1] : '        ';
        const dtIndent = veIndent + '  ';
        const childIndent = dtIndent + '  ';
        const showOnIndent = childIndent + '  ';
        let showNameXml = childIndent + '<uap:ShowNameOnTiles>\n';
        for (const tile of tiles) {
            showNameXml += showOnIndent + `<uap:ShowOn Tile="${tile}" />\n`;
        }
        showNameXml += childIndent + '</uap:ShowNameOnTiles>\n';
        const newDt = dtIndent + '<uap:DefaultTile>\n' + showNameXml + dtIndent + '</uap:DefaultTile>\n';
        // Insert before the line containing </VisualElements>
        const closeAbsPos = veStart + veCloseMatch.index;
        let lineStart = closeAbsPos;
        while (lineStart > 0 && xml[lineStart - 1] !== '\n') { lineStart--; }
        xml = xml.substring(0, lineStart) + newDt + xml.substring(lineStart);
        return xml;
    }

    // Find the existing ShowNameOnTiles block within this VE block (if any)
    const showNamePattern = /[ \t]*<[a-zA-Z0-9]*:?ShowNameOnTiles\b[\s\S]*?<\/[a-zA-Z0-9]*:?ShowNameOnTiles\s*>\s*/;
    const showNameMatch = showNamePattern.exec(veBlock);

    if (tiles.length === 0) {
        // Remove existing ShowNameOnTiles if present
        if (showNameMatch) {
            const absStart = veStart + showNameMatch.index;
            // Include preceding newline
            let removeStart = absStart;
            if (removeStart > 0 && xml[removeStart - 1] === '\n') { removeStart--; }
            xml = xml.substring(0, removeStart) + xml.substring(absStart + showNameMatch[0].length);

            // Check if DefaultTile now has no children — convert back to self-closing
            xml = collapseEmptyDefaultTile(xml, appIndex);
        }
        return xml;
    }

    // Build ShowNameOnTiles XML
    const dtIndentMatch = veBlock.match(/\n([ \t]*)<[a-zA-Z0-9]*:?DefaultTile\b/);
    const dtIndent = dtIndentMatch ? dtIndentMatch[1] : '          ';
    const childIndent = dtIndent + '  ';
    const showOnIndent = childIndent + '  ';

    let showNameXml = childIndent + '<uap:ShowNameOnTiles>\n';
    for (const tile of tiles) {
        showNameXml += showOnIndent + `<uap:ShowOn Tile="${tile}" />\n`;
    }
    showNameXml += childIndent + '</uap:ShowNameOnTiles>';

    if (showNameMatch) {
        // Replace existing ShowNameOnTiles — match includes leading [ \t]* and trailing \s*
        const absStart = veStart + showNameMatch.index;
        const absEnd = absStart + showNameMatch[0].length;
        xml = xml.substring(0, absStart) + showNameXml + '\n' + dtIndent + xml.substring(absEnd);
    } else {
        // Insert ShowNameOnTiles — need to handle self-closing vs open DefaultTile
        const dtSelfClose = /<([a-zA-Z0-9]*:?DefaultTile)\b([^>]*?)\/>/s;
        const dtSelfMatch = dtSelfClose.exec(veBlock);
        if (dtSelfMatch) {
            // Convert self-closing DefaultTile to open/close with ShowNameOnTiles inside
            const absPos = veStart + dtSelfMatch.index;
            const prefix = dtSelfMatch[1];
            const attrs = dtSelfMatch[2];
            const newDt = `<${prefix}${attrs}>\n` +
                showNameXml + '\n' +
                dtIndent + `</${prefix}>`;
            xml = xml.substring(0, absPos) + newDt + xml.substring(absPos + dtSelfMatch[0].length);
        } else {
            // Open DefaultTile — insert before closing tag
            const dtClosePattern = /<\/[a-zA-Z0-9]*:?DefaultTile\s*>/;
            const dtCloseMatch = dtClosePattern.exec(veBlock);
            if (dtCloseMatch) {
                const absPos = veStart + dtCloseMatch.index;
                xml = xml.substring(0, absPos) + showNameXml + '\n' + dtIndent + xml.substring(absPos);
            }
        }
    }

    return xml;
}

/** If DefaultTile is open/close but has no child elements, convert to self-closing. */
function collapseEmptyDefaultTile(xml: string, appIndex: number): string {
    const vePattern = /<[a-zA-Z0-9]*:?VisualElements\b[^>]*(?:\/>|>)/gs;
    let veMatch: RegExpExecArray | null;
    let count = 0;
    while ((veMatch = vePattern.exec(xml)) !== null) {
        if (count === appIndex) { break; }
        count++;
    }
    if (!veMatch || count !== appIndex) { return xml; }

    const veStart = veMatch.index;
    const veClosePattern = /<\/[a-zA-Z0-9]*:?VisualElements\s*>/;
    const afterVe = xml.substring(veStart);
    const veCloseMatch = veClosePattern.exec(afterVe);
    const veEndPos = veCloseMatch ? veStart + veCloseMatch.index + veCloseMatch[0].length : xml.length;
    const veBlock = xml.substring(veStart, veEndPos);

    // Match open/close DefaultTile with only whitespace inside
    const emptyDtPattern = /(<([a-zA-Z0-9]*:?DefaultTile)\b[^>]*)>\s*<\/\2\s*>/s;
    const emptyDtMatch = emptyDtPattern.exec(veBlock);
    if (emptyDtMatch) {
        const absPos = veStart + emptyDtMatch.index;
        const selfClosing = emptyDtMatch[1] + ' />';
        xml = xml.substring(0, absPos) + selfClosing + xml.substring(absPos + emptyDtMatch[0].length);
    }

    return xml;
}

/** Add an extension element to an application by index. */
/** Add a new Application element to the manifest. */
export function addApplication(xmlText: string): string {
    let result = xmlText;

    // Ensure uap namespace is declared
    const uapDecl = 'xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"';
    if (!result.includes(uapDecl)) {
        result = result.replace(/<Package\b/, '<Package ' + uapDecl);
    }

    // Detect indentation from existing Application elements
    const appIndentMatch = result.match(/^(\s+)<Application\b/m);
    const appIndent = appIndentMatch ? appIndentMatch[1] : '    ';
    const childIndent = appIndent + '  ';

    const template =
        appIndent + '<Application Id="" Executable="" EntryPoint="Windows.FullTrustApplication">\n' +
        childIndent + '<uap:VisualElements DisplayName="" Description="" BackgroundColor="transparent" Square150x150Logo="" Square44x44Logo="" />\n' +
        appIndent + '</Application>';

    // Insert before closing </Applications>
    const closeTag = '</Applications>';
    const closeIdx = result.lastIndexOf(closeTag);
    if (closeIdx < 0) { return result; }

    // Detect indent of </Applications> from its line
    const lineStart = result.lastIndexOf('\n', closeIdx - 1);
    const appsIndent = lineStart >= 0 ? result.substring(lineStart + 1, closeIdx).match(/^(\s*)/)?.[1] ?? '  ' : '  ';

    const before = result.substring(0, closeIdx).replace(/\s+$/, '');
    return before + '\n' + template + '\n' + appsIndent + result.substring(closeIdx);
}

/** Remove an Application element from the manifest. */
export function removeApplication(xmlText: string, index: number): string {
    const bounds = findParentBounds(xmlText, 'Applications');
    if (!bounds) { return xmlText; }

    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const apps = children.filter(c => /^<Application\b/.test(xmlText.substring(c.start, c.end)));
    if (index < 0 || index >= apps.length || apps.length <= 1) { return xmlText; }

    return removeElementWithWhitespace(xmlText, apps[index].start, apps[index].end, bounds.contentStart);
}

export function addExtension(xmlText: string, appIndex: number, extensionXml: string): string {
    // Determine if the target Application has an <Extensions> block (string-based check)
    let hasExtensions = false;
    const appOpenRegex = /<Application\b/g;
    let appOpenMatch: RegExpExecArray | null;
    let appOpenCount = 0;
    let targetAppStart = -1;
    while ((appOpenMatch = appOpenRegex.exec(xmlText)) !== null) {
        if (appOpenCount === appIndex) { targetAppStart = appOpenMatch.index; break; }
        appOpenCount++;
    }
    if (targetAppStart < 0) { return xmlText; }
    const appCloseTagStr = '</Application>';
    let targetAppClose = -1;
    let acCount = 0;
    let acFrom = 0;
    while (acCount <= appIndex) {
        targetAppClose = xmlText.indexOf(appCloseTagStr, acFrom);
        if (targetAppClose < 0) { return xmlText; }
        if (acCount === appIndex) { break; }
        acFrom = targetAppClose + appCloseTagStr.length;
        acCount++;
    }
    hasExtensions = xmlText.substring(targetAppStart, targetAppClose).includes('<Extensions>');

    let result = xmlText;

    // Ensure required namespace declarations are on the root <Package> element
    const nsMap: Record<string, string> = {
        'com:': 'xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"',
        'uap:': 'xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"',
        'uap3:': 'xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"',
        'uap5:': 'xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"',
        'desktop:': 'xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"',
    };
    for (const [prefix, decl] of Object.entries(nsMap)) {
        if (extensionXml.includes(prefix) && !result.includes(decl)) {
            // Insert the namespace declaration into the <Package> opening tag
            result = result.replace(/<Package\b/, '<Package ' + decl);
        }
    }

    // Detect the file's indentation by looking at existing content
    let indentMatch = result.match(/^( +)<Extensions>/m);
    if (!indentMatch) {
        // No existing Extensions — derive from Application indent + 2 spaces
        const appIndentMatch = result.match(/^( +)<Application\b/m);
        indentMatch = appIndentMatch ? [, appIndentMatch[1] + '  '] as unknown as RegExpMatchArray : null;
    }
    const extIndent = indentMatch?.[1] ?? '      ';
    const childIndent = extIndent + '  ';
    // Preserve the template's relative indentation, just add the base indent
    const indentedExt = extensionXml.split('\n').map(line => childIndent + line).join('\n');

    if (hasExtensions) {
        // Insert before the closing </Extensions> tag that belongs to this Application.
        // We find the nth </Application> and search backwards from there to locate
        // the correct </Extensions> (avoids hitting package-level Extensions).
        const closeTag = '</Extensions>';
        const closeAppTag = '</Application>';
        let appCloseIdx = -1;
        let appCount = 0;
        let searchFrom = 0;
        while (appCount <= appIndex) {
            appCloseIdx = result.indexOf(closeAppTag, searchFrom);
            if (appCloseIdx < 0) { return result; }
            if (appCount === appIndex) { break; }
            searchFrom = appCloseIdx + closeAppTag.length;
            appCount++;
        }
        const closeIdx = result.lastIndexOf(closeTag, appCloseIdx);
        if (closeIdx < 0) { return result; }
        // Trim trailing whitespace before the close tag so we don't double-indent
        const beforeClose = result.substring(0, closeIdx).replace(/\s+$/, '');
        return beforeClose + '\n' +
            indentedExt + '\n' + extIndent +
            result.substring(closeIdx);
    } else {
        // Insert a new <Extensions> block before the closing </Application> tag
        let closeIdx = -1;
        let count = 0;
        let searchFrom = 0;
        const closeAppTag = '</Application>';
        while (count <= appIndex) {
            closeIdx = result.indexOf(closeAppTag, searchFrom);
            if (closeIdx < 0) { return result; }
            if (count === appIndex) { break; }
            searchFrom = closeIdx + closeAppTag.length;
            count++;
        }

        // Detect application indent from the whitespace before </Application>
        const lineStart = result.lastIndexOf('\n', closeIdx - 1);
        const appIndent = lineStart >= 0 ? result.substring(lineStart + 1, closeIdx).match(/^(\s*)/)?.[1] ?? '    ' : '    ';
        const extBlockIndent = appIndent + '  ';
        // Trim trailing whitespace before </Application> since block includes its own indent
        const before = result.substring(0, closeIdx).replace(/\s+$/, '');
        const block = '\n' + extBlockIndent + '<Extensions>\n' +
            indentedExt + '\n' +
            extBlockIndent + '</Extensions>\n' +
            appIndent;
        return before + block + result.substring(closeIdx);
    }
}

/** Remove an extension element from an application (string-based to preserve formatting). */
export function removeExtension(xmlText: string, appIndex: number, extIndex: number): string {
    // Find the nth </Application> to scope to the correct application
    const closeAppTag = '</Application>';
    let appCloseIdx = -1;
    let count = 0;
    let searchFrom = 0;
    while (count <= appIndex) {
        appCloseIdx = xmlText.indexOf(closeAppTag, searchFrom);
        if (appCloseIdx < 0) { return xmlText; }
        if (count === appIndex) { break; }
        searchFrom = appCloseIdx + closeAppTag.length;
        count++;
    }

    // Find <Extensions> and </Extensions> within this Application
    const extOpenTag = '<Extensions>';
    const extCloseTag = '</Extensions>';
    const extOpenIdx = xmlText.lastIndexOf(extOpenTag, appCloseIdx);
    if (extOpenIdx < 0) { return xmlText; }
    const extCloseIdx = xmlText.indexOf(extCloseTag, extOpenIdx);
    if (extCloseIdx < 0 || extCloseIdx > appCloseIdx) { return xmlText; }

    const contentStart = extOpenIdx + extOpenTag.length;
    const contentEnd = extCloseIdx;

    // Find all direct child elements within <Extensions>...</Extensions>
    const children = findDirectChildElementBounds(xmlText, contentStart, contentEnd);
    if (extIndex < 0 || extIndex >= children.length) { return xmlText; }

    const target = children[extIndex];

    // Expand removal range to include leading whitespace (indentation) and trailing newline
    let removeStart = target.start;
    while (removeStart > contentStart && (xmlText[removeStart - 1] === ' ' || xmlText[removeStart - 1] === '\t')) {
        removeStart--;
    }
    // Also consume the preceding newline
    if (removeStart > contentStart && xmlText[removeStart - 1] === '\n') {
        removeStart--;
        if (removeStart > contentStart && xmlText[removeStart - 1] === '\r') {
            removeStart--;
        }
    }

    let result = xmlText.substring(0, removeStart) + xmlText.substring(target.end);

    // Check if Extensions is now empty (no more child elements)
    const newExtOpenIdx = result.lastIndexOf(extOpenTag, appCloseIdx);
    if (newExtOpenIdx >= 0) {
        const newExtCloseIdx = result.indexOf(extCloseTag, newExtOpenIdx);
        if (newExtCloseIdx >= 0) {
            const innerContent = result.substring(newExtOpenIdx + extOpenTag.length, newExtCloseIdx);
            if (innerContent.trim() === '') {
                // Remove the entire <Extensions>...</Extensions> block including surrounding whitespace
                let blockStart = newExtOpenIdx;
                while (blockStart > 0 && (result[blockStart - 1] === ' ' || result[blockStart - 1] === '\t')) {
                    blockStart--;
                }
                if (blockStart > 0 && result[blockStart - 1] === '\n') {
                    blockStart--;
                    if (blockStart > 0 && result[blockStart - 1] === '\r') {
                        blockStart--;
                    }
                }
                const blockEnd = newExtCloseIdx + extCloseTag.length;
                result = result.substring(0, blockStart) + result.substring(blockEnd);
            }
        }
    }

    return result;
}

/** Find the start/end positions of direct child elements within an XML region. */
function findDirectChildElementBounds(xml: string, regionStart: number, regionEnd: number): Array<{start: number; end: number}> {
    const elements: Array<{start: number; end: number}> = [];
    let pos = regionStart;

    while (pos < regionEnd) {
        const lt = xml.indexOf('<', pos);
        if (lt === -1 || lt >= regionEnd) { break; }

        // Skip comments
        if (xml[lt + 1] === '!' && xml[lt + 2] === '-' && xml[lt + 3] === '-') {
            const commentEnd = xml.indexOf('-->', lt);
            if (commentEnd === -1) { break; }
            pos = commentEnd + 3;
            continue;
        }

        // Skip closing tags (parent's close tag or unexpected)
        if (xml[lt + 1] === '/') { break; }

        // Skip processing instructions
        if (xml[lt + 1] === '?') {
            const piEnd = xml.indexOf('?>', lt);
            if (piEnd === -1) { break; }
            pos = piEnd + 2;
            continue;
        }

        // This is an element opening tag
        const elemStart = lt;
        const gt = xml.indexOf('>', lt);
        if (gt === -1) { break; }

        if (xml[gt - 1] === '/') {
            // Self-closing element
            elements.push({ start: elemStart, end: gt + 1 });
            pos = gt + 1;
            continue;
        }

        // Non-self-closing — track depth to find matching close
        let depth = 1;
        pos = gt + 1;
        while (pos < xml.length && depth > 0) {
            const nextLt = xml.indexOf('<', pos);
            if (nextLt === -1) { break; }

            if (xml[nextLt + 1] === '!' && xml[nextLt + 2] === '-' && xml[nextLt + 3] === '-') {
                const ce = xml.indexOf('-->', nextLt);
                if (ce === -1) { break; }
                pos = ce + 3;
                continue;
            }

            if (xml[nextLt + 1] === '/') {
                depth--;
                const closeGt = xml.indexOf('>', nextLt);
                if (closeGt === -1) { break; }
                pos = closeGt + 1;
                if (depth === 0) {
                    elements.push({ start: elemStart, end: closeGt + 1 });
                }
            } else {
                const openGt = xml.indexOf('>', nextLt);
                if (openGt === -1) { break; }
                if (xml[openGt - 1] === '/') {
                    // Self-closing nested element, doesn't change depth
                    pos = openGt + 1;
                } else {
                    depth++;
                    pos = openGt + 1;
                }
            }
        }
    }

    return elements;
}

/** Find the bounds of a parent element by local name (handles optional namespace prefix). */
function findParentBounds(xml: string, localName: string): { openStart: number; contentStart: number; contentEnd: number; closeEnd: number } | null {
    const openPattern = new RegExp(`<(?:[a-zA-Z0-9]+:)?${escapeRegex(localName)}\\b`);
    const openMatch = openPattern.exec(xml);
    if (!openMatch) { return null; }
    const openStart = openMatch.index;
    const gt = xml.indexOf('>', openStart);
    if (gt === -1) { return null; }
    if (xml[gt - 1] === '/') { return null; } // self-closing
    const contentStart = gt + 1;
    // Find matching close tag
    const closePattern = new RegExp(`</(?:[a-zA-Z0-9]+:)?${escapeRegex(localName)}\\s*>`);
    const closeMatch = closePattern.exec(xml.substring(contentStart));
    if (!closeMatch) { return null; }
    const contentEnd = contentStart + closeMatch.index;
    const closeEnd = contentEnd + closeMatch[0].length;
    return { openStart, contentStart, contentEnd, closeEnd };
}

/** Remove an element and its leading whitespace/newline from the XML string. */
function removeElementWithWhitespace(xml: string, elemStart: number, elemEnd: number, containerContentStart: number): string {
    let removeStart = elemStart;
    while (removeStart > containerContentStart && (xml[removeStart - 1] === ' ' || xml[removeStart - 1] === '\t')) {
        removeStart--;
    }
    if (removeStart > containerContentStart && xml[removeStart - 1] === '\n') {
        removeStart--;
        if (removeStart > containerContentStart && xml[removeStart - 1] === '\r') {
            removeStart--;
        }
    }
    return xml.substring(0, removeStart) + xml.substring(elemEnd);
}

/** Insert a child element before a closing tag with proper indentation. */
function insertChildBeforeClose(xml: string, closeTagPos: number, childXml: string, parentIndent: string): string {
    const childIndent = parentIndent + '  ';
    let lineStart = closeTagPos;
    while (lineStart > 0 && xml[lineStart - 1] !== '\n') { lineStart--; }
    return xml.substring(0, lineStart) + childIndent + childXml + '\n' + xml.substring(lineStart);
}

/** Check if an element tag string matches the expected capability namespace prefix. */
function matchesCapabilityTag(elemXml: string, capNs: string): boolean {
    if (capNs === 'device') { return /^<DeviceCapability\b/.test(elemXml); }
    if (capNs === 'rescap') { return /^<rescap:Capability\b/.test(elemXml); }
    return /^<Capability\b/.test(elemXml);
}

/** Check if an element tag string has a Name attribute with the given value. */
function hasNameAttribute(elemXml: string, name: string): boolean {
    const regex = new RegExp(`\\bName\\s*=\\s*["']${escapeRegex(name)}["']`);
    return regex.test(elemXml);
}

/**
 * Update an attribute on an extension element.
 * fieldPath is "ElementName.AttributeName" as produced by parseExtensionFields in the webview.
 */
export function updateExtensionField(
    xmlText: string, appIndex: number, extIndex: number, fieldPath: string, value: string, isTextContent?: boolean,
): string {
    // Find the nth </Application> to scope to the correct application
    const closeAppTag = '</Application>';
    let appCloseIdx = -1;
    let count = 0;
    let searchFrom = 0;
    while (count <= appIndex) {
        appCloseIdx = xmlText.indexOf(closeAppTag, searchFrom);
        if (appCloseIdx < 0) { return xmlText; }
        if (count === appIndex) { break; }
        searchFrom = appCloseIdx + closeAppTag.length;
        count++;
    }

    // Find <Extensions> and </Extensions> within this Application
    const extOpenTag = '<Extensions>';
    const extCloseTag = '</Extensions>';
    const extOpenIdx = xmlText.lastIndexOf(extOpenTag, appCloseIdx);
    if (extOpenIdx < 0) { return xmlText; }
    const extCloseIdx = xmlText.indexOf(extCloseTag, extOpenIdx);
    if (extCloseIdx < 0 || extCloseIdx > appCloseIdx) { return xmlText; }

    const contentStart = extOpenIdx + extOpenTag.length;
    const contentEnd = extCloseIdx;

    // Find all direct child elements within <Extensions>
    const children = findDirectChildElementBounds(xmlText, contentStart, contentEnd);
    if (extIndex < 0 || extIndex >= children.length) { return xmlText; }

    const target = children[extIndex];
    let extXml = xmlText.substring(target.start, target.end);

    if (isTextContent) {
        // fieldPath is just the element name — find <ElementName>text</ElementName>
        const elemPattern = new RegExp(
            `(<(?:[a-zA-Z0-9]+:)?${escapeRegex(fieldPath)}\\b[^>]*>)([\\s\\S]*?)(<\\/(?:[a-zA-Z0-9]+:)?${escapeRegex(fieldPath)}\\s*>)`
        );
        const match = elemPattern.exec(extXml);
        if (!match) { return xmlText; }
        extXml = extXml.substring(0, match.index) + match[1] + value + match[3] + extXml.substring(match.index + match[0].length);
    } else {
        const dotIdx = fieldPath.indexOf('.');
        if (dotIdx < 0) { return xmlText; }
        const elemName = fieldPath.substring(0, dotIdx);
        const attrName = fieldPath.substring(dotIdx + 1);

        // Find the element's opening tag within the extension XML
        const elemPattern = new RegExp(`<(?:[a-zA-Z0-9]+:)?${escapeRegex(elemName)}\\b[^>]*\\/?>`, 's');
        const match = elemPattern.exec(extXml);
        if (!match) { return xmlText; }

        // Replace the attribute value within the matched element tag
        const attrRegex = new RegExp(`(${escapeRegex(attrName)}\\s*=\\s*)(["'])([^"']*?)\\2`);
        const attrMatch = attrRegex.exec(match[0]);
        if (!attrMatch) { return xmlText; }

        const newElem = match[0].substring(0, attrMatch.index)
            + attrMatch[1] + attrMatch[2] + value + attrMatch[2]
            + match[0].substring(attrMatch.index + attrMatch[0].length);
        extXml = extXml.substring(0, match.index) + newElem + extXml.substring(match.index + match[0].length);
    }

    return xmlText.substring(0, target.start) + extXml + xmlText.substring(target.end);
}

// ─── Internal helpers ───────────────────────────────────────────────

function parseIdentity(root: Element): IdentityData {
    const el = getChildByLocalName(root, 'Identity');
    return {
        name: el?.getAttribute('Name') ?? '',
        publisher: el?.getAttribute('Publisher') ?? '',
        version: el?.getAttribute('Version') ?? '',
        processorArchitecture: el?.getAttribute('ProcessorArchitecture') ?? 'neutral',
    };
}

function parsePhoneIdentity(root: Element): PhoneIdentityData | null {
    const el = findChildByLocalNameNS(root, 'PhoneIdentity');
    if (!el) { return null; }
    return {
        phoneProductId: el.getAttribute('PhoneProductId') ?? '',
        phonePublisherId: el.getAttribute('PhonePublisherId') ?? '',
    };
}

function parseProperties(root: Element): PropertiesData {
    const el = getChildByLocalName(root, 'Properties');
    return {
        displayName: getChildTextContent(el, 'DisplayName'),
        publisherDisplayName: getChildTextContent(el, 'PublisherDisplayName'),
        description: getChildTextContent(el, 'Description'),
        logo: getChildTextContent(el, 'Logo'),
    };
}

function parseDependencies(root: Element): DependenciesData {
    const el = getChildByLocalName(root, 'Dependencies');
    const targetDeviceFamilies: TargetDeviceFamilyData[] = [];
    const packageDependencies: PackageDependencyData[] = [];

    if (el) {
        for (const child of getChildrenByLocalName(el, 'TargetDeviceFamily')) {
            targetDeviceFamilies.push({
                name: child.getAttribute('Name') ?? '',
                minVersion: child.getAttribute('MinVersion') ?? '',
                maxVersionTested: child.getAttribute('MaxVersionTested') ?? '',
            });
        }
        for (const child of getChildrenByLocalName(el, 'PackageDependency')) {
            packageDependencies.push({
                name: child.getAttribute('Name') ?? '',
                minVersion: child.getAttribute('MinVersion') ?? '',
                publisher: child.getAttribute('Publisher') ?? '',
            });
        }
    }

    return { targetDeviceFamilies, packageDependencies };
}

function parseApplications(root: Element): ApplicationData[] {
    const appsEl = getChildByLocalName(root, 'Applications');
    if (!appsEl) { return []; }

    const apps: ApplicationData[] = [];
    for (const appEl of getChildrenByLocalName(appsEl, 'Application')) {
        const visualEl = findChildByLocalNameNS(appEl, 'VisualElements');
        const defaultTile = visualEl ? findChildByLocalNameNS(visualEl, 'DefaultTile') : null;
        const lockScreen = visualEl ? findChildByLocalNameNS(visualEl, 'LockScreen') : null;
        const splashScreen = visualEl ? findChildByLocalNameNS(visualEl, 'SplashScreen') : null;

        // Parse ShowNameOnTiles
        const showNameOnTiles: string[] = [];
        if (defaultTile) {
            const showNameEl = findChildByLocalNameNS(defaultTile, 'ShowNameOnTiles');
            if (showNameEl) {
                const showOnEls = getChildrenByLocalName(showNameEl, 'ShowOn');
                for (const showOn of showOnEls) {
                    const tile = showOn.getAttribute('Tile');
                    if (tile) { showNameOnTiles.push(tile); }
                }
            }
        }

        // Gather extension raw XML for display and editing
        const extensions: string[] = [];
        const extEl = getChildByLocalName(appEl, 'Extensions');
        if (extEl) {
            const serializer = new XMLSerializer();
            const extChildren = extEl.childNodes;
            for (let i = 0; i < extChildren.length; i++) {
                const child = extChildren[i];
                if (child.nodeType === 1) {
                    extensions.push(serializer.serializeToString(child as Element));
                }
            }
        }

        apps.push({
            id: appEl.getAttribute('Id') ?? '',
            executable: appEl.getAttribute('Executable') ?? '',
            entryPoint: appEl.getAttribute('EntryPoint') ?? '',
            visualElements: {
                displayName: visualEl?.getAttribute('DisplayName') ?? '',
                description: visualEl?.getAttribute('Description') ?? '',
                backgroundColor: visualEl?.getAttribute('BackgroundColor') ?? '',
                square150x150Logo: visualEl?.getAttribute('Square150x150Logo') ?? '',
                square44x44Logo: visualEl?.getAttribute('Square44x44Logo') ?? '',
                wide310x150Logo: defaultTile?.getAttribute('Wide310x150Logo') ?? null,
                square71x71Logo: defaultTile?.getAttribute('Square71x71Logo') ?? null,
                square310x310Logo: defaultTile?.getAttribute('Square310x310Logo') ?? null,
                badgeLogo: lockScreen?.getAttribute('BadgeLogo') ?? null,
                splashScreenImage: splashScreen?.getAttribute('Image') ?? null,
                showNameOnTiles,
            },
            extensions,
        });
    }
    return apps;
}

function parseCapabilities(root: Element): string[] {
    const capsEl = getChildByLocalName(root, 'Capabilities');
    if (!capsEl) { return []; }

    const capabilities: string[] = [];
    const children = capsEl.childNodes;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        if (child.nodeType !== 1) { continue; }
        const el = child as Element;
        const name = el.getAttribute('Name') ?? '';
        if (!name) { continue; }

        const localName = el.localName ?? '';
        const prefix = el.prefix ?? '';

        if (localName === 'DeviceCapability') {
            capabilities.push(`device:${name}`);
        } else if (prefix === 'rescap') {
            capabilities.push(`rescap:${name}`);
        } else {
            capabilities.push(name);
        }
    }
    return capabilities;
}

function parseResources(root: Element): ResourceData[] {
    const resourcesEl = getChildByLocalName(root, 'Resources');
    if (!resourcesEl) { return []; }

    const resources: ResourceData[] = [];
    for (const child of getChildrenByLocalName(resourcesEl, 'Resource')) {
        resources.push({
            language: child.getAttribute('Language') ?? '',
        });
    }
    return resources;
}

// ─── Apply changes ──────────────────────────────────────────────────

// ─── Surgical string-based field change helpers ─────────────────────
// These replace only the specific attribute or element text in the XML
// string, preserving all original whitespace and formatting.

/** Escape special regex characters in a string. */
function escapeRegex(s: string): string {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Replace an XML attribute value in-place. Returns the original string if not found. */
function replaceAttribute(xml: string, elementPattern: RegExp, attrName: string, newValue: string): string {
    // Find the element in the XML
    const elementMatch = elementPattern.exec(xml);
    if (!elementMatch) { return xml; }

    // Within the matched element, find and replace the attribute value
    const elementStr = elementMatch[0];
    const attrRegex = new RegExp(`(${escapeRegex(attrName)}\\s*=\\s*)(["'])([^"']*?)\\2`);
    const attrMatch = attrRegex.exec(elementStr);
    if (!attrMatch) { return xml; }

    const newElementStr = elementStr.substring(0, attrMatch.index)
        + attrMatch[1] + attrMatch[2] + newValue + attrMatch[2]
        + elementStr.substring(attrMatch.index + attrMatch[0].length);

    return xml.substring(0, elementMatch.index) + newElementStr + xml.substring(elementMatch.index + elementStr.length);
}

/** Add a new attribute to an existing XML element. Returns the original string if element not found. */
function addAttributeToElement(xml: string, elementPattern: RegExp, attrName: string, value: string): string {
    const elementMatch = elementPattern.exec(xml);
    if (!elementMatch) { return xml; }

    const elementStr = elementMatch[0];
    // Insert the new attribute before the closing /> or >
    const closingMatch = /(\s*\/?>)\s*$/.exec(elementStr);
    if (!closingMatch) { return xml; }

    const insertPos = closingMatch.index;

    // Detect if element is multi-line; if so, match existing attribute indentation
    const attrIndentMatch = /\n([ \t]+)\w/.exec(elementStr);
    let attrText: string;
    if (attrIndentMatch) {
        // Multi-line element — put new attribute on its own line with same indent
        attrText = '\n' + attrIndentMatch[1] + `${attrName}="${value}"`;
    } else {
        // Single-line element — append with a space
        attrText = ` ${attrName}="${value}"`;
    }

    const newElementStr = elementStr.substring(0, insertPos) + attrText + elementStr.substring(insertPos);
    return xml.substring(0, elementMatch.index) + newElementStr + xml.substring(elementMatch.index + elementStr.length);
}

/** Replace the text content of an XML element in-place. Returns the original string if not found. */
function replaceElementText(xml: string, tagPattern: RegExp, newValue: string): string {
    const match = tagPattern.exec(xml);
    if (!match) { return xml; }

    // match[0] is the full match including tags, match[1] is the opening tag, match[2] is the old text
    return xml.substring(0, match.index) + match[1] + newValue + match[3] + xml.substring(match.index + match[0].length);
}

function applyIdentityChangeString(xml: string, field: string, value: string): string {
    const attrMap: Record<string, string> = {
        name: 'Name',
        publisher: 'Publisher',
        version: 'Version',
        processorArchitecture: 'ProcessorArchitecture',
    };
    const attr = attrMap[field];
    if (!attr) { return xml; }

    const pattern = /<Identity\b[^>]*>/s;
    const result = replaceAttribute(xml, pattern, attr, value);
    if (result !== xml) { return result; }

    // Attribute doesn't exist yet — add it
    return addAttributeToElement(xml, pattern, attr, value);
}

function applyPhoneIdentityChangeString(xml: string, field: string, value: string): string {
    const attrMap: Record<string, string> = {
        phoneProductId: 'PhoneProductId',
        phonePublisherId: 'PhonePublisherId',
    };
    const attr = attrMap[field];
    if (!attr) { return xml; }

    return replaceAttribute(xml, /<[a-zA-Z0-9]*:?PhoneIdentity\b[^>]*>/s, attr, value);
}

function applyPropertiesChangeString(xml: string, field: string, value: string): string {
    const tagMap: Record<string, string> = {
        displayName: 'DisplayName',
        publisherDisplayName: 'PublisherDisplayName',
        description: 'Description',
        logo: 'Logo',
    };
    const tag = tagMap[field];
    if (!tag) { return xml; }

    // Match <Tag>text</Tag> (with any namespace prefix)
    const tagRegex = new RegExp(`(<${tag}>|<[a-zA-Z0-9]+:${tag}>)(.*?)(<\\/${tag}>|<\\/[a-zA-Z0-9]+:${tag}>)`, 's');

    // If value is empty, remove the element entirely
    if (!value) {
        const fullTagRegex = new RegExp(`[ \t]*(?:<${tag}>|<[a-zA-Z0-9]+:${tag}>).*?(?:<\\/${tag}>|<\\/[a-zA-Z0-9]+:${tag}>)[ \t]*\\r?\\n?`, 's');
        const removeMatch = fullTagRegex.exec(xml);
        if (removeMatch) {
            return xml.substring(0, removeMatch.index) + xml.substring(removeMatch.index + removeMatch[0].length);
        }
        return xml;
    }

    const result = replaceElementText(xml, tagRegex, value);

    // If the element wasn't found and the value is non-empty, insert it into <Properties>
    if (result === xml && value) {
        let workXml = xml;
        let propsBounds = findParentBounds(workXml, 'Properties');
        if (!propsBounds) {
            // Create <Properties> before </Package>
            const pkgClose = workXml.lastIndexOf('</Package>');
            if (pkgClose < 0) { return xml; }
            const pkgIndent = detectIndent(workXml, pkgClose);
            const propsIndent = pkgIndent + '  ';
            const block = propsIndent + '<Properties>\n' + propsIndent + '</Properties>\n';
            let lineStart = pkgClose;
            while (lineStart > 0 && workXml[lineStart - 1] !== '\n') { lineStart--; }
            workXml = workXml.substring(0, lineStart) + block + workXml.substring(lineStart);
            propsBounds = findParentBounds(workXml, 'Properties');
            if (!propsBounds) { return xml; }
        }
        const propIndent = detectIndent(workXml, propsBounds.openStart);
        return insertChildBeforeClose(workXml, propsBounds.contentEnd, `<${tag}>${value}</${tag}>`, propIndent);
    }

    return result;
}

function applyDependenciesChangeString(xml: string, field: string, value: string, index: number): string {
    if (field.startsWith('targetDeviceFamily.')) {
        const subField = field.replace('targetDeviceFamily.', '');
        const attrMap: Record<string, string> = {
            name: 'Name',
            minVersion: 'MinVersion',
            maxVersionTested: 'MaxVersionTested',
        };
        const attr = attrMap[subField];
        if (!attr) { return xml; }

        // Find the Nth TargetDeviceFamily element
        const regex = /<TargetDeviceFamily\b[^>]*\/?>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                const result = replaceAttribute(xml, new RegExp(escapeRegex(match[0])), attr, value);
                if (result !== xml) { return result; }
                return addAttributeToElement(xml, new RegExp(escapeRegex(match[0])), attr, value);
            }
            count++;
        }
    } else if (field.startsWith('packageDependency.')) {
        const subField = field.replace('packageDependency.', '');
        const attrMap: Record<string, string> = {
            name: 'Name',
            minVersion: 'MinVersion',
            publisher: 'Publisher',
        };
        const attr = attrMap[subField];
        if (!attr) { return xml; }

        const regex = /<PackageDependency\b[^>]*\/?>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                const result = replaceAttribute(xml, new RegExp(escapeRegex(match[0])), attr, value);
                if (result !== xml) { return result; }
                // Attribute doesn't exist yet — add it
                return addAttributeToElement(xml, new RegExp(escapeRegex(match[0])), attr, value);
            }
            count++;
        }
    }
    return xml;
}

function applyResourcesChangeString(xml: string, field: string, value: string, index: number): string {
    if (field === 'language') {
        const regex = /<Resource\b[^>]*\/?>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                const result = replaceAttribute(xml, new RegExp(escapeRegex(match[0])), 'Language', value);
                if (result !== xml) { return result; }
                return addAttributeToElement(xml, new RegExp(escapeRegex(match[0])), 'Language', value);
            }
            count++;
        }
    }
    return xml;
}

function applyApplicationChangeString(xml: string, field: string, value: string, index: number): string {
    // Top-level Application attributes
    const appAttrMap: Record<string, string> = {
        id: 'Id',
        executable: 'Executable',
        entryPoint: 'EntryPoint',
    };
    if (appAttrMap[field]) {
        const regex = /<Application\b[^>]*>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                const result = replaceAttribute(xml, new RegExp(escapeRegex(match[0])), appAttrMap[field], value);
                if (result !== xml) { return result; }
                return addAttributeToElement(xml, new RegExp(escapeRegex(match[0])), appAttrMap[field], value);
            }
            count++;
        }
        return xml;
    }

    // VisualElements attributes
    if (field.startsWith('visualElements.')) {
        const veField = field.replace('visualElements.', '');

        // Attributes on DefaultTile
        const defaultTileAttrs: Record<string, string> = {
            wide310x150Logo: 'Wide310x150Logo',
            square71x71Logo: 'Square71x71Logo',
            square310x310Logo: 'Square310x310Logo',
        };
        if (defaultTileAttrs[veField]) {
            const result = replaceAttribute(xml, /<[a-zA-Z0-9]*:?DefaultTile\b[^>]*>/s, defaultTileAttrs[veField], value);
            if (result !== xml) { return result; }
            // Element exists but attribute doesn't — add the attribute
            const addResult = addAttributeToElement(xml, /<[a-zA-Z0-9]*:?DefaultTile\b[^>]*?\/?>/s, defaultTileAttrs[veField], value);
            if (addResult !== xml) { return addResult; }
        }

        // Attribute on LockScreen
        if (veField === 'badgeLogo') {
            const result = replaceAttribute(xml, /<[a-zA-Z0-9]*:?LockScreen\b[^>]*>/s, 'BadgeLogo', value);
            if (result !== xml) { return result; }
            const addResult = addAttributeToElement(xml, /<[a-zA-Z0-9]*:?LockScreen\b[^>]*?\/?>/s, 'BadgeLogo', value);
            if (addResult !== xml) { return addResult; }
        }

        // Attribute on SplashScreen
        if (veField === 'splashScreenImage') {
            const result = replaceAttribute(xml, /<[a-zA-Z0-9]*:?SplashScreen\b[^>]*>/s, 'Image', value);
            if (result !== xml) { return result; }
            const addResult = addAttributeToElement(xml, /<[a-zA-Z0-9]*:?SplashScreen\b[^>]*?\/?>/s, 'Image', value);
            if (addResult !== xml) { return addResult; }
        }

        const attrMap: Record<string, string> = {
            displayName: 'DisplayName',
            description: 'Description',
            backgroundColor: 'BackgroundColor',
            square150x150Logo: 'Square150x150Logo',
            square44x44Logo: 'Square44x44Logo',
        };
        if (attrMap[veField]) {
            return replaceAttribute(xml, /<[a-zA-Z0-9]*:?VisualElements\b[^>]*>/s, attrMap[veField], value);
        }

        // Fallback: surgically insert new child element inside VisualElements
        // This avoids DOM serialization which destroys whitespace formatting
        const veClosePattern = /(<[a-zA-Z0-9]*:?VisualElements\b[^>]*?)\s*\/>/s;
        const veCloseMatch = veClosePattern.exec(xml);
        if (veCloseMatch) {
            // Self-closing VisualElements — convert to open/close and insert child
            const indent = detectIndent(xml, veCloseMatch.index);
            const childIndent = indent + '  ';
            const childXml = buildVisualChildElement(veField, value);
            if (childXml) {
                return xml.substring(0, veCloseMatch.index)
                    + veCloseMatch[1] + '>\n'
                    + childIndent + childXml + '\n'
                    + indent + '</uap:VisualElements>'
                    + xml.substring(veCloseMatch.index + veCloseMatch[0].length);
            }
        } else {
            // Non-self-closing VisualElements — insert before closing tag
            const veEndPattern = /<\/[a-zA-Z0-9]*:?VisualElements\s*>/s;
            const veEndMatch = veEndPattern.exec(xml);
            if (veEndMatch) {
                // Try to detect child indent from an existing child element (e.g., DefaultTile)
                const existingChildPattern = /\n([ \t]+)<[a-zA-Z0-9]*:?(?:DefaultTile|LockScreen|SplashScreen)\b/;
                const existingChildMatch = existingChildPattern.exec(xml);
                const veEndIndent = detectIndent(xml, veEndMatch.index);
                const childIndent = existingChildMatch ? existingChildMatch[1] : (veEndIndent + '  ');
                const childXml = buildVisualChildElement(veField, value);
                if (childXml) {
                    // Find the start of the whitespace preceding the closing tag
                    const beforeClose = xml.substring(0, veEndMatch.index);
                    const trailingWsMatch = /\n[ \t]*$/.exec(beforeClose);
                    const insertPos = trailingWsMatch ? veEndMatch.index - trailingWsMatch[0].length : veEndMatch.index;
                    return xml.substring(0, insertPos)
                        + '\n' + childIndent + childXml
                        + '\n' + veEndIndent + veEndMatch[0]
                        + xml.substring(veEndMatch.index + veEndMatch[0].length);
                }
            }
        }

        return xml;
    }

    return xml;
}

// ─── Surgical string insertion helpers for visual asset child elements ─

/** Detect the indentation of the line containing the given position. */
function detectIndent(xml: string, pos: number): string {
    const lineStart = xml.lastIndexOf('\n', pos - 1);
    if (lineStart === -1) { return ''; }
    const lineContent = xml.substring(lineStart + 1, pos);
    const match = /^(\s*)/.exec(lineContent);
    return match ? match[1] : '';
}

/** Build the XML string for a new child element inside VisualElements. */
function buildVisualChildElement(veField: string, value: string): string | null {
    const defaultTileFields: Record<string, string> = {
        wide310x150Logo: 'Wide310x150Logo',
        square71x71Logo: 'Square71x71Logo',
        square310x310Logo: 'Square310x310Logo',
    };
    if (defaultTileFields[veField]) {
        return `<uap:DefaultTile ${defaultTileFields[veField]}="${value}" />`;
    }
    if (veField === 'badgeLogo') {
        return `<uap:LockScreen Notification="badge" BadgeLogo="${value}" />`;
    }
    if (veField === 'splashScreenImage') {
        return `<uap:SplashScreen Image="${value}" />`;
    }
    return null;
}

// ─── DOM utility helpers ────────────────────────────────────────────

function getChildByLocalName(parent: Element | null, localName: string): Element | null {
    if (!parent) { return null; }
    const children = parent.childNodes;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        if (child.nodeType === 1 && (child as Element).localName === localName) {
            return child as Element;
        }
    }
    return null;
}

function getChildrenByLocalName(parent: Element, localName: string): Element[] {
    const result: Element[] = [];
    const children = parent.childNodes;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        if (child.nodeType === 1 && (child as Element).localName === localName) {
            result.push(child as Element);
        }
    }
    return result;
}

/** Find a child element by local name, checking across all namespaces (for uap:VisualElements, etc.). */
function findChildByLocalNameNS(parent: Element, localName: string): Element | null {
    const children = parent.childNodes;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        if (child.nodeType === 1 && (child as Element).localName === localName) {
            return child as Element;
        }
    }
    return null;
}

function getChildTextContent(parent: Element | null, localName: string): string {
    const child = getChildByLocalName(parent, localName);
    return child?.textContent ?? '';
}

/** Determine the element info for creating a capability XML element. */
function getCapabilityElementInfo(capability: string): { elementName: string; ns: string | null; attrName: string } {
    if (capability.startsWith('rescap:')) {
        return { elementName: 'rescap:Capability', ns: NS.rescap, attrName: capability.replace('rescap:', '') };
    }
    if (capability.startsWith('device:')) {
        return { elementName: 'DeviceCapability', ns: NS.default, attrName: capability.replace('device:', '') };
    }
    return { elementName: 'Capability', ns: NS.default, attrName: capability };
}

/** Parse a capability string into its namespace and name parts. */
function parseCapabilityString(capability: string): { attrName: string; namespace: string } {
    if (capability.startsWith('rescap:')) {
        return { attrName: capability.replace('rescap:', ''), namespace: 'rescap' };
    }
    if (capability.startsWith('device:')) {
        return { attrName: capability.replace('device:', ''), namespace: 'device' };
    }
    return { attrName: capability, namespace: '' };
}
