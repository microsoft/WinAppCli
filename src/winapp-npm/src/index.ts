// Main entry point for the Windows SDK BuildTools package
import { execSyncWithBuildTools } from './buildtools-utils';
import { addMsixIdentityToExe, addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getGlobalWinappPath, getLocalWinappPath } from './winapp-path-utils';
import * as winappCommands from './winapp-commands';
import { uiRecord } from './ui-record-guard';

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

// Re-export all command types and functions automatically from the generated module.
// NOTE: winapp-commands.ts exports _uiRecordGenerated (not uiRecord) so there is no conflict
// with the guarded uiRecord exported below.
export * from './winapp-commands';

// Export the public, guarded uiRecord wrapper (overrides the internal _uiRecordGenerated).
// Importing from this module gives the safe version that enforces durationSec > 0.
export { uiRecord } from './ui-record-guard';

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
};

// Default export for CommonJS compatibility
export default {
  execWithBuildTools: execSyncWithBuildTools,
  addMsixIdentityToExe,
  addElectronDebugIdentity,
  clearElectronDebugIdentity,
  getGlobalWinappPath,
  getLocalWinappPath,
  ...winappCommands,
  uiRecord, // guarded wrapper overrides the _uiRecordGenerated from the spread above
};
