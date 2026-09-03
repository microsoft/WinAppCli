import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { spawn } from 'child_process';

export const WINAPP_CLI_CALLER_VALUE = 'nodejs-package';

/** Environment variable naming one logical UI workflow for cooperative desktop turns. */
export const WINAPP_UI_WORKFLOW_ID = 'WINAPP_UI_WORKFLOW_ID';

/**
 * Builds the child environment, adding the workflow id when one was supplied.
 *
 * The value is applied ONLY to the spawned child's environment — `process.env` is never mutated.
 * Mutating it would silently enrol every later call in this process, including unrelated ones, into
 * a workflow the caller only meant for one command, and would race across concurrent calls.
 */
function childEnv(workflowId?: string): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE,
  };

  if (workflowId !== undefined) {
    env[WINAPP_UI_WORKFLOW_ID] = workflowId;
  }

  return env;
}

export interface CallWinappCliOptions {
  exitOnError?: boolean;
  /**
   * Cancels the whole native invocation, not just a wait for the shared desktop.
   *
   * On Windows, Node force-terminates the child, so the CLI's own cleanup may not run. That is safe:
   * Windows closes the process's coordination file handles and deletes its `DeleteOnClose` participant
   * lease, and other `winapp ui` processes prune the entry through lease and PID/start validation.
   * If the abort lands after the command acquired the desktop, UI side effects may already have
   * happened, and aborting an active recording can leave partial or invalid output — this wrapper does
   * not promise graceful MP4 finalization.
   *
   * Rejects with an `AbortError`.
   */
  signal?: AbortSignal;
  /**
   * Groups this call with other `winapp ui` calls that pass the same value into one logical workflow.
   *
   * Collision arbitration is always on — every desktop-sensitive `winapp ui` command takes a turn
   * whether or not you set this. What a workflow id adds is *continuity*: commands sharing one keep the
   * desktop reserved between invocations for a short idle grace, may overlap with each other (a
   * recording and the clicks it is recording), and are never interleaved with another workflow's input.
   *
   * Without it each call is a self-contained one-shot that releases the desktop the moment it finishes.
   *
   * Applied to the spawned child only; `process.env` is never modified.
   */
  workflowId?: string;
}

export interface CallWinappCliResult {
  exitCode: number;
}

export interface CallWinappCliCaptureOptions {
  /** Working directory for the CLI process (defaults to process.cwd()) */
  cwd?: string;
  /**
   * Cancels the whole native invocation. See {@link CallWinappCliOptions.signal} for the exact
   * contract, including what is and is not guaranteed after an abort.
   */
  signal?: AbortSignal;
  /**
   * Groups this call into one logical UI workflow. See {@link CallWinappCliOptions.workflowId} for
   * what continuity buys and why arbitration does not depend on it.
   */
  workflowId?: string;
}

export interface CallWinappCliCaptureResult {
  exitCode: number;
  stdout: string;
  stderr: string;
}

/**
 * Helper function to get the path to the winapp-cli executable
 */
export function getWinappCliPath(): string {
  // Determine architecture
  const arch = os.arch() === 'arm64' ? 'win-arm64' : 'win-x64';

  // Look for the winapp-cli executable in various locations
  const possiblePaths = [
    // Distribution build (single-file executable in npm package)
    path.join(__dirname, `../bin/${arch}/winapp.exe`),
    // Build artifacts (published by build-cli.ps1, TFM-independent)
    path.join(__dirname, `../../../artifacts/cli/${arch}/winapp.exe`),
    // Global installation
    'winapp.exe',
  ];

  return possiblePaths.find((p) => fs.existsSync(p)) || possiblePaths[0];
}

/**
 * Helper function to call the native winapp-cli
 * Always captures output and returns it along with the exit code
 */
export async function callWinappCli(args: string[], options: CallWinappCliOptions = {}): Promise<CallWinappCliResult> {
  const { exitOnError = false, signal, workflowId } = options;
  const winappCliPath = getWinappCliPath();

  return new Promise((resolve, reject) => {
    const child = spawn(winappCliPath, args, {
      stdio: 'inherit',
      cwd: process.cwd(),
      shell: false,
      signal,
      env: childEnv(workflowId),
    });

    child.on('close', (code) => {
      if (code === 0) {
        resolve({ exitCode: code });
      } else {
        if (exitOnError) {
          process.exit(code ?? 1);
        } else {
          reject(new Error(`winapp-cli exited with code ${code}`));
        }
      }
    });

    child.on('error', (error) => {
      // An aborted spawn surfaces here as an AbortError. Propagate it unchanged so callers can
      // distinguish "I cancelled this" from "the CLI could not be launched", and never call
      // process.exit for it — cancellation is the caller's decision, not a fatal tool failure.
      if (isAbortError(error)) {
        reject(error);
        return;
      }

      if (exitOnError) {
        console.error(`Failed to execute winapp-cli: ${error.message}`);
        console.error(`Tried to run: ${winappCliPath}`);
        process.exit(1);
      } else {
        reject(new Error(`Failed to execute winapp-cli: ${error.message}`));
      }
    });
  });
}

/**
 * Call the native winapp-cli and capture stdout/stderr instead of inheriting stdio.
 * Use this for programmatic access where you need the output.
 */
export async function callWinappCliCapture(
  args: string[],
  options: CallWinappCliCaptureOptions = {}
): Promise<CallWinappCliCaptureResult> {
  const { cwd = process.cwd(), signal, workflowId } = options;
  const winappCliPath = getWinappCliPath();

  return new Promise((resolve, reject) => {
    const stdoutChunks: Buffer[] = [];
    const stderrChunks: Buffer[] = [];

    const child = spawn(winappCliPath, args, {
      stdio: ['pipe', 'pipe', 'pipe'],
      cwd,
      shell: false,
      signal,
      env: childEnv(workflowId),
    });

    child.stdout.on('data', (chunk: Buffer) => stdoutChunks.push(chunk));
    child.stderr.on('data', (chunk: Buffer) => stderrChunks.push(chunk));

    child.on('close', (code) => {
      const stdout = Buffer.concat(stdoutChunks).toString('utf8');
      const stderr = Buffer.concat(stderrChunks).toString('utf8');
      const exitCode = code ?? 1;

      if (exitCode === 0) {
        resolve({ exitCode, stdout, stderr });
      } else {
        const error = new Error(`winapp-cli exited with code ${exitCode}: ${stderr || stdout}`) as Error & {
          exitCode: number;
          stdout: string;
          stderr: string;
        };
        error.exitCode = exitCode;
        error.stdout = stdout;
        error.stderr = stderr;
        reject(error);
      }
    });

    child.on('error', (error) => {
      // Propagate an AbortError unchanged so callers can tell cancellation apart from a launch failure.
      reject(isAbortError(error) ? error : new Error(`Failed to execute winapp-cli: ${error.message}`));
    });
  });
}

/** Whether an error came from an aborted {@link AbortSignal} rather than a real spawn failure. */
function isAbortError(error: Error): boolean {
  return (error as NodeJS.ErrnoException).name === 'AbortError';
}
