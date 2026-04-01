/**
 * Type definitions for the AppxManifest visual editor.
 */

/** Data extracted from an appxmanifest.xml for the form editor. */
export interface ManifestData {
    identity: IdentityData;
    properties: PropertiesData;
    dependencies: DependenciesData;
    applications: ApplicationData[];
    capabilities: string[];
}

export interface IdentityData {
    name: string;
    publisher: string;
    version: string;
    processorArchitecture: string;
}

export interface PropertiesData {
    displayName: string;
    publisherDisplayName: string;
    description: string;
    logo: string;
}

export interface DependenciesData {
    targetDeviceFamilies: TargetDeviceFamilyData[];
    packageDependencies: PackageDependencyData[];
}

export interface TargetDeviceFamilyData {
    name: string;
    minVersion: string;
    maxVersionTested: string;
}

export interface PackageDependencyData {
    name: string;
    minVersion: string;
    publisher: string;
}

export interface ApplicationData {
    id: string;
    executable: string;
    entryPoint: string;
    visualElements: VisualElementsData;
    extensions: string[];
}

export interface VisualElementsData {
    displayName: string;
    description: string;
    backgroundColor: string;
    square150x150Logo: string;
    square44x44Logo: string;
    wide310x150Logo: string;
}

/** Validation error for a single field. */
export interface ValidationError {
    field: string;
    message: string;
    severity: 'error' | 'warning';
}

/** Message types sent from the extension to the webview. */
export type ExtensionToWebviewMessage =
    | { type: 'update'; data: ManifestData; errors: ValidationError[] }
    | { type: 'validationErrors'; errors: ValidationError[] }
    | { type: 'refreshImages' };

/** Message types sent from the webview to the extension. */
export type WebviewToExtensionMessage =
    | { type: 'fieldChanged'; section: string; field: string; value: string; index?: number }
    | { type: 'addCapability'; capability: string }
    | { type: 'removeCapability'; capability: string }
    | { type: 'addPackageDependency'; dependency: PackageDependencyData }
    | { type: 'removePackageDependency'; index: number }
    | { type: 'addTargetDeviceFamily'; family: TargetDeviceFamilyData }
    | { type: 'removeTargetDeviceFamily'; index: number }
    | { type: 'addApplication' }
    | { type: 'removeApplication'; index: number }
    | { type: 'addExtension'; index: number; xml: string }
    | { type: 'removeExtension'; appIndex: number; extIndex: number }
    | { type: 'updateExtensionField'; appIndex: number; extIndex: number; fieldPath: string; value: string; isTextContent?: boolean }
    | { type: 'browseFile'; appIndex: number; extIndex: number; fieldPath: string }
    | { type: 'updateAssets' }
    | { type: 'ready' };

/** Known capabilities organized by category for the checklist UI. */
export const KNOWN_CAPABILITIES = {
    general: [
        { name: 'internetClient', label: 'Internet (Client)', namespace: '' },
        { name: 'internetClientServer', label: 'Internet (Client & Server)', namespace: '' },
        { name: 'privateNetworkClientServer', label: 'Private Networks (Client & Server)', namespace: '' },
        { name: 'codeGeneration', label: 'Code Generation', namespace: '' },
    ],
    restricted: [
        { name: 'runFullTrust', label: 'Run Full Trust', namespace: 'rescap' },
        { name: 'allowElevation', label: 'Allow Elevation', namespace: 'rescap' },
        { name: 'unvirtualizedResources', label: 'Unvirtualized Resources', namespace: 'rescap' },
        { name: 'packagedShellExtension', label: 'Packaged Shell Extension', namespace: 'rescap' },
    ],
    device: [
        { name: 'microphone', label: 'Microphone', namespace: 'device' },
        { name: 'webcam', label: 'Webcam', namespace: 'device' },
        { name: 'location', label: 'Location', namespace: 'device' },
        { name: 'bluetooth', label: 'Bluetooth', namespace: 'device' },
    ],
} as const;

/** Extension templates for the Add Extension menu. */
export const EXTENSION_TEMPLATES = [
    {
        label: 'MCP Server',
        category: 'windows.appExtension',
        xml: '<uap3:Extension Category="windows.appExtension">\n  <uap3:AppExtension\n    Name=""\n    Id=""\n    DisplayName=""\n    PublicFolder="">\n    <uap3:Properties>\n      <Registration></Registration>\n    </uap3:Properties>\n  </uap3:AppExtension>\n</uap3:Extension>',
    },
    {
        label: 'COM Server',
        category: 'windows.comServer',
        xml: '<com:Extension Category="windows.comServer">\n  <com:ComServer>\n    <com:ExeServer Executable="" DisplayName="">\n      <com:Class Id="" />\n    </com:ExeServer>\n  </com:ComServer>\n</com:Extension>',
    },
] as const;

/** Descriptions for known capabilities. */
export const CAPABILITY_DESCRIPTIONS: Record<string, string> = {
    internetClient: 'Provides outbound access to the internet and networks in public places like airports and coffee shops.',
    internetClientServer: 'Provides inbound and outbound access to the internet and networks in public places.',
    privateNetworkClientServer: 'Provides inbound and outbound access to home and work networks through the firewall.',
    codeGeneration: 'Allows the app to generate code dynamically using JIT compilation.',
    runFullTrust: 'Allows a desktop app to run with full trust permissions outside the app container.',
    allowElevation: 'Allows a packaged app to request elevated (admin) privileges at launch.',
    unvirtualizedResources: 'Allows the app to access file system and registry locations without virtualization.',
    packagedShellExtension: 'Allows the app to register shell extensions (context menu handlers, preview handlers, etc.).',
    microphone: 'Provides access to the microphone for audio capture.',
    webcam: 'Provides access to the webcam for video capture.',
    location: 'Provides access to the device location (GPS, Wi-Fi, etc.).',
    bluetooth: 'Provides access to Bluetooth devices for communication.',
};

/** Processor architecture dropdown options. */
export const ARCHITECTURE_OPTIONS = ['x86', 'x64', 'arm', 'arm64', 'neutral'] as const;

/** Target device family dropdown options. */
export const DEVICE_FAMILY_OPTIONS = [
    'Windows.Universal',
    'Windows.Desktop',
    'Windows.Mobile',
    'Windows.Xbox',
    'Windows.Holographic',
    'Windows.IoT',
] as const;
