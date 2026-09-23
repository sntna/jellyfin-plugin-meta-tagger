import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const markup = readFileSync(new URL('../../Jellyfin.Plugin.MetaTagger/Configuration/configPage.html', import.meta.url), 'utf8');
const script = markup.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];

// A DOM substitute for the page's browser seam. Tests use events and rendered
// controls; only Jellyfin requests are deferred, never application functions.
class Element {
    constructor(id = '', type = 'text') {
        this.id = id;
        this.type = type;
        this.value = '';
        this.disabled = false;
        this.checked = false;
        this.hidden = false;
        this.open = false;
        this.children = [];
        this.listeners = new Map();
        this.attributes = new Map();
        this.text = '';
    }
    set textContent(text) { this.text = String(text); this.children = []; }
    get textContent() { return this.text + this.children.map(child => child.textContent).join(''); }
    replaceChildren(...children) { this.text = ''; this.children = children; }
    appendChild(child) { this.children.push(child); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    getAttribute(name) { return this.attributes.get(name); }
    focus() { this.focused = true; this.onFocus?.(); }
    scrollIntoView() { this.scrolledIntoView = true; }
    closest(selector) {
        for (let element = this; element; element = element.parentElement) {
            if (element.tagName === selector.toUpperCase()) return element;
        }
        return null;
    }
    addEventListener(name, callback) {
        this.listeners.set(name, [...this.listeners.get(name) || [], callback]);
    }
    dispatch(name, properties = {}) {
        for (const callback of this.listeners.get(name) || []) {
            callback.call(this, { target: this, preventDefault() {}, ...properties });
        }
    }
}

function dashboard() {
    const elements = new Map();
    let attached = true;
    for (const match of markup.matchAll(/<([\w-]+)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
        const element = new Element(match[3], match[2].match(/type="([^"]+)"/)?.[1] || match[1]);
        element.tagName = match[1].toUpperCase();
        element.disabled = /\bdisabled\b/.test(match[2]);
        element.hidden = /\bhidden\b/.test(match[2]);
        element.open = /\bopen\b/.test(match[2]);
        for (const attr of match[2].matchAll(/([\w-]+)="([^"]*)"/g)) element.setAttribute(attr[1], attr[2]);
        if (match[1] === 'select') {
            element.value = markup.slice(match.index).match(/<option value="([^"]*)"/)?.[1] || '';
        }
        elements.set('#' + element.id, element);
    }
    let focusedElement;
    for (const element of elements.values()) element.onFocus = () => { focusedElement = element; };
    const stack = [];
    const treeMarkup = markup.replace(/<(style|script)\b[\s\S]*?<\/\1>/g, '');
    for (const match of treeMarkup.matchAll(/<(\/?)([\w-]+)\b([^>]*)>/g)) {
        if (match[1]) { stack.pop(); continue; }
        const id = match[3].match(/\bid="([^"]+)"/)?.[1];
        const element = elements.get('#' + id) || new Element();
        element.tagName = match[2].toUpperCase();
        element.parentElement = stack.at(-1);
        if (!['input', 'img', 'meta', 'link', 'path', 'br', 'hr'].includes(match[2]) && !match[3].endsWith('/')) stack.push(element);
    }
    const configForm = markup.slice(markup.indexOf('id="MetaTaggerConfigForm"'), markup.indexOf('</form>'));
    const cleanupMarkup = markup.slice(markup.indexOf('id="CleanupControls"'), markup.indexOf('<script'));
    const inputsIn = text => [...elements.values()].filter(element =>
        ['input', 'select', 'button'].includes(element.type) || ['text', 'number', 'checkbox'].includes(element.type))
        .filter(element => text.includes('id="' + element.id + '"'));
    const requests = [];
    const timers = new Map();
    let nextTimer = 0;
    function request(kind, body) {
        return new Promise((resolve, reject) => requests.push({ kind, body: structuredClone(body), resolve, reject }));
    }
    vm.runInNewContext(script, {
        setTimeout(callback) { timers.set(++nextTimer, callback); return nextTimer; },
        clearTimeout(id) { timers.delete(id); },
        document: {
            get activeElement() { return focusedElement; },
            querySelector(selector) {
                if (!attached) return null;
                assert.ok(elements.has(selector), 'Unsupported selector ' + selector);
                return elements.get(selector);
            },
            querySelectorAll(selector) {
                if (selector.startsWith('#CleanupControls ')) return inputsIn(cleanupMarkup);
                if (selector.startsWith('#MetaTaggerConfigForm ')) return inputsIn(configForm);
                throw new Error('Unsupported selector ' + selector);
            },
            createElement() { return new Element(); },
            createTextNode(text) { const node = new Element(); node.textContent = text; return node; }
        },
        ApiClient: {
            getUrl(path, params) { return path + (params ? "?" + new URLSearchParams(params) : ""); },
            getJSON(path) { return request(path); },
            ajax(options) {
                return request(options.url, options.data ? JSON.parse(options.data) : undefined).then(value =>
                    options.dataType === 'json' && value === undefined ? JSON.parse('') : value);
            },
            getPluginConfiguration() { return request('load-settings'); },
            updatePluginConfiguration(id, config) { return request('save-settings', config); }
        },
        location: { reload() { elements.get('#ReloadSettingsButton').reloaded = true; } },
        Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} },
        Option: function(text, value) { const option = new Element(); option.textContent = text; option.value = value; return option; }
    });
    elements.get('#MetaTaggerConfigPage').querySelectorAll = () => [];
    const flush = () => new Promise(resolve => setImmediate(resolve));
    return {
        async disclose(id, open = true) {
            this.element(id).open = open;
            await this.event(id, 'toggle');
        },
        async activate(element) { element.dispatch("click"); await flush(); },
        detach() { attached = false; },
        async tick() { const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach(fn => fn()); await flush(); },
        element(id) { return elements.get('#' + id); },
        async event(id, name, properties) { elements.get('#' + id).dispatch(name, properties); await flush(); },
        async click(id) {
            if (!elements.get('#' + id).disabled) await this.event(id, 'click');
        },
        async edit(id, value) {
            const element = this.element(id);
            assert.equal(element.disabled, false, id + ' should be editable');
            if (element.type === 'checkbox') element.checked = value;
            else element.value = value;
            await this.event(id, 'input');
            await this.event('MetaTaggerConfigForm', 'input', { target: element });
        },
        request(kind) {
            const found = requests.find(request => (request.kind === kind || request.kind.startsWith(kind + "?")) && !request.done);
            assert.ok(found, 'Expected request ' + kind);
            found.done = true;
            return found;
        },
        async respond(kind, value) { this.request(kind).resolve(value); await flush(); },
        async fail(kind) { this.request(kind).reject(new Error('Injected request failure')); await flush(); },
        sent(kind) { return requests.filter(request => request.kind === kind || request.kind.startsWith(kind + "?")); },
        async open(configuration = {}) {
            await this.event('MetaTaggerConfigPage', 'pageshow');
            await this.respond('load-settings', {
                GeneratedTagPrefix: 'meta', ManualTagPrefix: 'manual', TagSeparator: ':',
                IsEnabled: true, ConfigurationRevision: 'revision', IncludeMovies: true, PreviewOnly: true, DefaultRunMode: 'Incremental', StaleTagMode: 'Keep', ...configuration
            });
        }
    };
}

const preview = {
    status: 'Ready', summary: {}, changes: [
        { itemId: 'movie', itemName: 'Old preview', itemType: 'Movie', addedTags: ['meta:genre:drama'] }
    ]
};

test('settings item choices search automatically and show selection with source values', async () => {
    const page = await readyDashboard();
    await page.click('TabSettings');
    page.element('ExampleSearch').value = 'Apollo';
    await page.event('ExampleSearch', 'input');
    await page.tick();
    assert.ok(page.sent('MetaTagger/Items').length, 'typing should search without a search button');
    await page.respond('MetaTagger/Items', { totalCount: 31, items: [{ itemId: 'apollo', name: 'Apollo', itemType: 'Video', year: 1971 }] });
    assert.match(page.element('ExampleSearchFeedback').textContent, /1 of 31/);
    const choice = page.element('ExampleResults').children[0].children[0];
    assert.match(choice.textContent, /Apollo.*Video.*1971/);
    await page.activate(choice);
    await page.tick();
    assert.match(page.element('ExampleSelected').textContent, /Selected: Apollo/);
    assert.equal(choice.getAttribute('aria-pressed'), 'true');
    await page.respond('MetaTagger/Example', { generatedTags: ['meta:audio-language:eng'],
        sources: [{ source: 'audio-language', values: ['eng'], tag: 'meta:audio-language:eng' }] });
    assert.match(page.element('ExampleTags').textContent, /meta:audio-language:eng.*Audio languages: eng/);
    assert.equal(page.sent('save-settings').length, 0);
});

test('editing settings keeps an earlier preview response out of the displayed results', async () => {
    const page = dashboard();
    await page.open();
    await page.edit('GeneratedTagPrefix', 'custom');
    await page.respond('MetaTagger/Preview', preview);
    assert.equal(page.element('PreviewChanges').children.length, 0);
    assert.match(page.element('PreviewFeedback').textContent, /Settings changed/);
});

const cleanupPreview = {
    token: 'approved-movie', summary: {}, changes: [
        { itemId: 'movie', itemName: 'Movie', itemType: 'Movie', removedTags: ['meta:genre:drama'] }
    ]
};

test('editing settings prevents a pending cleanup preview from restoring approval', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', preview);
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.edit('GeneratedTagPrefix', 'custom');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    assert.equal(page.element('CleanupChanges').children.length, 0);
    assert.equal(page.element('ConfirmCleanup').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    assert.match(page.element('CleanupFeedback').textContent, /Settings changed/);
});

test('settings edited during a save remain unsaved when the earlier save finishes', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', preview);
    await page.edit('GeneratedTagPrefix', 'saved-prefix');
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    const save = page.request('save-settings');
    assert.equal(save.body.GeneratedTagPrefix, 'saved-prefix');
    await page.edit('GeneratedTagPrefix', 'newer-prefix');
    save.resolve({});
    await page.event('GeneratedTagPrefix', 'blur');
    assert.equal(page.element('GeneratedTagPrefix').value, 'newer-prefix');
    assert.match(page.element('SettingsFeedback').textContent, /unsaved/);
    assert.equal(page.element('PreviewCleanupButton').disabled, true);
    assert.equal(page.element('SaveSettingsButton').disabled, false);
});

async function readyDashboard(configuration = {}) {
    const page = dashboard();
    await page.open(configuration);
    await page.respond('MetaTagger/Preview', preview);
    return page;
}

async function confirmCleanup(page) {
    page.element('ConfirmCleanup').checked = true;
    await page.event('ConfirmCleanup', 'change');
}

test('a fresh cleanup requires confirmation and consumes approval before sending one apply', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    assert.equal(page.element('PreviewCleanupButton').disabled, true);
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    assert.equal(page.element('CleanupChanges').children.length, 1);
    assert.equal(page.element('ConfirmCleanup').disabled, false);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await confirmCleanup(page);
    await page.click('ApplyCleanupButton');
    await page.click('ApplyCleanupButton');
    assert.equal(page.sent('MetaTagger/Cleanup/Apply').length, 1);
    assert.deepEqual(page.sent('MetaTagger/Cleanup/Apply')[0].body, { token: 'approved-movie' });
    assert.equal(page.element('ConfirmCleanup').checked, false);
    await page.respond('MetaTagger/Cleanup/Apply', { writesApplied: 1 });
    assert.match(page.element('CleanupFeedback').textContent, /Items updated: 1/);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

test('a late cleanup failure cannot replace the settings-changed warning', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.edit('EnableGenres', true);
    await page.fail('MetaTagger/Cleanup/Preview');
    assert.match(page.element('CleanupFeedback').textContent, /Settings changed/);
    assert.equal(page.element('ConfirmCleanup').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

test('current preview failures release controls and allow a successful retry', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.fail('MetaTagger/Cleanup/Preview');
    assert.match(page.element('CleanupFeedback').textContent, /Tag removal preview failed/);
    assert.equal(page.element('PreviewCleanupButton').disabled, false);
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    assert.equal(page.element('ConfirmCleanup').disabled, false);
});

test('a removal preview explains unavailable plugin records instead of implying there are no tags to remove', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', {
        summary: { outcome: 'Preview fallback', previewOnly: true, itemsScanned: 0 }, changes: []
    });
    assert.match(page.element('CleanupFeedback').textContent, /plugin could not read or save its tag records/);
    assert.match(page.element('CleanupFeedback').textContent, /No tags were changed/);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

test('saving settings keeps old approval invalid and permits a newly calculated preview', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    await confirmCleanup(page);
    await page.edit('GeneratedTagPrefix', 'custom');
    await page.event('MetaTaggerConfigForm', 'submit');
    assert.equal(page.element('PreviewCleanupButton').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await page.respond('load-settings', { ConfigurationRevision: 'revision', LastRunSummaryText: 'Keep host fields' });
    assert.equal(page.sent('save-settings')[0].body.LastRunSummaryText, 'Keep host fields');
    await page.respond('save-settings', {});
    assert.equal(page.element('PreviewCleanupButton').disabled, false);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', { ...cleanupPreview, token: 'new-approval' });
    await confirmCleanup(page);
    await page.click('ApplyCleanupButton');
    assert.deepEqual(page.sent('MetaTagger/Cleanup/Apply')[0].body, { token: 'new-approval' });
});

test('changing the cleanup target clears both displayed removals and confirmation', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    await confirmCleanup(page);
    page.element('CleanupScope').value = 'item';
    await page.event('CleanupScope', 'change');
    assert.equal(page.element('CleanupChanges').children.length, 0);
    assert.equal(page.element('ConfirmCleanup').checked, false);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

test('reopening the page ignores an earlier preview without unlocking the newer request', async () => {
    const page = dashboard();
    await page.open();
    const earlier = page.request('MetaTagger/Preview');
    await page.event('MetaTaggerConfigPage', 'pagehide');
    await page.open();
    earlier.resolve(preview);
    await page.event('MetaTaggerConfigPage', 'focus');
    assert.equal(page.element('PreviewChanges').children.length, 0);
    assert.equal(page.element('RefreshPreviewButton').disabled, true);
    await page.respond('MetaTagger/Preview', { status: 'NoChanges', summary: {}, changes: [] });
    assert.match(page.element('PreviewFeedback').textContent, /No tag differences found/);
    assert.equal(page.element('RefreshPreviewButton').disabled, false);
});

test('settings load failure leaves cleanup unavailable', async () => {
    const page = dashboard();
    await page.event('MetaTaggerConfigPage', 'pageshow');
    await page.fail('load-settings');
    assert.equal(page.element('PreviewCleanupButton').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    assert.match(page.element('SettingsFeedback').textContent, /could not be loaded/);
    assert.equal(page.element('PageLoadError').hidden, false);
    assert.equal(page.element('PageLoadError').getAttribute('role'), 'alert');
    assert.match(page.element('OverviewOutcome').textContent, /unavailable/i);
    assert.equal(page.element('OverviewEnabled').textContent, 'Unavailable');
    await page.click('ReloadSettingsButton');
    assert.equal(page.element('ReloadSettingsButton').reloaded, true);
});

test('save captures the submitted values before fetching the latest server configuration', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'submitted');
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.edit('GeneratedTagPrefix', 'edited-during-fetch');
    await page.respond('load-settings', {});
    assert.equal(page.sent('save-settings')[0].body.GeneratedTagPrefix, 'submitted');
    await page.respond('save-settings', {});
    assert.equal(page.element('GeneratedTagPrefix').value, 'edited-during-fetch');
    assert.match(page.element('SettingsFeedback').textContent, /unsaved/);
});

test('an obsolete settings load cannot restart the current preview request', async () => {
    const page = dashboard();
    await page.event('MetaTaggerConfigPage', 'pageshow');
    const earlier = page.request('load-settings');
    await page.event('MetaTaggerConfigPage', 'pagehide');
    await page.open();
    const current = page.request('MetaTagger/Preview');
    earlier.resolve({ GeneratedTagPrefix: 'obsolete', ManualTagPrefix: 'manual', TagSeparator: ':' });
    await page.event('MetaTaggerConfigPage', 'focus');
    current.resolve(preview);
    await page.event('MetaTaggerConfigPage', 'focus');
    assert.equal(page.element('GeneratedTagPrefix').value, 'meta');
    assert.equal(page.element('PreviewChanges').children.length, 1);
    assert.equal(page.element('RefreshPreviewButton').disabled, false);
});

test('reopening during a save waits for the write before reading saved settings', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'saved-prefix');
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    const save = page.request('save-settings');
    await page.event('MetaTaggerConfigPage', 'pagehide');
    await page.event('MetaTaggerConfigPage', 'pageshow');
    // A read now could return the configuration that the pending write replaces.
    assert.equal(page.sent('load-settings').length, 2);
    save.resolve({});
    await page.event('MetaTaggerConfigPage', 'focus');
    await page.respond('load-settings', { GeneratedTagPrefix: 'saved-prefix', ManualTagPrefix: 'manual', TagSeparator: ':' });
    assert.equal(page.element('GeneratedTagPrefix').value, 'saved-prefix');
    assert.equal(page.element('GeneratedTagPrefix').disabled, false);
});


test('requests finishing after the page is removed do not touch its DOM', async () => {
    const page = dashboard();
    await page.open();
    await page.event('MetaTaggerConfigPage', 'pagehide');
    page.detach();
    await page.respond('MetaTagger/Preview', preview);
    assert.equal(page.element('PreviewChanges').children.length, 0);
});

test('switching Settings to Review and back preserves the unsaved draft without requests', async () => {
    const page = await readyDashboard();
    await page.click('TabSettings');
    await page.edit('GeneratedTagPrefix', 'draft');
    await page.click('TabReview');
    assert.equal(page.element('PanelReview').hidden, false);
    assert.equal(page.element('PanelSettings').hidden, true);
    await page.click('TabSettings');
    assert.equal(page.element('GeneratedTagPrefix').value, 'draft');
    assert.equal(page.element('PanelSettings').hidden, false);
    assert.equal(page.sent('load-settings').length, 1);
    assert.equal(page.sent('save-settings').length, 0);
    assert.equal(page.sent('MetaTagger/Cleanup/Apply').length, 0);
});

test('arrow keys select one accessible tab and wrap around', async () => {
    const page = await readyDashboard();
    await page.event('TabOverview', 'keydown', { key: 'ArrowLeft' });
    assert.equal(page.element('TabMaintenance').getAttribute('aria-selected'), 'true');
    assert.equal(page.element('PanelOverview').hidden, true);
    assert.equal(page.element('PanelMaintenance').hidden, false);
    assert.equal(page.element('TabMaintenance').getAttribute('tabindex'), '0');
    await page.event('TabMaintenance', 'keydown', { key: 'Home' });
    assert.equal(page.element('PanelOverview').hidden, false);
});

test('the example uses draft settings and ignores a slower response from an older draft', async () => {
    const page = await readyDashboard();
    await chooseExample(page, { itemId: 'movie', name: 'Example', itemType: 'Movie' });
    const older = page.request('MetaTagger/Example');
    await page.edit('EnableGenres', false);
    await page.tick();
    const newer = page.request('MetaTagger/Example');
    assert.equal(newer.body.configuration.EnableGenres, false);
    newer.resolve({ generatedTags: ['meta:year:2024'] });
    await page.event('ExampleSearch', 'blur');
    older.resolve({ generatedTags: ['meta:genre:science-fiction'] });
    await page.event('ExampleSearch', 'blur');
    assert.match(page.element('ExampleTags').textContent, /meta:year:2024/);
    assert.doesNotMatch(page.element('ExampleTags').textContent, /science-fiction/);
    assert.equal(page.sent('save-settings').length, 0);
});

test('item search uses its library and page, discards stale results, and starts as Not checked', async () => {
    const page = await readyDashboard();
    page.element('ItemSearch').value = 'Dune';
    page.element('ItemLibrary').value = 'library-a';
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    const older = page.request('MetaTagger/Items');
    page.element('ItemSearch').value = 'Arrival';
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    const newer = page.request('MetaTagger/Items');
    assert.match(newer.kind, /searchTerm=Arrival/);
    assert.match(newer.kind, /libraryId=library-a/);
    newer.resolve({ totalCount: 51, startIndex: 0, items: [{ itemId: 'arrival', name: 'Arrival', itemType: 'Movie', year: 2016 }] });
    await page.event('ItemSearch', 'blur');
    older.resolve({ totalCount: 1, items: [{ itemId: 'dune', name: 'Dune' }] });
    await page.event('ItemSearch', 'blur');
    assert.match(page.element('ItemList').textContent, /Arrival/);
    assert.doesNotMatch(page.element('ItemList').textContent, /Dune/);
    assert.match(page.element('ItemList').textContent, /Not checked/);
    assert.match(page.element('ItemBrowserFeedback').textContent, /51/);
    await page.click('NextItemsButton');
    assert.match(page.request('MetaTagger/Items').kind, /startIndex=25/);
});

for (const previewOnly of [true, false]) {
    test(`Preview tag changes always starts only the preview task when scheduled preview is ${previewOnly}`, async () => {
        const page = await readyDashboard({ PreviewOnly: previewOnly });
        await page.click('RunPreviewButton');
        await page.click('RunPreviewButton');
        await page.respond('load-settings', { ConfigurationRevision: 'revision' });
        await page.respond('ScheduledTasks', [
            { Id: 'apply-task', Key: 'MetaTaggerApplyTags', State: 'Idle' },
            { Id: 'default-task', Key: 'MetaTaggerGenerateTags', State: 'Idle' },
            { Id: 'preview-task', Key: 'MetaTaggerPreviewTags', State: 'Idle' }
        ]);
        assert.equal(page.sent('ScheduledTasks/Running/preview-task').length, 1);
        assert.equal(page.sent('ScheduledTasks/Running/apply-task').length, 0);
        assert.equal(page.sent('ScheduledTasks/Running/default-task').length, 0);
        assert.equal(page.sent('save-settings').length, 0);
        await page.respond('ScheduledTasks/Running/preview-task', undefined);
        await page.tick();
        await page.respond('ScheduledTasks/preview-task', { State: 'Idle', LastExecutionResult: {
            StartTimeUtc: '2026-09-13T12:00:00Z', EndTimeUtc: '2026-09-13T12:00:02Z', Status: 'Completed'
        } });
        await page.respond('MetaTagger/Preview', { status: 'NoChanges', changes: [], summary: {
            invocation: 'MetaTaggerPreviewTags', configurationRevision: 'revision', previewOnly: true, lastRunUtc: '2026-09-13T12:00:01Z', itemsScanned: 5
        } });
        assert.equal(page.element('PanelReview').hidden, false);
        assert.match(page.element('TaskFeedback').textContent, /Completed/);
    });
}

test('item Apply requires its own preview and confirmation and submits only one opaque approval', async () => {
    const page = await readyDashboard();
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'movie', name: 'Dune', itemType: 'Movie' }] });
    await page.activate(page.element('ItemList').children[0]);
    assert.equal(page.element('ApplyItemButton').textContent, 'Apply changes to this item');
    assert.equal(page.element('ApplyItemButton').disabled, true);
    await page.click('PreviewItemButton');
    await page.respond('MetaTagger/Items/movie/Preview', { token: 'dune-approval', addedTags: ['meta:genre:science-fiction'],
        removedTags: ['meta:year:2023'], preservedTags: ['Favorites'], manualTags: ['manual:tagger:force'],
        sources: [{ tag: 'meta:rating:pg-13', source: 'rating', values: ['PG-13'] }] });
    assert.match(page.element('InspectorDetails').textContent, /Tags to add.*meta:genre:science-fiction/);
    assert.match(page.element('InspectorDetails').textContent, /from Parental rating: PG-13/);
    assert.equal(page.element('ApplyItemButton').disabled, true);
    page.element('ConfirmItem').checked = true;
    await page.event('ConfirmItem', 'change');
    await page.click('ApplyItemButton');
    await page.click('ApplyItemButton');
    assert.equal(page.sent('MetaTagger/Items/movie/Apply').length, 1);
    assert.deepEqual(page.sent('MetaTagger/Items/movie/Apply')[0].body, { token: 'dune-approval' });
    await page.respond('MetaTagger/Items/movie/Apply', { writesApplied: 1 });
    assert.equal(page.element('ApplyItemButton').disabled, true);
    await page.respond('MetaTagger/Items/movie/Preview', { name: 'Dune', status: 'Up to date', ownedTags: ['meta:genre:science-fiction'] });
    assert.match(page.element('InspectorDetails').textContent, /meta:genre:science-fiction/);
    await page.click('ClearItemButton');
    assert.equal(page.element('PanelMaintenance').hidden, false);
    assert.equal(page.element('CleanupDisclosure'), undefined);
    assert.equal(page.element('ClearPluginTagsHeading').focused, true);
    assert.equal(page.element('CleanupScope').value, 'item');
    assert.equal(page.element('CleanupItemId').value, 'movie');
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

test('Overview and Runs use persisted outcomes and historical details never authorize Apply', async () => {
    const page = await readyDashboard();
    await page.respond('MetaTagger/Runs', [{ runId: 'run-a', operation: 'Preview', outcome: 'Budget limited',
        scope: 'Configured item types across all libraries', configurationRevision: 'revision', startedUtc: '2026-09-13T12:00:00Z',
        summary: { itemsScanned: 25, itemsChanged: 2, itemsRemaining: 50 } }]);
    assert.match(page.element('OverviewOutcome').textContent, /Run limit reached/);
    assert.match(page.element('OverviewCounts').textContent, /25/);
    await page.respond('MetaTagger/Runs/run-a', { detailsAvailable: true, items: [] });
    await page.activate(page.element('RunList').children[0]);
    await page.respond('MetaTagger/Runs/run-a', { runId: 'run-a', operation: 'Preview', outcome: 'Budget limited', detailsAvailable: true,
        items: [
            { name: '<img onerror=alert(1)>', outcome: 'Changes', addedTags: ['<script>literal</script>'] },
            { name: 'Updated movie', outcome: 'Applied', addedTags: ['meta:year:2023'] },
            { name: 'Uncertain movie', outcome: 'Uncertain', removedTags: ['meta:year:2022'] }
        ] });
    assert.match(page.element('RunDetails').textContent, /<img onerror=alert\(1\)>/);
    assert.match(page.element('RunDetails').textContent, /<script>literal<\/script>/);
    assert.match(page.element('RunDetails').children[0].textContent, /Tag differences.*Tags to add/);
    assert.match(page.element('RunDetails').children[1].textContent, /Updated movie · Updated.*Tags added/);
    assert.match(page.element('RunDetails').children[2].textContent, /Update not confirmed.*Tags to remove/);
    assert.equal(page.element('ApplyItemButton').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

test('overview counts use the latest run and clear when history becomes unavailable', async () => {
    const page = await readyDashboard();
    await page.respond('MetaTagger/Runs', [{ runId: 'item-apply', operation: 'Apply', outcome: 'Partial failure',
        scope: '123456781234123412341234567890ab', summary: {
            itemsScanned: 7, itemsChanged: 3, writesApplied: 2, itemsSkippedLocked: 1, itemsSkippedManual: 1, failures: 1
        } }]);
    assert.match(page.element('OverviewCounts').textContent, /Items with tag differences: 3.*Items updated: 2.*Items skipped for protection: 2/);
    assert.match(page.element('OverviewCounts').textContent, /One item/);
    assert.match(page.element('OverviewCounts').textContent, /Items with tag differences: 3 · Items updated: 2/);
    await page.click('OverviewRunsButton');
    assert.equal(page.element('PanelRuns').hidden, false);
    await page.click('RefreshRunsButton');
    await page.fail('MetaTagger/Runs');
    assert.match(page.element('OverviewOutcome').textContent, /unavailable/);
    assert.equal(page.element('OverviewOutcome').getAttribute('data-feedback-tone'), undefined);
    assert.doesNotMatch(page.element('OverviewCounts').textContent, /Items updated/);
});

test('the saved configuration summary follows an older successful save while newer edits remain unsaved', async () => {
    const page = await readyDashboard();
    await page.edit('IsEnabled', false);
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    await page.edit('IsEnabled', true);
    await page.respond('save-settings', {});
    assert.equal(page.element('OverviewEnabled').textContent, 'Off');
    assert.equal(page.element('IsEnabled').checked, true);
    assert.match(page.element('SettingsFeedback').textContent, /unsaved/);
});

test('item rows distinguish potential stale removals from applicable removals', async () => {
    const page = await readyDashboard();
    await page.respond('MetaTagger/Runs', [{ runId: 'r', operation: 'Preview', scope: 'Configured item types across all libraries' }]);
    await page.respond('MetaTagger/Runs/r', { detailsAvailable: true, items: [{ itemId: 'm', outcome: 'Changes', addedTags: ['meta:genre:drama'], removedTags: [], previewRemovedTags: ['meta:year:2023'] }] });
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'm', name: 'Movie' }] });
    assert.match(page.element('ItemList').textContent, /1 outdated tag to keep/);
    assert.match(page.element('ItemList').textContent, /0 tags to remove/);
    assert.match(page.element('ItemList').textContent, /1 tag to add ·/);
});

test('preview counts include an item with only stale tags kept for review without claiming an update', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { status: 'Ready', summary: {}, changes: [
        { itemId: 'movie', itemName: 'Movie', addedTags: [], removedTags: [], previewRemovedTags: ['meta:year:2023'] }
    ] });
    assert.match(page.element('PreviewFeedback').textContent, /1 item with tag differences/);
    assert.match(page.element('PreviewFeedback').textContent, /Tags to add: 0. Tags to remove: 0/);
    assert.match(page.element('PreviewFeedback').textContent, /1 outdated tag to keep. No tags were changed/);
});

test('settings edits during an outstanding task poll still show task completion without reviving stale results', async () => {
    const page = await readyDashboard();
    await page.click('RunPreviewButton');
    await page.respond('load-settings', { ConfigurationRevision: 'revision' });
    await page.respond('ScheduledTasks', [{ Id: 'task', Key: 'MetaTaggerPreviewTags', State: 'Running' }]);
    const pending = page.request('ScheduledTasks/task');
    await page.edit('EnableGenres', false);
    pending.resolve({ State: 'Idle', LastExecutionResult: { StartTimeUtc: '2026-09-13T12:00:00Z', EndTimeUtc: '2026-09-13T12:00:02Z', Status: 'Completed' } });
    await page.event('EnableGenres', 'blur');
    assert.match(page.element('TaskFeedback').textContent, /older settings/);
    assert.equal(page.element('StopPreviewButton').disabled, true);
    assert.equal(page.element('PanelReview').hidden, true);
});

test('editing settings after dispatching a task launch still starts lifecycle polling', async () => {
    const page = await readyDashboard();
    await page.click('RunPreviewButton');
    await page.respond('load-settings', { ConfigurationRevision: 'revision' });
    await page.respond('ScheduledTasks', [{ Id: 'task', Key: 'MetaTaggerPreviewTags', State: 'Idle' }]);
    await page.edit('EnableGenres', false);
    await page.respond('ScheduledTasks/Running/task', {});
    await page.tick();
    assert.equal(page.sent('ScheduledTasks/task').length, 1);
    await page.respond('ScheduledTasks/task', { State: 'Idle', LastExecutionResult: {
        StartTimeUtc: '2026-09-13T12:00:00Z', EndTimeUtc: '2026-09-13T12:00:02Z', Status: 'Completed'
    } });
    assert.match(page.element('TaskFeedback').textContent, /older settings/);
    assert.equal(page.element('StopPreviewButton').disabled, true);
});

test('a fresh preview after saving uses the confirmed saved configuration revision', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'fresh');
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', { ConfigurationRevision: 'old' });
    await page.respond('save-settings', {});
    await page.respond('MetaTagger/ConfigurationRevision', 'new');
    await page.click('RefreshRunsButton');
    page.request('MetaTagger/Runs').resolve([]);
    await page.respond('MetaTagger/Runs', [{ runId: 'fresh-run', operation: 'Preview', scope: 'Configured item types across all libraries', configurationRevision: 'new' }]);
    await page.respond('MetaTagger/Runs/fresh-run', { detailsAvailable: true, items: [] });
    assert.doesNotMatch(page.element('ItemStatusSource').textContent, /Stale/);
});

test('searching for a new example invalidates the earlier example response and tags', async () => {
    const page = await readyDashboard();
    await chooseExample(page, { itemId: 'a', name: 'Old example', itemType: 'Movie' });
    const oldExample = page.request('MetaTagger/Example');
    page.element('ExampleSearch').value = 'new example';
    await page.event('ExampleSearch', 'input');
    await page.tick();
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'b', name: 'New example' }] });
    oldExample.resolve({ generatedTags: ['old:item:tags'] });
    await page.event('ExampleSearch', 'blur');
    assert.doesNotMatch(page.element('ExampleTags').textContent, /old:item:tags/);
});

async function chooseExample(page, item) {
    await page.click('TabSettings');
    await page.tick();
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [item] });
    await page.activate(page.element('ExampleResults').children[0].children[0]);
    await page.tick();
}

test('example selection rejects older targets and navigation responses without saving the draft', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'draft');
    await chooseExample(page, { itemId: 'a', name: 'First', itemType: 'Movie' });
    const first = page.request('MetaTagger/Example');
    page.element('ExampleSearch').value = 'Second';
    await page.event('ExampleSearch', 'input');
    await page.tick();
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'b', name: 'Second', itemType: 'Episode', year: 2025 }] });
    await page.activate(page.element('ExampleResults').children[0].children[0]);
    await page.tick();
    const second = page.request('MetaTagger/Example');
    assert.equal(second.body.itemId, 'b');
    assert.equal(second.body.configuration.GeneratedTagPrefix, 'draft');
    first.resolve({ generatedTags: ['obsolete:first'] });
    await page.event('ExampleSearch', 'blur');
    assert.doesNotMatch(page.element('ExampleTags').textContent, /obsolete/);
    await page.click('TabOverview');
    second.resolve({ generatedTags: ['obsolete:second'] });
    await page.event('ExampleSearch', 'blur');
    assert.doesNotMatch(page.element('ExampleTags').textContent, /obsolete/);
    assert.equal(page.element('GeneratedTagPrefix').value, 'draft');
    assert.equal(page.sent('save-settings').length, 0);
});

test('search feedback handles no matches, failure, retry, and Enter without saving', async () => {
    const page = await readyDashboard();
    await page.click('TabSettings');
    await page.event('ExampleSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 0, items: [] });
    assert.match(page.element('ExampleSearchFeedback').textContent, /No matching items/);
    await page.event('ExampleSearch', 'input');
    await page.tick();
    await page.fail('MetaTagger/Items');
    assert.equal(page.element('ExampleSearchRetry').hidden, false);
    await page.click('ExampleSearchRetry');
    await page.tick();
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'a', name: 'Retry result' }] });
    assert.match(page.element('ExampleResults').textContent, /Retry result/);
    assert.equal(page.sent('save-settings').length, 0);
});

test('language choices save and stay selected while metadata refresh preserves the draft', async () => {
    const page = await readyDashboard();
    await page.edit('EnableAudioLanguages', true);
    await page.edit('EnableSubtitleLanguages', true);
    await page.edit('EnableExistingTagsAsKeywords', true);
    await page.edit('EnableProviderIds', true);
    await page.edit('MaxKeywordTagsPerItem', '7');
    await page.edit('ExcludedKeywordPrefixes', 'private:');
    await chooseExample(page, { itemId: 'a', name: 'Audio test', itemType: 'Video' });
    await page.respond('MetaTagger/Example', { generatedTags: ['meta:audio-language:eng'] });
    await page.click('ExampleRefresh');
    await page.tick();
    const refresh = page.request('MetaTagger/Example');
    assert.equal(refresh.body.configuration.EnableAudioLanguages, true);
    assert.equal(refresh.body.configuration.EnableSubtitleLanguages, true);
    assert.match(page.element('ExampleSelected').textContent, /Audio test/);
    assert.equal(page.sent('save-settings').length, 0);
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    const save = page.request('save-settings');
    assert.equal(save.body.EnableExistingTagsAsKeywords, true);
    assert.equal(save.body.EnableProviderIds, true);
    assert.equal(save.body.EnableAudioLanguages, true);
    assert.equal(save.body.EnableSubtitleLanguages, true);
    assert.equal(save.body.MaxKeywordTagsPerItem, 7);
    assert.equal(save.body.ExcludedKeywordPrefixes, 'private:');
});

test('opening Settings during configuration loading starts item choices when loading finishes', async () => {
    const page = dashboard();
    await page.event('MetaTaggerConfigPage', 'pageshow');
    await page.click('TabSettings');
    await page.respond('load-settings', { GeneratedTagPrefix: 'meta', ManualTagPrefix: 'manual', TagSeparator: ':', IsEnabled: true });
    await page.tick();
    assert.equal(page.sent('MetaTagger/Items').length, 1);
});

test('saving settings restarts pending item search and selected item calculation', async () => {
    const page = await readyDashboard();
    await chooseExample(page, { itemId: 'a', name: 'Keep selected', itemType: 'Movie' });
    const earlierExample = page.request('MetaTagger/Example');
    await page.click('TabOverview');
    await page.click('TabSettings');
    await page.tick();
    const earlierSearch = page.request('MetaTagger/Items');
    page.request('MetaTagger/Example');
    await page.event('MetaTaggerConfigForm', 'submit');
    earlierSearch.resolve({ totalCount: 1, items: [{ itemId: 'old', name: 'Obsolete choices' }] });
    earlierExample.resolve({ generatedTags: ['obsolete:tag'] });
    await page.respond('load-settings', {});
    await page.respond('save-settings', {});
    await page.tick();
    assert.equal(page.sent('MetaTagger/Items').length, 3);
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'a', name: 'Keep selected', itemType: 'Movie' }] });
    await page.respond('MetaTagger/Example', { generatedTags: ['meta:genre:drama'] });
    assert.match(page.element('ExampleTags').textContent, /meta:genre:drama/);
    assert.doesNotMatch(page.element('ExampleTags').textContent, /obsolete/);
    assert.match(page.element('ExampleSelected').textContent, /Keep selected/);
});

test('the More sources count updates for both provider and keyword selections', async () => {
    const page = await readyDashboard();
    await page.edit('EnableAudioLanguages', true);
    assert.equal(page.element('AdditionalSourcesCount').textContent, '0 selected');
    page.element('EnableProviderIds').checked = true;
    await page.event('EnableProviderIds', 'change');
    assert.equal(page.element('AdditionalSourcesCount').textContent, '1 selected');
    page.element('EnableExistingTagsAsKeywords').checked = true;
    await page.event('EnableExistingTagsAsKeywords', 'change');
    assert.equal(page.element('AdditionalSourcesCount').textContent, '2 selected');
});

test('source summaries name every saved selection while later edits remain a separate draft', async () => {
    const page = await readyDashboard();
    for (const id of ['EnableGenres', 'EnableParentalRating', 'EnableStudios', 'EnableProductionCountries', 'EnableProductionYear', 'EnableAudioLanguages', 'EnableSubtitleLanguages', 'EnableExistingTagsAsKeywords', 'EnableProviderIds']) {
        await page.edit(id, true);
    }
    const names = 'Genres, Parental rating, Audio languages, Studios, Production countries, Production year, Subtitle languages, Existing tags as keywords, Metadata providers';
    assert.equal(page.element('SelectedSources').textContent, names);
    assert.equal(page.element('OverviewSources').textContent, 'None selected');
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    await page.edit('EnableAudioLanguages', false);
    await page.respond('save-settings', {});
    assert.equal(page.element('OverviewSources').textContent, names);
    assert.doesNotMatch(page.element('SelectedSources').textContent, /Audio languages/);
    assert.equal(page.element('EnableSubtitleLanguages').checked, true);
    assert.match(page.element('SettingsFeedback').textContent, /unsaved/);
});


test('the Settings aside requests only while visible and rejects responses after navigation', async () => {
    const page = await readyDashboard();
    await page.click('TabSettings');
    await page.tick();
    assert.equal(page.sent('MetaTagger/Items').length, 1);
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'kept', name: 'Keep selected' }] });
    await page.activate(page.element('ExampleResults').children[0].children[0]);
    await page.tick();
    const older = page.request('MetaTagger/Example');
    await page.click('TabOverview');
    await page.edit('GeneratedTagPrefix', 'latest');
    await page.tick();
    older.resolve({ generatedTags: ['obsolete:tag'] });
    await page.event('ExampleSearch', 'blur');
    assert.equal(page.sent('MetaTagger/Example').length, 1);
    assert.doesNotMatch(page.element('ExampleTags').textContent, /obsolete/);
    await page.click('TabSettings');
    await page.tick();
    const reopened = page.request('MetaTagger/Example');
    assert.equal(reopened.body.itemId, 'kept');
    assert.equal(reopened.body.configuration.GeneratedTagPrefix, 'latest');
    assert.match(page.element('ExampleSelected').textContent, /Keep selected/);
    assert.equal(page.sent('save-settings').length, 0);
});


test('collapsed setup summaries follow the draft and validation opens its enclosing controls', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'catalog');
    await page.edit('TagSeparator', '/');
    await page.edit('IncludeEpisodes', true);
    await page.edit('IncludeParentSeriesMetadataOnEpisodes', true);
    assert.equal(page.element('TagFormatSummary').textContent, 'catalog/genre/animation');
    assert.equal(page.element('ItemTypesSummary').textContent, 'Movies, Episodes · Series metadata included');
    assert.equal(page.element('TagFormatOptions').open, false);
    assert.equal(page.element('ItemTypeOptions').open, false);
    await page.edit('GeneratedTagPrefix', 'bad prefix');
    await page.event('MetaTaggerConfigForm', 'submit');
    assert.equal(page.element('TagFormatOptions').open, true);
    assert.equal(page.element('TagNamespaceErrors').focused, true);
    await page.edit('EnableExistingTagsAsKeywords', true);
    await page.event('MaxKeywordTagsPerItem', 'invalid');
    assert.equal(page.element('MoreSources').open, true);
    assert.equal(page.element('KeywordOptions').open, true);
    await page.event('MaxItemsPerRun', 'invalid');
    assert.equal(page.element('AdvancedOptions').open, true);
    assert.equal(page.sent('save-settings').length, 0);
});

test('leaving Maintenance rejects a pending removal preview even after returning', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    assert.equal(page.element('PreviewCleanupButton').disabled, false);
    assert.equal(page.element('CleanupDisclosure'), undefined);
    assert.equal(page.element('CleanupScope').closest('details'), null);
    await page.click('PreviewCleanupButton');
    const pending = page.request('MetaTagger/Cleanup/Preview');
    await page.click('TabOverview');
    assert.equal(page.element('PreviewCleanupButton').disabled, true);
    assert.equal(page.element('ConfirmCleanup').checked, false);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await page.click('TabMaintenance');
    pending.resolve(cleanupPreview);
    await page.event('CleanupScope', 'blur');
    assert.equal(page.element('CleanupChanges').children.length, 0);
    assert.equal(page.element('ConfirmCleanup').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', { ...cleanupPreview, token: 'new-review' });
    await confirmCleanup(page);
    await page.click('ApplyCleanupButton');
    assert.deepEqual(page.sent('MetaTagger/Cleanup/Apply')[0].body, { token: 'new-review' });
});


test('invalid saved values in conditional controls can be corrected even when their option is off', async () => {
    const page = await readyDashboard();
    assert.equal(page.element('PostScanOptions').hidden, true);
    await page.event('MinimumMinutesBetweenAutoRuns', 'invalid');
    assert.equal(page.element('PostScanOptions').hidden, false);
    assert.equal(page.element('AutomationOptions').open, true);
    assert.equal(Boolean(page.element('RunAfterLibraryScan').checked), false);
});


test('saved run action stays explicit and editing settings or opening Scheduled Tasks never applies a draft', async () => {
    const page = await readyDashboard();
    assert.equal(page.element('OverviewMode').textContent, 'Preview only');
    assert.match(page.element('OverviewAutomationHelp').textContent, /Scheduled Tasks/);
    assert.equal(page.element('EditAutomationButton'), undefined);
    await page.click('TaskSettingsButton');
    assert.equal(page.element('PanelSettings').hidden, false);
    await page.edit('PreviewOnly', false);
    await page.edit('RunAfterLibraryScan', true);
    assert.match(page.element('AutomationSummary').textContent, /Apply.*scan/i);
    assert.match(page.element('OverviewMode').textContent, /Preview/);
    assert.match(page.element('LibraryApplyFeedback').textContent, /Save your settings before running the task/);
    assert.equal(page.element('OpenScheduledTasks').getAttribute('href'), '#/dashboard/tasks');
    await page.click('OpenScheduledTasks');
    assert.equal(page.sent('save-settings').length, 0);
    assert.equal(page.sent('ScheduledTasks').length, 0);
    assert.equal(page.sent('MetaTagger/Items/movie/Apply').length, 0);
    await page.edit('IsEnabled', false);
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    await page.respond('save-settings', {});
    assert.match(page.element('OverviewTaggingHelp').textContent, /generation and previews are stopped/i);
    assert.equal(page.element('OverviewMode').textContent, 'Apply changes');
    assert.equal(page.element('OverviewAutomation').textContent, 'On');
    assert.equal(page.element('RunPreviewButton').disabled, true);
});

test('Overview describes the actual latest operation once with its time and exceptional outcome', async () => {
    const page = await readyDashboard();
    await page.respond('MetaTagger/Runs', [
        { runId: 'cleanup', operation: 'Cleanup apply', outcome: 'Uncertain', scope: 'Entire library', startedUtc: '2026-09-13T12:00:00Z',
            summary: { itemsScanned: 7, itemsChanged: 3, writesApplied: 2, itemsSkippedLocked: 1, failures: 1, itemsRemaining: 4, budgetLimitReached: true } },
        { runId: 'preview', operation: 'Preview', outcome: 'Completed', scope: 'Configured item types across all libraries', summary: { itemsChanged: 99 } }
    ]);
    assert.match(page.element('OverviewOutcome').textContent, /Remove plugin tags.*Update not confirmed/);
    assert.match(page.element('OverviewWhen').textContent, /Sep.*13/);
    assert.match(page.element('OverviewCounts').textContent, /Items with tag differences: 3.*Items updated: 2.*protection: 1.*Failures: 1/);
    assert.match(page.element('OverviewCounts').textContent, /Run limit reached/);
    assert.match(page.element('OverviewCounts').textContent, /could not be confirmed/);
    assert.equal(page.element('OverviewStats'), undefined);
    assert.equal(page.element('RecentRuns'), undefined);
    await page.click('RefreshRunsButton');
    await page.fail('MetaTagger/Runs');
    assert.equal(page.element('OverviewWhen').textContent, '');
    assert.doesNotMatch(page.element('OverviewCounts').textContent, /Items updated: 2/);
    assert.match(page.element('RunsFeedback').textContent, /Refresh history/);
    assert.equal(page.element('RunList').children.length, 0);
});

test('Review opens latest differences and links to persistent inspection and history destinations', async () => {
    const page = await readyDashboard();
    await page.click('OverviewItemsButton');
    assert.equal(page.element('PanelReview').hidden, false);
    assert.equal(page.element('ReviewResults').hidden, false);
    assert.equal(page.element('InspectionPanel').hidden, true);
    assert.ok(page.element('TabRuns'));
    assert.equal(page.sent('MetaTagger/Items').length, 0);
    assert.match(page.element('PreviewChanges').textContent, /Old preview/);
    await page.click('InspectItemsButton');
    assert.equal(page.element('PanelReview').hidden, true);
    assert.equal(page.element('InspectionPanel').hidden, false);
    assert.equal(page.element('ItemSearch').focused, true);
    await page.respond('MetaTagger/Items', { totalCount: 0, items: [] });
    assert.equal(page.element('ItemStatus').closest('details'), null);
    assert.equal(page.element('ItemStatus').value, '');
    assert.equal(page.element('ItemStatus').getAttribute('aria-describedby'), 'ItemStatusHelp');
    assert.match(markup, /<label[^>]*for="ItemStatus">Filter<\/label>/);
    page.element('ItemStatus').value = 'Protected';
    await page.event('ItemStatus', 'change');
    assert.equal(page.element('ItemStatus').value, 'Protected');
    await page.click('BackToReviewButton');
    assert.equal(page.element('ReviewResults').hidden, false);
    await page.click('ReviewRunsButton');
    assert.equal(page.element('PanelRuns').hidden, false);
    assert.equal(page.element('PanelReview').hidden, true);
    assert.equal(page.element('PanelRuns').getAttribute('role'), 'tabpanel');
    assert.match(page.element('HistoryBackButton').textContent, /preview results/);
    await page.click('HistoryBackButton');
    assert.equal(page.element('PanelReview').hidden, false);
    assert.equal(page.element('PanelRuns').hidden, true);
});

test('a preview Inspect action targets its own item without borrowing history approval', async () => {
    const page = await readyDashboard();
    await page.click('OverviewItemsButton');
    const article = page.element('PreviewChanges').children[0];
    const inspect = article.children.find(child => child.textContent === 'Preview this item');
    assert.ok(inspect, 'preview item offers inspection');
    await page.activate(inspect);
    assert.equal(page.element('InspectionPanel').hidden, false);
    assert.equal(page.element('PanelReview').hidden, true);
    assert.equal(page.element('InspectorHeading').textContent, 'Old preview');
    assert.equal(page.element('InspectorHeading').focused, true);
    await page.respond('MetaTagger/Items/movie/Preview', { status: 'Unavailable', reason: 'This item was deleted or is unavailable.' });
    assert.match(page.element('InspectorFeedback').textContent, /deleted or is unavailable/);
    assert.equal(page.element('ApplyItemButton').disabled, true);
    assert.equal(page.element('ConfirmItem').disabled, true);
    assert.equal(page.sent('MetaTagger/Items/movie/Preview').length, 1);
});

test('Review falls back to one identified generation preview after a later Apply in either response order', async () => {
    for (const previewFirst of [true, false]) {
        const page = dashboard();
        await page.open();
        const respondPreview = () => page.respond('MetaTagger/Preview', { status: 'LatestRunApplied', summary: { runId: 'apply', runMode: 'Incremental', previewOnly: false } });
        if (previewFirst) await respondPreview();
        await page.respond('MetaTagger/Runs', [
            { runId: 'apply', operation: 'Apply', outcome: 'Completed', scope: 'Configured item types across all libraries', summary: { writesApplied: 1 } },
            { runId: 'generation', operation: 'Preview', outcome: 'Budget limited', scope: 'Configured item types across all libraries', configurationRevision: 'revision', startedUtc: '2026-09-13T12:00:00Z',
                summary: { runMode: 'Incremental', itemsScanned: 4, itemsChanged: 1, budgetLimitReached: true, itemsRemaining: 2 } }
        ]);
        await page.respond('MetaTagger/Runs/generation', { runId: 'generation', detailsAvailable: true, detailsTruncated: true,
            items: [{ itemId: 'kept', name: 'Outdated only', itemType: 'Movie', previewRemovedTags: ['meta:year:2023'] }] });
        if (!previewFirst) await respondPreview();
        await page.click('OverviewItemsButton');
        assert.match(page.element('OverviewOutcome').textContent, /Apply tag changes/);
        assert.match(page.element('PreviewContext').textContent, /tag preview.*Sep.*13/i);
        assert.equal(page.element('PreviewRunId').textContent, 'Run generation');
        assert.equal(page.element('PreviewRunDetails'), undefined);
        assert.equal(page.element('PreviewRunId').hidden, false);
        assert.equal(page.element('PreviewRunId').closest('details'), null);
        assert.match(page.element('PreviewContext').textContent, /Some item details were not retained.*Run limit reached/);
        assert.match(page.element('PreviewChanges').textContent, /Outdated only.*Outdated tags to keep.*meta:year:2023/);
        assert.equal(page.element('PreviewChanges').children.length, 1);
        assert.equal(page.element('ApplyItemButton').disabled, true);
    }
});

test('saving a new revision cannot revive cached direct preview differences', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { ...preview, summary: { runId: 'direct', configurationRevision: 'revision', previewOnly: true, runMode: 'Incremental' } });
    await page.edit('GeneratedTagPrefix', 'fresh');
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    await page.respond('save-settings', {});
    await page.respond('MetaTagger/ConfigurationRevision', 'fresh-revision');
    assert.equal(page.element('PreviewChanges').children.length, 0);
    assert.match(page.element('PreviewFeedback').textContent, /Settings changed after this preview/);
});

test('an interrupted preview keeps its incomplete, protection, and failure context beside empty differences', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { status: 'NoChanges', changes: [], summary: {
        runId: 'cancelled', previewOnly: true, runMode: 'Incremental', configurationRevision: 'revision', outcome: 'Cancelled',
        itemsScanned: 2, itemsSkippedLocked: 1, failures: 1, lastRunUtc: '2026-09-13T12:00:00Z'
    } });
    assert.match(page.element('PreviewContext').textContent, /Stopped.*incomplete/);
    assert.match(page.element('PreviewContext').textContent, /Items skipped for protection: 1.*Failures: 1/);
    assert.equal(page.element('PreviewChanges').children.length, 0);
});

test('a newer direct generation preview survives late older history without merging changes or moving focus', async () => {
    const page = dashboard();
    await page.open();
    await page.click('TabSettings');
    await page.respond('MetaTagger/Preview', { status: 'Ready', summary: {
        runId: 'newer', previewOnly: true, runMode: 'Incremental', configurationRevision: 'revision', lastRunUtc: '2026-09-14T12:00:00Z'
    }, changes: [{ itemId: 'newer-item', itemName: 'New generation', addedTags: ['meta:year:2026'] }] });
    await page.respond('MetaTagger/Runs', [{ runId: 'older', operation: 'Preview', scope: 'Configured item types across all libraries', configurationRevision: 'revision', startedUtc: '2026-09-13T12:00:00Z' }]);
    await page.respond('MetaTagger/Runs/older', { detailsAvailable: true, items: [{ itemId: 'old-item', name: 'Old generation', removedTags: ['meta:year:2025'] }] });
    assert.match(page.element('PreviewRunId').textContent, /Run newer/);
    assert.match(page.element('PreviewChanges').textContent, /New generation/);
    assert.doesNotMatch(page.element('PreviewChanges').textContent, /Old generation/);
    assert.equal(page.element('PanelSettings').hidden, false);
    assert.equal(page.element('TabReview').focused, undefined);
});

test('cleanup previews and unavailable retained details never masquerade as an empty generation preview', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { status: 'NoChanges', summary: { runId: 'cleanup', previewOnly: true, runMode: 'ClearGeneratedTags' } });
    await page.respond('MetaTagger/Runs', [
        { runId: 'cleanup', operation: 'Cleanup preview', scope: 'Entire library' },
        { runId: 'generation', operation: 'Preview', scope: 'Configured item types across all libraries', configurationRevision: 'revision', summary: { itemsChanged: 8 } }
    ]);
    await page.respond('MetaTagger/Runs/generation', { detailsAvailable: false, items: [] });
    assert.match(page.element('PreviewRunId').textContent, /Run generation/);
    assert.match(page.element('PreviewFeedback').textContent, /details are unavailable/);
    assert.doesNotMatch(page.element('PreviewFeedback').textContent, /No tag differences/);
    assert.equal(page.element('PreviewChanges').children.length, 0);
});

test('all selected sources stay summarized after collapsed choices are saved and reloaded', async () => {
    const page = await readyDashboard();
    for (const id of ['EnableGenres', 'EnableParentalRating', 'EnableStudios', 'EnableProductionCountries', 'EnableProductionYear', 'EnableAudioLanguages', 'EnableSubtitleLanguages', 'EnableExistingTagsAsKeywords', 'EnableProviderIds']) await page.edit(id, true);
    await page.disclose('MoreSources');
    await page.disclose('MoreSources', false);
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    const saved = page.request('save-settings');
    const savedValues = saved.body;
    saved.resolve({});
    await page.event('GeneratedTagPrefix', 'blur');
    await page.event('MetaTaggerConfigPage', 'pagehide');
    await page.event('MetaTaggerConfigPage', 'pageshow');
    await page.respond('load-settings', savedValues);
    assert.equal(page.element('MoreSources').open, false);
    assert.equal(page.element('SelectedSources').textContent, 'Genres, Parental rating, Audio languages, Studios, Production countries, Production year, Subtitle languages, Existing tags as keywords, Metadata providers');
    assert.equal(page.element('OverviewSources').textContent, page.element('SelectedSources').textContent);
});


test('correcting an invalid conditional value keeps its editor reachable through successive edits', async () => {
    const page = await readyDashboard();
    await page.event('MinimumMinutesBetweenAutoRuns', 'invalid');
    await page.edit('MinimumMinutesBetweenAutoRuns', '1');
    assert.equal(page.element('PostScanOptions').hidden, false);
    await page.edit('MinimumMinutesBetweenAutoRuns', '15');
    assert.equal(page.element('PostScanOptions').hidden, false);
    assert.equal(Boolean(page.element('RunAfterLibraryScan').checked), false);
    await page.edit('RunAfterLibraryScan', true);
    await page.edit('RunAfterLibraryScan', false);
    assert.equal(page.element('PostScanOptions').hidden, true);
});

test('history selection reveals matching details and ignores a slower previous selection', async () => {
    const page = await readyDashboard();
    await page.respond('MetaTagger/Runs', [{ runId: 'a', operation: 'Apply' }, { runId: 'b', operation: 'Apply' }]);
    const [first, second] = page.element('RunList').children;
    await page.activate(first);
    const older = page.request('MetaTagger/Runs/a');
    assert.equal(first.getAttribute('aria-pressed'), 'true');
    assert.equal(page.element('RunDetailHeading').focused, undefined);
    assert.match(page.element('RunDetailFeedback').textContent, /Loading/);
    await page.activate(second);
    assert.equal(first.getAttribute('aria-pressed'), 'false');
    assert.equal(second.getAttribute('aria-pressed'), 'true');
    assert.match(page.element('HistoryRunId').textContent, /Run b/);
    await page.respond('MetaTagger/Runs/b', { detailsAvailable: true, items: [{ name: 'Selected result' }] });
    older.resolve({ detailsAvailable: true, items: [{ name: 'Obsolete result' }] });
    await page.event('RunDetails', 'blur');
    assert.equal(page.element('RunDetailHeading').scrolledIntoView, undefined);
    assert.match(page.element('RunDetails').textContent, /Selected result/);
    assert.doesNotMatch(page.element('RunDetails').textContent, /Obsolete/);
    await page.activate(first);
    await page.fail('MetaTagger/Runs/a');
    assert.match(page.element('RunDetailFeedback').textContent, /unavailable/);
    assert.equal(page.element('RunDetails').textContent, '');
    await page.activate(second);
    await page.respond('MetaTagger/Runs/b', { detailsAvailable: false });
    assert.match(page.element('RunDetailFeedback').textContent, /details.*unavailable/);
    assert.equal(page.element('ApplyItemButton').disabled, true);
});

test('page removal cancels example work even when Jellyfin detaches the page before pagehide', async () => {
    const page = await readyDashboard();
    await chooseExample(page, { itemId: 'a', name: 'Detached example' });
    const pending = page.request('MetaTagger/Example');
    page.detach();
    await page.event('MetaTaggerConfigPage', 'pagehide');
    pending.resolve({ generatedTags: ['obsolete:tag'] });
    await page.tick();
    assert.doesNotMatch(page.element('ExampleTags').textContent, /obsolete/);
});

test('opening a Review item automatically previews it and requires explicit confirmation', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    assert.equal(page.element('ConfirmItem').disabled, true);
    assert.equal(page.element('ApplyItemButton').disabled, true);
    await page.respond('MetaTagger/Items/movie/Preview', { token: 'fresh-review', addedTags: ['meta:genre:drama'] });
    assert.equal(page.element('ConfirmItem').disabled, false);
    assert.equal(page.element('ApplyItemButton').disabled, true);
    page.element('ConfirmItem').checked = true;
    await page.event('ConfirmItem', 'change');
    await page.click('ApplyItemButton');
    assert.deepEqual(page.sent('MetaTagger/Items/movie/Apply')[0].body, { token: 'fresh-review' });
});

test('selecting another item cannot display or approve the previous automatic preview', async () => {
    const page = await readyDashboard();
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 2, items: [
        { itemId: 'a', name: 'First' }, { itemId: 'b', name: 'Second' }
    ] });
    await page.activate(page.element('ItemList').children[0]);
    await page.activate(page.element('ItemList').children[1]);
    await page.respond('MetaTagger/Items/a/Preview', { token: 'obsolete', addedTags: ['old'] });
    assert.equal(page.element('ConfirmItem').disabled, true);
    assert.doesNotMatch(page.element('InspectorDetails').textContent, /old/);
    await page.respond('MetaTagger/Items/b/Preview', { token: 'current', addedTags: ['new'] });
    assert.equal(page.element('ConfirmItem').disabled, false);
    await page.edit('GeneratedTagPrefix', 'custom');
    assert.equal(page.element('ConfirmItem').disabled, true);
});

test('an automatic preview selected with unsaved settings explains why confirmation is unavailable', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'custom');
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'a', name: 'First' }] });
    await page.activate(page.element('ItemList').children[0]);
    assert.match(page.element('InspectorFeedback').textContent, /Save settings/);
    assert.equal(page.element('ConfirmItem').disabled, true);
    assert.equal(page.sent('MetaTagger/Items/a/Preview').length, 0);
});

test('Inspect and History have persistent keyboard tabs and retain browsing context', async () => {
    const page = await readyDashboard();
    assert.ok(page.element('TabInspect'));
    assert.ok(page.element('TabRuns'));
    await page.click('TabInspect');
    assert.equal(page.element('InspectionPanel').hidden, false);
    assert.equal(page.element('TabInspect').getAttribute('aria-selected'), 'true');
    page.element('ItemSearch').value = 'Dune';
    await page.click('TabRuns');
    assert.equal(page.element('TabRuns').getAttribute('aria-selected'), 'true');
    await page.click('TabInspect');
    assert.equal(page.element('ItemSearch').value, 'Dune');
    await page.event('TabInspect', 'keydown', { key: 'ArrowRight' });
    assert.equal(page.element('TabRuns').getAttribute('aria-selected'), 'true');
});

test('Preview tag removal opens a dedicated destination and lists the selected item tags before confirmation', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    await page.respond('MetaTagger/Items/movie/Preview', { token: 'generation', addedTags: ['new'] });
    await page.click('ClearItemButton');
    assert.equal(page.element('PanelMaintenance').hidden, false);
    assert.equal(page.element('PanelSettings').hidden, true);
    assert.equal(page.element('ConfirmCleanup').disabled, true);
    assert.deepEqual(page.sent('MetaTagger/Cleanup/Preview')[0].body, { itemId: 'movie' });
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    assert.match(page.element('CleanupChanges').textContent, /meta:genre:drama/);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    page.element('ConfirmCleanup').checked = true;
    await page.event('ConfirmCleanup', 'change');
    await page.click('ApplyCleanupButton');
    assert.deepEqual(page.sent('MetaTagger/Cleanup/Apply')[0].body, { token: 'approved-movie' });
});

test('failed and no-change automatic previews keep confirmation disabled and permit refresh', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    await page.fail('MetaTagger/Items/movie/Preview');
    assert.match(page.element('InspectorFeedback').textContent, /Could not preview.*Refresh item preview/);
    assert.equal(page.element('ConfirmItem').disabled, true);
    await page.click('PreviewItemButton');
    await page.respond('MetaTagger/Items/movie/Preview', { addedTags: [], removedTags: [] });
    assert.match(page.element('InspectorFeedback').textContent, /No tags to add or remove/);
    assert.equal(page.element('ConfirmItem').disabled, true);
    await page.click('PreviewItemButton');
    await page.edit('GeneratedTagPrefix', 'new');
    await page.respond('MetaTagger/Items/movie/Preview', { token: 'late', addedTags: ['late'] });
    assert.match(page.element('InspectorFeedback').textContent, /Settings changed/);
    assert.equal(page.element('ConfirmItem').disabled, true);
});

test('Maintenance is a persistent tab and leaving it invalidates removal approval', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    assert.equal(page.element('TabMaintenance').getAttribute('aria-selected'), 'true');
    assert.equal(page.element('TabMaintenance').getAttribute('tabindex'), '0');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    await confirmCleanup(page);
    assert.equal(page.element('ApplyCleanupButton').disabled, false);
    await page.click('TabSettings');
    assert.equal(page.element('PanelSettings').hidden, false);
    assert.equal(page.element('ConfirmCleanup').checked, false);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    assert.match(page.element('CleanupFeedback').textContent, /left Maintenance/);
});

test('namespace conflict identifies both fields and clears their errors after correction', async () => {
    const page = await readyDashboard();
    await page.edit('GeneratedTagPrefix', 'manual');
    for (const id of ['GeneratedTagPrefix', 'ManualTagPrefix']) {
        assert.equal(page.element(id).getAttribute('aria-invalid'), 'true');
        assert.match(page.element(id).getAttribute('aria-describedby'), /TagNamespaceErrors/);
    }
    assert.equal(page.element('TagSeparator').getAttribute('aria-invalid'), 'false');
    await page.edit('GeneratedTagPrefix', 'catalog');
    for (const id of ['GeneratedTagPrefix', 'ManualTagPrefix', 'TagSeparator']) {
        assert.equal(page.element(id).getAttribute('aria-invalid'), 'false');
        assert.doesNotMatch(page.element(id).getAttribute('aria-describedby'), /TagNamespaceErrors/);
    }
    assert.equal(page.element('TagNamespaceErrors').hidden, true);
});

test('Inspect groups missing recorded additions inline and refresh removes the explanation', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    await page.respond('MetaTagger/Items/movie/Preview', {
        token: 'restore', addedTags: ['meta:genre:drama', 'meta:year:2024', 'meta:studio:new'],
        missingRecordedTags: ['meta:genre:drama', 'meta:year:2024'], preservedTags: ['edited-drama']
    });
    const details = page.element('InspectorDetails');
    assert.match(details.children[0].textContent, /Previously recorded tags are missing.*Applying changes will add them again/);
    assert.equal((details.textContent.match(/Applying changes will add them again/g) || []).length, 1);
    assert.match(details.textContent, /Other tags to keep.*edited-drama/);
    assert.equal(page.sent('MetaTagger/Items/movie/Apply').length, 0);
    page.element('ConfirmItem').checked = true;
    await page.event('ConfirmItem', 'change');
    await page.click('ApplyItemButton');
    await page.respond('MetaTagger/Items/movie/Apply', { writesApplied: 1 });
    await page.respond('MetaTagger/Items/movie/Preview', { ownedTags: ['meta:genre:drama', 'meta:year:2024'] });
    assert.doesNotMatch(details.textContent, /missing|add them again/);
});

test('Inspect identifies absent tags that stay absent with source wording only when supported', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    await page.respond('MetaTagger/Items/movie/Preview', {
        missingRecordedTags: ['meta:genre:drama', 'earlier:year:2023'], missingTagsWithSourceOff: ['meta:genre:drama']
    });
    const text = page.element('InspectorDetails').textContent;
    assert.match(text, /meta:genre:drama.*source is unchecked.*remain absent.*No action is needed/);
    assert.match(text, /earlier:year:2023.*no longer generated.*remain absent.*No action is needed/);
    assert.equal(page.element('ConfirmItem').disabled, true);
    assert.equal(page.element('ApplyItemButton').disabled, true);
});

test('selected-item removal explains already absent tags without enabling an empty action', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    await page.respond('MetaTagger/Items/movie/Preview', { missingRecordedTags: ['meta:genre:drama'] });
    await page.click('ClearItemButton');
    await page.respond('MetaTagger/Cleanup/Preview', {
        itemId: 'movie', summary: { itemsChanged: 0 }, changes: [], missingRecordedTags: ['meta:genre:drama', 'meta:year:2024']
    });
    assert.match(page.element('CleanupChanges').textContent, /meta:genre:drama.*meta:year:2024.*already absent.*need no removal/);
    assert.equal(page.element('ConfirmCleanup').disabled, true);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
});

for (const mode of ['Remove', 'Keep', 'Preview']) {
    test(`prefix change describes actual ${mode} plan beside existing tag groups`, async () => {
        const page = await readyDashboard();
        await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
        await page.respond('MetaTagger/Items/movie/Preview', {
            token: 'prefix', addedTags: ['new:genre:drama'], generatedTags: ['new:genre:drama'],
            removedTags: mode === 'Remove' ? ['meta:genre:drama'] : [],
            ownedTags: mode === 'Remove' ? [] : ['meta:genre:drama'],
            previewRemovedTags: mode === 'Preview' ? ['meta:genre:drama'] : []
        });
        const groups = page.element('InspectorDetails').children;
        const oldGroup = groups.find(group => group.textContent.includes(mode === 'Remove' ? 'Tags to remove' : mode === 'Preview' ? 'Outdated tags to keep' : 'Plugin tags to keep'));
        assert.match(oldGroup.textContent, mode === 'Remove' ? /previously recorded tag.*Applying changes will remove it/ : /previously recorded tag.*Applying changes will keep it/);
        assert.doesNotMatch(page.element('InspectorDetails').textContent, /missing|rename|replacement/);
    });
}

test('claim warnings follow draft choices while the tag-return warning follows saved tagging state', async () => {
    const page = await readyDashboard();
    assert.equal(page.element('ClaimTagsWarning').hidden, true);
    assert.equal(page.element('TemporaryClaimTagsWarning').hidden, true);
    assert.equal(page.element('CleanupReturnWarning').hidden, false);
    await page.edit('ClaimExistingGeneratedTagsForCleanup', true);
    assert.equal(page.element('ClaimTagsWarning').hidden, false);
    await page.edit('ClaimExistingGeneratedTagsOnNextRun', true);
    assert.equal(page.element('TemporaryClaimTagsWarning').hidden, false);
    await page.edit('IsEnabled', false);
    assert.equal(page.element('CleanupReturnWarning').hidden, false);
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', { ConfigurationRevision: 'revision' });
    await page.respond('save-settings', {});
    assert.equal(page.element('CleanupReturnWarning').hidden, true);
    assert.match(page.element('SettingsFeedback').textContent, /Tagging is off/);
    assert.doesNotMatch(page.element('SettingsFeedback').textContent, /Select Preview tag changes/);
});

test('partial removal reports updated items without claiming all removals succeeded', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    await confirmCleanup(page);
    await page.click('ApplyCleanupButton');
    await page.respond('MetaTagger/Cleanup/Apply', { writesApplied: 1, failures: 1, outcome: 'Partial failure' });
    assert.match(page.element('CleanupFeedback').textContent, /Items updated: 1.*unfinished removals.*1 failure/);
    assert.doesNotMatch(page.element('CleanupFeedback').textContent, /Removed the previewed plugin tags/);
    assert.equal(page.element('CleanupFeedback').getAttribute('role'), 'alert');
    assert.equal(page.element('CleanupFeedback').getAttribute('data-feedback-tone'), 'error');
});

test('incomplete preview uses a warning and limits its empty-result claim to checked items', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { status: 'NoChanges', changes: [], summary: {
        runId: 'limited', previewOnly: true, runMode: 'Incremental', configurationRevision: 'revision',
        outcome: 'Budget limited', budgetLimitReached: true, itemsScanned: 2
    } });
    assert.equal(page.element('PreviewContext').getAttribute('data-feedback-tone'), 'warning');
    assert.match(page.element('PreviewContext').textContent, /same settings to continue/);
    assert.match(page.element('PreviewFeedback').textContent, /in the items checked/);
});

test('example distinguishes no selected sources from missing metadata', async () => {
    const page = await readyDashboard();
    await chooseExample(page, { itemId: 'a', name: 'Example', itemType: 'Movie' });
    await page.respond('MetaTagger/Example', { generatedTags: [] });
    assert.match(page.element('ExampleFeedback').textContent, /No metadata sources are selected/);
    await page.edit('EnableGenres', true);
    await page.tick();
    await page.respond('MetaTagger/Example', { generatedTags: [] });
    assert.match(page.element('ExampleFeedback').textContent, /selected sources have no values/);
});

test('item feedback distinguishes blocked settings from protection and clears an error during retry', async () => {
    const page = await readyDashboard();
    await page.activate(page.element('PreviewChanges').children[0].children.at(-1));
    await page.respond('MetaTagger/Items/movie/Preview', { status: 'InvalidSettings', reason: 'The generated and manual prefixes must be different.' });
    assert.equal(page.element('InspectorFeedback').getAttribute('role'), 'alert');
    assert.equal(page.element('InspectorFeedback').getAttribute('data-feedback-tone'), 'error');
    assert.equal(page.element('ApplyItemButton').disabled, true);
    assert.equal(page.element('ItemApprovalHelp').hidden, true);
    await page.click('PreviewItemButton');
    assert.equal(page.element('InspectorFeedback').getAttribute('role'), 'status');
    assert.equal(page.element('InspectorFeedback').getAttribute('data-feedback-tone'), 'neutral');
    assert.match(page.element('InspectorFeedback').textContent, /Checking/);
    await page.respond('MetaTagger/Items/movie/Preview', { status: 'Protected', reason: 'Jellyfin metadata lock protects this item.' });
    assert.equal(page.element('InspectorFeedback').getAttribute('data-feedback-tone'), 'neutral');
    assert.equal(page.element('ConfirmItem').disabled, true);
    await page.click('PreviewItemButton');
    await page.respond('MetaTagger/Items/movie/Preview', { status: 'Changes', token: 'fresh', addedTags: ['meta:year:2026'] });
    assert.equal(page.element('ItemApprovalHelp').hidden, false);
    assert.equal(page.element('ApplyItemButton').disabled, true);
    await page.edit('EnableGenres', true);
    assert.equal(page.element('ItemApprovalHelp').hidden, true);
    assert.equal(page.element('InspectorFeedback').getAttribute('data-feedback-tone'), 'warning');
});

test('history failure clears on retry and incomplete retained results receive a warning', async () => {
    const page = await readyDashboard();
    await page.respond('MetaTagger/Runs', [{ runId: 'history', operation: 'Apply', outcome: 'Completed' }]);
    const row = page.element('RunList').children[0];
    await page.activate(row);
    await page.fail('MetaTagger/Runs/history');
    assert.equal(page.element('RunDetailFeedback').getAttribute('role'), 'alert');
    assert.equal(page.element('RunDetailFeedback').getAttribute('data-feedback-tone'), 'error');
    await page.activate(row);
    assert.equal(page.element('RunDetailFeedback').getAttribute('role'), 'status');
    assert.equal(page.element('RunDetailFeedback').getAttribute('data-feedback-tone'), 'neutral');
    await page.respond('MetaTagger/Runs/history', { outcome: 'Completed', detailsAvailable: true, detailsTruncated: true, items: [] });
    assert.equal(page.element('RunDetailFeedback').getAttribute('data-feedback-tone'), 'warning');
    assert.match(page.element('RunDetailFeedback').textContent, /Some item details were not retained/);
    await page.activate(row);
    await page.respond('MetaTagger/Runs/history', { outcome: 'Completed', detailsAvailable: true, items: [] });
    assert.equal(page.element('RunDetailFeedback').getAttribute('data-feedback-tone'), 'neutral');
});

test('history continuation guidance requires a new approval for cleanup and single-item runs', async () => {
    for (const [operation, scope, expected] of [
        ['Cleanup apply', 'Entire library', /Preview tag removal again, then confirm/],
        ['Apply', 'movie', /Refresh the item preview/],
        ['Preview', 'Configured item types across all libraries', /same action with the same settings/]
    ]) {
        const page = await readyDashboard();
        await page.respond('MetaTagger/Runs', [{ runId: 'limited', operation, scope, outcome: 'Budget limited', summary: { budgetLimitReached: true } }]);
        assert.match(page.element('OverviewCounts').textContent, expected);
    }
});

test('outdated tags kept by policy stay neutral in Review, Inspect, and History', async () => {
    const page = dashboard();
    await page.open();
    const item = { itemId: 'movie', itemName: 'Movie', previewRemovedTags: ['meta:year:2025'] };
    await page.respond('MetaTagger/Preview', { status: 'Ready', summary: {}, changes: [item] });
    const review = page.element('PreviewChanges').children[0];
    assert.equal(review.children.find(child => child.textContent.includes('Outdated tags to keep')).getAttribute('data-feedback-tone'), undefined);
    await page.activate(review.children.at(-1));
    await page.respond('MetaTagger/Items/movie/Preview', { status: 'Changes', previewRemovedTags: item.previewRemovedTags });
    const inspector = page.element('InspectorDetails').children[0];
    assert.equal(inspector.getAttribute('data-feedback-tone'), undefined);
    assert.equal(page.element('ConfirmItem').disabled, true);
    await page.respond('MetaTagger/Runs', [{ runId: 'kept', operation: 'Apply' }]);
    await page.activate(page.element('RunList').children[0]);
    await page.respond('MetaTagger/Runs/kept', { detailsAvailable: true, items: [item] });
    const history = page.element('RunDetails').children[0].children.find(child => child.textContent.includes('Outdated tags to keep'));
    assert.equal(history.getAttribute('data-feedback-tone'), undefined);
});

test('a failed save stays an error with unsaved settings, then becomes plain loading during retry', async () => {
    const page = await readyDashboard();
    await page.edit('EnableGenres', true);
    assert.equal(page.element('SettingsFeedback').getAttribute('data-feedback-tone'), 'warning');
    assert.match(page.element('SettingsFeedback').textContent, /example uses your unsaved changes/);
    await page.event('MetaTaggerConfigForm', 'submit');
    await page.respond('load-settings', {});
    await page.fail('save-settings');
    assert.equal(page.element('SettingsFeedback').getAttribute('data-feedback-tone'), 'error');
    assert.equal(page.element('SettingsFeedback').getAttribute('role'), 'alert');
    assert.equal(page.element('RunPreviewButton').disabled, true);
    await page.event('MetaTaggerConfigForm', 'submit');
    assert.match(page.element('SettingsFeedback').textContent, /Saving settings/);
    assert.equal(page.element('SettingsFeedback').getAttribute('data-feedback-tone'), 'neutral');
    await page.respond('load-settings', {});
    await page.respond('save-settings', {});
    assert.equal(page.element('TaskFeedback').getAttribute('data-feedback-tone'), 'neutral');
});

test('cleanup search errors appear beside search and clear when searching again', async () => {
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    page.element('CleanupScope').value = 'item';
    await page.event('CleanupScope', 'change');
    page.element('CleanupSearchTerm').value = 'Movie';
    await page.click('CleanupSearchButton');
    await page.fail('MetaTagger/Cleanup/Items');
    assert.equal(page.element('CleanupSearchFeedback').getAttribute('role'), 'alert');
    assert.equal(page.element('CleanupFeedback').getAttribute('data-feedback-tone'), 'neutral');
    await page.click('CleanupSearchButton');
    assert.equal(page.element('CleanupSearchFeedback').getAttribute('data-feedback-tone'), 'neutral');
    await page.respond('MetaTagger/Cleanup/Items', []);
    assert.match(page.element('CleanupSearchFeedback').textContent, /No matching items/);
    assert.equal(page.element('ConfirmCleanup').disabled, true);
});

test('settings example uses error treatment for invalid settings and clears it for recalculation', async () => {
    const page = await readyDashboard();
    await chooseExample(page, { itemId: 'a', name: 'Example', itemType: 'Movie' });
    await page.respond('MetaTagger/Example', { status: 'InvalidSettings', reason: 'The generated and manual prefixes must be different.' });
    assert.equal(page.element('ExampleFeedback').getAttribute('role'), 'alert');
    await page.edit('EnableGenres', true);
    assert.equal(page.element('ExampleFeedback').getAttribute('data-feedback-tone'), 'neutral');
    await page.tick();
    await page.respond('MetaTagger/Example', { status: 'Changes', generatedTags: ['meta:genre:drama'] });
    assert.equal(page.element('ExampleFeedback').getAttribute('role'), 'status');
    assert.match(page.element('ExampleFeedback').textContent, /including unsaved changes.*Nothing was saved/);
});

test('artwork loads without a stored image flag and falls back from episode to season to series', async () => {
    const page = await readyDashboard();
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 1, items: [{ itemId: 'episode', name: 'Pilot', itemType: 'Episode', artworkItemIds: ['episode', 'season', 'series'] }] });
    const artwork = page.element('ItemList').children[0].children[0];
    const [fallback, image] = artwork.children;
    assert.match(artwork.className, /ArtworkEpisode/);
    assert.match(image.src, /Items\/episode\/Images\/Primary/);
    assert.equal(image.hidden, false, 'lazy image stays in layout so the browser requests it');
    image.dispatch('error');
    assert.match(image.src, /Items\/season\/Images\/Primary/);
    image.dispatch('error');
    assert.match(image.src, /Items\/series\/Images\/Primary/);
    image.dispatch('load');
    assert.equal(fallback.hidden, true);
    assert.equal(image.hidden, false);
    image.dispatch('error');
    assert.equal(image.hidden, true);
    assert.equal(fallback.hidden, false);
});

test('preview-result inspection requests artwork immediately and adds parent fallback from inspection', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { status: 'Ready', changes: [{ itemId: 'episode', itemName: 'Pilot', itemType: 'Episode', addedTags: ['meta:genre:drama'] }], summary: {} });
    await page.click('TabReview');
    const article = page.element('PreviewChanges').children[0];
    await page.activate(article.children.find(child => child.textContent === 'Preview this item'));
    assert.match(page.element('InspectorArtwork').children[0].children[1].src, /Items\/episode\/Images\/Primary/);
    await page.respond('MetaTagger/Items/episode/Preview', { itemId: 'episode', name: 'Pilot', itemType: 'Episode', artworkItemIds: ['episode', 'season', 'series'], status: 'Up to date' });
    const image = page.element('InspectorArtwork').children[0].children[1];
    image.dispatch('error');
    assert.match(image.src, /Items\/season\/Images\/Primary/);
});

test('series and season navigation restores search and page through breadcrumbs and preserves inspection', async () => {
    const page = await readyDashboard();
    page.element('ItemLibrary').value = 'shows';
    page.element('ItemSearch').value = 'Robin';
    await page.event('ItemSearch', 'keydown', { key: 'Enter' });
    await page.respond('MetaTagger/Items', { totalCount: 30, items: [] });
    await page.click('NextItemsButton');
    const series = { itemId: 'series', name: 'Robin Hood', itemType: 'Series' };
    await page.respond('MetaTagger/Items', { totalCount: 30, items: [series] });
    const entry = page.element('ItemList').children[0];
    await page.activate(entry.children[0]);
    await page.respond('MetaTagger/Items/series/Preview', { status: 'Up to date' });
    await page.activate(entry.children[1]);
    let request = page.request('MetaTagger/Items');
    assert.match(request.kind, /parentId=series/);
    assert.match(request.kind, /libraryId=shows/);
    assert.match(request.kind, /searchTerm=&/);
    assert.match(request.kind, /startIndex=0/);
    request.resolve({ totalCount: 1, items: [{ itemId: 'season', name: 'Season 1', itemType: 'Season' }] });
    await page.event('ItemSearch', 'blur');
    await page.activate(page.element('ItemList').children[0]);
    request = page.request('MetaTagger/Items');
    assert.match(request.kind, /parentId=season/);
    request.resolve({ totalCount: 1, items: [{ itemId: 'episode', name: 'Pilot', itemType: 'Episode' }] });
    await page.event('ItemSearch', 'blur');
    assert.match(page.element('ItemBreadcrumbs').textContent, /Robin Hood.*Season 1/);
    assert.equal(page.element('InspectorHeading').textContent, 'Robin Hood');
    await page.activate(page.element('ItemBreadcrumbs').children[0]);
    request = page.request('MetaTagger/Items');
    assert.match(request.kind, /searchTerm=Robin/);
    assert.match(request.kind, /startIndex=25/);
    assert.doesNotMatch(request.kind, /parentId=/);
    request.resolve({ totalCount: 30, items: [series] });
    await page.event('ItemSearch', 'blur');
    assert.equal(page.element('ItemList').children[0].children[0].getAttribute('aria-pressed'), 'true');
    await page.event('ItemLibrary', 'change');
    assert.doesNotMatch(page.request('MetaTagger/Items').kind, /parentId=/);
});


test('live item filters search without a button and retry preserves the failed page', async () => {
    const page = await readyDashboard();
    assert.equal(page.element('SearchItemsButton'), undefined);
    assert.equal(page.element('RetryItemsButton').hidden, true);
    page.element('ItemSearch').value = 'Arrival';
    await page.event('ItemSearch', 'input');
    assert.equal(page.sent('MetaTagger/Items').length, 0);
    await page.tick();
    const search = page.request('MetaTagger/Items');
    assert.match(search.kind, /searchTerm=Arrival/);
    search.resolve({ totalCount: 51, items: [{ itemId: 'arrival', name: 'Arrival' }] });
    await page.event('ItemSearch', 'blur');
    await page.click('NextItemsButton');
    const failed = page.request('MetaTagger/Items');
    failed.reject(new Error('Unavailable'));
    await page.event('ItemSearch', 'blur');
    assert.equal(page.element('RetryItemsButton').hidden, false);
    assert.match(page.element('ItemBrowserFeedback').textContent, /could not be loaded/);
    await page.click('RetryItemsButton');
    assert.equal(page.element('RetryItemsButton').hidden, true);
    const retry = page.request('MetaTagger/Items');
    assert.equal(retry.kind, failed.kind);
    retry.resolve({ totalCount: 51, items: [{ itemId: 'arrival', name: 'Arrival' }] });
    await page.event('ItemSearch', 'blur');
    assert.equal(page.element('RetryItemsButton').hidden, true);
    page.element('ItemLibrary').value = 'library-a';
    await page.event('ItemLibrary', 'change');
    const library = page.request('MetaTagger/Items');
    assert.match(library.kind, /libraryId=library-a/);
    assert.match(library.kind, /startIndex=0/);
    library.resolve({ totalCount: 1, items: [{ itemId: 'arrival', name: 'Arrival' }] });
    await page.event('ItemSearch', 'blur');
    page.element('ItemStatus').value = 'Failed';
    await page.event('ItemStatus', 'change');
    assert.equal(page.element('ItemList').children.length, 0);
});


test('tag changes expose feedback tones while the overview heading stays unstyled', async () => {
    const page = dashboard();
    await page.open();
    await page.respond('MetaTagger/Preview', { status: 'Ready', summary: {}, changes: [
        { itemId: 'movie', itemName: 'Movie', addedTags: ['meta:year:2026'], removedTags: ['meta:year:2025'] }
    ] });
    const groups = page.element('PreviewChanges').children[0].children;
    assert.equal(groups.find(child => child.textContent.includes('Tags to add')).getAttribute('data-feedback-tone'), 'positive');
    assert.equal(groups.find(child => child.textContent.includes('Tags to remove')).getAttribute('data-feedback-tone'), 'negative');
    await page.respond('MetaTagger/Runs', [{ runId: 'finished', operation: 'Preview', outcome: 'Completed', summary: {} }]);
    assert.equal(page.element('OverviewOutcome').getAttribute('data-feedback-tone'), undefined);
    assert.doesNotMatch(markup, /data-tone/);
});

test('cleanup uses the danger variant while remaining disabled until approved', async () => {
    assert.match(markup, /id="ApplyCleanupButton"[^>]*class="[^"]*button-danger[^"]*"[^>]*disabled/);
    const page = await readyDashboard();
    await page.click('TabMaintenance');
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await page.click('PreviewCleanupButton');
    await page.respond('MetaTagger/Cleanup/Preview', cleanupPreview);
    assert.equal(page.element('ApplyCleanupButton').disabled, true);
    await confirmCleanup(page);
    assert.equal(page.element('ApplyCleanupButton').disabled, false);
});

test('review and history thumbnails share lazy loading and missing-artwork fallback', async () => {
    const page = await readyDashboard();
    const reviewArtwork = page.element('PreviewChanges').children[0].children[0].children[0];
    assert.match(reviewArtwork.children[1].src, /Items\/movie\/Images\/Primary/);
    assert.equal(reviewArtwork.children[1].loading, 'lazy');
    assert.equal(reviewArtwork.children[1].alt, '');
    await page.respond('MetaTagger/Runs', [{ runId: 'recorded', operation: 'Apply', outcome: 'Completed' }]);
    await page.activate(page.element('RunList').children[0]);
    await page.respond('MetaTagger/Runs/recorded', { detailsAvailable: true, scope: 'One item', outcome: 'Completed',
        summary: { itemsScanned: 1, itemsChanged: 1, writesApplied: 0, itemsSkippedLocked: 2, itemsSkippedManual: 1, failures: 0, itemsRemaining: 4 },
        items: [{ itemId: 'movie', name: 'Movie', itemType: 'Movie' }] });
    const artwork = page.element('RunDetails').children[0].children[0].children[0];
    assert.match(artwork.children[1].src, /Items\/movie\/Images\/Primary/);
    artwork.children[1].dispatch('error');
    assert.equal(artwork.children[1].hidden, true);
    assert.equal(artwork.children[0].hidden, false);
    assert.doesNotMatch(page.element('RunDetailFeedback').textContent, /Items checked|Items updated/);
    assert.match(page.element('RunDetailFeedback').textContent, /Preview an item again/);
    assert.deepEqual(page.element('RunDetailCounts').children.map(pair => pair.children.map(child => child.textContent)),
        [['Scope', 'One item'], ['Items checked', '1'], ['With tag differences', '1'], ['Updated', '0'], ['Skipped for protection', '3'], ['Failures', '0'], ['Remaining', '4']]);
    await page.activate(page.element('RunList').children[0]);
    assert.equal(page.element('RunDetailCounts').hidden, true);
    await page.fail('MetaTagger/Runs/recorded');
    assert.equal(page.element('RunDetailCounts').hidden, true);
});


test('initial settings expose three sources and keep optional sources collapsed', async () => {
    const primary = markup.slice(markup.indexOf('<h2 id="SourcesHeading">'), markup.indexOf('<details id="MoreSources"'));
    assert.deepEqual([...primary.matchAll(/<input id="(Enable[^\"]+)"/g)].map(match => match[1]),
        ['EnableGenres', 'EnableParentalRating', 'EnableAudioLanguages']);
    const page = await readyDashboard({ EnableGenres: true, EnableParentalRating: true, EnableAudioLanguages: true,
        IncludeMovies: true, IncludeSeries: true, PreviewOnly: true });
    assert.equal(page.element('MoreSources').open, false);
    assert.equal(page.element('AdditionalSourcesCount').textContent, '0 selected');
    assert.equal(page.element('SelectedSources').textContent, 'Genres, Parental rating, Audio languages');
    assert.equal(page.element('ItemTypesSummary').textContent, 'Movies, Series');
});
