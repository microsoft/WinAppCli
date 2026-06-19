/**
 * Type definitions for the AppxManifest visual editor.
 */

/** Data extracted from an appxmanifest.xml for the form editor. */
export interface ManifestData {
    identity: IdentityData;
    phoneIdentity: PhoneIdentityData | null;
    properties: PropertiesData;
    dependencies: DependenciesData;
    applications: ApplicationData[];
    capabilities: string[];
    resources: ResourceData[];
}

export interface IdentityData {
    name: string;
    publisher: string;
    version: string;
    processorArchitecture: string;
    resourceId: string;
}

export interface PhoneIdentityData {
    phoneProductId: string;
    phonePublisherId: string | undefined;
}

export interface PropertiesData {
    displayName: string;
    publisherDisplayName: string;
    description: string;
    logo: string;
    framework: string;
    resourcePackage: string;
    supportedUsers: string;
    allowExecution: string;
    fileSystemWriteVirtualization: string;
    registryWriteVirtualization: string;
    modificationPackage: string;
    allowExternalContent: string;
    autoUpdateUri: string;
    packageIntegrityEnforcement: string;
    updateWhileInUse: string;
}

export interface DependenciesData {
    targetDeviceFamilies: TargetDeviceFamilyData[];
    packageDependencies: PackageDependencyData[];
    mainPackageDependencies: MainPackageDependencyData[];
    driverConstraints: DriverConstraintData[];
    osPackageDependencies: OSPackageDependencyData[];
    hostRuntimeDependencies: HostRuntimeDependencyData[];
    externalDependencies: ExternalDependencyData[];
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
    optional: string;
}

export interface MainPackageDependencyData {
    name: string;
}

export interface DriverConstraintData {
    name: string;
    minVersion: string;
    minDate: string;
}

export interface OSPackageDependencyData {
    name: string;
    version: string;
}

export interface HostRuntimeDependencyData {
    name: string;
    publisher: string;
    minVersion: string;
}

export interface ExternalDependencyData {
    name: string;
    publisher: string;
    minVersion: string;
    optional: string;
}

export interface ApplicationData {
    id: string;
    executable: string;
    entryPoint: string;
    trustLevel: string;
    runtimeBehavior: string;
    supportsMultipleInstances: string;
    parameters: string;
    visualElements: VisualElementsData;
    extensions: string[];
}

export interface VisualElementsData {
    displayName: string;
    description: string;
    backgroundColor: string;
    square150x150Logo: string;
    square44x44Logo: string;
    appListEntry: string;
    wide310x150Logo: string | null;
    square71x71Logo: string | null;
    square310x310Logo: string | null;
    badgeLogo: string | null;
    splashScreenImage: string | null;
    splashScreenBackgroundColor: string;
    lockScreenNotification: string;
    shortName: string;
    showNameOnTiles: string[];
}

export interface ResourceData {
    language: string;
    scale: string;
    dxFeatureLevel: string;
}

export const RESOURCE_SCALE_OPTIONS = ['', '80', '100', '120', '125', '140', '150', '160', '175', '180', '200', '225', '250', '300', '350', '400', '450'] as const;
export const RESOURCE_DX_OPTIONS = ['', 'dx9', 'dx10', 'dx11', 'dx12'] as const;

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
    | { type: 'refreshImages' }
    | { type: 'flushChanges' };

/** Message types sent from the webview to the extension. */
export type WebviewToExtensionMessage =
    | { type: 'fieldChanged'; section: string; field: string; value: string; index?: number; subIndex?: number }
    | { type: 'setShowNameOnTiles'; appIndex: number; tiles: string[] }
    | { type: 'addResource'; resource: ResourceData }
    | { type: 'removeResource'; index: number }
    | { type: 'moveResource'; index: number; direction: 'up' | 'down' }
    | { type: 'addCapability';capability: string }
    | { type: 'removeCapability'; capability: string }
    | { type: 'addPackageDependency'; dependency: PackageDependencyData }
    | { type: 'removePackageDependency'; index: number }
    | { type: 'addTargetDeviceFamily'; family: TargetDeviceFamilyData }
    | { type: 'removeTargetDeviceFamily'; index: number }
    | { type: 'moveTargetDeviceFamily'; index: number; direction: 'up' | 'down' }
    | { type: 'addApplication' }
    | { type: 'removeApplication'; index: number }
    | { type: 'addExtension'; index: number; xml: string }
    | { type: 'removeExtension'; appIndex: number; extIndex: number }
    | { type: 'updateExtensionField'; appIndex: number; extIndex: number; fieldPath: string; value: string; isTextContent?: boolean }
    | { type: 'browseFile'; appIndex: number; extIndex: number; fieldPath: string }
    | { type: 'browseImage'; section: string; field: string; index?: number }
    | { type: 'removeVisualAsset'; field: string; index: number }
    | { type: 'checkImagePath'; imagePath: string; field: string; index?: number }
    | { type: 'copyToAssets'; sourcePath: string; section: string; field: string; index?: number }
    | { type: 'browseExe'; section: string; field: string; index?: number }
    | { type: 'movePackageDependency'; index: number; direction: 'up' | 'down' }
    | { type: 'addMainPackageDependency'; dependency: MainPackageDependencyData }
    | { type: 'removeMainPackageDependency'; index: number }
    | { type: 'moveMainPackageDependency'; index: number; direction: 'up' | 'down' }
    | { type: 'addDriverConstraint'; constraint: DriverConstraintData }
    | { type: 'removeDriverConstraint'; index: number }
    | { type: 'moveDriverConstraint'; index: number; direction: 'up' | 'down' }
    | { type: 'addOSPackageDependency'; dependency: OSPackageDependencyData }
    | { type: 'removeOSPackageDependency'; index: number }
    | { type: 'moveOSPackageDependency'; index: number; direction: 'up' | 'down' }
    | { type: 'addHostRuntimeDependency'; dependency: HostRuntimeDependencyData }
    | { type: 'removeHostRuntimeDependency'; index: number }
    | { type: 'moveHostRuntimeDependency'; index: number; direction: 'up' | 'down' }
    | { type: 'addExternalDependency'; dependency: ExternalDependencyData }
    | { type: 'removeExternalDependency'; index: number }
    | { type: 'moveExternalDependency'; index: number; direction: 'up' | 'down' }
    | { type: 'updateAssets' }
    | { type: 'openAsText' }
    | { type: 'addPhoneIdentity' }
    | { type: 'removePhoneIdentity' }
    | { type: 'packageTypeChanged'; value: string }
    | { type: 'ready' }
    | { type: 'changesFlushed'; changes: Array<{ section: string; field: string; value: string; index: number }>; nonce?: string };

/** Known capabilities organized by category for the checklist UI. */
export const KNOWN_CAPABILITIES = {
    general: [
        { name: 'internetClient', label: 'Internet (Client)', namespace: '' },
        { name: 'internetClientServer', label: 'Internet (Client & Server)', namespace: '' },
        { name: 'privateNetworkClientServer', label: 'Private Networks (Client & Server)', namespace: '' },
        { name: 'codeGeneration', label: 'Code Generation', namespace: '' },
        { name: 'musicLibrary', label: 'Music Library', namespace: 'uap' },
        { name: 'picturesLibrary', label: 'Pictures Library', namespace: 'uap' },
        { name: 'videosLibrary', label: 'Videos Library', namespace: 'uap' },
        { name: 'removableStorage', label: 'Removable Storage', namespace: 'uap' },
        { name: 'appointments', label: 'Appointments', namespace: 'uap' },
        { name: 'contacts', label: 'Contacts', namespace: 'uap' },
        { name: 'enterpriseAuthentication', label: 'Enterprise Authentication', namespace: '' },
        { name: 'sharedUserCertificates', label: 'Shared User Certificates', namespace: '' },
        { name: 'phoneCall', label: 'Phone Call', namespace: 'uap' },
        { name: 'userAccountInformation', label: 'User Account Information', namespace: 'uap' },
        { name: 'voipCall', label: 'VoIP Call', namespace: 'uap' },
        { name: 'objects3D', label: '3D Objects', namespace: 'uap' },
        { name: 'chat', label: 'Chat', namespace: 'uap' },
        { name: 'blockedChatMessages', label: 'Blocked Chat Messages', namespace: 'uap' },
        { name: 'backgroundMediaPlayback', label: 'Background Media Playback', namespace: 'uap3' },
        { name: 'remoteSystem', label: 'Remote System', namespace: 'uap4' },
        { name: 'spatialPerception', label: 'Spatial Perception', namespace: 'uap2' },
        { name: 'globalMediaControl', label: 'Global Media Control', namespace: 'uap7' },
        { name: 'graphicsCapture', label: 'Graphics Capture', namespace: 'uap6' },
        { name: 'userDataTasks', label: 'User Data Tasks', namespace: 'uap4' },
        { name: 'userNotificationListener', label: 'User Notification Listener', namespace: 'uap3' },
    ],
    restricted: [
        { name: 'runFullTrust', label: 'Run Full Trust', namespace: 'rescap' },
        { name: 'allowElevation', label: 'Allow Elevation', namespace: 'rescap' },
        { name: 'unvirtualizedResources', label: 'Unvirtualized Resources', namespace: 'rescap' },
        { name: 'packagedShellExtension', label: 'Packaged Shell Extension', namespace: 'rescap' },
        { name: 'appDiagnostics', label: 'App Diagnostics', namespace: 'rescap' },
        { name: 'broadFileSystemAccess', label: 'Broad File System Access', namespace: 'rescap' },
        { name: 'packageManagement', label: 'Package Management', namespace: 'rescap' },
        { name: 'packageQuery', label: 'Package Query', namespace: 'rescap' },
        { name: 'localSystemServices', label: 'Local System Services', namespace: 'rescap' },
        { name: 'inputForegroundObservation', label: 'Input Foreground Observation', namespace: 'rescap' },
        { name: 'confirmAppClose', label: 'Confirm App Close', namespace: 'rescap' },
    ],
    device: [
        { name: 'microphone', label: 'Microphone', namespace: 'device' },
        { name: 'webcam', label: 'Webcam', namespace: 'device' },
        { name: 'location', label: 'Location', namespace: 'device' },
        { name: 'bluetooth', label: 'Bluetooth', namespace: 'device' },
        { name: 'proximity', label: 'Proximity', namespace: 'device' },
        { name: 'usb', label: 'USB', namespace: 'device' },
        { name: 'humaninterfacedevice', label: 'Human Interface Device (HID)', namespace: 'device' },
        { name: 'pointOfService', label: 'Point of Service', namespace: 'device' },
        { name: 'wiFiControl', label: 'Wi-Fi Control', namespace: 'device' },
        { name: 'radios', label: 'Radios', namespace: 'device' },
        { name: 'optical', label: 'Optical', namespace: 'device' },
        { name: 'activity', label: 'Activity', namespace: 'device' },
        { name: 'serialcommunication', label: 'Serial Communication', namespace: 'device' },
        { name: 'gazeInput', label: 'Gaze Input', namespace: 'device' },
        { name: 'lowLevelDevices', label: 'Low Level Devices', namespace: 'device' },
        { name: 'lowLevel', label: 'Low Level', namespace: 'device' },
        { name: 'systemAIModels', label: 'System AI Models', namespace: 'systemai' },
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
    {
        label: 'App Execution Alias',
        category: 'windows.appExecutionAlias',
        xml: '<uap5:Extension Category="windows.appExecutionAlias">\n  <uap5:AppExecutionAlias>\n    <uap5:ExecutionAlias Alias="" />\n  </uap5:AppExecutionAlias>\n</uap5:Extension>',
    },
    {
        label: 'Background Tasks',
        category: 'windows.backgroundTasks',
        xml: '<Extension Category="windows.backgroundTasks" EntryPoint="">\n  <BackgroundTasks>\n    <Task Type="timer" />\n  </BackgroundTasks>\n</Extension>',
    },
    {
        label: 'Protocol Activation',
        category: 'windows.protocol',
        xml: '<uap:Extension Category="windows.protocol">\n  <uap:Protocol Name="" />\n</uap:Extension>',
    },
    {
        label: 'File Type Association',
        category: 'windows.fileTypeAssociation',
        xml: '<uap:Extension Category="windows.fileTypeAssociation">\n  <uap:FileTypeAssociation Name="">\n    <uap:DisplayName></uap:DisplayName>\n    <uap:SupportedFileTypes>\n      <uap:FileType>.example</uap:FileType>\n    </uap:SupportedFileTypes>\n  </uap:FileTypeAssociation>\n</uap:Extension>',
    },
    {
        label: 'Startup Task',
        category: 'windows.startupTask',
        xml: '<desktop:Extension Category="windows.startupTask">\n  <desktop:StartupTask TaskId="" Enabled="true" DisplayName="" />\n</desktop:Extension>',
    },
    {
        label: 'Share Target',
        category: 'windows.shareTarget',
        xml: '<uap:Extension Category="windows.shareTarget">\n  <uap:ShareTarget>\n    <uap:SupportedFileTypes>\n      <uap:SupportsAnyFileType />\n    </uap:SupportedFileTypes>\n    <uap:DataFormat>Text</uap:DataFormat>\n  </uap:ShareTarget>\n</uap:Extension>',
    },
    {
        label: 'App Service',
        category: 'windows.appService',
        xml: '<uap:Extension Category="windows.appService">\n  <uap:AppService Name="" />\n</uap:Extension>',
    },
    {
        label: 'Toast Notification Activation',
        category: 'windows.toastNotificationActivation',
        xml: '<desktop:Extension Category="windows.toastNotificationActivation">\n  <desktop:ToastNotificationActivation ToastActivatorCLSID="" />\n</desktop:Extension>',
    },
] as const;

/** Descriptions for known capabilities. */
export const CAPABILITY_DESCRIPTIONS: Record<string, string> = {
    internetClient: 'Provides outbound access to the internet and networks in public places like airports and coffee shops.',
    internetClientServer: 'Provides inbound and outbound access to the internet and networks in public places.',
    privateNetworkClientServer: 'Provides inbound and outbound access to home and work networks through the firewall.',
    codeGeneration: 'Allows the app to generate code dynamically using JIT compilation.',
    musicLibrary: 'Provides access to the user\'s music library.',
    picturesLibrary: 'Provides access to the user\'s pictures library.',
    videosLibrary: 'Provides access to the user\'s videos library.',
    removableStorage: 'Provides access to files on removable storage (USB drives, external hard drives).',
    appointments: 'Provides access to the user\'s appointment store.',
    contacts: 'Provides access to the user\'s contacts.',
    enterpriseAuthentication: 'Allows the app to use Windows integrated authentication (Kerberos/NTLM).',
    sharedUserCertificates: 'Provides access to software and hardware certificates (smart cards, etc.).',
    phoneCall: 'Allows the app to access phone lines and place calls.',
    userAccountInformation: 'Provides access to the user\'s name and picture.',
    voipCall: 'Allows the app to access VoIP calling APIs.',
    objects3D: 'Provides programmatic access to the user\'s 3D Objects folder.',
    chat: 'Allows the app to read and delete text messages.',
    blockedChatMessages: 'Allows the app to read chat messages blocked by the spam filter.',
    backgroundMediaPlayback: 'Allows audio and video playback while the app is in the background.',
    remoteSystem: 'Allows the app to discover and connect to remote devices.',
    spatialPerception: 'Provides access to spatial mapping data for mixed-reality apps.',
    globalMediaControl: 'Allows the app to access system media transport controls.',
    graphicsCapture: 'Allows the app to capture screen, window, or display content.',
    userDataTasks: 'Provides access to user data tasks (to-do items).',
    userNotificationListener: 'Provides access to user notifications in the action center.',
    runFullTrust: 'Allows a desktop app to run with full trust permissions outside the app container.',
    allowElevation: 'Allows a packaged app to request elevated (admin) privileges at launch.',
    unvirtualizedResources: 'Allows the app to access file system and registry locations without virtualization.',
    packagedShellExtension: 'Allows the app to register shell extensions (context menu handlers, preview handlers, etc.).',
    appDiagnostics: 'Allows the app to access diagnostic information about other running apps.',
    broadFileSystemAccess: 'Provides broad access to the file system (beyond specific libraries).',
    packageManagement: 'Allows the app to manage other packages (install, remove, etc.).',
    packageQuery: 'Allows the app to query information about installed packages.',
    localSystemServices: 'Allows the app to communicate with local system services.',
    inputForegroundObservation: 'Allows the app to observe foreground input even when not in the foreground.',
    confirmAppClose: 'Allows the app to intercept and confirm close operations.',
    systemAIModels: 'Grants the app access to system-wide on-device AI models provided by Windows.',
    microphone: 'Provides access to the microphone for audio capture.',
    webcam: 'Provides access to the webcam for video capture.',
    location: 'Provides access to the device location (GPS, Wi-Fi, etc.).',
    bluetooth: 'Provides access to Bluetooth devices for communication.',
    proximity: 'Provides access to Near Field Communication (NFC) devices.',
    usb: 'Provides access to USB devices.',
    humaninterfacedevice: 'Provides access to Human Interface Devices (HID).',
    pointOfService: 'Provides access to point-of-service peripherals (barcode scanners, etc.).',
    wiFiControl: 'Allows the app to scan for and connect to Wi-Fi networks.',
    radios: 'Allows the app to toggle device radios (Wi-Fi, Bluetooth, etc.).',
    optical: 'Provides access to optical disc drives.',
    activity: 'Provides access to activity sensors (accelerometer, pedometer).',
    serialcommunication: 'Provides access to serial communication ports.',
    gazeInput: 'Provides access to eye-tracking/gaze input devices.',
    lowLevelDevices: 'Provides low-level access to GPIO, I2C, SPI, and PWM devices.',
    lowLevel: 'Provides access to low-level device resources.',
};

/** Processor architecture dropdown options. */
export const ARCHITECTURE_OPTIONS = ['x86', 'x64', 'arm', 'arm64', 'x86a64', 'neutral'] as const;

/** Target device family dropdown options. */
export const DEVICE_FAMILY_OPTIONS = [
    'Windows.Universal',
    'Windows.Desktop',
    'Windows.Mobile',
    'Windows.Xbox',
    'Windows.Holographic',
    'Windows.IoT',
] as const;

/** Optional visual asset types that can be added to an application. */
export const OPTIONAL_VISUAL_ASSETS = [
    { field: 'wide310x150Logo', label: 'Wide 310x150 Logo', placeholder: 'Assets\\Wide310x150Logo.png', description: 'Wide tile image for the Start menu — package-relative path or key in resources.pri' },
    { field: 'square71x71Logo', label: 'Square 71x71 Logo', placeholder: 'Assets\\Square71x71Logo.png', description: 'Small tile image — package-relative path or key in resources.pri' },
    { field: 'square310x310Logo', label: 'Square 310x310 Logo', placeholder: 'Assets\\Square310x310Logo.png', description: 'Large tile image for the Start menu — package-relative path or key in resources.pri' },
    { field: 'badgeLogo', label: 'Badge Logo', placeholder: 'Assets\\BadgeLogo.png', description: 'Badge notification image shown on the lock screen — package-relative path or key in resources.pri' },
    { field: 'splashScreenImage', label: 'Splash Screen', placeholder: 'Assets\\SplashScreen.png', description: 'Image displayed while the app is launching — package-relative path or key in resources.pri' },
] as const;

/** Tile sizes that support showing the app name overlay. */
export const SHOW_NAME_ON_TILES_OPTIONS = [
    { tile: 'square150x150Logo', label: 'Medium (150×150)', veField: 'square150x150Logo' },
    { tile: 'wide310x150Logo', label: 'Wide (310×150)', veField: 'wide310x150Logo' },
    { tile: 'square310x310Logo', label: 'Large (310×310)', veField: 'square310x310Logo' },
] as const;
