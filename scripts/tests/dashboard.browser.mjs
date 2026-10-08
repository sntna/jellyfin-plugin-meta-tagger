import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';

export async function ready(page) {
    await page.locator('#MetaTaggerConfigPage').waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelector('#SettingsFeedback')?.textContent === 'Settings loaded.');
}

export async function geometry(page, width, diagnostics) {
    const result = await page.evaluate(() => {
        const box = id => {
            const { x, y, width, height } = document.getElementById(id).getBoundingClientRect();
            return { x, y, width, height };
        };
        return { viewport: innerWidth, documentWidth: document.documentElement.scrollWidth,
            pageWidth: document.getElementById('MetaTaggerConfigPage').scrollWidth,
            pageClientWidth: document.getElementById('MetaTaggerConfigPage').clientWidth,
            sources: box('SourceOptions'), automation: box('AutomationOptions'), items: box('ItemTypeOptions') };
    });
    diagnostics.push({ scenario: 'settings geometry', width, ...result });
    assert(result.documentWidth <= result.viewport + 1, `Horizontal document overflow at ${width}px`);
    assert(result.pageWidth <= result.pageClientWidth + 1, `Horizontal dashboard overflow at ${width}px`);
    if (width > 760) {
        assert(Math.abs(result.sources.y - result.automation.y) < 2, `Settings must have two columns at ${width}px`);
        assert(result.automation.x >= result.sources.x + result.sources.width - 1, 'Settings columns must not overlap');
    } else {
        assert(Math.abs(result.sources.x - result.automation.x) < 2, `Settings must stack at ${width}px`);
        assert(result.automation.y >= result.items.y + result.items.height - 1, 'Automation must stack below item types');
    }
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

export async function installedScenarios(page, diagnostics) {
    for (const width of [1440, 761, 760, 759, 390]) {
        await page.setViewportSize({ width, height: 1000 });
        await geometry(page, width, diagnostics);
        for (const view of [
            ['OpenInspectButton', 'PanelInspect', 'BrowseHeading', 'BackToReviewButton'],
            ['OpenRunsButton', 'PanelRuns', 'HistoryHeading', 'HistoryBackButton'],
            ['OpenMaintenanceButton', 'PanelMaintenance', 'MaintenanceHeading', 'MaintenanceBackButton']
        ]) {
            const [entry, panel, heading, back] = view;
            await keyboardControl(page, entry, diagnostics);
            await page.keyboard.press('Enter');
            await page.locator('#' + panel).waitFor({ state: 'visible' });
            await currentFocus(page, heading, diagnostics);
            if (panel === 'PanelInspect') {
                await page.locator('#ItemList .metadataTaggerItemRow').first().waitFor();
                await page.locator('#ItemList .metadataTaggerItemRow').filter({ hasText: 'Generated browser fixture' }).first().click();
                await page.locator('#PreviewItemButton').click();
                await page.waitForFunction(() => document.getElementById('InspectorDetails').childElementCount > 0);
                assert((await page.locator('#ItemList').textContent()).includes('Generated browser fixture'), 'Browse must contain generated long labels');
            } else if (panel === 'PanelRuns') {
                await page.locator('#RunList .metadataTaggerRunRow').first().waitFor();
                await page.locator('#RunList .metadataTaggerRunRow').first().click();
                await page.waitForFunction(() => document.getElementById('RunDetails').childElementCount > 0);
            } else {
                await page.locator('#PreviewCleanupButton').click();
                await page.waitForFunction(() => document.getElementById('CleanupChanges').childElementCount > 0);
                // Never authorize removal. The generated preview is enough to check populated layout.
            }
            await noOverflow(page, `${panel} at ${width}px`, diagnostics);
            await page.locator('#' + back).focus();
            await page.keyboard.press('Enter');
            await currentFocus(page, entry, diagnostics);
            await page.keyboard.press('Enter');
            await currentFocus(page, heading, diagnostics);
            await page.keyboard.press('Escape');
            await currentFocus(page, entry, diagnostics);
        }
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
        assert.equal(await page.locator('[role="tab"]').count(), 0);
        assert.equal(await page.evaluate(() => sessionStorage.getItem('meta-tagger-dev-tab')), null);
        assert.equal(await page.locator('#PanelOverview').isVisible(), true);
        await page.locator('#OpenInspectButton').click();
        await page.locator('#ItemList .metadataTaggerItemRow').first().click();
        assert.equal(await page.locator('#InspectorHeading').textContent(), name);
        await page.locator('#OpenMaintenanceButton').click();
        await page.locator('#PreviewCleanupButton').click();
        await page.locator('#ConfirmCleanup').check();
        assert.equal(await page.locator('#ApplyCleanupButton').isEnabled(), true);
        const reloaded = page.waitForEvent('load');
        revision++;
        await reloaded;
        await ready(page);
        assert.equal(await page.locator('#PanelOverview').isVisible(), true, 'Development reload returns to workspace');
        assert.equal(await page.locator('#ConfirmCleanup').isChecked(), false);
        assert.equal(await page.locator('#ConfirmCleanup').isDisabled(), true);
        assert.equal(await page.locator('#ApplyCleanupButton').isDisabled(), true);
        assert.equal(await page.locator('#InspectorHeading').textContent(), 'Select an item');
        diagnostics.push({ scenario: 'development reload against actual markup', passed: true });
    } finally {
        await new Promise(resolve => host.close(resolve));
    }
}
