/**
 * XML surgery/mutation operations for the manifest editor.
 * All functions perform string-based manipulation to preserve formatting.
 */

import {
    NS,
    CAPABILITY_NS_URIS,
    escapeRegex,
    escapeXmlAttr,
    findParentBounds,
    findDirectChildElementBounds,
    ensureNamespace,
    removeNamespaceIfUnused,
    swapAdjacentElements,
    findNthApplicationRegion,
    detectIndent,
    getCapabilityElementInfo,
} from './xml-utils';

import type {
    PackageDependencyData,
    TargetDeviceFamilyData,
    MainPackageDependencyData,
    DriverConstraintData,
    OSPackageDependencyData,
    HostRuntimeDependencyData,
    ExternalDependencyData,
    ResourceData,
} from './manifest-types';

// ─── Internal helpers ───────────────────────────────────────────────

/**
 * Find the bounds of a package-level element (direct child of <Package>), skipping
 * any matches that are nested inside <Applications>…</Applications>.
 * Falls back to findParentBounds if no <Applications> section exists.
 */
function findPackageLevelParentBounds(xml: string, localName: string): { openStart: number; contentStart: number; contentEnd: number; closeEnd: number } | null {
    // Find the <Applications> region to exclude
    const appsBounds = findParentBounds(xml, 'Applications');

    const openPattern = new RegExp(`<(?:[a-zA-Z0-9]+:)?${escapeRegex(localName)}\\b`, 'g');
    let match: RegExpExecArray | null;
    while ((match = openPattern.exec(xml)) !== null) {
        const openStart = match.index;

        // Skip matches inside the <Applications> region
        if (appsBounds && openStart > appsBounds.openStart && openStart < appsBounds.closeEnd) {
            continue;
        }

        const gt = xml.indexOf('>', openStart);
        if (gt === -1) { continue; }
        if (xml[gt - 1] === '/') { continue; } // self-closing
        const contentStart = gt + 1;
        const closePattern = new RegExp(`</(?:[a-zA-Z0-9]+:)?${escapeRegex(localName)}\\s*>`);
        const closeMatch = closePattern.exec(xml.substring(contentStart));
        if (!closeMatch) { continue; }
        const contentEnd = contentStart + closeMatch.index;
        const closeEnd = contentEnd + closeMatch[0].length;
        return { openStart, contentStart, contentEnd, closeEnd };
    }
    return null;
}

/**
 * Expand a self-closing element like `<Capabilities />` into `<Capabilities>\n</Capabilities>`.
 * Returns the original XML unchanged if the element is not self-closing or not found.
 */
function expandSelfClosingElement(xml: string, localName: string): string {
    const pattern = new RegExp(`(<(?:[a-zA-Z0-9]+:)?${escapeRegex(localName)}\\b[^>]*)\\s*/>`);
    const match = pattern.exec(xml);
    if (!match) { return xml; }
    const tagName = match[0].match(/<([a-zA-Z0-9:]+)/)?.[1] ?? localName;
    const indent = detectIndent(xml, match.index);
    return xml.substring(0, match.index)
        + match[1] + '>\n'
        + indent + `</${tagName}>`
        + xml.substring(match.index + match[0].length);
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
export function insertChildBeforeClose(xml: string, closeTagPos: number, childXml: string, parentIndent: string): string {
    const childIndent = parentIndent + '  ';
    let lineStart = closeTagPos;
    while (lineStart > 0 && xml[lineStart - 1] !== '\n') { lineStart--; }
    return xml.substring(0, lineStart) + childIndent + childXml + '\n' + xml.substring(lineStart);
}

/** Check if an element tag string matches the expected capability namespace prefix. */
function matchesCapabilityTag(elemXml: string, capNs: string): boolean {
    if (capNs === 'device') { return /^<DeviceCapability\b/.test(elemXml); }
    if (capNs === 'uap4:custom') { return /^<uap4:CustomCapability\b/.test(elemXml); }
    if (capNs === '') { return /^<Capability\b/.test(elemXml); }
    const prefixPattern = new RegExp(`^<${escapeRegex(capNs)}:Capability\\b`);
    return prefixPattern.test(elemXml);
}

/** Check if an element tag string has a Name attribute with the given value. */
function hasNameAttribute(elemXml: string, name: string): boolean {
    const regex = new RegExp(`\\bName\\s*=\\s*["']${escapeRegex(name)}["']`);
    return regex.test(elemXml);
}

/** Parse a capability string into its namespace and name parts. */
function parseCapabilityString(capability: string): { attrName: string; namespace: string } {
    if (capability.startsWith('device:')) {
        return { attrName: capability.replace('device:', ''), namespace: 'device' };
    }
    const colonIdx = capability.indexOf(':');
    if (colonIdx > 0) {
        return { attrName: capability.substring(colonIdx + 1), namespace: capability.substring(0, colonIdx) };
    }
    // Custom capability: company.name_publisherId → uap4:CustomCapability
    if (/^[a-zA-Z0-9]+(\.[a-zA-Z0-9]+)+_[a-z0-9]{13}$/.test(capability)) {
        return { attrName: capability, namespace: 'uap4:custom' };
    }
    return { attrName: capability, namespace: '' };
}

/** Remove nth child matching tagPattern within a parent section. */
export function removeNthChildByTag(xmlText: string, parentLocalName: string, tagPattern: RegExp, index: number, findParent?: typeof findParentBounds): string {
    const find = findParent ?? findParentBounds;
    const bounds = find(xmlText, parentLocalName);
    if (!bounds) { return xmlText; }
    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const items = children.filter(c => tagPattern.test(xmlText.substring(c.start, c.end)));
    if (index < 0 || index >= items.length) { return xmlText; }
    return removeElementWithWhitespace(xmlText, items[index].start, items[index].end, bounds.contentStart);
}

/** Move nth child matching tagPattern within a parent section. */
function moveNthChildByTag(xmlText: string, parentLocalName: string, tagPattern: RegExp, index: number, direction: 'up' | 'down', findParent?: typeof findParentBounds): string {
    const find = findParent ?? findParentBounds;
    const bounds = find(xmlText, parentLocalName);
    if (!bounds) { return xmlText; }
    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const items = children.filter(c => tagPattern.test(xmlText.substring(c.start, c.end)));
    const swapIdx = direction === 'up' ? index - 1 : index + 1;
    if (index < 0 || index >= items.length || swapIdx < 0 || swapIdx >= items.length) { return xmlText; }
    return swapAdjacentElements(xmlText, items[index], items[swapIdx]);
}

/** Insert a child element into a section, creating the section if needed. */
function addChildToSection(xmlText: string, sectionName: string, childXml: string, opts?: { expandSelfClosing?: boolean; findParent?: typeof findParentBounds; createIfMissing?: boolean }): string {
    let result = xmlText;
    if (opts?.expandSelfClosing) {
        result = expandSelfClosingElement(result, sectionName);
    }
    const find = opts?.findParent ?? findParentBounds;
    const bounds = find(result, sectionName);
    if (bounds) {
        const parentIndent = detectIndent(result, bounds.openStart);
        return insertChildBeforeClose(result, bounds.contentEnd, childXml, parentIndent);
    }
    if (opts?.createIfMissing === false) { return result; }
    const pkgClose = result.lastIndexOf('</Package>');
    if (pkgClose < 0) { return result; }
    const pkgIndent = detectIndent(result, pkgClose);
    const parentIndent = pkgIndent + '  ';
    const block = parentIndent + '<' + sectionName + '>\n' +
        parentIndent + '  ' + childXml + '\n' +
        parentIndent + '</' + sectionName + '>\n';
    let lineStart = pkgClose;
    while (lineStart > 0 && result[lineStart - 1] !== '\n') { lineStart--; }
    return result.substring(0, lineStart) + block + result.substring(lineStart);
}

/** Ensure a prefix is listed in the IgnorableNamespaces attribute on Package. */
function ensureIgnorableNamespace(xmlText: string, prefix: string): string {
    const pkgMatch = /<Package\b[^>]*>/s.exec(xmlText);
    if (!pkgMatch) { return xmlText; }
    const pkgTag = pkgMatch[0];

    const ignorableMatch = /IgnorableNamespaces="([^"]*)"/.exec(pkgTag);
    if (ignorableMatch) {
        const namespaces = ignorableMatch[1].split(/\s+/);
        if (namespaces.includes(prefix)) { return xmlText; }
        const newAttr = `IgnorableNamespaces="${ignorableMatch[1]} ${prefix}"`;
        const newTag = pkgTag.replace(ignorableMatch[0], newAttr);
        return xmlText.substring(0, pkgMatch.index) + newTag + xmlText.substring(pkgMatch.index + pkgTag.length);
    }

    // No IgnorableNamespaces attribute — add one
    const pkgTagInner = /<Package\b/.exec(pkgTag);
    const newTag = pkgTag.substring(0, pkgTagInner!.index + pkgTagInner![0].length) + ` IgnorableNamespaces="${prefix}"` + pkgTag.substring(pkgTagInner!.index + pkgTagInner![0].length);
    return xmlText.substring(0, pkgMatch.index) + newTag + xmlText.substring(pkgMatch.index + pkgTag.length);
}

/** Ensure both the namespace declaration and IgnorableNamespaces entry for a prefix. */
function ensureNamespaceWithIgnorable(xmlText: string, prefix: string, uri: string): string {
    let result = ensureNamespace(xmlText, prefix, uri);
    result = ensureIgnorableNamespace(result, prefix);
    return result;
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

// ─── Exported operations ────────────────────────────────────────────

/** Add a capability element to the XML. */
export function addCapability(xmlText: string, capability: string): string {
    const { elementName, attrName } = getCapabilityElementInfo(capability);
    const childXml = `<${elementName} Name="${escapeXmlAttr(attrName)}" />`;

    let result = xmlText;

    // Ensure namespace is declared for prefixed capabilities
    const colonIdx = capability.indexOf(':');
    if (colonIdx > 0 && !capability.startsWith('device:')) {
        const prefix = capability.substring(0, colonIdx);
        const nsUri = CAPABILITY_NS_URIS[prefix];
        if (nsUri) {
            result = ensureNamespace(result, prefix, nsUri);
        }
    }

    // Custom capabilities (no prefix) need uap4 namespace for uap4:CustomCapability element
    if (elementName === 'uap4:CustomCapability') {
        result = ensureNamespace(result, 'uap4', CAPABILITY_NS_URIS['uap4']);
    }

    return addChildToSection(result, 'Capabilities', childXml, { expandSelfClosing: true, findParent: findPackageLevelParentBounds });
}

/** Remove a capability element from the XML. */
export function removeCapability(xmlText: string, capability: string): string {
    const bounds = findPackageLevelParentBounds(xmlText, 'Capabilities');
    if (!bounds) { return xmlText; }

    const { attrName, namespace: capNs } = parseCapabilityString(capability);
    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);

    // Determine which tag patterns to try. For unprefixed capabilities (capNs === ''
    // or 'uap4:custom'), also check uap4:CustomCapability since the parser stores
    // CustomCapability elements without a prefix.
    const tagsToTry: string[] = [capNs];
    if (capNs === '' || capNs === 'uap4:custom') {
        if (!tagsToTry.includes('')) { tagsToTry.push(''); }
        if (!tagsToTry.includes('uap4:custom')) { tagsToTry.push('uap4:custom'); }
    }

    // Search backwards (last match first, same as original behavior)
    for (let i = children.length - 1; i >= 0; i--) {
        const child = children[i];
        const childXml = xmlText.substring(child.start, child.end);
        if (!hasNameAttribute(childXml, attrName)) { continue; }
        if (!tagsToTry.some(ns => matchesCapabilityTag(childXml, ns))) { continue; }
        const result = removeElementWithWhitespace(xmlText, child.start, child.end, bounds.contentStart);
        // Clean up the namespace declaration if no longer used
        if (capNs && capNs !== 'uap4:custom') {
            const nsPrefix = capNs.includes(':') ? capNs.split(':')[0] : capNs;
            if (CAPABILITY_NS_URIS[nsPrefix]) {
                return removeNamespaceIfUnused(result, nsPrefix);
            }
        }
        return result;
    }

    return xmlText;
}

/** Add a PackageDependency element. */
export function addPackageDependency(xmlText: string, dep: PackageDependencyData): string {
    let result = xmlText;
    let attrs = `Name="${escapeXmlAttr(dep.name)}"`;
    if (dep.minVersion) { attrs += ` MinVersion="${escapeXmlAttr(dep.minVersion)}"`; }
    if (dep.publisher) { attrs += ` Publisher="${escapeXmlAttr(dep.publisher)}"`; }
    if (dep.optional === 'true' || dep.optional === 'false') {
        attrs += ` uap6:Optional="${dep.optional}"`;
        result = ensureNamespace(result, 'uap6', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/6');
    }
    const childXml = `<PackageDependency ${attrs} />`;
    return addChildToSection(result, 'Dependencies', childXml, { expandSelfClosing: true });
}

/** Remove a PackageDependency element by index. */
export function removePackageDependency(xmlText: string, index: number): string {
    return removeNthChildByTag(xmlText, 'Dependencies', /^<PackageDependency\b/, index);
}

/** Add a TargetDeviceFamily element. */
export function addTargetDeviceFamily(xmlText: string, family: TargetDeviceFamilyData): string {
    const childXml = `<TargetDeviceFamily Name="${escapeXmlAttr(family.name)}" MinVersion="${escapeXmlAttr(family.minVersion)}" MaxVersionTested="${escapeXmlAttr(family.maxVersionTested)}" />`;
    return addChildToSection(xmlText, 'Dependencies', childXml);
}

/** Remove a TargetDeviceFamily element by index. */
export function removeTargetDeviceFamily(xmlText: string, index: number): string {
    return removeNthChildByTag(xmlText, 'Dependencies', /^<TargetDeviceFamily\b/, index);
}

/** Move a TargetDeviceFamily element up or down by one position. */
export function moveTargetDeviceFamily(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Dependencies', /^<TargetDeviceFamily\b/, index, direction);
}

/** Move a PackageDependency element up or down by one position. */
export function movePackageDependency(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Dependencies', /^<PackageDependency\b/, index, direction);
}

// ── MainPackageDependency (uap3) ──

/** Add a uap3:MainPackageDependency element. */
export function addMainPackageDependency(xmlText: string, dep: MainPackageDependencyData): string {
    let result = ensureNamespace(xmlText, 'uap3', NS.uap3);
    const childXml = `<uap3:MainPackageDependency Name="${escapeXmlAttr(dep.name)}" />`;
    return addChildToSection(result, 'Dependencies', childXml, { createIfMissing: false });
}

/** Remove a uap3:MainPackageDependency by index. */
export function removeMainPackageDependency(xmlText: string, index: number): string {
    const result = removeNthChildByTag(xmlText, 'Dependencies', /^<uap3:MainPackageDependency\b/, index);
    return result !== xmlText ? removeNamespaceIfUnused(result, 'uap3') : result;
}

/** Move a uap3:MainPackageDependency up or down by swapping with its neighbor. */
export function moveMainPackageDependency(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Dependencies', /^<uap3:MainPackageDependency\b/, index, direction);
}

// ── DriverDependency (uap5) ──

/** Add a uap5:DriverConstraint, auto-creating the single DriverDependency wrapper if needed. */
export function addDriverConstraint(xmlText: string, constraint: DriverConstraintData): string {
    let result = ensureNamespace(xmlText, 'uap5', NS.uap5);
    const bounds = findParentBounds(result, 'Dependencies');
    if (!bounds) { return result; }

    // Check if a DriverDependency wrapper already exists
    const children = findDirectChildElementBounds(result, bounds.contentStart, bounds.contentEnd);
    const driverDeps = children.filter(c => /^<uap5:DriverDependency\b/.test(result.substring(c.start, c.end)));

    let attrs = `Name="${escapeXmlAttr(constraint.name)}"`;
    if (constraint.minVersion) { attrs += ` MinVersion="${escapeXmlAttr(constraint.minVersion)}"`; }
    if (constraint.minDate) { attrs += ` MinDate="${escapeXmlAttr(constraint.minDate)}"`; }
    const constraintXml = `<uap5:DriverConstraint ${attrs} />`;

    if (driverDeps.length === 0) {
        // Create wrapper with the constraint inside
        const parentIndent = detectIndent(result, bounds.openStart);
        const childIndent = parentIndent + '  ';
        const grandchildIndent = childIndent + '  ';
        const wrapperXml = `<uap5:DriverDependency>\n${grandchildIndent}${constraintXml}\n${childIndent}</uap5:DriverDependency>`;
        return insertChildBeforeClose(result, bounds.contentEnd, wrapperXml, parentIndent);
    }

    // Append to the first (only) DriverDependency
    const dd = driverDeps[0];
    const ddText = result.substring(dd.start, dd.end);
    const closeTag = '</uap5:DriverDependency>';
    const closeIdx = ddText.lastIndexOf(closeTag);
    if (closeIdx < 0) { return result; }
    const closePos = dd.start + closeIdx;
    const ddIndent = detectIndent(result, dd.start);
    const constraintIndent = ddIndent + '  ';

    // Walk back to find start of whitespace before the close tag
    let wsStart = closePos;
    while (wsStart > 0 && result[wsStart - 1] !== '\n') { wsStart--; }

    return result.substring(0, wsStart) + constraintIndent + constraintXml + '\n' + ddIndent + result.substring(closePos);
}

/** Remove a uap5:DriverConstraint by flat index. Removes the DriverDependency wrapper if empty. */
export function removeDriverConstraint(xmlText: string, index: number): string {
    const bounds = findParentBounds(xmlText, 'Dependencies');
    if (!bounds) { return xmlText; }
    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const driverDeps = children.filter(c => /^<uap5:DriverDependency\b/.test(xmlText.substring(c.start, c.end)));

    // Collect all constraints across all DriverDependency elements with their parent info
    let flatIdx = 0;
    for (const dd of driverDeps) {
        const ddContentStart = xmlText.indexOf('>', dd.start) + 1;
        const ddContentEnd = xmlText.lastIndexOf('</uap5:DriverDependency>', dd.end);
        if (ddContentEnd < 0) { continue; }
        const constraints = findDirectChildElementBounds(xmlText, ddContentStart, ddContentEnd);
        const dcItems = constraints.filter(c => /^<uap5:DriverConstraint\b/.test(xmlText.substring(c.start, c.end)));
        for (let i = 0; i < dcItems.length; i++) {
            if (flatIdx === index) {
                let result = removeElementWithWhitespace(xmlText, dcItems[i].start, dcItems[i].end, ddContentStart);
                // If this was the last constraint, remove the entire DriverDependency wrapper
                if (dcItems.length === 1) {
                    const newBounds = findParentBounds(result, 'Dependencies');
                    if (newBounds) {
                        const newChildren = findDirectChildElementBounds(result, newBounds.contentStart, newBounds.contentEnd);
                        const newDd = newChildren.filter(c => /^<uap5:DriverDependency\b/.test(result.substring(c.start, c.end)));
                        // Find the corresponding empty wrapper and remove it
                        for (const wrapper of newDd) {
                            const wrapperText = result.substring(wrapper.start, wrapper.end).replace(/\s+/g, '');
                            if (wrapperText === '<uap5:DriverDependency></uap5:DriverDependency>') {
                                result = removeElementWithWhitespace(result, wrapper.start, wrapper.end, newBounds.contentStart);
                                break;
                            }
                        }
                    }
                }
                                return removeNamespaceIfUnused(result, 'uap5');
            }
            flatIdx++;
        }
    }
    return xmlText;
}

/** Collect all uap5:DriverConstraint elements across all DriverDependency wrappers. */
function collectDriverConstraints(xmlText: string): { start: number; end: number }[] {
    const bounds = findParentBounds(xmlText, 'Dependencies');
    if (!bounds) { return []; }
    const children = findDirectChildElementBounds(xmlText, bounds.contentStart, bounds.contentEnd);
    const driverDeps = children.filter(c => /^<uap5:DriverDependency\b/.test(xmlText.substring(c.start, c.end)));
    const all: { start: number; end: number }[] = [];
    for (const dd of driverDeps) {
        const ddContentStart = xmlText.indexOf('>', dd.start) + 1;
        const ddContentEnd = xmlText.lastIndexOf('</uap5:DriverDependency>', dd.end);
        if (ddContentEnd < 0) { continue; }
        const constraints = findDirectChildElementBounds(xmlText, ddContentStart, ddContentEnd);
        all.push(...constraints.filter(c => /^<uap5:DriverConstraint\b/.test(xmlText.substring(c.start, c.end))));
    }
    return all;
}

/** Move a uap5:DriverConstraint up or down by flat index. */
export function moveDriverConstraint(xmlText: string, index: number, direction: 'up' | 'down'): string {
    const allConstraints = collectDriverConstraints(xmlText);
    const swapIdx = direction === 'up' ? index - 1 : index + 1;
    if (index < 0 || index >= allConstraints.length || swapIdx < 0 || swapIdx >= allConstraints.length) { return xmlText; }
    return swapAdjacentElements(xmlText, allConstraints[index], allConstraints[swapIdx]);
}

// ── OSPackageDependency (uap7) ──

/** Add a uap7:OSPackageDependency element. */
export function addOSPackageDependency(xmlText: string, dep: OSPackageDependencyData): string {
    let result = ensureNamespace(xmlText, 'uap7', NS.uap7);
    let attrs = `Name="${escapeXmlAttr(dep.name)}"`;
    if (dep.version) { attrs += ` Version="${escapeXmlAttr(dep.version)}"`; }
    const childXml = `<uap7:OSPackageDependency ${attrs} />`;
    return addChildToSection(result, 'Dependencies', childXml, { createIfMissing: false });
}

/** Remove a uap7:OSPackageDependency by index. */
export function removeOSPackageDependency(xmlText: string, index: number): string {
    const result = removeNthChildByTag(xmlText, 'Dependencies', /^<uap7:OSPackageDependency\b/, index);
    return result !== xmlText ? removeNamespaceIfUnused(result, 'uap7') : result;
}

/** Move a uap7:OSPackageDependency up or down by swapping with its neighbor. */
export function moveOSPackageDependency(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Dependencies', /^<uap7:OSPackageDependency\b/, index, direction);
}

// ── HostRuntimeDependency (uap10) ──

/** Add a uap10:HostRuntimeDependency element. */
export function addHostRuntimeDependency(xmlText: string, dep: HostRuntimeDependencyData): string {
    let result = ensureNamespace(xmlText, 'uap10', NS.uap10);
    let attrs = `Name="${escapeXmlAttr(dep.name)}"`;
    if (dep.publisher) { attrs += ` Publisher="${escapeXmlAttr(dep.publisher)}"`; }
    if (dep.minVersion) { attrs += ` MinVersion="${escapeXmlAttr(dep.minVersion)}"`; }
    const childXml = `<uap10:HostRuntimeDependency ${attrs} />`;
    return addChildToSection(result, 'Dependencies', childXml, { createIfMissing: false });
}

/** Remove a uap10:HostRuntimeDependency by index. */
export function removeHostRuntimeDependency(xmlText: string, index: number): string {
    const result = removeNthChildByTag(xmlText, 'Dependencies', /^<uap10:HostRuntimeDependency\b/, index);
    return result !== xmlText ? removeNamespaceIfUnused(result, 'uap10') : result;
}

/** Move a uap10:HostRuntimeDependency up or down by swapping with its neighbor. */
export function moveHostRuntimeDependency(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Dependencies', /^<uap10:HostRuntimeDependency\b/, index, direction);
}

// ── ExternalDependency (win32dependencies) ──

/** Add a win32dependencies:ExternalDependency element. */
export function addExternalDependency(xmlText: string, dep: ExternalDependencyData): string {
    let result = ensureNamespace(xmlText, 'win32dependencies', NS.win32dependencies);
    let attrs = `Name="${escapeXmlAttr(dep.name)}"`;
    if (dep.publisher) { attrs += ` Publisher="${escapeXmlAttr(dep.publisher)}"`; }
    if (dep.minVersion) { attrs += ` MinVersion="${escapeXmlAttr(dep.minVersion)}"`; }
    if (dep.optional === 'true' || dep.optional === 'false') { attrs += ` Optional="${dep.optional}"`; }
    const childXml = `<win32dependencies:ExternalDependency ${attrs} />`;
    return addChildToSection(result, 'Dependencies', childXml, { createIfMissing: false });
}

/** Remove a win32dependencies:ExternalDependency by index. */
export function removeExternalDependency(xmlText: string, index: number): string {
    const result = removeNthChildByTag(xmlText, 'Dependencies', /^<win32dependencies:ExternalDependency\b/, index);
    return result !== xmlText ? removeNamespaceIfUnused(result, 'win32dependencies') : result;
}

/** Move a win32dependencies:ExternalDependency up or down by swapping with its neighbor. */
export function moveExternalDependency(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Dependencies', /^<win32dependencies:ExternalDependency\b/, index, direction);
}

/** Add a Resource element to the XML. */
export function addResource(xmlText: string, resource: ResourceData): string {
    let attrs = '';
    if (resource.language) { attrs += ` Language="${resource.language}"`; }
    if (resource.scale) { attrs += ` uap:Scale="${resource.scale}"`; }
    if (resource.dxFeatureLevel) { attrs += ` uap:DXFeatureLevel="${resource.dxFeatureLevel}"`; }
    let result = xmlText;
    if (resource.scale || resource.dxFeatureLevel) {
        result = ensureNamespace(result, 'uap', NS.uap);
    }
    const childXml = `<Resource${attrs} />`;
    return addChildToSection(result, 'Resources', childXml, { expandSelfClosing: true });
}

/** Remove a Resource element by index. */
export function removeResource(xmlText: string, index: number): string {
    return removeNthChildByTag(xmlText, 'Resources', /^<Resource\b/, index);
}

/** Move a Resource element up or down by one position. */
export function moveResource(xmlText: string, index: number, direction: 'up' | 'down'): string {
    return moveNthChildByTag(xmlText, 'Resources', /^<Resource\b/, index, direction);
}

/** Add an mp:PhoneIdentity element to the manifest. */
export function addPhoneIdentity(xmlText: string): string {
    // Check if PhoneIdentity already exists
    if (/<[a-zA-Z0-9]*:?PhoneIdentity\b/s.test(xmlText)) { return xmlText; }

    // Generate a random GUID for PhoneProductId, omit PhonePublisherId (user can add it later)
    const productId = '00000000-0000-0000-0000-000000000000';

    // Ensure mp namespace is declared
    let result = ensureNamespaceWithIgnorable(xmlText, 'mp', 'http://schemas.microsoft.com/appx/2014/phone/manifest');

    const phoneElement = `<mp:PhoneIdentity PhoneProductId="${productId}" />`;

    // Insert after <Identity .../> element
    const identityPattern = /<(?:[a-zA-Z0-9]+:)?Identity\b[^>]*\/>/s;
    const identityMatch = identityPattern.exec(result);
    if (identityMatch) {
        const insertPos = identityMatch.index + identityMatch[0].length;
        const indent = detectIndent(result, identityMatch.index);
        return result.substring(0, insertPos) + '\n' + indent + phoneElement + result.substring(insertPos);
    }

    // Fallback: insert before </Package>
    const pkgClose = result.lastIndexOf('</Package>'); // lgtm[js/incomplete-multi-character-sanitization]
    if (pkgClose < 0) { return result; }
    const pkgIndent = detectIndent(result, pkgClose);
    const childIndent = pkgIndent + '  ';
    let lineStart = pkgClose;
    while (lineStart > 0 && result[lineStart - 1] !== '\n') { lineStart--; }
    return result.substring(0, lineStart) + childIndent + phoneElement + '\n' + result.substring(lineStart);
}

/** Remove the mp:PhoneIdentity element from the manifest. */
export function removePhoneIdentity(xmlText: string): string {
    const pattern = /[ \t]*<[a-zA-Z0-9]*:?PhoneIdentity\b[^>]*(?:\/>|>[^<]*<\/[a-zA-Z0-9]*:?PhoneIdentity\s*>)[ \t]*\r?\n?/s;
    const match = pattern.exec(xmlText);
    if (!match) { return xmlText; }
    const result = xmlText.substring(0, match.index) + xmlText.substring(match.index + match[0].length);
    return removeNamespaceIfUnused(result, 'mp');
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

/**
 * Remove an optional visual asset from the nth Application.
 * For DefaultTile attributes (wide310x150Logo, square71x71Logo, square310x310Logo):
 *   removes the attribute, and if DefaultTile has no remaining attributes, removes the element.
 * For LockScreen (badgeLogo): removes the entire LockScreen element.
 * For SplashScreen (splashScreenImage): removes the entire SplashScreen element.
 */
export function removeVisualAsset(xmlText: string, appIndex: number, veField: string): string {
    const appRegion = findNthApplicationRegion(xmlText, appIndex);
    if (!appRegion) { return xmlText; }
    const appXml = xmlText.substring(appRegion.start, appRegion.end);

    const defaultTileAttrs: Record<string, string> = {
        wide310x150Logo: 'Wide310x150Logo',
        square71x71Logo: 'Square71x71Logo',
        square310x310Logo: 'Square310x310Logo',
    };

    if (defaultTileAttrs[veField]) {
        // Remove the attribute from DefaultTile
        const dtPattern = /<[a-zA-Z0-9]*:?DefaultTile\b[^>]*?\/?>/s;
        const dtMatch = dtPattern.exec(appXml);
        if (!dtMatch) { return xmlText; }
        const attrName = defaultTileAttrs[veField];
        const attrPattern = new RegExp(`\\s*${attrName}="[^"]*"`);
        const newDtTag = dtMatch[0].replace(attrPattern, '');
        let result = xmlText.substring(0, appRegion.start) + appXml.replace(dtMatch[0], newDtTag) + xmlText.substring(appRegion.end);
        // If DefaultTile has no remaining content attributes, remove the element entirely
        const cleanDtMatch = dtPattern.exec(result.substring(appRegion.start, appRegion.start + appXml.length + 50));
        if (cleanDtMatch) {
            const defaultTileTagMatch = /^<[a-zA-Z0-9]*:?DefaultTile\b([^>]*)\/?>$/s.exec(cleanDtMatch[0]);
            const tagContent = (defaultTileTagMatch?.[1] ?? '').trim();
            if (!tagContent) {
                // Remove the entire DefaultTile element and its surrounding whitespace
                const absStart = appRegion.start + cleanDtMatch.index;
                let removeStart = absStart;
                while (removeStart > 0 && (result[removeStart - 1] === ' ' || result[removeStart - 1] === '\t')) { removeStart--; }
                if (removeStart > 0 && result[removeStart - 1] === '\n') { removeStart--; }
                if (removeStart > 0 && result[removeStart - 1] === '\r') { removeStart--; }
                result = result.substring(0, removeStart) + result.substring(absStart + cleanDtMatch[0].length);
            }
        }
        return result;
    }

    if (veField === 'badgeLogo') {
        // Remove the entire LockScreen element
        const lsPattern = /[ \t]*<[a-zA-Z0-9]*:?LockScreen\b[^>]*?\/?>[^\n]*\r?\n?/s;
        const lsMatch = lsPattern.exec(appXml);
        if (!lsMatch) { return xmlText; }
        const absPos = appRegion.start + lsMatch.index;
        return xmlText.substring(0, absPos) + xmlText.substring(absPos + lsMatch[0].length);
    }

    if (veField === 'splashScreenImage') {
        // Remove the entire SplashScreen element
        const ssPattern = /[ \t]*<[a-zA-Z0-9]*:?SplashScreen\b[^>]*?\/?>[^\n]*\r?\n?/s;
        const ssMatch = ssPattern.exec(appXml);
        if (!ssMatch) { return xmlText; }
        const absPos = appRegion.start + ssMatch.index;
        return xmlText.substring(0, absPos) + xmlText.substring(absPos + ssMatch[0].length);
    }

    return xmlText;
}

// ─── Re-exports from split files ────────────────────────────────────
export { addApplication, removeApplication, addExtension, removeExtension, updateExtensionField } from './manifest-xml-ops-applications';
