/**
 * Parse and modify appxmanifest.xml using @xmldom/xmldom.
 * Reads XML into ManifestData for the form, and applies edits back to the XML text.
 */

import { DOMParser, XMLSerializer } from '@xmldom/xmldom';
import type { Element, Document } from '@xmldom/xmldom';
import {
    ManifestData,
    IdentityData,
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

/** Remove an element and its preceding whitespace text node. */
function removeElementClean(parent: Element, child: Element): void {
    const prev = child.previousSibling;
    if (prev && prev.nodeType === 3 && /^\s*$/.test(prev.nodeValue || '')) {
        parent.removeChild(prev);
    }
    parent.removeChild(child);
}

/** Collapse consecutive blank lines into a single newline. */
function cleanupBlankLines(xml: string): string {
    return xml.replace(/\n[ \t]*\n([ \t]*\n)*/g, '\n');
}

/** Parse appxmanifest.xml text into a ManifestData object. */
export function parseManifest(xmlText: string): ManifestData {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement;
    if (!root) { throw new Error('Invalid XML: no root element'); }

    return {
        identity: parseIdentity(root),
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
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    let capsEl = getChildByLocalName(root, 'Capabilities');

    if (!capsEl) {
        capsEl = doc.createElementNS(NS.default, 'Capabilities');
        root.appendChild(capsEl);
    }

    // Determine namespace and element name
    const { elementName, ns, attrName } = getCapabilityElementInfo(capability);
    const el = ns ? doc.createElementNS(ns, elementName) : doc.createElementNS(NS.default, elementName);
    el.setAttribute('Name', attrName);
    capsEl.appendChild(doc.createTextNode('  '));
    capsEl.appendChild(el);
    capsEl.appendChild(doc.createTextNode('\n  '));

    return new XMLSerializer().serializeToString(doc);
}

/** Remove a capability element from the XML. */
export function removeCapability(xmlText: string, capability: string): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const capsEl = getChildByLocalName(root, 'Capabilities');
    if (!capsEl) { return xmlText; }

    const { attrName, namespace: capNs } = parseCapabilityString(capability);

    const children = capsEl.childNodes;
    for (let i = children.length - 1; i >= 0; i--) {
        const child = children[i];
        if (child.nodeType === 1) { // ELEMENT_NODE
            const el = child as Element;
            if (el.getAttribute('Name') === attrName && matchesCapabilityNamespace(el, capNs)) {
                removeElementClean(capsEl, el);
                break;
            }
        }
    }

    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
}

/** Add a PackageDependency element. */
export function addPackageDependency(xmlText: string, dep: PackageDependencyData): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    let depsEl = getChildByLocalName(root, 'Dependencies');

    if (!depsEl) {
        depsEl = doc.createElementNS(NS.default, 'Dependencies');
        root.appendChild(depsEl);
    }

    const el = doc.createElementNS(NS.default, 'PackageDependency');
    el.setAttribute('Name', dep.name);
    if (dep.minVersion) { el.setAttribute('MinVersion', dep.minVersion); }
    if (dep.publisher) { el.setAttribute('Publisher', dep.publisher); }
    depsEl.appendChild(doc.createTextNode('  '));
    depsEl.appendChild(el);
    depsEl.appendChild(doc.createTextNode('\n  '));

    return new XMLSerializer().serializeToString(doc);
}

/** Remove a PackageDependency element by index. */
export function removePackageDependency(xmlText: string, index: number): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const depsEl = getChildByLocalName(root, 'Dependencies');
    if (!depsEl) { return xmlText; }

    const pkgDeps = getChildrenByLocalName(depsEl, 'PackageDependency');
    if (index >= 0 && index < pkgDeps.length) {
        removeElementClean(depsEl, pkgDeps[index]);
    }

    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
}

/** Add a TargetDeviceFamily element. */
export function addTargetDeviceFamily(xmlText: string, family: TargetDeviceFamilyData): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    let depsEl = getChildByLocalName(root, 'Dependencies');

    if (!depsEl) {
        depsEl = doc.createElementNS(NS.default, 'Dependencies');
        root.appendChild(depsEl);
    }

    const el = doc.createElementNS(NS.default, 'TargetDeviceFamily');
    el.setAttribute('Name', family.name);
    el.setAttribute('MinVersion', family.minVersion);
    el.setAttribute('MaxVersionTested', family.maxVersionTested);
    depsEl.appendChild(doc.createTextNode('  '));
    depsEl.appendChild(el);
    depsEl.appendChild(doc.createTextNode('\n  '));

    return new XMLSerializer().serializeToString(doc);
}

/** Remove a TargetDeviceFamily element by index. */
export function removeTargetDeviceFamily(xmlText: string, index: number): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const depsEl = getChildByLocalName(root, 'Dependencies');
    if (!depsEl) { return xmlText; }

    const families = getChildrenByLocalName(depsEl, 'TargetDeviceFamily');
    if (index >= 0 && index < families.length) {
        removeElementClean(depsEl, families[index]);
    }

    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
}

/** Add a Resource element to the XML. */
export function addResource(xmlText: string, resource: ResourceData): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    let resourcesEl = getChildByLocalName(root, 'Resources');

    if (!resourcesEl) {
        resourcesEl = doc.createElementNS(NS.default, 'Resources');
        root.appendChild(resourcesEl);
    }

    const el = doc.createElementNS(NS.default, 'Resource');
    if (resource.language) { el.setAttribute('Language', resource.language); }
    resourcesEl.appendChild(doc.createTextNode('  '));
    resourcesEl.appendChild(el);
    resourcesEl.appendChild(doc.createTextNode('\n  '));

    return new XMLSerializer().serializeToString(doc);
}

/** Remove a Resource element by index. */
export function removeResource(xmlText: string, index: number): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const resourcesEl = getChildByLocalName(root, 'Resources');
    if (!resourcesEl) { return xmlText; }

    const resources = getChildrenByLocalName(resourcesEl, 'Resource');
    if (index >= 0 && index < resources.length) {
        removeElementClean(resourcesEl, resources[index]);
    }

    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
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
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const appsEl = getChildByLocalName(root, 'Applications');
    if (!appsEl) { return xmlText; }

    const apps = getChildrenByLocalName(appsEl, 'Application');
    if (index < 0 || index >= apps.length || apps.length <= 1) { return xmlText; }

    removeElementClean(appsEl, apps[index]);
    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
}

export function addExtension(xmlText: string, appIndex: number, extensionXml: string): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const appsEl = getChildByLocalName(root, 'Applications');
    if (!appsEl) { return xmlText; }

    const apps = getChildrenByLocalName(appsEl, 'Application');
    if (appIndex >= apps.length) { return xmlText; }
    const appEl = apps[appIndex];
    const hasExtensions = getChildByLocalName(appEl, 'Extensions') !== null;

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
        // Insert before the closing </Extensions> tag
        const closeTag = '</Extensions>';
        const closeIdx = result.lastIndexOf(closeTag);
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

/** Remove an extension element from an application. */
export function removeExtension(xmlText: string, appIndex: number, extIndex: number): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const appsEl = getChildByLocalName(root, 'Applications');
    if (!appsEl) { return xmlText; }

    const apps = getChildrenByLocalName(appsEl, 'Application');
    if (appIndex >= apps.length) { return xmlText; }
    const appEl = apps[appIndex];

    const extEl = getChildByLocalName(appEl, 'Extensions');
    if (!extEl) { return xmlText; }

    const extChildren: Element[] = [];
    const nodes = extEl.childNodes;
    for (let i = 0; i < nodes.length; i++) {
        if (nodes[i].nodeType === 1) { extChildren.push(nodes[i] as Element); }
    }
    if (extIndex >= 0 && extIndex < extChildren.length) {
        removeElementClean(extEl, extChildren[extIndex]);
    }

    // If no extensions remain, remove the empty <Extensions> element
    let remainingElements = 0;
    for (let i = 0; i < extEl.childNodes.length; i++) {
        if (extEl.childNodes[i].nodeType === 1) { remainingElements++; }
    }
    if (remainingElements === 0) {
        removeElementClean(appEl, extEl);
    }

    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
}

/**
 * Update an attribute on an extension element.
 * fieldPath is "ElementName.AttributeName" as produced by parseExtensionFields in the webview.
 */
export function updateExtensionField(
    xmlText: string, appIndex: number, extIndex: number, fieldPath: string, value: string, isTextContent?: boolean,
): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;
    const appsEl = getChildByLocalName(root, 'Applications');
    if (!appsEl) { return xmlText; }

    const apps = getChildrenByLocalName(appsEl, 'Application');
    if (appIndex >= apps.length) { return xmlText; }
    const appEl = apps[appIndex];

    const extEl = getChildByLocalName(appEl, 'Extensions');
    if (!extEl) { return xmlText; }

    const extChildren: Element[] = [];
    const nodes = extEl.childNodes;
    for (let i = 0; i < nodes.length; i++) {
        if (nodes[i].nodeType === 1) { extChildren.push(nodes[i] as Element); }
    }
    if (extIndex < 0 || extIndex >= extChildren.length) { return xmlText; }

    const extRoot = extChildren[extIndex];

    // Find the matching element by walking the extension tree
    function findElement(el: Element, name: string): Element | null {
        if ((el.localName || el.nodeName) === name) { return el; }
        const children = el.childNodes;
        for (let i = 0; i < children.length; i++) {
            if (children[i].nodeType === 1) {
                const found = findElement(children[i] as Element, name);
                if (found) { return found; }
            }
        }
        return null;
    }

    if (isTextContent) {
        // fieldPath is just the element name (e.g., "Registration")
        const targetEl = findElement(extRoot, fieldPath);
        if (targetEl) {
            // Clear existing text content and set new value
            while (targetEl.firstChild) { targetEl.removeChild(targetEl.firstChild); }
            targetEl.appendChild(doc.createTextNode(value));
        }
    } else {
        const dotIdx = fieldPath.indexOf('.');
        if (dotIdx < 0) { return xmlText; }
        const elemName = fieldPath.substring(0, dotIdx);
        const attrName = fieldPath.substring(dotIdx + 1);
        const targetEl = findElement(extRoot, elemName);
        if (targetEl) {
            targetEl.setAttribute(attrName, value);
        }
    }

    return new XMLSerializer().serializeToString(doc);
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
    const newElementStr = elementStr.substring(0, insertPos) + ` ${attrName}="${value}"` + elementStr.substring(insertPos);
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

    return replaceAttribute(xml, /<Identity\b[^>]*>/s, attr, value);
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
    const result = replaceElementText(xml, tagRegex, value);

    // If the element wasn't found and the value is non-empty, fall back to DOM
    if (result === xml && value) {
        const doc = new DOMParser().parseFromString(xml, 'application/xml');
        const root = doc.documentElement!;
        applyPropertiesChange(root, doc, field, value);
        return new XMLSerializer().serializeToString(doc);
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
        const regex = /<TargetDeviceFamily\b[^>]*>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                return replaceAttribute(xml, new RegExp(escapeRegex(match[0])), attr, value);
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

        const regex = /<PackageDependency\b[^>]*>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                return replaceAttribute(xml, new RegExp(escapeRegex(match[0])), attr, value);
            }
            count++;
        }
    }
    return xml;
}

function applyResourcesChangeString(xml: string, field: string, value: string, index: number): string {
    if (field === 'language') {
        const regex = /<Resource\b[^>]*>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                return replaceAttribute(xml, new RegExp(escapeRegex(match[0])), 'Language', value);
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
                return replaceAttribute(xml, new RegExp(escapeRegex(match[0])), appAttrMap[field], value);
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

// ─── DOM-based change helpers (used as fallback for element creation) ─

function applyIdentityChange(root: Element, field: string, value: string): void {
    const el = getChildByLocalName(root, 'Identity');
    if (!el) { return; }

    const attrMap: Record<string, string> = {
        name: 'Name',
        publisher: 'Publisher',
        version: 'Version',
        processorArchitecture: 'ProcessorArchitecture',
    };
    const attr = attrMap[field];
    if (attr) { el.setAttribute(attr, value); }
}

function applyPropertiesChange(root: Element, doc: Document, field: string, value: string): void {
    let propsEl = getChildByLocalName(root, 'Properties');
    if (!propsEl) {
        propsEl = doc.createElementNS(NS.default, 'Properties');
        root.appendChild(propsEl);
    }

    const tagMap: Record<string, string> = {
        displayName: 'DisplayName',
        publisherDisplayName: 'PublisherDisplayName',
        description: 'Description',
        logo: 'Logo',
    };
    const tag = tagMap[field];
    if (!tag) { return; }

    let child = getChildByLocalName(propsEl, tag);

    // Only remove tags for optional fields when value is cleared
    const optionalFields = ['Description'];
    if (!value && child && optionalFields.includes(tag)) {
        removeElementClean(propsEl, child);
        return;
    }

    if (!child) {
        child = doc.createElementNS(NS.default, tag);
        // Insert with proper indentation before the closing whitespace of Properties
        const lastChild = propsEl.lastChild;
        if (lastChild && lastChild.nodeType === 3 && /^\s*$/.test(lastChild.nodeValue || '')) {
            // Insert newline + indent before the element, then element, before trailing whitespace
            propsEl.insertBefore(doc.createTextNode('\n    '), lastChild);
            propsEl.insertBefore(child, lastChild);
        } else {
            propsEl.appendChild(doc.createTextNode('\n    '));
            propsEl.appendChild(child);
            propsEl.appendChild(doc.createTextNode('\n  '));
        }
    }
    // Replace text content
    while (child.firstChild) { child.removeChild(child.firstChild); }
    child.appendChild(doc.createTextNode(value));
}

function applyDependenciesChange(root: Element, field: string, value: string, index: number): void {
    const depsEl = getChildByLocalName(root, 'Dependencies');
    if (!depsEl) { return; }

    if (field.startsWith('targetDeviceFamily.')) {
        const subField = field.replace('targetDeviceFamily.', '');
        const families = getChildrenByLocalName(depsEl, 'TargetDeviceFamily');
        if (index < families.length) {
            const attrMap: Record<string, string> = {
                name: 'Name',
                minVersion: 'MinVersion',
                maxVersionTested: 'MaxVersionTested',
            };
            const attr = attrMap[subField];
            if (attr) { families[index].setAttribute(attr, value); }
        }
    } else if (field.startsWith('packageDependency.')) {
        const subField = field.replace('packageDependency.', '');
        const deps = getChildrenByLocalName(depsEl, 'PackageDependency');
        if (index < deps.length) {
            const attrMap: Record<string, string> = {
                name: 'Name',
                minVersion: 'MinVersion',
                publisher: 'Publisher',
            };
            const attr = attrMap[subField];
            if (attr) { deps[index].setAttribute(attr, value); }
        }
    }
}

function applyApplicationChange(root: Element, field: string, value: string, index: number): void {
    const appsEl = getChildByLocalName(root, 'Applications');
    if (!appsEl) { return; }

    const apps = getChildrenByLocalName(appsEl, 'Application');
    if (index >= apps.length) { return; }
    const appEl = apps[index];

    // Top-level Application attributes
    const appAttrMap: Record<string, string> = {
        id: 'Id',
        executable: 'Executable',
        entryPoint: 'EntryPoint',
    };
    if (appAttrMap[field]) {
        appEl.setAttribute(appAttrMap[field], value);
        return;
    }

    // VisualElements attributes
    if (field.startsWith('visualElements.')) {
        const veField = field.replace('visualElements.', '');
        const visualEl = findChildByLocalNameNS(appEl, 'VisualElements');
        if (!visualEl) { return; }

        // DefaultTile attributes
        const defaultTileAttrs: Record<string, string> = {
            wide310x150Logo: 'Wide310x150Logo',
            square71x71Logo: 'Square71x71Logo',
            square310x310Logo: 'Square310x310Logo',
        };
        if (defaultTileAttrs[veField]) {
            let defaultTile = findChildByLocalNameNS(visualEl, 'DefaultTile');
            if (!defaultTile) {
                defaultTile = visualEl.ownerDocument!.createElementNS(NS.uap, 'uap:DefaultTile');
                visualEl.appendChild(defaultTile);
            }
            defaultTile.setAttribute(defaultTileAttrs[veField], value);
            return;
        }

        // LockScreen attribute
        if (veField === 'badgeLogo') {
            let lockScreen = findChildByLocalNameNS(visualEl, 'LockScreen');
            if (!lockScreen) {
                lockScreen = visualEl.ownerDocument!.createElementNS(NS.uap, 'uap:LockScreen');
                lockScreen.setAttribute('Notification', 'badge');
                visualEl.appendChild(lockScreen);
            }
            lockScreen.setAttribute('BadgeLogo', value);
            return;
        }

        // SplashScreen attribute
        if (veField === 'splashScreenImage') {
            let splashScreen = findChildByLocalNameNS(visualEl, 'SplashScreen');
            if (!splashScreen) {
                splashScreen = visualEl.ownerDocument!.createElementNS(NS.uap, 'uap:SplashScreen');
                visualEl.appendChild(splashScreen);
            }
            splashScreen.setAttribute('Image', value);
            return;
        }

        const attrMap: Record<string, string> = {
            displayName: 'DisplayName',
            description: 'Description',
            backgroundColor: 'BackgroundColor',
            square150x150Logo: 'Square150x150Logo',
            square44x44Logo: 'Square44x44Logo',
        };
        const attr = attrMap[veField];
        if (attr) { visualEl.setAttribute(attr, value); }
    }
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

/** Check if an element matches the expected capability namespace. */
function matchesCapabilityNamespace(el: Element, capNs: string): boolean {
    if (capNs === 'device') { return el.localName === 'DeviceCapability'; }
    if (capNs === 'rescap') { return (el.prefix ?? '') === 'rescap'; }
    return el.localName === 'Capability' && (el.prefix ?? '') !== 'rescap';
}
