/**
 * Custom Text Editor Provider for appxmanifest.xml files.
 * Opens a webview-based form editor when an appxmanifest.xml is opened.
 */

import * as vscode from 'vscode';
import * as path from 'path';
import * as crypto from 'crypto';
import { execFile } from 'child_process';
import { parseManifest, applyFieldChange, addCapability, removeCapability, addPackageDependency, removePackageDependency, addTargetDeviceFamily, removeTargetDeviceFamily, moveTargetDeviceFamily, movePackageDependency, addMainPackageDependency, removeMainPackageDependency, moveMainPackageDependency, addDriverConstraint, removeDriverConstraint, moveDriverConstraint, addOSPackageDependency, removeOSPackageDependency, moveOSPackageDependency, addHostRuntimeDependency, removeHostRuntimeDependency, moveHostRuntimeDependency, addExternalDependency, removeExternalDependency, moveExternalDependency, addApplication, removeApplication, addExtension, removeExtension, updateExtensionField, addResource, removeResource, moveResource, setShowNameOnTiles, addPhoneIdentity, removePhoneIdentity } from './manifest-parser';
import { validateManifest } from './manifest-validator';
import { getWebviewContent, getParseErrorContent } from './webview-content';
import { WebviewToExtensionMessage } from './manifest-types';
import { getWinappCliPath, WINAPP_CLI_CALLER_VALUE } from '../winapp-cli-utils';

export class ManifestEditorProvider implements vscode.CustomTextEditorProvider {
    public static readonly viewType = 'winapp.manifestEditor';

    constructor(private readonly context: vscode.ExtensionContext) {}

    public static register(context: vscode.ExtensionContext): vscode.Disposable {
        const provider = new ManifestEditorProvider(context);
        return vscode.window.registerCustomEditorProvider(
            ManifestEditorProvider.viewType,
            provider,
            {
                webviewOptions: { retainContextWhenHidden: true },
                supportsMultipleEditorsPerDocument: false,
            },
        );
    }

    public async resolveCustomTextEditor(
        document: vscode.TextDocument,
        webviewPanel: vscode.WebviewPanel,
        _token: vscode.CancellationToken,
    ): Promise<void> {
        // When opened from Source Control diff or other non-file contexts,
        // fall back to the default text editor so the user sees a proper diff.
        if (document.uri.scheme !== 'file') {
            webviewPanel.webview.html = '';
            await vscode.commands.executeCommand('vscode.openWith', document.uri, 'default');
            return;
        }

        const manifestDir = vscode.Uri.file(path.dirname(document.uri.fsPath));
        const resourceRoots: vscode.Uri[] = [this.context.extensionUri, manifestDir];
        // Include workspace folder roots so relative paths with ".." can resolve
        if (vscode.workspace.workspaceFolders) {
            for (const wf of vscode.workspace.workspaceFolders) {
                resourceRoots.push(wf.uri);
            }
        }
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: resourceRoots,
        };

        const freshNonce = () => crypto.randomBytes(16).toString('hex');
        const manifestDirUri = webviewPanel.webview.asWebviewUri(manifestDir).toString();

        // Track whether we're currently applying an edit to avoid feedback loops
        let isApplyingEdit = false;
        let showingErrorView = false;

        /** Try to parse — if it fails, show error view; if it succeeds, show/update editor. */
        const tryParseOrShowError = (text: string): boolean => {
            try {
                parseManifest(text);
                return true;
            } catch (e) {
                const errMsg = e instanceof Error ? e.message : String(e);
                if (!showingErrorView) {
                    showingErrorView = true;
                    webviewPanel.webview.html = getParseErrorContent(webviewPanel.webview, freshNonce(), errMsg);
                }
                return false;
            }
        };

        /** Load the full editor view. */
        const showEditorView = () => {
            showingErrorView = false;
            webviewPanel.webview.html = getWebviewContent(webviewPanel.webview, freshNonce(), manifestDirUri);
            // The editor will send 'ready' once loaded, which triggers updateWebview
        };

        /** Send the current document state to the webview. */
        const updateWebview = (forceAll = false) => {
            const text = document.getText();
            if (!tryParseOrShowError(text)) { return; }
            if (showingErrorView) { showEditorView(); return; }
            try {
                const data = parseManifest(text);
                const errors = validateManifest(data);
                webviewPanel.webview.postMessage({ type: 'update', data, errors, forceAll });
            } catch {
                // Should not happen since tryParseOrShowError succeeded
            }
        };

        // Initial load: check if XML is valid
        if (tryParseOrShowError(document.getText())) {
            webviewPanel.webview.html = getWebviewContent(webviewPanel.webview, freshNonce(), manifestDirUri);
        }

        // Listen for document changes (e.g., from the text editor, undo, or external edits)
        const changeDocSub = vscode.workspace.onDidChangeTextDocument(e => {
            if (e.document.uri.toString() === document.uri.toString() && !isApplyingEdit) {
                if (showingErrorView) {
                    // Check if the XML is now valid — if so, switch to editor
                    const text = document.getText();
                    if (tryParseOrShowError(text)) {
                        showEditorView();
                    }
                } else {
                    // External change (undo, redo, text editor) — force-update all fields
                    updateWebview(true);
                }
            }
        });

        // Flush pending webview input changes before save so Ctrl+S captures edits
        // that are still in the 300ms debounce window.
        let pendingSaveResolve: ((edits: vscode.TextEdit[]) => void) | null = null;
        let pendingSaveNonce: string | null = null;
        const willSaveSub = vscode.workspace.onWillSaveTextDocument(e => {
            if (e.document.uri.toString() === document.uri.toString()) {
                e.waitUntil(new Promise<vscode.TextEdit[]>((resolve) => {
                    const nonce = crypto.randomUUID();
                    pendingSaveResolve = resolve;
                    pendingSaveNonce = nonce;
                    webviewPanel.webview.postMessage({ type: 'flushChanges', nonce });
                    // Timeout fallback — don't block save forever
                    setTimeout(() => {
                        if (pendingSaveNonce === nonce) {
                            pendingSaveResolve = null;
                            pendingSaveNonce = null;
                            resolve([]);
                        }
                    }, 500);
                }));
            }
        });

        webviewPanel.onDidDispose(() => {
            changeDocSub.dispose();
            willSaveSub.dispose();
        });

        // Handle messages from the webview
        webviewPanel.webview.onDidReceiveMessage(async (message: WebviewToExtensionMessage) => {
            const text = document.getText();
            let newText: string | undefined;

            try {
                switch (message.type) {
                    case 'ready':
                        updateWebview();
                        return;

                    case 'changesFlushed': {
                        // Apply all pending field changes and resolve the save promise
                        // Match nonce to prevent stale resolution from rapid double-saves
                        if (pendingSaveResolve && message.nonce === pendingSaveNonce) {
                            let result = text;
                            for (const change of message.changes) {
                                result = applyFieldChange(result, change.section, change.field, change.value, change.index);
                            }
                            const edits = result !== text
                                ? [vscode.TextEdit.replace(new vscode.Range(0, 0, document.lineCount, 0), result)]
                                : [];
                            const resolve = pendingSaveResolve;
                            pendingSaveResolve = null;
                            pendingSaveNonce = null;
                            resolve(edits);
                        }
                        return;
                    }

                    case 'openAsText':
                        await vscode.commands.executeCommand('vscode.openWith', document.uri, 'default');
                        return;

                    case 'fieldChanged':
                        newText = applyFieldChange(text, message.section, message.field, message.value, message.index, message.subIndex);
                        break;

                    case 'packageTypeChanged': {
                        // Set/clear the three mutually exclusive package type properties
                        let result = text;
                        result = applyFieldChange(result, 'properties', 'framework', message.value === 'framework' ? 'true' : '');
                        result = applyFieldChange(result, 'properties', 'resourcePackage', message.value === 'resource' ? 'true' : '');
                        result = applyFieldChange(result, 'properties', 'modificationPackage', message.value === 'modification' ? 'true' : '');
                        newText = result;
                        break;
                    }

                    case 'addCapability':
                        newText = addCapability(text, message.capability);
                        break;

                    case 'removeCapability':
                        newText = removeCapability(text, message.capability);
                        break;

                    case 'addPhoneIdentity':
                        newText = addPhoneIdentity(text);
                        break;

                    case 'removePhoneIdentity':
                        newText = removePhoneIdentity(text);
                        break;

                    case 'addResource':
                        newText = addResource(text, message.resource);
                        break;

                    case 'removeResource':
                        newText = removeResource(text, message.index);
                        break;

                    case 'moveResource':
                        newText = moveResource(text, message.index, message.direction);
                        break;

                    case 'setShowNameOnTiles':
                        newText = setShowNameOnTiles(text, message.appIndex, message.tiles);
                        break;

                    case 'addPackageDependency':
                        newText = addPackageDependency(text, message.dependency);
                        break;

                    case 'removePackageDependency':
                        newText = removePackageDependency(text, message.index);
                        break;

                    case 'addTargetDeviceFamily':
                        newText = addTargetDeviceFamily(text, message.family);
                        break;

                    case 'removeTargetDeviceFamily':
                        newText = removeTargetDeviceFamily(text, message.index);
                        break;

                    case 'moveTargetDeviceFamily':
                        newText = moveTargetDeviceFamily(text, message.index, message.direction);
                        break;

                    case 'movePackageDependency':
                        newText = movePackageDependency(text, message.index, message.direction);
                        break;

                    case 'addMainPackageDependency':
                        newText = addMainPackageDependency(text, message.dependency);
                        break;
                    case 'removeMainPackageDependency':
                        newText = removeMainPackageDependency(text, message.index);
                        break;
                    case 'moveMainPackageDependency':
                        newText = moveMainPackageDependency(text, message.index, message.direction);
                        break;

                    case 'addDriverConstraint':
                        newText = addDriverConstraint(text, message.constraint);
                        break;
                    case 'removeDriverConstraint':
                        newText = removeDriverConstraint(text, message.index);
                        break;
                    case 'moveDriverConstraint':
                        newText = moveDriverConstraint(text, message.index, message.direction);
                        break;

                    case 'addOSPackageDependency':
                        newText = addOSPackageDependency(text, message.dependency);
                        break;
                    case 'removeOSPackageDependency':
                        newText = removeOSPackageDependency(text, message.index);
                        break;
                    case 'moveOSPackageDependency':
                        newText = moveOSPackageDependency(text, message.index, message.direction);
                        break;

                    case 'addHostRuntimeDependency':
                        newText = addHostRuntimeDependency(text, message.dependency);
                        break;
                    case 'removeHostRuntimeDependency':
                        newText = removeHostRuntimeDependency(text, message.index);
                        break;
                    case 'moveHostRuntimeDependency':
                        newText = moveHostRuntimeDependency(text, message.index, message.direction);
                        break;

                    case 'addExternalDependency':
                        newText = addExternalDependency(text, message.dependency);
                        break;
                    case 'removeExternalDependency':
                        newText = removeExternalDependency(text, message.index);
                        break;
                    case 'moveExternalDependency':
                        newText = moveExternalDependency(text, message.index, message.direction);
                        break;

                    case 'addApplication':
                        newText = addApplication(text);
                        break;

                    case 'removeApplication':
                        newText = removeApplication(text, message.index);
                        break;

                    case 'addExtension':
                        newText = addExtension(text, message.index, message.xml);
                        break;

                    case 'removeExtension':
                        newText = removeExtension(text, message.appIndex, message.extIndex);
                        break;

                    case 'updateExtensionField':
                        newText = updateExtensionField(text, message.appIndex, message.extIndex, message.fieldPath, message.value, message.isTextContent);
                        break;

                    case 'browseFile': {
                        const filePath = await vscode.window.showOpenDialog({
                            canSelectFiles: true,
                            canSelectFolders: false,
                            canSelectMany: false,
                            title: 'Select JSON file',
                            filters: { 'JSON': ['json'] },
                            defaultUri: vscode.Uri.file(path.dirname(document.uri.fsPath)),
                        });
                        if (!filePath || filePath.length === 0) { return; }
                        const relativePath = path.relative(path.dirname(document.uri.fsPath), filePath[0].fsPath);
                        newText = updateExtensionField(text, message.appIndex, message.extIndex, message.fieldPath, relativePath, true);
                        break;
                    }

                    case 'browseImage': {
                        const imgPath = await vscode.window.showOpenDialog({
                            canSelectFiles: true,
                            canSelectFolders: false,
                            canSelectMany: false,
                            title: 'Select image',
                            filters: { 'Images': ['png', 'jpg', 'jpeg', 'svg', 'ico'] },
                            defaultUri: vscode.Uri.file(path.dirname(document.uri.fsPath)),
                        });
                        if (!imgPath || imgPath.length === 0) { return; }
                        const relPath = path.relative(path.dirname(document.uri.fsPath), imgPath[0].fsPath);
                        newText = applyFieldChange(text, message.section, message.field, relPath, message.index);
                        break;
                    }

                    case 'browseExe': {
                        const exePath = await vscode.window.showOpenDialog({
                            canSelectFiles: true,
                            canSelectFolders: false,
                            canSelectMany: false,
                            title: 'Select executable',
                            filters: { 'Executables': ['exe'] },
                            defaultUri: vscode.Uri.file(path.dirname(document.uri.fsPath)),
                        });
                        if (!exePath || exePath.length === 0) { return; }
                        const relExePath = path.relative(path.dirname(document.uri.fsPath), exePath[0].fsPath);
                        newText = applyFieldChange(text, message.section, message.field, relExePath, message.index);
                        break;
                    }

                    case 'updateAssets': {
                        const imagePath = await vscode.window.showOpenDialog({
                            canSelectFiles: true,
                            canSelectFolders: false,
                            canSelectMany: false,
                            title: 'Select source image for assets',
                            filters: { 'Images': ['png', 'jpg', 'jpeg', 'svg'] },
                        });
                        if (!imagePath || imagePath.length === 0) { return; }

                        const cliPath = getWinappCliPath(this.context.extensionPath);
                        const cwd = path.dirname(document.uri.fsPath);

                        await vscode.window.withProgress(
                            { location: vscode.ProgressLocation.Notification, title: 'Regenerating assets…', cancellable: false },
                            () => new Promise<void>((resolve, reject) => {
                                execFile(cliPath, ['manifest', 'update-assets', imagePath[0].fsPath], { cwd, env: { ...process.env, WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE } }, (error) => {
                                    if (error) {
                                        vscode.window.showErrorMessage(`Asset regeneration failed: ${error.message}`);
                                        reject(error);
                                    } else {
                                        resolve();
                                    }
                                });
                            }),
                        );

                        webviewPanel.webview.postMessage({ type: 'refreshImages' });
                        return;
                    }
                }
            } catch (err) {
                console.warn('[ManifestEditor] XML manipulation failed:', err);
                return;
            }

            if (newText !== undefined && newText !== text) {
                isApplyingEdit = true;
                try {
                    const edit = new vscode.WorkspaceEdit();
                    edit.replace(
                        document.uri,
                        new vscode.Range(0, 0, document.lineCount, 0),
                        newText,
                    );
                    await vscode.workspace.applyEdit(edit);
                } finally {
                    isApplyingEdit = false;
                }

                // Update webview with the new state (including validation)
                updateWebview();
            }
        });
    }
}
