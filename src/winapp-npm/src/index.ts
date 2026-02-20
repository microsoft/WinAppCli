// Main entry point for the Windows SDK BuildTools package
import { execSyncWithBuildTools } from './buildtools-utils';
import { addMsixIdentityToExe, addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getGlobalWinappPath, getLocalWinappPath } from './winapp-path-utils';
import {
  init,
  restore,
  update,
  manifestGenerate,
  manifestUpdateAssets,
  certGenerate,
  certInstall,
  packageApp,
  sign,
  createDebugIdentity,
  getWinappPath,
  tool,
  store,
} from './winapp-commands';

// Re-export types from child_process for convenience
export type { ExecSyncOptions } from 'child_process';

// Re-export types
export {
  MsixIdentityOptions,
  MsixIdentityResult,
  ElectronDebugIdentityResult,
  ClearElectronDebugIdentityResult,
} from './msix-utils';
export {
  CallWinappCliOptions,
  CallWinappCliResult,
  CallWinappCliCaptureOptions,
  CallWinappCliCaptureResult,
} from './winapp-cli-utils';
export { GenerateCppAddonOptions, GenerateCppAddonResult } from './cpp-addon-utils';
export { GenerateCsAddonOptions, GenerateCsAddonResult } from './cs-addon-utils';

// Re-export all command option/result types
export {
  CommonOptions,
  WinappResult,
  IfExistsPolicy,
  SdkInstallMode,
  ManifestTemplate,
  InitOptions,
  RestoreOptions,
  UpdateOptions,
  ManifestGenerateOptions,
  ManifestUpdateAssetsOptions,
  CertGenerateOptions,
  CertInstallOptions,
  PackageOptions,
  SignOptions,
  CreateDebugIdentityOptions,
  GetWinappPathOptions,
  ToolOptions,
  StoreOptions,
} from './winapp-commands';

// Re-export functions
export {
  // BuildTools utilities
  execSyncWithBuildTools as execWithBuildTools,

  // MSIX manifest utilities
  addMsixIdentityToExe,
  addElectronDebugIdentity,
  clearElectronDebugIdentity,

  // winapp directory utilities
  getGlobalWinappPath,
  getLocalWinappPath,

  // Programmatic CLI command wrappers
  init,
  restore,
  update,
  manifestGenerate,
  manifestUpdateAssets,
  certGenerate,
  certInstall,
  packageApp,
  sign,
  createDebugIdentity,
  getWinappPath,
  tool,
  store,
};

// Default export for CommonJS compatibility
export default {
  execWithBuildTools: execSyncWithBuildTools,
  addMsixIdentityToExe,
  addElectronDebugIdentity,
  clearElectronDebugIdentity,
  getGlobalWinappPath,
  getLocalWinappPath,
  init,
  restore,
  update,
  manifestGenerate,
  manifestUpdateAssets,
  certGenerate,
  certInstall,
  packageApp,
  sign,
  createDebugIdentity,
  getWinappPath,
  tool,
  store,
};
