// Embed the supplied logo exports; plugin.png is supplied artwork, not regenerated.
const fs = require('node:fs');
const path = require('node:path');

const assets = path.join(__dirname, 'assets');
const logos = ['light', 'dark'].map(tone => {
  const source = fs.readFileSync(path.join(assets, `logo-${tone}.svg`), 'utf8').trim();
  return `<img class="metadataTaggerLogo metadataTaggerLogo-${tone}" alt="" width="1127" height="200" src="data:image/svg+xml;base64,${Buffer.from(source).toString('base64')}">`;
}).join('');
const dashboardPath = path.resolve(__dirname, '../../Jellyfin.Plugin.MetaTagger/Configuration/configPage.html');
const dashboard = fs.readFileSync(dashboardPath, 'utf8');
const pattern = /<!-- brand-logo:start -->[\s\S]*?<!-- brand-logo:end -->/g;
if ([...dashboard.matchAll(pattern)].length !== 1) {
  throw new Error('Expected exactly one dashboard logo to update.');
}
fs.writeFileSync(dashboardPath, dashboard.replace(pattern, `<!-- brand-logo:start -->${logos}<!-- brand-logo:end -->`));
