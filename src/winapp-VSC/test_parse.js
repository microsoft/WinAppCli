const { DOMParser } = require('@xmldom/xmldom');

// Simulate the XML that setShowNameOnTiles generates
const xml = `<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
  <Applications>
    <Application Id="App" Executable="test.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="test"
        Description="My App"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\\MedTile.png"
        Square44x44Logo="Assets\\AppList.png">
        <uap:DefaultTile>
          <uap:ShowNameOnTiles>
            <uap:ShowOn Tile="square150x150Logo" />
          </uap:ShowNameOnTiles>
        </uap:DefaultTile>
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>`;

const doc = new DOMParser().parseFromString(xml, 'text/xml');
const root = doc.documentElement;

function findChildByLocalNameNS(parent, localName) {
    const children = parent.childNodes;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        if (child.nodeType === 1 && child.localName === localName) {
            return child;
        }
    }
    return null;
}

function getChildrenByLocalName(parent, localName) {
    const result = [];
    const children = parent.childNodes;
    for (let i = 0; i < children.length; i++) {
        const child = children[i];
        if (child.nodeType === 1 && child.localName === localName) {
            result.push(child);
        }
    }
    return result;
}

const appsEl = findChildByLocalNameNS(root, 'Applications');
console.log('Applications found:', !!appsEl);

const appEls = getChildrenByLocalName(appsEl, 'Application');
console.log('App count:', appEls.length);

const appEl = appEls[0];
const visualEl = findChildByLocalNameNS(appEl, 'VisualElements');
console.log('VisualElements found:', !!visualEl);

const defaultTile = visualEl ? findChildByLocalNameNS(visualEl, 'DefaultTile') : null;
console.log('DefaultTile found:', !!defaultTile);

if (defaultTile) {
    const showNameEl = findChildByLocalNameNS(defaultTile, 'ShowNameOnTiles');
    console.log('ShowNameOnTiles found:', !!showNameEl);
    
    if (showNameEl) {
        const showOnEls = getChildrenByLocalName(showNameEl, 'ShowOn');
        console.log('ShowOn count:', showOnEls.length);
        for (const showOn of showOnEls) {
            const tile = showOn.getAttribute('Tile');
            console.log('Tile value:', JSON.stringify(tile));
        }
    }
}
