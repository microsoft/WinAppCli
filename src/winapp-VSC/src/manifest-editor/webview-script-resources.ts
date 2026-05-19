/**
 * Resources chunk for the AppxManifest editor webview script.
 * Returns raw JavaScript to be concatenated into the webview IIFE.
 */
export function getResourcesScript(): string {
    return `
        // ─── Add resource ───────────────────────────────────
        document.getElementById('add-resource-btn').addEventListener('click', () => {
            vscode.postMessage({
                type: 'addResource',
                resource: { language: '', scale: '', dxFeatureLevel: '' }
            });
        });

        function renderResources(resources) {
            const scaleOptions = ['', '80', '100', '120', '125', '140', '150', '160', '175', '180', '200', '225', '250', '300', '350', '400', '450'];
            const dxOptions = ['', 'dx9', 'dx10', 'dx11', 'dx12'];
            renderReorderableList('resources-list', resources, {
                titleFn: () => 'Language:',
                removeType: 'removeResource',
                moveType: 'moveResource',
                hasCustomSelects: true,
                fieldsFn: (res, idx) => {
                    const scaleOptionsHtml = scaleOptions.map(s =>
                        '<div class="custom-select-option' + (res.scale === s ? ' selected' : '') + '" data-value="' + s + '">' + (s || '(none)') + '</div>'
                    ).join('');
                    const dxOptionsHtml = dxOptions.map(d =>
                        '<div class="custom-select-option' + (res.dxFeatureLevel === d ? ' selected' : '') + '" data-value="' + d + '">' + (d || '(none)') + '</div>'
                    ).join('');
                    const scaleLabel = res.scale || '(none)';
                    const dxLabel = res.dxFeatureLevel || '(none)';
                    return \`
                    <div class="form-group" data-field="resources.\${idx}.language">
                        <input type="text" data-section="resources" data-field-name="language" data-index="\${idx}" value="\${escapeHtml(res.language)}" placeholder="en-us" />
                        <div class="description">BCP-47 language tag (e.g. "en-us", "fr-fr", "ja-jp") or "x-generate"</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="resources.\${idx}.scale">
                        <label>Scale:</label>
                        <div class="custom-select">
                            <button class="custom-select-trigger" type="button" data-section="resources" data-field-name="scale" data-index="\${idx}">\${scaleLabel}</button>
                            <div class="custom-select-options">
                                \${scaleOptionsHtml}
                            </div>
                        </div>
                        <div class="description">Resolution scale for resource selection (e.g. 100, 200, 400)</div>
                        <div class="validation-msg"></div>
                    </div>
                    <div class="form-group" data-field="resources.\${idx}.dxFeatureLevel">
                        <label>DirectX Feature Level:</label>
                        <div class="custom-select">
                            <button class="custom-select-trigger" type="button" data-section="resources" data-field-name="dxFeatureLevel" data-index="\${idx}">\${dxLabel}</button>
                            <div class="custom-select-options">
                                \${dxOptionsHtml}
                            </div>
                        </div>
                        <div class="description">DirectX feature level for resource selection</div>
                        <div class="validation-msg"></div>
                    </div>\`;
                },
            });
        }
`;
}
