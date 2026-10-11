// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

/**
 * capture.cjs
 *
 * Drives a browser through the portal screenshots that Capture-Screenshots.ps1 hands it (#582). The PowerShell side
 * validates the manifest, works out each file name and checks every image's size afterwards; this side only signs in,
 * sets each theme, follows each entry's steps, waits for the page to settle and takes the screenshot.
 *
 * Terminal screenshots arrive with their screen already worked out (TerminalScreen.ps1); this draws each one in a
 * terminal window (terminal-window.cjs) and captures it with a transparent background.
 *
 * Usage: node capture.cjs <run-config.json> <results.json>
 *   The run config carries the portal's base URL, the output folder, the font folder, the profiles and the jobs (one
 *   per file). The sign-in account arrives in JIM_CAPTURE_USERNAME and JIM_CAPTURE_PASSWORD, never on the command
 *   line or on disk. JIM_PLAYWRIGHT_MODULE names the playwright package to load (see scripts/JimPlaywright.ps1).
 */

const fs = require('fs');
const path = require('path');
const { chromium } = require(process.env.JIM_PLAYWRIGHT_MODULE);
const { terminalWindowHtml } = require('./terminal-window.cjs');

const [configPath, resultsPath] = process.argv.slice(2);
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const baseOrigin = new URL(config.baseUrl).origin;

// Navigation groups the portal remembers per user; every published screenshot shows them all expanded.
const NAV_GROUPS = ['users', 'groups', 'activity', 'dashboard', 'admin'];
// What the portal shows while it is still loading. Determinate progress bars are content (a running Activity) and
// are deliberately not in this list.
const LOADING = '.mud-progress-indeterminate, .mud-skeleton';
const STEP_TIMEOUT_MS = 30000;
const SETTLE_MS = 750;

function locate(page, locator) {
  if (locator.text !== undefined) return page.getByText(locator.text, { exact: !!locator.exact }).first();
  if (locator.role !== undefined) return page.getByRole(locator.role, { name: locator.name, exact: !!locator.exact }).first();
  return page.locator(locator.selector).first();
}

async function runStep(page, step) {
  const [action] = Object.keys(step);
  const value = step[action];
  switch (action) {
    case 'goto': await page.goto(baseOrigin + value, { waitUntil: 'networkidle' }); break;
    case 'click': await locate(page, value).click({ timeout: STEP_TIMEOUT_MS }); break;
    case 'fill':
      // Search boxes filter after a 300ms debounce (SearchField), so let it fire before the next step looks.
      await locate(page, value).fill(value.value, { timeout: STEP_TIMEOUT_MS });
      await page.waitForTimeout(600);
      await page.waitForLoadState('networkidle');
      break;
    case 'waitFor': await locate(page, value).waitFor({ state: 'visible', timeout: STEP_TIMEOUT_MS }); break;
    case 'scroll': await locate(page, value).scrollIntoViewIfNeeded({ timeout: STEP_TIMEOUT_MS }); break;
    case 'wait': await page.waitForTimeout(value); break;
    default: throw new Error(`Unknown step '${action}'.`);
  }
}

async function waitUntilSettled(page) {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(selector => {
    const shown = el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
    const loading = [...document.querySelectorAll(selector)].some(shown);
    const reconnect = document.getElementById('components-reconnect-modal');
    const reconnecting = !!reconnect && shown(reconnect) && getComputedStyle(reconnect).display !== 'none';
    return !loading && !reconnecting;
  }, LOADING, { timeout: STEP_TIMEOUT_MS });
  await page.waitForTimeout(SETTLE_MS);
}

async function signIn(page, username, password) {
  await page.goto(baseOrigin + '/', { waitUntil: 'networkidle' });
  if (new URL(page.url()).origin === baseOrigin) return; // already signed in, or the portal needs no sign-in
  await page.locator('#username').fill(username);
  await page.locator('#password').fill(password);
  await Promise.all([
    page.waitForURL(url => url.origin === baseOrigin, { timeout: STEP_TIMEOUT_MS }),
    page.locator('#kc-login').click(),
  ]);
  await page.waitForLoadState('networkidle');
  if (new URL(page.url()).origin !== baseOrigin) throw new Error('Sign-in did not return to the portal; check the account.');
}

async function newContext(browser, theme, profile) {
  const context = await browser.newContext({
    viewport: { width: profile.ViewportWidth, height: profile.ViewportHeight },
    deviceScaleFactor: profile.DeviceScaleFactor,
    colorScheme: theme,
    locale: 'en-GB',
    timezoneId: 'Europe/London',
  });
  // Set the preferences a user would have left, before any of the portal's own script reads them.
  await context.addInitScript(({ origin, dark, groups }) => {
    if (location.origin !== origin) return;
    localStorage.setItem('jim_darkMode', dark ? 'true' : 'false');
    for (const group of groups) localStorage.setItem('jim_navGroup_' + group, 'true');
  }, { origin: baseOrigin, dark: theme === 'dark', groups: NAV_GROUPS });
  return context;
}

// Draws each terminal job's screen in a window and captures it. Nothing here signs in: the screen was recorded from
// a real session before the browser started.
async function captureTerminalJobs(browser, jobs, results) {
  for (const job of jobs) {
    const profile = config.profiles[job.profile];
    const target = path.join(config.outputDir, job.fileName);
    const started = Date.now();
    const context = await browser.newContext({
      viewport: { width: profile.ViewportWidth, height: profile.ViewportHeight },
      deviceScaleFactor: profile.DeviceScaleFactor,
    });
    try {
      const page = await context.newPage();
      const htmlPath = path.join(path.dirname(configPath), `${job.fileName}.html`);
      fs.writeFileSync(htmlPath, terminalWindowHtml({ rows: job.screen, fontsDir: config.fontsDir }));
      await page.goto('file://' + htmlPath);
      await page.evaluate(() => document.fonts.ready);
      // A missing font falls back silently to another monospace face, which would change every row's width.
      const unloaded = await page.evaluate(() => [...document.fonts].filter(f => f.status !== 'loaded').map(f => f.family));
      if (unloaded.length > 0) throw new Error(`Font not loaded: ${[...new Set(unloaded)].join(', ')}`);
      await page.screenshot({ path: target, omitBackground: !!profile.Transparent });
      results.push({ fileName: job.fileName, ok: true, milliseconds: Date.now() - started });
    } catch (error) {
      results.push({ fileName: job.fileName, ok: false, error: String(error.message).split('\n')[0] });
    } finally {
      await context.close();
    }
  }
}

(async () => {
  const username = process.env.JIM_CAPTURE_USERNAME;
  const password = process.env.JIM_CAPTURE_PASSWORD;
  const launchOptions = { headless: true };
  if (process.env.JIM_PLAYWRIGHT_CHROMIUM) launchOptions.executablePath = process.env.JIM_PLAYWRIGHT_CHROMIUM;
  const browser = await chromium.launch(launchOptions);
  const results = [];

  await captureTerminalJobs(browser, config.jobs.filter(job => job.kind === 'terminal'), results);

  // One signed-in context per theme and profile: the theme is set before the first page loads, and density is fixed
  // for a context's lifetime.
  const groups = new Map();
  for (const job of config.jobs.filter(job => job.kind !== 'terminal')) {
    const key = `${job.theme}|${job.profile}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(job);
  }

  for (const jobs of groups.values()) {
    const { theme, profile: profileName } = jobs[0];
    const profile = config.profiles[profileName];
    const context = await newContext(browser, theme, profile);
    const page = await context.newPage();
    try {
      await signIn(page, username, password);
    } catch (error) {
      for (const job of jobs) results.push({ fileName: job.fileName, ok: false, error: `Sign-in failed: ${error.message}` });
      await context.close();
      continue;
    }

    for (const job of jobs) {
      const target = path.join(config.outputDir, job.fileName);
      const started = Date.now();
      try {
        for (const step of job.steps) await runStep(page, step);
        await waitUntilSettled(page);
        await page.evaluate(() => document.activeElement && document.activeElement.blur && document.activeElement.blur());
        await page.mouse.move(profile.ViewportWidth - 1, profile.ViewportHeight - 1);
        await page.waitForTimeout(150);
        await page.screenshot({ path: target, animations: 'disabled', caret: 'hide', omitBackground: !!profile.Transparent });
        results.push({ fileName: job.fileName, ok: true, milliseconds: Date.now() - started });
      } catch (error) {
        // Keep what the page looked like when it failed, to see why, outside the publishable output.
        const failedDir = path.join(config.outputDir, '_failed');
        fs.mkdirSync(failedDir, { recursive: true });
        await page.screenshot({ path: path.join(failedDir, job.fileName) }).catch(() => {});
        results.push({ fileName: job.fileName, ok: false, error: String(error.message).split('\n')[0] });
      }
    }
    await context.close();
  }

  await browser.close();
  fs.writeFileSync(resultsPath, JSON.stringify(results, null, 2));
})().catch(error => {
  console.error(error);
  process.exit(1);
});
