import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../dev-client.js', import.meta.url), 'utf8');
function browser() {
    let state = { revision: 'first', ready: true };
    let dirty = false;
    let timer;
    let reloads = 0;
    let clicks = 0;
    let available = true;
    const storage = new Map();
    const banner = { style: {}, setAttribute() {} };
    const tab = { id: 'TabSettings', click() { clicks++; } };
    const context = {
        sessionStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
        document: {
            body: { appendChild() {} },
            createElement: () => banner,
            getElementById: id => id === tab.id ? tab : null,
            querySelector: query => query.includes('data-unsaved') ? dirty ? {} : null : tab
        },
        location: { reload() { reloads++; } },
        fetch: async () => { if (!available) throw new Error('offline'); return { ok: true, json: async () => state }; },
        setTimeout: callback => { timer = callback; }
    };
    vm.runInNewContext(source, context);
    return {
        banner, storage,
        set state(value) { state = value; }, set dirty(value) { dirty = value; }, set available(value) { available = value; },
        get reloads() { return reloads; }, get clicks() { return clicks; },
        flush: () => new Promise(resolve => setImmediate(resolve)),
        async tick() { await timer(); }
    };
}

test('new source reloads the page and remembers the active plugin tab', async () => {
    const page = browser();
    await page.flush();
    page.state = { revision: 'second', ready: true };
    await page.tick();
    assert.equal(page.reloads, 1);
    assert.equal(page.storage.get('meta-tagger-dev-tab'), 'TabSettings');
});

test('unsaved settings defer reload until saved and builds never reload early', async () => {
    const page = browser();
    await page.flush();
    page.dirty = true;
    page.state = { revision: 'second', ready: false, status: 'Building' };
    await page.tick();
    assert.equal(page.reloads, 0);
    assert.equal(page.banner.textContent, 'Building');
    page.state = { revision: 'second', ready: true };
    await page.tick();
    assert.equal(page.reloads, 0);
    assert.match(page.banner.textContent, /Save your settings/);
    page.dirty = false;
    await page.tick();
    assert.equal(page.reloads, 1);
});

test('dev server reconnect recovers and saved tab restoration is consumed once', async () => {
    const page = browser();
    await page.flush();
    page.storage.set('meta-tagger-dev-tab', 'TabSettings');
    page.available = false;
    await page.tick();
    assert.equal(page.clicks, 1);
    assert.match(page.banner.textContent, /disconnected/);
    page.available = true;
    page.state = { revision: 'restarted', ready: true };
    await page.tick();
    assert.equal(page.reloads, 1);
});
