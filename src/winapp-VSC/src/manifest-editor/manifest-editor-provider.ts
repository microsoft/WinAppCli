/**
 * Custom Text Editor Provider for appxmanifest.xml files.
 * Opens a webview-based form editor when an appxmanifest.xml is opened.
 */

import * as vscode from 'vscode';
import * as crypto from 'crypto';
import { parseManifest, applyFieldChange, addCapability, removeCapability, addPackageDependency, removePackageDependency, addTargetDeviceFamily, removeTargetDeviceFamily } from './manifest-parser';
import { validateManifest } from './manifest-validator';
import { getWebviewContent } from './webview-content';
import { WebviewToExtensionMessage } from './manifest-types';

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
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [this.context.extensionUri],
        };

        const nonce = crypto.randomBytes(16).toString('hex');
        webviewPanel.webview.html = getWebviewContent(webviewPanel.webview, nonce);

        // Track whether we're currently applying an edit to avoid feedback loops
        let isApplyingEdit = false;

        /** Send the current document state to the webview. */
        const updateWebview = () => {
            const text = document.getText();
            try {
                const data = parseManifest(text);
                const errors = validateManifest(data);
                webviewPanel.webview.postMessage({ type: 'update', data, errors });
            } catch {
                // If parsing fails, the XML is malformed — don't crash the editor
            }
        };

        // Listen for document changes (e.g., from the text editor or external edits)
        const changeDocSub = vscode.workspace.onDidChangeTextDocument(e => {
            if (e.document.uri.toString() === document.uri.toString() && !isApplyingEdit) {
                updateWebview();
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
