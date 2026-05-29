/**
 * Parse and modify appxmanifest.xml using @xmldom/xmldom.
 * Reads XML into ManifestData for the form, and applies edits back to the XML text.
 */

export * from './xml-utils';
export * from './manifest-xml-ops';

import { DOMParser, XMLSerializer } from '@xmldom/xmldom';
import type { Element } from '@xmldom/xmldom';
import {
    ManifestData,
    IdentityData,
    PhoneIdentityData,
    PropertiesData,
    DependenciesData,
    TargetDeviceFamilyData,
    PackageDependencyData,
    MainPackageDependencyData,
    DriverConstraintData,
    OSPackageDependencyData,
    HostRuntimeDependencyData,
    ExternalDependencyData,
    ApplicationData,
    VisualElementsData,
    ResourceData,
} from './manifest-types';

import {
    NS,
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
    findNthApplicationRegion,
    detectIndent,
    buildVisualChildElement,
} from './xml-utils';

import { insertChildBeforeClose } from './manifest-xml-ops';

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
    subIndex?: number,
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
            return applyDependenciesChangeString(xmlText, field, value, idx, subIndex);
        case 'applications':
            return applyApplicationChangeString(xmlText, field, value, idx);
        case 'resources':
            return applyResourcesChangeString(xmlText, field, value, idx);
        default:
            return xmlText;
    }
}

// ─── Internal parsing helpers ───────────────────────────────────────

function parseIdentity(root: Element): IdentityData {
    const el = getChildByLocalName(root, 'Identity');
    return {
        name: el?.getAttribute('Name') ?? '',
        publisher: el?.getAttribute('Publisher') ?? '',
        version: el?.getAttribute('Version') ?? '',
        processorArchitecture: el?.getAttribute('ProcessorArchitecture') ?? 'neutral',
        resourceId: el?.getAttribute('ResourceId') ?? '',
    };
}

function parsePhoneIdentity(root: Element): PhoneIdentityData | null {
    const el = findChildByLocalNameNS(root, 'PhoneIdentity');
    if (!el) { return null; }
    return {
        phoneProductId: el.getAttribute('PhoneProductId') ?? '',
        phonePublisherId: el.hasAttribute('PhonePublisherId') ? (el.getAttribute('PhonePublisherId') ?? '') : undefined,
    };
}

function parseProperties(root: Element): PropertiesData {
    const el = getChildByLocalName(root, 'Properties');

    // Parse uap13:AutoUpdate → AppInstaller Uri
    let autoUpdateUri = '';
    if (el) {
        const autoUpdateEl = findChildByLocalNameNS(el, 'AutoUpdate');
        if (autoUpdateEl) {
            const appInstallerEl = findChildByLocalNameNS(autoUpdateEl, 'AppInstaller');
            if (appInstallerEl) {
                autoUpdateUri = appInstallerEl.getAttribute('Uri') ?? '';
            }
        }
    }

    // Parse uap10:PackageIntegrity → Content Enforcement
    let packageIntegrityEnforcement = '';
    if (el) {
        const pkgIntEl = findChildByLocalNameNS(el, 'PackageIntegrity');
        if (pkgIntEl) {
            const contentEl = findChildByLocalNameNS(pkgIntEl, 'Content');
            if (contentEl) {
                packageIntegrityEnforcement = contentEl.getAttribute('Enforcement') ?? '';
            } else {
                // PackageIntegrity exists but no Content child — mark as present but not enforced
                packageIntegrityEnforcement = 'false';
            }
        }
    }

    return {
        displayName: getChildTextContent(el, 'DisplayName'),
        publisherDisplayName: getChildTextContent(el, 'PublisherDisplayName'),
        description: getChildTextContent(el, 'Description'),
        logo: getChildTextContent(el, 'Logo'),
        framework: getChildTextContent(el, 'Framework').toLowerCase(),
        resourcePackage: getChildTextContent(el, 'ResourcePackage').toLowerCase(),
        supportedUsers: getChildTextContent(el, 'SupportedUsers'),
        allowExecution: getChildTextContent(el, 'AllowExecution'),
        fileSystemWriteVirtualization: getChildTextContent(el, 'FileSystemWriteVirtualization'),
        registryWriteVirtualization: getChildTextContent(el, 'RegistryWriteVirtualization'),
        modificationPackage: getChildTextContent(el, 'ModificationPackage').toLowerCase(),
        allowExternalContent: getChildTextContent(el, 'AllowExternalContent'),
        autoUpdateUri,
        packageIntegrityEnforcement,
        updateWhileInUse: getChildTextContent(el, 'UpdateWhileInUse'),
    };
}

function parseDependencies(root: Element): DependenciesData {
    const el = getChildByLocalName(root, 'Dependencies');
    const targetDeviceFamilies: TargetDeviceFamilyData[] = [];
    const packageDependencies: PackageDependencyData[] = [];
    const mainPackageDependencies: MainPackageDependencyData[] = [];
    const driverConstraints: DriverConstraintData[] = [];
    const osPackageDependencies: OSPackageDependencyData[] = [];
    const hostRuntimeDependencies: HostRuntimeDependencyData[] = [];
    const externalDependencies: ExternalDependencyData[] = [];

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
                optional: child.getAttribute('uap6:Optional') ?? '',
            });
        }
        for (const child of getChildrenByLocalName(el, 'MainPackageDependency')) {
            mainPackageDependencies.push({
                name: child.getAttribute('Name') ?? '',
            });
        }
        for (const child of getChildrenByLocalName(el, 'DriverDependency')) {
            for (const dc of getChildrenByLocalName(child, 'DriverConstraint')) {
                driverConstraints.push({
                    name: dc.getAttribute('Name') ?? '',
                    minVersion: dc.getAttribute('MinVersion') ?? '',
                    minDate: dc.getAttribute('MinDate') ?? '',
                });
            }
        }
        for (const child of getChildrenByLocalName(el, 'OSPackageDependency')) {
            osPackageDependencies.push({
                name: child.getAttribute('Name') ?? '',
                version: child.getAttribute('Version') ?? '',
            });
        }
        for (const child of getChildrenByLocalName(el, 'HostRuntimeDependency')) {
            hostRuntimeDependencies.push({
                name: child.getAttribute('Name') ?? '',
                publisher: child.getAttribute('Publisher') ?? '',
                minVersion: child.getAttribute('MinVersion') ?? '',
            });
        }
        for (const child of getChildrenByLocalName(el, 'ExternalDependency')) {
            externalDependencies.push({
                name: child.getAttribute('Name') ?? '',
                publisher: child.getAttribute('Publisher') ?? '',
                minVersion: child.getAttribute('MinVersion') ?? '',
                optional: child.getAttribute('Optional') ?? '',
            });
        }
    }

    return {
        targetDeviceFamilies, packageDependencies,
        mainPackageDependencies, driverConstraints, osPackageDependencies,
        hostRuntimeDependencies, externalDependencies,
    };
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
            trustLevel: appEl.getAttribute('uap10:TrustLevel') ?? appEl.getAttribute('TrustLevel') ?? '',
            runtimeBehavior: appEl.getAttribute('uap10:RuntimeBehavior') ?? appEl.getAttribute('RuntimeBehavior') ?? '',
            supportsMultipleInstances: appEl.getAttribute('uap10:SupportsMultipleInstances') ?? appEl.getAttribute('desktop4:SupportsMultipleInstances') ?? '',
            parameters: appEl.getAttribute('uap10:Parameters') ?? '',
            visualElements: {
                displayName: visualEl?.getAttribute('DisplayName') ?? '',
                description: visualEl?.getAttribute('Description') ?? '',
                backgroundColor: visualEl?.getAttribute('BackgroundColor') ?? '',
                square150x150Logo: visualEl?.getAttribute('Square150x150Logo') ?? '',
                square44x44Logo: visualEl?.getAttribute('Square44x44Logo') ?? '',
                appListEntry: visualEl?.getAttribute('AppListEntry') ?? '',
                wide310x150Logo: defaultTile?.getAttribute('Wide310x150Logo') ?? null,
                square71x71Logo: defaultTile?.getAttribute('Square71x71Logo') ?? null,
                square310x310Logo: defaultTile?.getAttribute('Square310x310Logo') ?? null,
                shortName: defaultTile?.getAttribute('ShortName') ?? '',
                badgeLogo: lockScreen?.getAttribute('BadgeLogo') ?? null,
                lockScreenNotification: lockScreen?.getAttribute('Notification') ?? '',
                splashScreenImage: splashScreen?.getAttribute('Image') ?? null,
                splashScreenBackgroundColor: splashScreen?.getAttribute('BackgroundColor') ?? '',
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
        } else if (localName === 'CustomCapability') {
            // uap4:CustomCapability — store as just the Name (no prefix)
            capabilities.push(name);
        } else if (prefix) {
            capabilities.push(`${prefix}:${name}`);
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
            scale: child.getAttribute('uap:Scale') ?? child.getAttribute('Scale') ?? '',
            dxFeatureLevel: child.getAttribute('uap:DXFeatureLevel') ?? child.getAttribute('DXFeatureLevel') ?? '',
        });
    }
    return resources;
}

// ─── Surgical string-based field change helpers ─────────────────────



function applyIdentityChangeString(xml: string, field: string, value: string): string {
    const attrMap: Record<string, string> = {
        name: 'Name',
        publisher: 'Publisher',
        version: 'Version',
        processorArchitecture: 'ProcessorArchitecture',
        resourceId: 'ResourceId',
    };
    const attr = attrMap[field];
    if (!attr) { return xml; }

    const pattern = /<Identity\b[^>]*>/s;
    // For optional fields, empty value means remove the attribute
    if (!value && field === 'resourceId') {
        return removeAttribute(xml, pattern, attr);
    }
    const result = replaceAttribute(xml, pattern, attr, value);
    if (result !== null) { return result; }

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

    const pattern = /<[a-zA-Z0-9]*:?PhoneIdentity\b[^>]*>/s;

    // For optional PhonePublisherId, empty value means remove the attribute
    if (!value && field === 'phonePublisherId') {
        return removeAttribute(xml, pattern, attr);
    }

    const result = replaceAttribute(xml, pattern, attr, value);
    if (result !== null) { return result; }

    // Attribute doesn't exist yet — add it
    return addAttributeToElement(xml, pattern, attr, value);
}

function applyPropertiesChangeString(xml: string, field: string, value: string): string {
    const tagMap: Record<string, string> = {
        displayName: 'DisplayName',
        publisherDisplayName: 'PublisherDisplayName',
        description: 'Description',
        logo: 'Logo',
        framework: 'Framework',
        resourcePackage: 'ResourcePackage',
        supportedUsers: 'SupportedUsers',
        allowExecution: 'AllowExecution',
        fileSystemWriteVirtualization: 'FileSystemWriteVirtualization',
        registryWriteVirtualization: 'RegistryWriteVirtualization',
        modificationPackage: 'ModificationPackage',
        allowExternalContent: 'AllowExternalContent',
        updateWhileInUse: 'UpdateWhileInUse',
    };

    // Map of fields that need namespace prefixes when inserting new elements
    const nsPrefix: Record<string, { prefix: string; uri: string }> = {
        supportedUsers: { prefix: 'uap', uri: NS.uap },
        allowExecution: { prefix: 'uap6', uri: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/6' },
        fileSystemWriteVirtualization: { prefix: 'desktop6', uri: 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/6' },
        registryWriteVirtualization: { prefix: 'desktop6', uri: 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/6' },
        modificationPackage: { prefix: 'rescap6', uri: 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities/6' },
        allowExternalContent: { prefix: 'uap10', uri: NS.uap10 },
        updateWhileInUse: { prefix: 'uap17', uri: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/17' },
    };

    // Special handling for autoUpdateUri (nested: uap13:AutoUpdate > uap13:AppInstaller Uri="...")
    if (field === 'autoUpdateUri') {
        return applyAutoUpdateUri(xml, value);
    }

    // Special handling for packageIntegrityEnforcement (nested: uap10:PackageIntegrity > uap10:Content Enforcement="...")
    if (field === 'packageIntegrityEnforcement') {
        return applyPackageIntegrityEnforcement(xml, value);
    }

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

        // Ensure namespace for prefixed elements
        const ns = nsPrefix[field];
        if (ns) {
            workXml = ensureNamespace(workXml, ns.prefix, ns.uri);
        }

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
        const elemTag = ns ? `${ns.prefix}:${tag}` : tag;
        return insertChildBeforeClose(workXml, propsBounds.contentEnd, `<${elemTag}>${escapeXmlText(value)}</${elemTag}>`, propIndent);
    }

    return result;
}

/** Handle autoUpdateUri: manages uap13:AutoUpdate > uap13:AppInstaller Uri="..." */
function applyAutoUpdateUri(xml: string, value: string): string {
    const autoUpdateRegex = /[ \t]*<[a-zA-Z0-9]*:?AutoUpdate\b[^>]*>[\s\S]*?<\/[a-zA-Z0-9]*:?AutoUpdate\s*>[ \t]*\r?\n?/s;
    if (!value) {
        // Remove entire AutoUpdate block
        const match = autoUpdateRegex.exec(xml);
        if (match) {
            return xml.substring(0, match.index) + xml.substring(match.index + match[0].length);
        }
        return xml;
    }

    // Try to update existing AppInstaller Uri attribute (scoped to AutoUpdate block)
    const autoUpdateBounds = findParentBounds(xml, 'AutoUpdate');
    if (autoUpdateBounds) {
        const appInstallerRegex = /<[a-zA-Z0-9]*:?AppInstaller\b[^>]*\/?>/s;
        const scopedXml = xml.substring(autoUpdateBounds.contentStart, autoUpdateBounds.contentEnd);
        const appInstallerMatch = appInstallerRegex.exec(scopedXml);
        if (appInstallerMatch) {
            const absStart = autoUpdateBounds.contentStart + appInstallerMatch.index;
            const absEnd = absStart + appInstallerMatch[0].length;
            const fullRegex = new RegExp(escapeRegex(xml.substring(absStart, absEnd)));
            const result = replaceAttribute(xml, fullRegex, 'Uri', value);
            if (result !== null) { return result; }
        }
    }

    // No AutoUpdate element — insert one into Properties (guard against duplicate)
    if (/AutoUpdate/i.test(xml)) { return xml; }
    let workXml = ensureNamespace(xml, 'uap13', 'http://schemas.microsoft.com/appx/manifest/uap/windows/10/13');
    const propsBounds = findParentBounds(workXml, 'Properties');
    if (!propsBounds) { return xml; }
    const propIndent = detectIndent(workXml, propsBounds.openStart);
    const childIndent = propIndent + '  ';
    const block = `<uap13:AutoUpdate>\n${childIndent}  <uap13:AppInstaller Uri="${escapeXmlAttr(value)}" />\n${childIndent}</uap13:AutoUpdate>`;
    return insertChildBeforeClose(workXml, propsBounds.contentEnd, block, propIndent);
}

/** Handle packageIntegrityEnforcement: manages uap10:PackageIntegrity > uap10:Content Enforcement="..." */
function applyPackageIntegrityEnforcement(xml: string, value: string): string {
    const pkgIntRegex = /[ \t]*<[a-zA-Z0-9]*:?PackageIntegrity\b[^>]*>[\s\S]*?<\/[a-zA-Z0-9]*:?PackageIntegrity\s*>[ \t]*\r?\n?/s;
    if (!value) {
        // Remove entire PackageIntegrity block
        const match = pkgIntRegex.exec(xml);
        if (match) {
            return xml.substring(0, match.index) + xml.substring(match.index + match[0].length);
        }
        return xml;
    }

    // Try to update existing Content Enforcement attribute (scoped to PackageIntegrity block)
    const pkgIntBounds = findParentBounds(xml, 'PackageIntegrity');
    if (pkgIntBounds) {
        const contentRegex = /<[a-zA-Z0-9]*:?Content\b[^>]*\/?>/s;
        const scopedXml = xml.substring(pkgIntBounds.contentStart, pkgIntBounds.contentEnd);
        const contentMatch = contentRegex.exec(scopedXml);
        if (contentMatch) {
            const absStart = pkgIntBounds.contentStart + contentMatch.index;
            const absEnd = absStart + contentMatch[0].length;
            const fullRegex = new RegExp(escapeRegex(xml.substring(absStart, absEnd)));
            const result = replaceAttribute(xml, fullRegex, 'Enforcement', value);
            if (result !== null) { return result; }
        }
    }

    // No PackageIntegrity element — insert one into Properties (guard against duplicate)
    if (/PackageIntegrity/i.test(xml)) { return xml; }
    let workXml = ensureNamespace(xml, 'uap10', NS.uap10);
    const propsBounds = findParentBounds(workXml, 'Properties');
    if (!propsBounds) { return xml; }
    const propIndent = detectIndent(workXml, propsBounds.openStart);
    const childIndent = propIndent + '  ';
    const block = `<uap10:PackageIntegrity>\n${childIndent}  <uap10:Content Enforcement="${escapeXmlAttr(value)}" />\n${childIndent}</uap10:PackageIntegrity>`;
    return insertChildBeforeClose(workXml, propsBounds.contentEnd, block, propIndent);
}

/** Find the Nth element matching tagRegex, then replace or add the given attribute. */
function applyNthElementAttrChange(
    xml: string, tagRegex: RegExp, index: number, attr: string, value: string,
    opts?: { removeOnEmpty?: boolean }
): string {
    let match: RegExpExecArray | null;
    let count = 0;
    while ((match = tagRegex.exec(xml)) !== null) {
        if (count === index) {
            // Use positional slicing to ensure we operate on this specific match,
            // not the first textually-identical element elsewhere in the XML.
            const pos = match.index;
            const elemText = match[0];
            const before = xml.substring(0, pos);
            const after = xml.substring(pos + elemText.length);
            const elemRegex = new RegExp(escapeRegex(elemText));
            if (opts?.removeOnEmpty && !value) {
                const modified = removeAttribute(elemText, elemRegex, attr);
                return before + modified + after;
            }
            const replaced = replaceAttribute(elemText, elemRegex, attr, value);
            if (replaced !== null) { return before + replaced + after; }
            const added = addAttributeToElement(elemText, elemRegex, attr, value);
            return before + added + after;
        }
        count++;
    }
    return xml;
}

const DEP_FIELD_CONFIG: Record<string, {
    prefix: string;
    tagRegex: RegExp;
    attrMap: Record<string, string>;
    removeOnEmpty?: string[];
    ensureNamespace?: { prefix: string; uri: string; fields: string[] };
}> = {
    targetDeviceFamily: {
        prefix: 'targetDeviceFamily.',
        tagRegex: /<TargetDeviceFamily\b[^>]*\/?>/gs,
        attrMap: { name: 'Name', minVersion: 'MinVersion', maxVersionTested: 'MaxVersionTested' },
    },
    packageDependency: {
        prefix: 'packageDependency.',
        tagRegex: /<PackageDependency\b[^>]*\/?>/gs,
        attrMap: { name: 'Name', minVersion: 'MinVersion', publisher: 'Publisher', optional: 'uap6:Optional' },
        removeOnEmpty: ['optional'],
        ensureNamespace: { prefix: 'uap6', uri: 'http://schemas.microsoft.com/appx/manifest/uap/windows10/6', fields: ['optional'] },
    },
    mainPackageDependency: {
        prefix: 'mainPackageDependency.',
        tagRegex: /<uap3:MainPackageDependency\b[^>]*\/?>/gs,
        attrMap: { name: 'Name' },
    },
    osPackageDependency: {
        prefix: 'osPackageDependency.',
        tagRegex: /<uap7:OSPackageDependency\b[^>]*\/?>/gs,
        attrMap: { name: 'Name', version: 'Version' },
    },
    hostRuntimeDependency: {
        prefix: 'hostRuntimeDependency.',
        tagRegex: /<uap10:HostRuntimeDependency\b[^>]*\/?>/gs,
        attrMap: { name: 'Name', publisher: 'Publisher', minVersion: 'MinVersion' },
    },
    externalDependency: {
        prefix: 'externalDependency.',
        tagRegex: /<win32dependencies:ExternalDependency\b[^>]*\/?>/gs,
        attrMap: { name: 'Name', publisher: 'Publisher', minVersion: 'MinVersion', optional: 'Optional' },
        removeOnEmpty: ['optional'],
    },
    driverConstraint: {
        prefix: 'driverConstraint.',
        tagRegex: /<uap5:DriverConstraint\b[^>]*\/?>/gs,
        attrMap: { name: 'Name', minVersion: 'MinVersion', minDate: 'MinDate' },
    },
};

function applyDependenciesChangeString(xml: string, field: string, value: string, index: number, subIndex?: number): string {
    for (const cfg of Object.values(DEP_FIELD_CONFIG)) {
        if (field.startsWith(cfg.prefix)) {
            const subField = field.replace(cfg.prefix, '');
            const attr = cfg.attrMap[subField];
            if (!attr) { return xml; }
            // Reset lastIndex since regexes have the global flag
            cfg.tagRegex.lastIndex = 0;
            const removeOnEmpty = cfg.removeOnEmpty?.includes(subField);
            // Ensure namespace if configured for this field
            if (cfg.ensureNamespace && cfg.ensureNamespace.fields.includes(subField) && value) {
                xml = ensureNamespace(xml, cfg.ensureNamespace.prefix, cfg.ensureNamespace.uri); // lgtm[js/incomplete-multi-character-sanitization]
            }
            return applyNthElementAttrChange(xml, cfg.tagRegex, index, attr, value,
                removeOnEmpty ? { removeOnEmpty: true } : undefined);
        }
    }

    return xml;
}

function applyResourcesChangeString(xml: string, field: string, value: string, index: number): string {
    const attrMap: Record<string, string> = {
        language: 'Language',
        scale: 'uap:Scale',
        dxFeatureLevel: 'uap:DXFeatureLevel',
    };
    let attr = attrMap[field];
    if (!attr) { return xml; }

    const regex = /<Resource\b[^>]*\/?>/gs;
    let match: RegExpExecArray | null;
    let count = 0;
    while ((match = regex.exec(xml)) !== null) {
        if (count === index) {
            // M4: Detect if the element uses unprefixed variant (Scale vs uap:Scale)
            if (attr.startsWith('uap:')) {
                const bareAttr = attr.substring(4);
                if (match[0].includes(`${bareAttr}=`) && !match[0].includes(`uap:${bareAttr}=`)) {
                    attr = bareAttr;
                }
            }

            const elemRegex = new RegExp(escapeRegex(match[0]));
            if (!value) {
                return removeAttribute(xml, elemRegex, attr);
            }

            // Ensure uap namespace when adding uap-prefixed attrs
            let workXml = xml;
            if (attr.startsWith('uap:')) {
                workXml = ensureNamespace(workXml, 'uap', NS.uap);
                // Re-find element after possible namespace insertion shift
                const reMatch = new RegExp(escapeRegex(match[0])).exec(workXml);
                if (!reMatch) { return workXml; }
            }

            const workElemRegex = new RegExp(escapeRegex(match[0]));
            const result = replaceAttribute(workXml, workElemRegex, attr, value);
            if (result !== null) { return result; }
            return addAttributeToElement(workXml, workElemRegex, attr, value);
        }
        count++;
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
    // Optional Application attributes that should be removed when empty
    const optionalAppAttrs: Record<string, string> = {
        trustLevel: 'uap10:TrustLevel',
        runtimeBehavior: 'uap10:RuntimeBehavior',
        supportsMultipleInstances: 'uap10:SupportsMultipleInstances',
        parameters: 'uap10:Parameters',
    };
    if (appAttrMap[field] || optionalAppAttrs[field]) {
        const attr = appAttrMap[field] || optionalAppAttrs[field];
        const regex = /<Application\b[^>]*>/gs;
        let match: RegExpExecArray | null;
        let count = 0;
        while ((match = regex.exec(xml)) !== null) {
            if (count === index) {
                const elemRegex = new RegExp(escapeRegex(match[0]));
                // Optional attrs: remove when empty
                if (optionalAppAttrs[field] && !value) {
                    return removeAttribute(xml, elemRegex, attr);
                }
                // Ensure uap10 namespace for uap10: attributes
                if (optionalAppAttrs[field]?.startsWith('uap10:')) {
                    xml = ensureNamespace(xml, 'uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10');
                    // Re-find after namespace insertion
                    const regex2 = /<Application\b[^>]*>/gs;
                    let m2: RegExpExecArray | null;
                    let c2 = 0;
                    while ((m2 = regex2.exec(xml)) !== null) {
                        if (c2 === index) {
                            const elemRegex2 = new RegExp(escapeRegex(m2[0]));
                            const result = replaceAttribute(xml, elemRegex2, attr, value);
                            if (result !== null) { return result; }
                            return addAttributeToElement(xml, elemRegex2, attr, value);
                        }
                        c2++;
                    }
                    return xml;
                }
                const result = replaceAttribute(xml, elemRegex, attr, value);
                if (result !== null) { return result; }
                return addAttributeToElement(xml, elemRegex, attr, value);
            }
            count++;
        }
        return xml;
    }

    // VisualElements attributes — scope searches to the nth Application's region
    if (field.startsWith('visualElements.')) {
        const veField = field.replace('visualElements.', '');

        // Find the bounds of the nth Application element to scope all searches
        const appRegion = findNthApplicationRegion(xml, index);
        if (!appRegion) { return xml; }
        const { start: appStart, end: appEnd } = appRegion;
        const appXml = xml.substring(appStart, appEnd);

        function applyScopedAttrOp(
            fullXml: string, pattern: RegExp, attrName: string,
            op: 'replace' | 'add' | 'remove', newValue?: string
        ): string | null {
            const region = fullXml.substring(appStart, appEnd);
            const match = pattern.exec(region);
            if (!match) { return null; }
            const absIdx = appStart + match.index;
            const elemRegex = new RegExp(escapeRegex(match[0]));
            const before = fullXml.substring(0, absIdx);
            const after = fullXml.substring(absIdx);
            if (op === 'remove') {
                const removed = removeAttribute(after, elemRegex, attrName);
                return removed !== after ? before + removed : fullXml;
            } else if (op === 'replace') {
                const replaced = replaceAttribute(after, elemRegex, attrName, newValue!);
                return replaced !== null ? before + replaced : null;
            } else {
                const added = addAttributeToElement(after, elemRegex, attrName, newValue!);
                return before + added;
            }
        }

        // Attributes on DefaultTile
        const defaultTileAttrs: Record<string, string> = {
            wide310x150Logo: 'Wide310x150Logo',
            square71x71Logo: 'Square71x71Logo',
            square310x310Logo: 'Square310x310Logo',
            shortName: 'ShortName',
        };
        if (defaultTileAttrs[veField]) {
            const dtPattern = /<[a-zA-Z0-9]*:?DefaultTile\b[^>]*?\/?>/s;
            if (!value && veField === 'shortName') {
                return applyScopedAttrOp(xml, dtPattern, defaultTileAttrs[veField], 'remove') ?? xml;
            }
            const result = applyScopedAttrOp(xml, /<[a-zA-Z0-9]*:?DefaultTile\b[^>]*>/s, defaultTileAttrs[veField], 'replace', value);
            if (result !== null) { return result; }
            const addResult = applyScopedAttrOp(xml, dtPattern, defaultTileAttrs[veField], 'add', value);
            if (addResult !== null) { return addResult; }
            // No DefaultTile element exists — fall through to create one
        }

        // Attributes on LockScreen
        if (veField === 'badgeLogo' || veField === 'lockScreenNotification') {
            const lockAttr = veField === 'badgeLogo' ? 'BadgeLogo' : 'Notification';
            const lsPattern = /<[a-zA-Z0-9]*:?LockScreen\b[^>]*?\/?>/s;
            if (!value && veField === 'lockScreenNotification') {
                return applyScopedAttrOp(xml, lsPattern, lockAttr, 'remove') ?? xml;
            }
            const result = applyScopedAttrOp(xml, /<[a-zA-Z0-9]*:?LockScreen\b[^>]*>/s, lockAttr, 'replace', value);
            if (result !== null) { return result; }
            const addResult = applyScopedAttrOp(xml, lsPattern, lockAttr, 'add', value);
            if (addResult !== null) { return addResult; }
            // No LockScreen element exists — fall through to create one
        }

        // Attributes on SplashScreen
        if (veField === 'splashScreenImage' || veField === 'splashScreenBackgroundColor') {
            const splashAttr = veField === 'splashScreenImage' ? 'Image' : 'BackgroundColor';
            const ssPattern = /<[a-zA-Z0-9]*:?SplashScreen\b[^>]*?\/?>/s;
            if (!value && veField === 'splashScreenBackgroundColor') {
                return applyScopedAttrOp(xml, ssPattern, splashAttr, 'remove') ?? xml;
            }
            const result = applyScopedAttrOp(xml, /<[a-zA-Z0-9]*:?SplashScreen\b[^>]*>/s, splashAttr, 'replace', value);
            if (result !== null) { return result; }
            const addResult = applyScopedAttrOp(xml, ssPattern, splashAttr, 'add', value);
            if (addResult !== null) { return addResult; }
            // No SplashScreen element exists — fall through to create one
        }

        // AppListEntry on VisualElements
        const attrMap: Record<string, string> = {
            displayName: 'DisplayName',
            description: 'Description',
            backgroundColor: 'BackgroundColor',
            square150x150Logo: 'Square150x150Logo',
            square44x44Logo: 'Square44x44Logo',
            appListEntry: 'AppListEntry',
        };
        if (attrMap[veField]) {
            if (!value && veField === 'appListEntry') {
                return applyScopedAttrOp(xml, /<[a-zA-Z0-9]*:?VisualElements\b[^>]*?\/?>/s, attrMap[veField], 'remove') ?? xml;
            }
            return applyScopedAttrOp(xml, /<[a-zA-Z0-9]*:?VisualElements\b[^>]*>/s, attrMap[veField], 'replace', value) ?? xml;
        }

        // Fallback: surgically insert new child element inside VisualElements
        const veClosePattern = /(<[a-zA-Z0-9]*:?VisualElements\b[^>]*?)\s*\/>/s;
        const veCloseMatch = veClosePattern.exec(appXml);
        if (veCloseMatch) {
            // Self-closing VisualElements — convert to open/close and insert child
            const absPos = appStart + veCloseMatch.index;
            const indent = detectIndent(xml, absPos);
            const childIndent = indent + '  ';
            const childXml = buildVisualChildElement(veField, value);
            if (childXml) {
                return xml.substring(0, absPos)
                    + veCloseMatch[1] + '>\n'
                    + childIndent + childXml + '\n'
                    + indent + '</uap:VisualElements>'
                    + xml.substring(absPos + veCloseMatch[0].length);
            }
        } else {
            // Non-self-closing VisualElements — insert before closing tag
            const veEndPattern = /<\/[a-zA-Z0-9]*:?VisualElements\s*>/s;
            const veEndMatch = veEndPattern.exec(appXml);
            if (veEndMatch) {
                const absEndPos = appStart + veEndMatch.index;
                // Try to detect child indent from an existing child element (e.g., DefaultTile)
                const existingChildPattern = /\n([ \t]+)<[a-zA-Z0-9]*:?(?:DefaultTile|LockScreen|SplashScreen)\b/;
                const existingChildMatch = existingChildPattern.exec(appXml);
                const veEndIndent = detectIndent(xml, absEndPos);
                const childIndent = existingChildMatch ? existingChildMatch[1] : (veEndIndent + '  ');
                const childXml = buildVisualChildElement(veField, value);
                if (childXml) {
                    // Find the start of the whitespace preceding the closing tag
                    const beforeClose = xml.substring(0, absEndPos);
                    const trailingWsMatch = /\n[ \t]*$/.exec(beforeClose);
                    const insertPos = trailingWsMatch ? absEndPos - trailingWsMatch[0].length : absEndPos;
                    return xml.substring(0, insertPos)
                        + '\n' + childIndent + childXml
                        + '\n' + veEndIndent + veEndMatch[0]
                        + xml.substring(absEndPos + veEndMatch[0].length);
                }
            }
        }

        return xml;
    }

    return xml;
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
