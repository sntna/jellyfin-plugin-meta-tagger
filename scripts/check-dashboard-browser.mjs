#!/usr/bin/env node
import { createRequire } from 'node:module';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdir, writeFile, rm } from 'node:fs/promises';
import { resolve } from 'node:path';
import { installedScenarios, developmentContract, ready } from './tests/dashboard.browser.mjs';

const require = createRequire(new URL('./browser/package.json', import.meta.url));
const { chromium } = require('playwright');
const root = resolve(new URL('..', import.meta.url).pathname);
const directory = process.argv[2];
if (!directory || process.argv.length !== 3) {
    console.error('Usage: node scripts/check-dashboard-browser.mjs <generated-directory|--development-only>');
    process.exit(2);
}
const output = resolve(root, 'artifacts/dashboard-browser');
await mkdir(output, { recursive: true });
for (const name of ['result.json', 'diagnostics.json', 'browser-errors.json', 'failure.png']) {
    await rm(resolve(output, name), { force: true });
}
const diagnostics = [];
const errors = [];
const started = performance.now();
const result = { head: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim(),
    browserDependency: `playwright ${require('playwright/package.json').version}`,
    passed: false, mode: directory === '--development-only' ? 'development' : 'installed-package' };
let browser;
let currentPage;
let fixture;
function redact(text) {
    let safe = String(text).replace(/https?:\/\/[^\s"')]+/g, '[url omitted]');
    for (const secret of Object.values(fixture?.credentials || {})) safe = safe.replaceAll(secret, '[credential omitted]');
    return safe.slice(0, 1200);
}
function observe(page) {
    currentPage = page;
    page.on('pageerror', error => { if (errors.length < 20) errors.push({ type: 'pageerror', name: error.name }); });
    page.on('console', message => {
        // Generated media has no artwork. Jellyfin's image 404 is the expected fallback.
        const location = message.location().url;
        if (message.type() === 'error' && !/\/Items\/[^/]+\/Images\//.test(location) && errors.length < 20) {
            let path;
            try { path = new URL(location).pathname.replace(/\/Users\/.*/, '/Users/[omitted]'); } catch { path = '[no resource path]'; }
            errors.push({ type: 'console-error', path, status: Number(message.text().match(/status of (\d+)/)?.[1]) || undefined });
        }
    });
}
try {
    browser = await chromium.launch();
    if (directory !== '--development-only') {
        // Python reuses all generated-container identity checks. No URL option exists.
        fixture = JSON.parse(execFileSync('python3', ['scripts/browser-fixture.py', resolve(directory)], {
            cwd: root, encoding: 'utf8', timeout: 240000, maxBuffer: 1024 * 1024, stdio: ['pipe', 'pipe', 'pipe'] }));
        Object.assign(result, { packageSha256: fixture.packageSha256, pluginVersion: fixture.pluginVersion,
            serverVersion: fixture.serverVersion });
        const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
        const page = await context.newPage();
        currentPage = page;
        page.setDefaultTimeout(15000);
        page.setDefaultNavigationTimeout(30000);
        await page.goto(fixture.base + '/web/index.html#/login');
        await page.locator('#txtManualName').fill(fixture.credentials.username);
        await page.locator('#txtManualPassword').fill(fixture.credentials.password);
        await page.getByRole('button', { name: 'Sign In', exact: true }).click();
        await page.waitForURL(url => !url.hash.includes('login'), { timeout: 30000 });
        await page.goto(fixture.base + '/web/index.html#/configurationpage?name=Meta%20Tagger');
        await ready(page);
        // Starting the dashboard can abort Jellyfin home-screen requests. Record
        // runtime errors once the installed dashboard has finished loading.
        observe(page);
        const scripts = await page.locator('script').allTextContents();
        if (scripts.some(script => script.includes('__meta_tagger_dev'))) {
            throw new Error('Installed-package verification must not contain the development client');
        }
        await installedScenarios(page, diagnostics);
        await context.close();
    }
    await developmentContract(browser, diagnostics, observe);
    if (errors.length) throw new Error('Browser reported script or console errors; see browser-errors.json');
    result.passed = true;
} catch (error) {
    // Never save HTML, traces, network headers, cookies, localStorage, or login screenshots.
    result.failure = error.name;
    result.diagnostic = redact(error.message);
    if (currentPage && !currentPage.isClosed() && await currentPage.locator('#MetaTaggerConfigPage').isVisible().catch(() => false)) {
        await currentPage.locator('#MetaTaggerConfigPage').screenshot({ path: resolve(output, 'failure.png'), timeout: 5000 }).catch(() => {});
    }
    process.exitCode = 1;
} finally {
    await browser?.close();
    if (fixture) {
        const restore = spawnSync('python3', ['scripts/browser-fixture.py', resolve(directory), '--restore'], {
            cwd: root, input: JSON.stringify(fixture.original), encoding: 'utf8', timeout: 120000 });
        if (restore.status !== 0) {
            result.restoreFailed = true;
            result.passed = false;
            process.exitCode = 1;
        }
    }
    result.durationSeconds = Number(((performance.now() - started) / 1000).toFixed(2));
    await writeFile(resolve(output, 'result.json'), JSON.stringify(result, null, 2) + '\n');
    await writeFile(resolve(output, 'diagnostics.json'), JSON.stringify(diagnostics.slice(0, 300), null, 2) + '\n');
    await writeFile(resolve(output, 'browser-errors.json'), JSON.stringify(errors, null, 2) + '\n');
    console.log(`${result.passed ? 'PASS' : 'FAIL'} dashboard browser ${result.mode} (${result.durationSeconds}s). Safe evidence: artifacts/dashboard-browser/`);
}
