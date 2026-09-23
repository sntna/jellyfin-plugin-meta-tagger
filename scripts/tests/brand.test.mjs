import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const root = new URL('../../', import.meta.url);
test('dashboard embeds both supplied full logos with an accessible heading', () => {
    const html = readFileSync(new URL('Jellyfin.Plugin.MetaTagger/Configuration/configPage.html', root), 'utf8');
    for (const tone of ['light', 'dark']) {
        const source = readFileSync(new URL(`docs/brand/assets/logo-${tone}.svg`, root), 'utf8').trim();
        const embedded = html.match(new RegExp(`class="metadataTaggerLogo metadataTaggerLogo-${tone}"[^>]*src="data:image/svg\\+xml;base64,([^\"]+)"`));
        assert.ok(embedded, `${tone} logo is embedded`);
        assert.equal(Buffer.from(embedded[1], 'base64').toString(), source);
    }
    assert.match(html, /<h1 aria-label="Meta Tagger">/);
});
