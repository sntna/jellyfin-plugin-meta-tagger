import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../dev-client.js', import.meta.url), 'utf8');
function browser({ rememberedTab } = {}) {
    let state = { revision: 'first', ready: true };
    let dirty = false;
    let timer;
    let reloads = 0;
    let available = true;
    const storage = new Map();
    if (rememberedTab) storage.set('meta-tagger-dev-tab', rememberedTab);
    const banner = { style: {}, setAttribute() {} };
    const settingsFeedback = {};
    const context = {
        sessionStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
        document: {
            body: { appendChild() {} },
            createElement: () => banner,
            querySelector(query) {
                assert.equal(query, '#SettingsFeedback[data-unsaved="true"]', 'Only the dirty-settings selector belongs to the polling seam');
                return dirty ? settingsFeedback : null;
            }
        },
        location: { reload() { reloads++; } },
        fetch: async () => { if (!available) throw new Error('offline'); return { ok: true, json: async () => state }; },
        setTimeout: callback => { timer = callback; }
    };
    vm.runInNewContext(source, context);
    return {
        banner, storage,
        set state(value) { state = value; }, set dirty(value) { dirty = value; }, set available(value) { available = value; },
        get reloads() { return reloads; },
        get pollingScheduled() { return Boolean(timer); },
        flush: () => new Promise(resolve => setImmediate(resolve)),
        async tick() { const callback = timer; timer = undefined; await callback(); }
    };
}

test('a ready revision reloads once and stops polling the departing page', async () => {
    const page = browser();
    await page.flush();
    page.state = { revision: 'second', ready: true };
    await page.tick();
    assert.equal(page.reloads, 1);
    assert.equal(page.storage.has('meta-tagger-dev-tab'), false);
    assert.equal(page.pollingScheduled, false);
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

test('leftover tab storage is discarded without restoring a removed control', async () => {
    const page = browser({ rememberedTab: 'TabSettings' });
    await page.flush();
    assert.equal(page.storage.has('meta-tagger-dev-tab'), false);
    assert.equal(page.reloads, 0);
    assert.equal(page.banner.textContent, '');
});

test('dev server reconnect recovers and reloads when a new revision is ready', async () => {
    const page = browser();
    await page.flush();
    page.available = false;
    await page.tick();
    assert.match(page.banner.textContent, /disconnected/);
    page.available = true;
    page.state = { revision: 'restarted', ready: true };
    await page.tick();
    assert.equal(page.reloads, 1);
});

test('a failed build keeps the current page until a later build is ready', async () => {
    const page = browser();
    await page.flush();
    page.state = { revision: 'second', ready: false, status: 'Build failed' };
    await page.tick();
    assert.equal(page.reloads, 0);
    assert.equal(page.banner.textContent, 'Build failed');
    assert.equal(page.pollingScheduled, true);
    page.state = { revision: 'second', ready: true };
    await page.tick();
    assert.equal(page.reloads, 1);
    assert.equal(page.pollingScheduled, false);
});
