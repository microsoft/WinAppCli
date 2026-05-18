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

        // Table-driven dispatch for simple XML operations
        const simpleDispatch: Record<string, (text: string, msg: any) => string> = {
            addCapability: (t, m) => addCapability(t, m.capability),
            removeCapability: (t, m) => removeCapability(t, m.capability),
            addPhoneIdentity: (t) => addPhoneIdentity(t),
            removePhoneIdentity: (t) => removePhoneIdentity(t),
            addResource: (t, m) => addResource(t, m.resource),
            removeResource: (t, m) => removeResource(t, m.index),
            moveResource: (t, m) => moveResource(t, m.index, m.direction),
            addPackageDependency: (t, m) => addPackageDependency(t, m.dependency),
            removePackageDependency: (t, m) => removePackageDependency(t, m.index),
            movePackageDependency: (t, m) => movePackageDependency(t, m.index, m.direction),
            addTargetDeviceFamily: (t, m) => addTargetDeviceFamily(t, m.family),
            removeTargetDeviceFamily: (t, m) => removeTargetDeviceFamily(t, m.index),
            moveTargetDeviceFamily: (t, m) => moveTargetDeviceFamily(t, m.index, m.direction),
            addMainPackageDependency: (t, m) => addMainPackageDependency(t, m.dependency),
            removeMainPackageDependency: (t, m) => removeMainPackageDependency(t, m.index),
            moveMainPackageDependency: (t, m) => moveMainPackageDependency(t, m.index, m.direction),
            addDriverConstraint: (t, m) => addDriverConstraint(t, m.constraint),
            removeDriverConstraint: (t, m) => removeDriverConstraint(t, m.index),
            moveDriverConstraint: (t, m) => moveDriverConstraint(t, m.index, m.direction),
            addOSPackageDependency: (t, m) => addOSPackageDependency(t, m.dependency),
            removeOSPackageDependency: (t, m) => removeOSPackageDependency(t, m.index),
            moveOSPackageDependency: (t, m) => moveOSPackageDependency(t, m.index, m.direction),
            addHostRuntimeDependency: (t, m) => addHostRuntimeDependency(t, m.dependency),
            removeHostRuntimeDependency: (t, m) => removeHostRuntimeDependency(t, m.index),
            moveHostRuntimeDependency: (t, m) => moveHostRuntimeDependency(t, m.index, m.direction),
            addExternalDependency: (t, m) => addExternalDependency(t, m.dependency),
            removeExternalDependency: (t, m) => removeExternalDependency(t, m.index),
            moveExternalDependency: (t, m) => moveExternalDependency(t, m.index, m.direction),
            addApplication: (t) => addApplication(t),
            removeApplication: (t, m) => removeApplication(t, m.index),
            addExtension: (t, m) => addExtension(t, m.index, m.xml),
            removeExtension: (t, m) => removeExtension(t, m.appIndex, m.extIndex),
            updateExtensionField: (t, m) => updateExtensionField(t, m.appIndex, m.extIndex, m.fieldPath, m.value, m.isTextContent),
            setShowNameOnTiles: (t, m) => setShowNameOnTiles(t, m.appIndex, m.tiles),
        };

        /** Browse for a file and return its path relative to the manifest directory. */
        async function browseAndApplyField(title: string, filters: Record<string, string[]>): Promise<string | undefined> {
            const filePath = await vscode.window.showOpenDialog({
                canSelectFiles: true, canSelectFolders: false, canSelectMany: false,
                title, filters,
                defaultUri: vscode.Uri.file(path.dirname(document.uri.fsPath)),
            });
            if (!filePath || filePath.length === 0) { return undefined; }
            return path.relative(path.dirname(document.uri.fsPath), filePath[0].fsPath);
        }

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
                // Table-driven dispatch for simple XML operations
                if (simpleDispatch[message.type]) {
                    newText = simpleDispatch[message.type](text, message);
                } else {
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

                        case 'browseFile': {
                            const relPath = await browseAndApplyField('Select JSON file', { 'JSON': ['json'] });
                            if (!relPath) { return; }
                            newText = updateExtensionField(text, message.appIndex, message.extIndex, message.fieldPath, relPath, true);
                            break;
                        }

                        case 'browseImage': {
                            const relPath = await browseAndApplyField('Select image', { 'Images': ['png', 'jpg', 'jpeg', 'svg', 'ico'] });
                            if (!relPath) { return; }
                            newText = applyFieldChange(text, message.section, message.field, relPath, message.index);
                            break;
                        }

                        case 'browseExe': {
                            const relPath = await browseAndApplyField('Select executable', { 'Executables': ['exe'] });
                            if (!relPath) { return; }
                            newText = applyFieldChange(text, message.section, message.field, relPath, message.index);
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
                }
            } catch (err) {
                const errMsg = err instanceof Error ? err.message : String(err);
                console.warn('[ManifestEditor] XML manipulation failed:', err);
                vscode.window.showErrorMessage(`Manifest edit failed: ${errMsg}`);
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
