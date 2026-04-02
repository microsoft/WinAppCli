/**
 * Custom Text Editor Provider for appxmanifest.xml files.
 * Opens a webview-based form editor when an appxmanifest.xml is opened.
 */

import * as vscode from 'vscode';
import * as path from 'path';
import * as crypto from 'crypto';
import { execFile } from 'child_process';
import { parseManifest, applyFieldChange, addCapability, removeCapability, addPackageDependency, removePackageDependency, addTargetDeviceFamily, removeTargetDeviceFamily, addApplication, removeApplication, addExtension, removeExtension, updateExtensionField } from './manifest-parser';
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
        const manifestDir = vscode.Uri.file(path.dirname(document.uri.fsPath));
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [this.context.extensionUri, manifestDir],
        };

        const nonce = crypto.randomBytes(16).toString('hex');
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
                    webviewPanel.webview.html = getParseErrorContent(webviewPanel.webview, nonce, errMsg);
                }
                return false;
            }
        };

        /** Load the full editor view. */
        const showEditorView = () => {
            showingErrorView = false;
            webviewPanel.webview.html = getWebviewContent(webviewPanel.webview, nonce, manifestDirUri);
            // The editor will send 'ready' once loaded, which triggers updateWebview
        };

        /** Send the current document state to the webview. */
        const updateWebview = () => {
            const text = document.getText();
            if (!tryParseOrShowError(text)) { return; }
            if (showingErrorView) { showEditorView(); return; }
            try {
                const data = parseManifest(text);
                const errors = validateManifest(data);
                webviewPanel.webview.postMessage({ type: 'update', data, errors });
            } catch {
                // Should not happen since tryParseOrShowError succeeded
            }
        };

        // Initial load: check if XML is valid
        if (tryParseOrShowError(document.getText())) {
            webviewPanel.webview.html = getWebviewContent(webviewPanel.webview, nonce, manifestDirUri);
        }

        // Listen for document changes (e.g., from the text editor or external edits)
        const changeDocSub = vscode.workspace.onDidChangeTextDocument(e => {
            if (e.document.uri.toString() === document.uri.toString() && !isApplyingEdit) {
                if (showingErrorView) {
                    // Check if the XML is now valid — if so, switch to editor
                    const text = document.getText();
                    if (tryParseOrShowError(text)) {
                        showEditorView();
                    }
                } else {
                    updateWebview();
                }
            }
        });

        webviewPanel.onDidDispose(() => {
            changeDocSub.dispose();
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

                    case 'openAsText':
                        await vscode.commands.executeCommand('vscode.openWith', document.uri, 'default');
                        return;

                    case 'fieldChanged':
                        newText = applyFieldChange(text, message.section, message.field, message.value, message.index);
                        break;

                    case 'addCapability':
                        newText = addCapability(text, message.capability);
                        break;

                    case 'removeCapability':
                        newText = removeCapability(text, message.capability);
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
            } catch {
                // XML manipulation failed — ignore to avoid corrupting the document
                return;
            }

            if (newText !== undefined && newText !== text) {
                isApplyingEdit = true;
                const edit = new vscode.WorkspaceEdit();
                edit.replace(
                    document.uri,
                    new vscode.Range(0, 0, document.lineCount, 0),
                    newText,
                );
                await vscode.workspace.applyEdit(edit);
                isApplyingEdit = false;

                // Update webview with the new state (including validation)
                updateWebview();
            }
        });
    }
}
