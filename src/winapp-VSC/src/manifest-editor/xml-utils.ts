/**
 * Low-level XML string manipulation utilities for the manifest editor.
 */

// Common AppxManifest namespace URIs
export const NS = {
    default: 'http://schemas.microsoft.com/appx/manifest/foundation/windows10',
    uap: 'http://schemas.microsoft.com/appx/manifest/uap/windows10',
    uap3: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3',
    uap5: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5',
    uap7: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/7',
    uap10: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10',
    rescap: 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities',
    desktop: 'http://schemas.microsoft.com/appx/manifest/desktop/windows10',
    win32dependencies: 'http://schemas.microsoft.com/appx/manifest/win32dependencies/windows10',
    systemai: 'http://schemas.microsoft.com/appx/manifest/systemai/windows10',
};

/** Namespace URIs for capability prefixes. */
export const CAPABILITY_NS_URIS: Record<string, string> = {
    uap: NS.uap,
    uap2: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/2',
    uap3: NS.uap3,
    uap4: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/4',
    uap5: NS.uap5,
    uap6: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/6',
    uap7: NS.uap7,
    rescap: NS.rescap,
    iot: 'http://schemas.microsoft.com/appx/manifest/iot/windows10',
    systemai: NS.systemai,
};

/** Escape special regex characters in a string. */
export function escapeRegex(s: string): string {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Escape XML-special characters for use in attribute values. */
export function escapeXmlAttr(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
}

export function escapeXmlText(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** Replace an XML attribute value in-place. Returns `null` if the element or attribute is not found. */
export function replaceAttribute(xml: string, elementPattern: RegExp, attrName: string, newValue: string): string | null {
    const escaped = escapeXmlAttr(newValue);
    // Find the element in the XML
    const elementMatch = elementPattern.exec(xml);
    if (!elementMatch) { return null; }

    // Within the matched element, find and replace the attribute value
    const elementStr = elementMatch[0];
    const attrRegex = new RegExp(`(${escapeRegex(attrName)}\\s*=\\s*)(["'])((?:(?!\\2).)*?)\\2`);
    const attrMatch = attrRegex.exec(elementStr);
    if (!attrMatch) { return null; }

    const newElementStr = elementStr.substring(0, attrMatch.index)
        + attrMatch[1] + attrMatch[2] + escaped + attrMatch[2]
        + elementStr.substring(attrMatch.index + attrMatch[0].length);

    return xml.substring(0, elementMatch.index) + newElementStr + xml.substring(elementMatch.index + elementStr.length);
}

/** Remove an XML attribute from an element in-place. Returns the original string if not found. */
export function removeAttribute(xml: string, elementPattern: RegExp, attrName: string): string {
    const elementMatch = elementPattern.exec(xml);
    if (!elementMatch) { return xml; }

    const elementStr = elementMatch[0];
    // Match the attribute with surrounding whitespace (consume leading space)
    const attrRegex = new RegExp(`\\s+${escapeRegex(attrName)}\\s*=\\s*(["'])(?:(?!\\1).)*?\\1`);
    const attrMatch = attrRegex.exec(elementStr);
    if (!attrMatch) { return xml; }

    const newElementStr = elementStr.substring(0, attrMatch.index) + elementStr.substring(attrMatch.index + attrMatch[0].length);
    return xml.substring(0, elementMatch.index) + newElementStr + xml.substring(elementMatch.index + elementStr.length);
}

/** Add a new attribute to an existing XML element. Returns the original string if element not found. */
export function addAttributeToElement(xml: string, elementPattern: RegExp, attrName: string, value: string): string {
    const escaped = escapeXmlAttr(value);
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
        attrText = '\n' + attrIndentMatch[1] + `${attrName}="${escaped}"`;
    } else {
        // Single-line element — append with a space
        attrText = ` ${attrName}="${escaped}"`;
    }

    const newElementStr = elementStr.substring(0, insertPos) + attrText + elementStr.substring(insertPos);
    return xml.substring(0, elementMatch.index) + newElementStr + xml.substring(elementMatch.index + elementStr.length);
}

/** Replace the text content of an XML element in-place. Returns the original string if not found. */
export function replaceElementText(xml: string, tagPattern: RegExp, newValue: string): string {
    const match = tagPattern.exec(xml);
    if (!match) { return xml; }

    // Escape XML-special characters in text content
    const escaped = newValue.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    // match[0] is the full match including tags, match[1] is the opening tag, match[2] is the old text
    return xml.substring(0, match.index) + match[1] + escaped + match[3] + xml.substring(match.index + match[0].length);
}

/** Find the bounds of a parent element by local name (handles optional namespace prefix). */
export function findParentBounds(xml: string, localName: string): { openStart: number; contentStart: number; contentEnd: number; closeEnd: number } | null {
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

/** Find the start/end positions of direct child elements within an XML region. */
export function findDirectChildElementBounds(xml: string, regionStart: number, regionEnd: number): Array<{start: number; end: number}> {
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

        // Skip CDATA sections
        if (xml[lt + 1] === '!' && xml[lt + 2] === '[' && xml.startsWith('CDATA[', lt + 3)) {
            const cdataEnd = xml.indexOf(']]>', lt);
            if (cdataEnd === -1) { break; }
            pos = cdataEnd + 3;
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

            // Skip CDATA sections inside depth tracking
            if (xml[nextLt + 1] === '!' && xml[nextLt + 2] === '[' && xml.startsWith('CDATA[', nextLt + 3)) {
                const ce = xml.indexOf(']]>', nextLt);
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

/** Ensure a namespace declaration is present on the Package element. */
export function ensureNamespace(xmlText: string, prefix: string, uri: string): string {
    const decl = `xmlns:${prefix}="${uri}"`;
    // Check for both double-quoted and single-quoted declarations
    if (xmlText.includes(`xmlns:${prefix}="${uri}"`) || xmlText.includes(`xmlns:${prefix}='${uri}'`)) {
        return xmlText;
    }

    // Find the full <Package ...> opening tag (may span multiple lines)
    const pkgMatch = /<Package\b[^>]*>/s.exec(xmlText);
    if (!pkgMatch) {
        // Fallback: try partial match for malformed XML
        const partialMatch = /<Package\b/.exec(xmlText);
        if (partialMatch) {
            const pos = partialMatch.index + partialMatch[0].length;
            return xmlText.substring(0, pos) + ' ' + decl + xmlText.substring(pos);
        }
        return xmlText;
    }

    const pkgTag = pkgMatch[0];
    const pkgStart = pkgMatch.index;

    // Detect indentation of existing xmlns attributes on continuation lines
    const xmlnsLineMatch = /\n([ \t]+)xmlns[:\s]/.exec(pkgTag);
    if (xmlnsLineMatch) {
        // Insert new xmlns on its own line before the closing > of the Package tag
        // Find the last xmlns attribute line to insert after it
        const indent = xmlnsLineMatch[1];
        // Find the position right before the last attribute or the closing >
        // Strategy: insert before IgnorableNamespaces if present, otherwise before >
        const ignorablePos = pkgTag.indexOf('IgnorableNamespaces=');
        if (ignorablePos > 0) {
            // Find start of that line
            let lineStart = ignorablePos;
            while (lineStart > 0 && pkgTag[lineStart - 1] !== '\n') { lineStart--; }
            const insertPos = pkgStart + lineStart;
            return xmlText.substring(0, insertPos) + indent + decl + '\n' + xmlText.substring(insertPos);
        }
        // No IgnorableNamespaces — insert before the closing >
        const closePos = pkgStart + pkgTag.length - 1; // position of >
        // Check if there's whitespace/newline before >
        let beforeClose = closePos - 1;
        while (beforeClose > pkgStart && (xmlText[beforeClose] === ' ' || xmlText[beforeClose] === '\t')) { beforeClose--; }
        if (xmlText[beforeClose] === '\n' || xmlText[beforeClose] === '\r') {
            // > is on its own or at end of line — insert before it on a new line
            return xmlText.substring(0, closePos) + indent + decl + '\n' + xmlText.substring(closePos);
        }
        // Insert on a new line before >
        return xmlText.substring(0, closePos) + '\n' + indent + decl + xmlText.substring(closePos);
    }

    // No multiline xmlns pattern detected — Package tag is single-line
    // Insert after "<Package " with proper formatting
    // Detect the indent of the <Package line itself
    let pkgLineStart = pkgStart;
    while (pkgLineStart > 0 && xmlText[pkgLineStart - 1] !== '\n') { pkgLineStart--; }
    const pkgIndent = xmlText.substring(pkgLineStart, pkgStart);
    // Use one extra level of indent for attributes (tab or two spaces based on file)
    const tabInFile = xmlText.includes('\t');
    const attrIndent = pkgIndent + (tabInFile ? '\t' : '  ');

    // If there are existing inline xmlns declarations, put the new one after the last one on a new line
    const lastXmlnsInTag = /.*xmlns[^"]*"[^"]*"/s.exec(pkgTag);
    if (lastXmlnsInTag) {
        const afterLastXmlns = pkgStart + lastXmlnsInTag[0].length;
        return xmlText.substring(0, afterLastXmlns) + '\n' + attrIndent + decl + xmlText.substring(afterLastXmlns);
    }

    // Fallback: simple inline insert — use substring splicing to avoid CodeQL
    // false positive on .replace() with angle-bracket patterns (js/incomplete-multi-character-sanitization)
    const pkgFallbackMatch = /<Package\b/.exec(xmlText);
    if (pkgFallbackMatch) {
        const insertPos = pkgFallbackMatch.index + pkgFallbackMatch[0].length;
        return xmlText.substring(0, insertPos) + ' ' + decl + xmlText.substring(insertPos);
    }
    return xmlText;
}

/**
 * Remove an xmlns:prefix declaration from the <Package> tag if the prefix
 * is no longer used anywhere else in the document body.
 * Also removes the prefix from IgnorableNamespaces if present.
 */
export function removeNamespaceIfUnused(xmlText: string, prefix: string): string {
    // Check if the prefix is still used anywhere in the document (e.g., <prefix:Something or prefix:attr)
    const usagePattern = new RegExp(`<${prefix}:|\\b${prefix}:`, 'i');
    // Search the document body (after <Package ...>) for any usage of this prefix
    const pkgMatch = /<Package\b[^>]*>/s.exec(xmlText);
    if (!pkgMatch) { return xmlText; }
    const bodyStart = pkgMatch.index + pkgMatch[0].length;
    const body = xmlText.substring(bodyStart);
    if (usagePattern.test(body)) {
        return xmlText; // prefix still in use
    }

    // Remove the xmlns:prefix="..." declaration from the Package tag
    let result = xmlText;
    const pkgTag = pkgMatch[0];
    const declPattern = new RegExp(`\\s*xmlns:${prefix}=["'][^"']*["']`);
    const declMatch = declPattern.exec(pkgTag);
    if (!declMatch) { return xmlText; }

    const declStart = pkgMatch.index + declMatch.index;
    const declEnd = declStart + declMatch[0].length;
    result = result.substring(0, declStart) + result.substring(declEnd);

    // Also remove prefix from IgnorableNamespaces attribute
    const ignorablePattern = /IgnorableNamespaces=["']([^"']*)["']/;
    const ignorableMatch = ignorablePattern.exec(result);
    if (ignorableMatch) {
        const namespaces = ignorableMatch[1].split(/\s+/).filter(ns => ns !== prefix);
        if (namespaces.length === 0) {
            // Remove the entire IgnorableNamespaces attribute
            const attrPattern = new RegExp(`\\s*IgnorableNamespaces=["'][^"']*["']`);
            result = result.replace(attrPattern, '');
        } else {
            const newAttr = `IgnorableNamespaces="${namespaces.join(' ')}"`;
            result = result.replace(ignorablePattern, newAttr);
        }
    }

    return result;
}
export function swapAdjacentElements(xmlText: string, a: { start: number; end: number }, b: { start: number; end: number }): string {
    // a must come before b
    const first = a.start < b.start ? a : b;
    const second = a.start < b.start ? b : a;
    const firstText = xmlText.substring(first.start, first.end);
    const secondText = xmlText.substring(second.start, second.end);
    return xmlText.substring(0, first.start) + secondText + xmlText.substring(first.end, second.start) + firstText + xmlText.substring(second.end);
}

/** Find the start/end positions of the nth Application element. */
export function findNthApplicationRegion(xml: string, index: number): { start: number; end: number } | null {
    const bounds = findParentBounds(xml, 'Applications');
    if (!bounds) { return null; }
    const children = findDirectChildElementBounds(xml, bounds.contentStart, bounds.contentEnd);
    const apps = children.filter(c => /^<Application\b/.test(xml.substring(c.start, c.end)));
    if (index < 0 || index >= apps.length) { return null; }
    return apps[index];
}

/** Detect the indentation of the line containing the given position. */
export function detectIndent(xml: string, pos: number): string {
    const lineStart = xml.lastIndexOf('\n', pos - 1);
    if (lineStart === -1) { return ''; }
    const lineContent = xml.substring(lineStart + 1, pos);
    const match = /^(\s*)/.exec(lineContent);
    return match ? match[1] : '';
}

/** Determine the element info for creating a capability XML element. */
export function getCapabilityElementInfo(capability: string): { elementName: string; ns: string | null; attrName: string } {
    if (capability.startsWith('device:')) {
        return { elementName: 'DeviceCapability', ns: NS.default, attrName: capability.replace('device:', '') };
    }
    const colonIdx = capability.indexOf(':');
    if (colonIdx > 0) {
        const prefix = capability.substring(0, colonIdx);
        const name = capability.substring(colonIdx + 1);
        return { elementName: `${prefix}:Capability`, ns: null, attrName: name };
    }
    // Custom capability: company.name_publisherId format → uap4:CustomCapability
    if (/^[a-zA-Z0-9]+(\.[a-zA-Z0-9]+)+_[a-z0-9]{13}$/.test(capability)) {
        return { elementName: 'uap4:CustomCapability', ns: null, attrName: capability };
    }
    return { elementName: 'Capability', ns: NS.default, attrName: capability };
}

/** Build the XML string for a new child element inside VisualElements. */
export function buildVisualChildElement(veField: string, value: string): string | null {
    const defaultTileFields: Record<string, string> = {
        wide310x150Logo: 'Wide310x150Logo',
        square71x71Logo: 'Square71x71Logo',
        square310x310Logo: 'Square310x310Logo',
    };
    if (defaultTileFields[veField]) {
        return `<uap:DefaultTile ${defaultTileFields[veField]}="${escapeXmlAttr(value)}" />`;
    }
    if (veField === 'badgeLogo') {
        return `<uap:LockScreen Notification="badge" BadgeLogo="${escapeXmlAttr(value)}" />`;
    }
    if (veField === 'splashScreenImage') {
        return `<uap:SplashScreen Image="${escapeXmlAttr(value)}" />`;
    }
    return null;
}
