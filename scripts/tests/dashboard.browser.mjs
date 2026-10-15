import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';

export async function ready(page) {
    await page.locator('#MetaTaggerConfigPage').waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelector('#SettingsFeedback')?.textContent === 'Settings loaded.');
}

export async function geometry(page, width, diagnostics) {
    // The settings workspace only has a layout while the Settings tab is selected.
    await page.locator('#TabSettings').click();
    await page.locator('#PanelSettings').waitFor({ state: 'visible' });
    const cards = ['CardSources', 'CardAutomation', 'CardTagFormat', 'CardOutdated', 'CardLimits'];
    const measure = () => page.evaluate(ids => {
        const box = id => {
            const { x, y, width, height } = document.getElementById(id).getBoundingClientRect();
            return { x, y, width, height };
        };
        return { viewport: innerWidth, documentWidth: document.documentElement.scrollWidth,
            pageWidth: document.getElementById('MetaTaggerConfigPage').scrollWidth,
            pageClientWidth: document.getElementById('MetaTaggerConfigPage').clientWidth,
            cards: ids.map(box), aside: box('ExampleAside') }; }, cards);
    const result = await measure();
    diagnostics.push({ scenario: 'settings geometry', width, ...result });
    assert(result.documentWidth <= result.viewport + 1, `Horizontal document overflow at ${width}px`);
    assert(result.pageWidth <= result.pageClientWidth + 1, `Horizontal dashboard overflow at ${width}px`);
    for (const card of result.cards) assert(Math.abs(card.x - result.cards[0].x) < 2, `Cards must share one column at ${width}px`);
    const last = result.cards.at(-1);
    if (width > 800) {
        assert(result.aside.x >= result.cards[0].x + result.cards[0].width - 1, `Try unsaved settings must sit beside the cards at ${width}px`);
        assert(Math.abs(result.aside.y - result.cards[0].y) < 120, `Try unsaved settings must start beside the cards at ${width}px`);
    } else {
        assert(result.aside.y >= last.y + last.height - 1, `Try unsaved settings must stack below the cards at ${width}px`);
        assert(Math.abs(result.aside.x - result.cards[0].x) < 2, `Try unsaved settings must align with the cards at ${width}px`);
    }
    // Every card opens in place, one at a time, without overflowing.
    for (const card of cards) {
        await page.locator(`#${card}Head`).click();
        assert.equal(await page.locator(`#${card}Body`).isVisible(), true, `${card} opens`);
        assert.equal(await page.locator('[data-card-body]:visible').count(), 1, 'Only one card is open');
        await noOverflow(page, `${card} open at ${width}px`, diagnostics);
    }
    await page.locator('#CardLimitsHead').click();
    // The save bar follows unsaved edits, stays in view on every tab, and Discard restores the saved value.
    assert.equal(await page.locator('#SaveBar').isVisible(), false);
    await page.locator('#CardLimitsHead').click();
    const original = await page.locator('#MaxItemsPerRun').inputValue();
    await page.locator('#MaxItemsPerRun').fill('7');
    for (const tab of ['TabSettings', 'TabMaintenance', 'TabActivity']) {
        await page.locator('#' + tab).click();
        assert.equal(await page.locator('#SaveBar').isVisible(), true, `Unsaved bar visible on ${tab} at ${width}px`);
        const bar = await page.evaluate(() => { const { top, bottom, height } = document.getElementById('SaveBar').getBoundingClientRect(); return { top, bottom, height, viewport: innerHeight }; });
        diagnostics.push({ scenario: 'save bar', tab, width, ...bar });
        assert(bar.bottom <= bar.viewport + 1 && bar.top >= 0, `Save bar must stay inside the viewport on ${tab} at ${width}px`);
        await noOverflow(page, `save bar on ${tab} at ${width}px`, diagnostics);
    }
    await page.locator('#DiscardSettingsButton').click();
    assert.equal(await page.locator('#SaveBar').isVisible(), false);
    await page.locator('#TabSettings').click();
    assert.equal(await page.locator('#MaxItemsPerRun').inputValue(), original);
    // Invalid numbers reveal their card and take focus even from another tab.
    await page.locator('#MaxRunMinutes').fill('-1');
    await page.locator('#TabMaintenance').click();
    await page.locator('#SaveSettingsButton').click();
    assert.equal(await page.locator('#PanelSettings').isVisible(), true);
    assert.equal(await page.locator('#CardLimitsBody').isVisible(), true);
    await page.waitForFunction(() => document.activeElement?.id === 'MaxRunMinutes');
    await page.locator('#DiscardSettingsButton').click();
    await page.locator('#CardLimitsHead').click();
}

async function currentFocus(page, id, diagnostics) {
    const focus = await page.evaluate(() => {
        const element = document.activeElement;
        const style = getComputedStyle(element);
        return { id: element.id, outlineStyle: style.outlineStyle, outlineWidth: style.outlineWidth,
            outlineColor: style.outlineColor, focusVisible: element.matches(':focus-visible') };
    });
    diagnostics.push({ scenario: 'keyboard focus', expected: id, ...focus });
    assert.equal(focus.id, id, `Current keyboard focus must be ${id}`);
    return focus;
}

export async function keyboardControl(page, id, diagnostics) {
    // Reach the control through Tab so :focus-visible is browser-generated.
    for (let step = 0; step < 80; step++) {
        await page.keyboard.press('Tab');
        if (await page.evaluate(id => document.activeElement.id === id, id)) {
            const focus = await currentFocus(page, id, diagnostics);
            assert(focus.focusVisible && focus.outlineStyle !== 'none' && parseFloat(focus.outlineWidth) >= 1 &&
                focus.outlineColor !== 'rgba(0, 0, 0, 0)', `${id} needs a visible keyboard focus indicator`);
            return;
        }
    }
    assert.fail(`${id} is not reachable by Tab within 80 steps`);
}

async function noOverflow(page, scenario, diagnostics) {
    const measurement = await page.evaluate(() => ({ viewport: innerWidth, documentWidth: document.documentElement.scrollWidth }));
    diagnostics.push({ scenario, ...measurement });
    assert(measurement.documentWidth <= measurement.viewport + 1, `${scenario} overflows horizontally`);
}

async function populate(page, panel) {
    if (panel === 'PanelActivity') {
        await page.locator('#RunFeed .metadataTaggerFeedHead').first().waitFor();
        const head = page.locator('#RunFeed .metadataTaggerFeedHead').first();
        if ((await head.getAttribute('aria-expanded')) !== 'true') await head.click();
        await page.waitForFunction(() => document.querySelector('#RunFeed .metadataTaggerFeedBody:not([hidden]) dl') !== null);
    } else if (panel === 'PanelItems') {
        await page.locator('#ItemList .metadataTaggerItemRow').first().waitFor();
        await page.locator('#ItemList .metadataTaggerItemRow').filter({ hasText: 'Generated browser fixture' }).first().click();
        await page.locator('#PreviewItemButton').click();
        await page.waitForFunction(() => document.getElementById('InspectorDetails').childElementCount > 0);
        assert((await page.locator('#ItemList').textContent()).includes('Generated browser fixture'), 'Items must contain generated long labels');
    } else if (panel === 'PanelMaintenance') {
        await page.locator('#PreviewCleanupButton').click();
        await page.waitForFunction(() => document.getElementById('CleanupChanges').childElementCount > 0);
        // Never authorize removal. The generated preview is enough to check populated layout.
    }
}

export async function installedScenarios(page, diagnostics) {
    const tabs = [['TabActivity', 'PanelActivity'], ['TabItems', 'PanelItems'], ['TabSettings', 'PanelSettings'], ['TabMaintenance', 'PanelMaintenance']];
    for (const width of [1440, 761, 760, 759, 390]) {
        await page.setViewportSize({ width, height: 1000 });
        await geometry(page, width, diagnostics);
        await page.locator('#TabActivity').click();
        // Reach the selected tab through Tab so :focus-visible is browser-generated, then walk the tablist with arrow keys.
        await page.evaluate(() => document.activeElement.blur());
        await keyboardControl(page, 'TabActivity', diagnostics);
        for (const [index, [tab, panel]] of tabs.entries()) {
            if (index > 0) {
                await page.keyboard.press('ArrowRight');
                await currentFocus(page, tab, diagnostics);
            }
            await page.locator('#' + panel).waitFor({ state: 'visible' });
            assert.equal(await page.locator('#' + tab).getAttribute('aria-selected'), 'true', `${tab} selected at ${width}px`);
            assert.equal(await page.locator('[role="tab"][tabindex="0"]').count(), 1, 'Only the selected tab is in the tab order');
            await populate(page, panel);
            await noOverflow(page, `${panel} at ${width}px`, diagnostics);
            // Populating moves focus into the panel; return to the tablist for the next arrow key.
            await page.locator('#' + tab).focus();
        }
        await page.keyboard.press('ArrowRight');
        await currentFocus(page, 'TabActivity', diagnostics);
        await page.keyboard.press('End');
        await currentFocus(page, 'TabMaintenance', diagnostics);
        await page.keyboard.press('Home');
        await currentFocus(page, 'TabActivity', diagnostics);
        assert.equal(new URL(page.url()).hash.includes('Tab'), false, 'Tabs must not write to the location hash');
    }
}

export async function developmentContract(browser, diagnostics, onPage = () => {}) {
    const markup = await readFile(new URL('../../Jellyfin.Plugin.MetaTagger/Configuration/configPage.html', import.meta.url), 'utf8');
    const client = await readFile(new URL('../dev-client.js', import.meta.url), 'utf8');
    let revision = 1;
    const name = 'Generated development fixture with a long title ' + 'longlabel'.repeat(16);
    const boot = `<script>
        window.ApiClient = {
            getUrl(path, params) { return '/' + path + (params ? '?' + new URLSearchParams(params) : ''); },
            getJSON(url) { return fetch(url).then(r => r.json()); },
            getPluginConfiguration() { return fetch('/configuration').then(r => r.json()); },
            updatePluginConfiguration() { return Promise.resolve({}); },
            ajax(options) { return fetch(options.url, {method: options.type, body: options.data}).then(r => r.json()); }
        };
        window.Dashboard = {showLoadingMsg(){}, hideLoadingMsg(){}, processPluginConfigurationUpdateResult(){}};
        if (!sessionStorage.getItem('browser-contract-started')) {
            sessionStorage.setItem('meta-tagger-dev-tab', 'TabSettings');
            sessionStorage.setItem('browser-contract-started', 'true');
        }
    </script>`;
    const host = createServer((request, response) => {
        const path = new URL(request.url, 'http://127.0.0.1').pathname;
        if (path === '/') {
            response.setHeader('Content-Type', 'text/html');
            response.end(boot + markup + `<script>document.getElementById('MetaTaggerConfigPage').dispatchEvent(new Event('pageshow'));\n${client}</script>`);
            return;
        }
        if (path === '/favicon.ico') { response.statusCode = 204; response.end(); return; }
        if (path === '/Items/generated/Images/Primary') {
            response.setHeader('Content-Type', 'image/svg+xml');
            response.end('<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><rect width="16" height="16" fill="#444"/></svg>');
            return;
        }
        const fixtures = {
            '/configuration': { GeneratedTagPrefix: 'meta', ManualTagPrefix: 'manual', TagSeparator: ':',
                IsEnabled: true, EnableGenres: true, IncludeMovies: true, PreviewOnly: true,
                ConfigurationRevision: 'development-revision', DefaultRunMode: 'Incremental', StaleTagMode: 'Keep' },
            '/MetaTagger/Libraries': [], '/MetaTagger/Runs': [], '/MetaTagger/ItemRuns/Current': null,
            '/MetaTagger/Preview': { status: 'Ready', summary: {}, changes: [] },
            '/MetaTagger/Items': { totalCount: 1, items: [{ itemId: 'generated', name, itemType: 'Movie' }] },
            '/MetaTagger/Items/generated': { itemId: 'generated', name, itemType: 'Movie', generatedTags: ['meta:genre:drama'], sources: [] },
            '/MetaTagger/Items/generated/Preview': { itemId: 'generated', name, itemType: 'Movie', generatedTags: ['meta:genre:drama'], addedTags: ['meta:genre:drama'], sources: [] },
            '/MetaTagger/Cleanup/Preview': { token: 'generated-preview', summary: {}, changes: [
                { itemId: 'generated', itemName: name, itemType: 'Movie', removedTags: ['meta:genre:drama'] }] },
            '/__meta_tagger_dev/status': { revision, ready: true, status: 'Ready' }
        };
        response.setHeader('Content-Type', 'application/json');
        if (!(path in fixtures)) {
            diagnostics.push({ scenario: 'development unmatched request', path });
            response.statusCode = 404; response.end('{}'); return;
        }
        response.end(JSON.stringify(fixtures[path]));
    });
    await new Promise(resolve => host.listen(0, '127.0.0.1', resolve));
    const context = await browser.newContext();
    const page = await context.newPage();
    onPage(page);
    page.setDefaultTimeout(10000);
    try {
        await page.goto(`http://127.0.0.1:${host.address().port}/`);
        await ready(page);
        assert.equal(await page.locator('[role="tab"]').count(), 4);
        // The development client restores the remembered tab once settings are editable.
        await page.waitForFunction(() => document.getElementById('TabSettings').getAttribute('aria-selected') === 'true');
        assert.equal(await page.evaluate(() => sessionStorage.getItem('meta-tagger-dev-tab')), null);
        assert.equal(await page.locator('#PanelSettings').isVisible(), true);
        await page.locator('#TabItems').click();
        await page.locator('#ItemList .metadataTaggerItemRow').first().click();
        assert.equal(await page.locator('#InspectorHeading').textContent(), name);
        await page.locator('#TabMaintenance').click();
        await page.locator('#PreviewCleanupButton').click();
        await page.locator('#ConfirmCleanup').check();
        assert.equal(await page.locator('#ApplyCleanupButton').isEnabled(), true);
        const reloaded = page.waitForEvent('load');
        revision++;
        await reloaded;
        await ready(page);
        await page.waitForFunction(() => document.getElementById('TabMaintenance').getAttribute('aria-selected') === 'true');
        assert.equal(await page.locator('#ConfirmCleanup').isChecked(), false);
        assert.equal(await page.locator('#ConfirmCleanup').isDisabled(), true);
        assert.equal(await page.locator('#ApplyCleanupButton').isDisabled(), true);
        assert.equal(await page.locator('#InspectorHeading').textContent(), 'Select an item');
        diagnostics.push({ scenario: 'development reload against actual markup', passed: true });
    } finally {
        await new Promise(resolve => host.close(resolve));
    }
}
