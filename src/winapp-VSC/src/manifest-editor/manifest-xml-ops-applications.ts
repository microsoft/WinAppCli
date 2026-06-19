/**
 * Application and extension XML operations for the manifest editor.
 * Split from manifest-xml-ops.ts for maintainability.
 */

import {
    NS,
    escapeRegex,
    escapeXmlAttr,
    escapeXmlText,
    ensureNamespace,
    findParentBounds,
    findDirectChildElementBounds,
    findNthApplicationRegion,
    removeNamespaceIfUnused,
} from './xml-utils';

import { removeNthChildByTag } from './manifest-xml-ops';

/** Add a new Application element to the manifest. */
export function addApplication(xmlText: string): string {
    let result = ensureNamespace(xmlText, 'uap', NS.uap);

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
    if (apps.length <= 1) { return xmlText; }
    return removeNthChildByTag(xmlText, 'Applications', /^<Application\b/, index);
}

export function addExtension(xmlText: string, appIndex: number, extensionXml: string): string {
    // Use findNthApplicationRegion for reliable application boundary detection
    const appRegion = findNthApplicationRegion(xmlText, appIndex);
    if (!appRegion) { return xmlText; }

    const appBlock = xmlText.substring(appRegion.start, appRegion.end);
    const hasExtensions = appBlock.includes('<Extensions>');

    let result = xmlText;

    // Ensure required namespace declarations
    const nsMap: Record<string, { prefix: string; uri: string }> = {
        'com:': { prefix: 'com', uri: 'http://schemas.microsoft.com/appx/manifest/com/windows10' },
        'uap:': { prefix: 'uap', uri: NS.uap },
        'uap3:': { prefix: 'uap3', uri: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3' },
        'uap5:': { prefix: 'uap5', uri: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5' },
        'desktop:': { prefix: 'desktop', uri: 'http://schemas.microsoft.com/appx/manifest/desktop/windows10' },
    };
    for (const [prefix, ns] of Object.entries(nsMap)) {
        if (extensionXml.includes(prefix)) {
            result = ensureNamespace(result, ns.prefix, ns.uri);
        }
    }

    // Re-find the region since namespace insertion may have shifted positions
    const updatedRegion = findNthApplicationRegion(result, appIndex);
    if (!updatedRegion) { return result; }
    const appCloseIdx = updatedRegion.end;

    // Detect the file's indentation by looking at existing content
    let indentMatch = result.match(/^([ \t]+)<Extensions>/m);
    if (!indentMatch) {
        const appIndentMatch = result.match(/^([ \t]+)<Application\b/m);
        if (appIndentMatch) {
            const baseIndent = appIndentMatch[1];
            const indentChar = baseIndent.includes('\t') ? '\t' : '  ';
            indentMatch = [, baseIndent + indentChar] as unknown as RegExpMatchArray;
        }
    }
    const extIndent = indentMatch?.[1] ?? '      ';
    const indentChar = extIndent.includes('\t') ? '\t' : '  ';
    const childIndent = extIndent + indentChar;
    const indentedExt = extensionXml.split('\n').map(line => childIndent + line).join('\n');

    if (hasExtensions) {
        const closeTag = '</Extensions>';
        const closeIdx = result.lastIndexOf(closeTag, appCloseIdx);
        if (closeIdx < 0) { return result; }
        const beforeClose = result.substring(0, closeIdx).replace(/\s+$/, '');
        return beforeClose + '\n' +
            indentedExt + '\n' + extIndent +
            result.substring(closeIdx);
    } else {
        // Insert a new <Extensions> block before the closing </Application> tag
        const closeAppTag = '</Application>';
        const closeIdx = result.lastIndexOf(closeAppTag, appCloseIdx);
        if (closeIdx < 0) { return result; }

        const lineStart = result.lastIndexOf('\n', closeIdx - 1);
        const appIndent = lineStart >= 0 ? result.substring(lineStart + 1, closeIdx).match(/^(\s*)/)?.[1] ?? '    ' : '    ';
        const extBlockIndent = appIndent + '  ';
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
    // Use findNthApplicationRegion for reliable application boundary detection
    const appRegion = findNthApplicationRegion(xmlText, appIndex);
    if (!appRegion) { return xmlText; }
    const appCloseIdx = appRegion.end;

    // Find <Extensions> and </Extensions> within this Application
    const extOpenTag = '<Extensions>';
    const extCloseTag = '</Extensions>';
    const extOpenIdx = xmlText.lastIndexOf(extOpenTag, appCloseIdx);
    if (extOpenIdx < 0 || extOpenIdx < appRegion.start) { return xmlText; }
    const extCloseIdx = xmlText.indexOf(extCloseTag, extOpenIdx);
    if (extCloseIdx < 0 || extCloseIdx > appCloseIdx) { return xmlText; }

    const contentStart = extOpenIdx + extOpenTag.length;
    const contentEnd = extCloseIdx;

    // Find all direct child elements within <Extensions>...</Extensions>
    const children = findDirectChildElementBounds(xmlText, contentStart, contentEnd);
    if (extIndex < 0 || extIndex >= children.length) { return xmlText; }

    const target = children[extIndex];

    // Capture the namespace prefix of the extension being removed
    const targetXml = xmlText.substring(target.start, target.end);
    const extNsMatch = /^<([a-zA-Z0-9]+):/.exec(targetXml);
    const extNsPrefix = extNsMatch ? extNsMatch[1] : null;

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

    // Clean up namespace if no longer used
    if (extNsPrefix) {
        result = removeNamespaceIfUnused(result, extNsPrefix);
    }

    return result;
}

/**
 * Update an attribute on an extension element.
 * fieldPath is "ElementName.AttributeName" as produced by parseExtensionFields in the webview.
 */
export function updateExtensionField(
    xmlText: string, appIndex: number, extIndex: number, fieldPath: string, value: string, isTextContent?: boolean,
): string {
    // Use findNthApplicationRegion for reliable application boundary detection
    const appRegion = findNthApplicationRegion(xmlText, appIndex);
    if (!appRegion) { return xmlText; }
    const appCloseIdx = appRegion.end;

    // Find <Extensions> and </Extensions> within this Application
    const extOpenTag = '<Extensions>';
    const extCloseTag = '</Extensions>';
    const extOpenIdx = xmlText.lastIndexOf(extOpenTag, appCloseIdx);
    if (extOpenIdx < 0 || extOpenIdx < appRegion.start) { return xmlText; }
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
        extXml = extXml.substring(0, match.index) + match[1] + escapeXmlText(value) + match[3] + extXml.substring(match.index + match[0].length);
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
        const attrRegex = new RegExp(`(${escapeRegex(attrName)}\\s*=\\s*)(["'])((?:(?!\\2).)*?)\\2`);
        const attrMatch = attrRegex.exec(match[0]);
        if (!attrMatch) { return xmlText; }

        const newElem = match[0].substring(0, attrMatch.index)
            + attrMatch[1] + attrMatch[2] + escapeXmlAttr(value) + attrMatch[2]
            + match[0].substring(attrMatch.index + attrMatch[0].length);
        extXml = extXml.substring(0, match.index) + newElem + extXml.substring(match.index + match[0].length);
    }

    return xmlText.substring(0, target.start) + extXml + xmlText.substring(target.end);
}
