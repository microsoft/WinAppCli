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
    };
}

/**
 * Apply a field change to the XML text and return the updated XML string.
 * Uses xmldom to parse, modify, and re-serialize.
 */
export function applyFieldChange(
    xmlText: string,
    section: string,
    field: string,
    value: string,
    index?: number,
): string {
    const doc = new DOMParser().parseFromString(xmlText, 'application/xml');
    const root = doc.documentElement!;

    switch (section) {
        case 'identity':
            applyIdentityChange(root, field, value);
            break;
        case 'properties':
            applyPropertiesChange(root, doc, field, value);
            break;
        case 'dependencies':
            applyDependenciesChange(root, field, value, index ?? 0);
            break;
        case 'applications':
            applyApplicationChange(root, field, value, index ?? 0);
            break;
    }

    return new XMLSerializer().serializeToString(doc);
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

/** Add an extension element to an application by index. */
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
    const indentMatch = result.match(/^( +)<Extensions>/m);
    const extIndent = indentMatch ? indentMatch[1] : '      ';
    const childIndent = extIndent + '  ';
    // Preserve the template's relative indentation, just add the base indent
    const indentedExt = extensionXml.split('\n').map(line => childIndent + line).join('\n');

    if (hasExtensions) {
        // Insert before the closing </Extensions> tag
        const closeTag = '</Extensions>';
        const closeIdx = result.lastIndexOf(closeTag);
        if (closeIdx < 0) { return result; }
        // Trim trailing whitespace before the close tag so we don't double-indent
        const beforeClose = result.substring(0, closeIdx).replace(/[ \t]+$/, '');
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

        const appIndent = extIndent.substring(0, extIndent.length - 2) || '    ';
        const block = extIndent + '<Extensions>\n' +
            indentedExt + '\n' +
            extIndent + '</Extensions>\n' +
            appIndent;
        return result.substring(0, closeIdx) + block + result.substring(closeIdx);
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

    return cleanupBlankLines(new XMLSerializer().serializeToString(doc));
}

/**
 * Update an attribute on an extension element.
 * fieldPath is "ElementName.AttributeName" as produced by parseExtensionFields in the webview.
 */
export function updateExtensionField(
    xmlText: string, appIndex: number, extIndex: number, fieldPath: string, value: string,
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
    const dotIdx = fieldPath.indexOf('.');
    if (dotIdx < 0) { return xmlText; }
    const elemName = fieldPath.substring(0, dotIdx);
    const attrName = fieldPath.substring(dotIdx + 1);

    // Find the matching element by walking the extension tree
    function findElement(el: Element): Element | null {
        if ((el.localName || el.nodeName) === elemName) { return el; }
        const children = el.childNodes;
        for (let i = 0; i < children.length; i++) {
            if (children[i].nodeType === 1) {
                const found = findElement(children[i] as Element);
                if (found) { return found; }
            }
        }
        return null;
    }

    const targetEl = findElement(extRoot);
    if (targetEl) {
        targetEl.setAttribute(attrName, value);
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
                wide310x150Logo: defaultTile?.getAttribute('Wide310x150Logo') ?? '',
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

// ─── Apply changes ──────────────────────────────────────────────────

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
    if (!child) {
        child = doc.createElementNS(NS.default, tag);
        propsEl.appendChild(child);
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

        if (veField === 'wide310x150Logo') {
            // This is on the DefaultTile child element
            let defaultTile = findChildByLocalNameNS(visualEl, 'DefaultTile');
            if (!defaultTile) {
                defaultTile = visualEl.ownerDocument!.createElementNS(NS.uap, 'uap:DefaultTile');
                visualEl.appendChild(defaultTile);
            }
            defaultTile.setAttribute('Wide310x150Logo', value);
        } else {
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
