#!/usr/bin/env node
/*
 * Explicit Trip Editor asset-mode smoke runner for #297 Batch 6.
 * These checks prove asset loading only; they do not prove editor CRUD behavior.
 */
import { chromium, expect } from '@playwright/test';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const isWindows = process.platform === 'win32';
const npmCommand = isWindows ? 'npm.cmd' : 'npm';
const dotnetCommand = isWindows ? 'dotnet.exe' : 'dotnet';
const localDir = path.join(rootDir, '.local');
const publishDir = path.join(localDir, 'publish-smoke');
const logsDir = path.join(localDir, 'asset-smoke');
const viteBaseUrl = 'http://localhost:5173';
const tripEditorEntryKey = 'ClientApps/trip-editor/src/main.ts';
const defaultBuildOutputDir = path.join(rootDir, 'wwwroot', 'vite', 'trip-editor');

const args = process.argv.slice(2);
const mode = parseMode(args);
const config = mode === 'built' ? null : loadTripEditorConfig();
const startedProcesses = [];

process.on('SIGINT', () => {
  void stopStartedProcesses().finally(() => process.exit(130));
});

try {
  if (mode === 'built') {
    runBuiltAssetSmoke(resolveBuildOutputDir(args));
  } else {
    fs.mkdirSync(logsDir, { recursive: true });
    printScopeBoundary();
  }

  if (mode === 'development' || mode === 'all') {
    await runDevelopmentSmoke();
  }

  if (mode === 'published' || mode === 'all') {
    await runPublishedSmoke();
  }

  console.log('\nTrip Editor asset smoke complete.');
} catch (error) {
  if (mode !== 'built') {
    throw error;
  }

  const message = error instanceof Error ? error.message : String(error);
  console.error(`[built] FAIL: ${message.slice(0, 500)}`);
  process.exitCode = 1;
} finally {
  await stopStartedProcesses();
}

// Parses the explicit smoke mode requested by the npm script or direct CLI use.
function parseMode(args) {
  const modeArg = args.find(arg => arg.startsWith('--mode='))?.split('=')[1] ?? 'all';
  if (!['built', 'development', 'published', 'all'].includes(modeArg)) {
    throw new Error(`Unsupported smoke mode "${modeArg}". Use built, development, published, or all.`);
  }

  return modeArg;
}

// Resolves an optional isolated build-output directory used by deterministic validation.
function resolveBuildOutputDir(args) {
  const outputArg = args.find(arg => arg.startsWith('--build-output='));
  return path.resolve(rootDir, outputArg?.slice('--build-output='.length) || defaultBuildOutputDir);
}

// Validates the complete manifest-owned Trip Editor asset graph without starting runtime services.
function runBuiltAssetSmoke(buildOutputDir) {
  console.log(`[built] Validating ${path.relative(rootDir, buildOutputDir) || '.'}.`);
  const manifest = readTripEditorManifest(buildOutputDir);
  validateManifestEntry(manifest, tripEditorEntryKey, buildOutputDir, new Set());
  console.log('[built] PASS: Trip Editor manifest entry and all referenced build assets are non-empty.');
}

// Reads the generated Vite manifest shared by deterministic and published asset checks.
function readTripEditorManifest(buildOutputDir) {
  const manifestPath = path.join(buildOutputDir, 'manifest.json');
  requireNonEmptyFile(manifestPath, 'Trip Editor manifest');

  try {
    return JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  } catch (error) {
    throw new Error(`Trip Editor manifest is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
}

// Recursively validates a manifest chunk and every declared import, stylesheet, and asset.
function validateManifestEntry(manifest, entryKey, buildOutputDir, visited) {
  if (visited.has(entryKey)) {
    return;
  }

  const entry = manifest[entryKey];
  if (!entry || typeof entry !== 'object') {
    throw new Error(`Trip Editor manifest entry is missing: ${entryKey}`);
  }

  visited.add(entryKey);
  if (!/\.m?js$/i.test(entry.file ?? '')) {
    throw new Error(`Manifest entry ${entryKey} does not reference a JavaScript file.`);
  }

  validateBuildReference(buildOutputDir, entry.file, `JavaScript for ${entryKey}`);
  for (const cssFile of entry.css ?? []) {
    validateBuildReference(buildOutputDir, cssFile, `CSS for ${entryKey}`);
  }

  for (const assetFile of entry.assets ?? []) {
    validateBuildReference(buildOutputDir, assetFile, `asset for ${entryKey}`);
  }

  for (const importKey of [...(entry.imports ?? []), ...(entry.dynamicImports ?? [])]) {
    validateManifestEntry(manifest, importKey, buildOutputDir, visited);
  }
}

// Rejects escaped paths before requiring a referenced artifact to be present and non-empty.
function validateBuildReference(buildOutputDir, reference, label) {
  if (typeof reference !== 'string' || !reference.trim()) {
    throw new Error(`${label} has an invalid manifest path.`);
  }

  const outputRoot = path.resolve(buildOutputDir);
  const artifactPath = path.resolve(outputRoot, reference);
  if (artifactPath === outputRoot || !artifactPath.startsWith(`${outputRoot}${path.sep}`)) {
    throw new Error(`${label} escapes the build-output directory: ${reference}`);
  }

  requireNonEmptyFile(artifactPath, `${label} (${reference})`);
}

// Requires one ordinary non-empty file and reports a bounded artifact-specific failure.
function requireNonEmptyFile(filePath, label) {
  let stats;
  try {
    stats = fs.statSync(filePath);
  } catch {
    throw new Error(`${label} is missing.`);
  }

  if (!stats.isFile() || stats.size === 0) {
    throw new Error(`${label} must be a non-empty file.`);
  }
}

// Loads the same runbook credentials used by the focused Trip Editor Playwright specs.
function loadTripEditorConfig() {
  const localConfig = readLocalManualVerification();
  const getValue = key => (process.env[key] || localConfig[key] || '').trim();
  const result = {
    devBaseUrl: (process.env.WAYFARER_ASSET_SMOKE_DEV_URL || getValue('WAYFARER_E2E_BASE_URL') || 'http://localhost:5012').replace(/\/+$/, ''),
    configuredPublishedBaseUrl: (process.env.WAYFARER_ASSET_SMOKE_PUBLISHED_URL || '').replace(/\/+$/, ''),
    publishedBaseUrl: '',
    username: getValue('WAYFARER_E2E_USERNAME'),
    password: process.env.WAYFARER_E2E_PASSWORD || localConfig.WAYFARER_E2E_PASSWORD || '',
    tripId: getValue('WAYFARER_E2E_TRIP_ID')
  };

  const missing = [];
  if (!result.username) missing.push('WAYFARER_E2E_USERNAME');
  if (!result.password) missing.push('WAYFARER_E2E_PASSWORD');
  if (!result.tripId) missing.push('WAYFARER_E2E_TRIP_ID');

  if (missing.length > 0) {
    throw new Error(
      [
        `Missing Trip Editor asset-smoke configuration: ${missing.join(', ')}.`,
        'Set WAYFARER_E2E_* environment variables or add KEY=value lines to .local/manual-verification.md.'
      ].join(' ')
    );
  }

  return result;
}

// Reads ignored local runbook values without printing secrets.
function readLocalManualVerification() {
  const filePath = path.join(localDir, 'manual-verification.md');
  if (!fs.existsSync(filePath)) {
    return {};
  }

  const result = {};
  for (const line of fs.readFileSync(filePath, 'utf8').split(/\r?\n/)) {
    const envMatch = line.match(/^\s*(?:\$env:)?(WAYFARER_E2E_[A-Z_]+)\s*=\s*['"`]?([^'"`\r\n]+)['"`]?\s*$/);
    if (envMatch) {
      result[envMatch[1]] = envMatch[2].trim();
      continue;
    }

    const labelMatch = line.match(/^\s*(Username|Password|Trip ID|ASP\.NET dev server):\s*(.+?)\s*$/i);
    if (!labelMatch) {
      continue;
    }

    const value = labelMatch[2].trim().replace(/`/g, '');
    if (/^Username$/i.test(labelMatch[1])) result.WAYFARER_E2E_USERNAME = value;
    if (/^Password$/i.test(labelMatch[1])) result.WAYFARER_E2E_PASSWORD = value;
    if (/^Trip ID$/i.test(labelMatch[1])) result.WAYFARER_E2E_TRIP_ID = value;
    if (/^ASP\.NET dev server$/i.test(labelMatch[1])) result.WAYFARER_E2E_BASE_URL = value.replace(/;.*$/, '').trim();
  }

  return result;
}

function printScopeBoundary() {
  console.log('Trip Editor asset-mode smoke scope:');
  console.log('- Development smoke proves ASP.NET Development + Vite dev-server integration only.');
  console.log('- Published smoke proves dotnet publish output and production bundle serving only.');
  console.log('- These smokes do not prove CRUD or editor workflow behavior; earlier #297 batches cover those contracts.');
}

async function runDevelopmentSmoke() {
  console.log('\n[development] Starting ASP.NET Development + Vite asset smoke.');
  await ensureDevelopmentServers();

  const browser = await chromium.launch();
  const context = await browser.newContext({ baseURL: config.devBaseUrl });
  const page = await context.newPage();
  const requestedUrls = collectRequestedUrls(page);

  try {
    await signIn(page, config.devBaseUrl);
    await page.goto(`${config.devBaseUrl}${editorPath()}`, { waitUntil: 'domcontentloaded' });
    await expectMountedEditor(page);

    await expectNonEmptyUrl(`${viteBaseUrl}/@vite/client`, 'Vite client module');
    await expectNonEmptyUrl(`${viteBaseUrl}/ClientApps/trip-editor/src/main.ts`, 'Trip Editor Vite entry module');
    expect(requestedUrls.some(url => url === `${viteBaseUrl}/@vite/client`), 'Development mode should request the Vite client.').toBeTruthy();
    expect(requestedUrls.some(url => url === `${viteBaseUrl}/ClientApps/trip-editor/src/main.ts`), 'Development mode should request the Vite entry module.').toBeTruthy();
    expect(requestedUrls.some(isTripEditorProductionAsset), 'Development mode should not require Trip Editor production bundle assets.').toBeFalsy();
    await expect(page.locator('#trip-editor-app')).not.toContainText('Trip Editor assets are missing');
    await expect(page.locator('#trip-editor-app')).not.toContainText('Trip Editor development server is not available');

    console.log('[development] PASS: ASP.NET Development shell mounted through Vite dev assets.');
  } finally {
    await browser.close();
  }
}

async function ensureDevelopmentServers() {
  if (!(await urlResponds(`${config.devBaseUrl}/Identity/Account/Login`))) {
    startProcess(dotnetCommand, ['run', '--no-launch-profile', '--urls', config.devBaseUrl], {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: config.devBaseUrl
    }, 'aspnet-development');
  } else {
    console.log(`[development] Reusing ASP.NET server at ${config.devBaseUrl}.`);
  }

  if (!(await urlResponds(`${viteBaseUrl}/@vite/client`))) {
    startProcess(npmCommand, ['run', 'dev'], {}, 'vite-development');
  } else {
    console.log(`[development] Reusing Vite server at ${viteBaseUrl}.`);
  }

  await waitForUrl(`${config.devBaseUrl}/Identity/Account/Login`, 'ASP.NET Development server');
  await waitForUrl(`${viteBaseUrl}/@vite/client`, 'Vite dev server');
}

async function runPublishedSmoke() {
  console.log('\n[published] Starting published-output production asset smoke.');
  await preparePublishedOutput();
  const publishedProcess = await startPublishedApp();

  const browser = await chromium.launch();
  const context = await browser.newContext({ baseURL: config.publishedBaseUrl });
  const page = await context.newPage();
  const requestedUrls = collectRequestedUrls(page);

  try {
    await signIn(page, config.publishedBaseUrl);
    await page.goto(`${config.publishedBaseUrl}${editorPath()}`, { waitUntil: 'domcontentloaded' });
    await expectMountedEditor(page);
    assertProcessRunning(publishedProcess, 'published Production app');

    await expectServedAsset(context, '/Wayfarer.styles.css', 'Razor scoped stylesheet');
    await expectServedAsset(context, '/vite/trip-editor/manifest.json', 'Trip Editor manifest');
    await expectDocumentAssetsServed(context, page, 'link[href^="/dist/css/"]', 'MvcFrontendKit page stylesheet');
    await expectDocumentAssetsServed(context, page, 'script[src^="/dist/js/"]', 'MvcFrontendKit page script');
    await expectManifestAssetsServed(context);

    expect(requestedUrls.some(url => url.includes('localhost:5173')), 'Published mode must not request Vite dev-server assets.').toBeFalsy();
    await expect(page.locator('#trip-editor-app')).not.toContainText('Trip Editor assets are missing');
    await expect(page.locator('#trip-editor-app')).not.toContainText('Trip Editor development server is not available');

    console.log('[published] PASS: published output served scoped CSS, /dist assets, manifest assets, and mounted the Vue editor without Vite dev-server requests.');
  } finally {
    await browser.close();
  }
}

async function preparePublishedOutput() {
  safeRemoveDirectory(publishDir);
  fs.mkdirSync(publishDir, { recursive: true });
  runCommand(dotnetCommand, ['frontend', 'build'], 'dotnet frontend build');
  runCommand(npmCommand, ['run', 'build'], 'npm run build');
  runCommand(dotnetCommand, ['publish', 'Wayfarer.csproj', '-c', 'Release', '-o', publishDir], 'dotnet publish Wayfarer.csproj -c Release');
}

async function startPublishedApp() {
  config.publishedBaseUrl = await resolvePublishedBaseUrl();
  const connectionString = process.env.ConnectionStrings__DefaultConnection || readDevelopmentConnectionString();
  const env = {
    ASPNETCORE_ENVIRONMENT: 'Production',
    ASPNETCORE_URLS: config.publishedBaseUrl,
    Logging__LogFilePath__Default: path.join(logsDir, 'published-wayfarer-.log'),
    CacheSettings__TileCacheDirectory: path.join(localDir, 'asset-smoke-cache', 'TileCache'),
    CacheSettings__ImageCacheDirectory: path.join(localDir, 'asset-smoke-cache', 'ImageCache'),
    CacheSettings__ChromeCacheDirectory: path.join(localDir, 'asset-smoke-cache', 'ChromeCache'),
    ConnectionStrings__DefaultConnection: connectionString
  };

  if (!connectionString) {
    throw new Error('Published smoke requires a usable ConnectionStrings__DefaultConnection value. Set it in the environment or appsettings.Development.json.');
  }

  const executable = isWindows ? path.join(publishDir, 'Wayfarer.exe') : dotnetCommand;
  const args = isWindows ? [] : [path.join(publishDir, 'Wayfarer.dll')];
  const publishedProcess = startProcess(executable, args, env, 'published-production', publishDir);
  await waitForUrl(`${config.publishedBaseUrl}/Identity/Account/Login`, 'published Production app', publishedProcess);
  assertProcessRunning(publishedProcess, 'published Production app');
  return publishedProcess;
}

// Uses an ephemeral port by default so published smoke cannot accidentally test an older local server.
async function resolvePublishedBaseUrl() {
  if (config.configuredPublishedBaseUrl) {
    const loginUrl = `${config.configuredPublishedBaseUrl}/Identity/Account/Login`;
    if (await urlResponds(loginUrl)) {
      throw new Error(
        [
          `Configured published smoke URL is already responding at ${loginUrl}.`,
          'Stop the existing server or choose a free WAYFARER_ASSET_SMOKE_PUBLISHED_URL; this smoke must launch and verify the just-published output.'
        ].join(' ')
      );
    }

    console.log(`[published] Using configured published smoke URL ${config.configuredPublishedBaseUrl}.`);
    return config.configuredPublishedBaseUrl;
  }

  const port = await allocateFreeLocalhostPort();
  const baseUrl = `http://127.0.0.1:${port}`;
  console.log(`[published] Using allocated published smoke URL ${baseUrl}.`);
  return baseUrl;
}

function allocateFreeLocalhostPort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen({ host: '127.0.0.1', port: 0 }, () => {
      const address = server.address();
      server.close(error => {
        if (error) {
          reject(error);
          return;
        }

        if (!address || typeof address === 'string') {
          reject(new Error('Failed to allocate a localhost port for published smoke.'));
          return;
        }

        resolve(address.port);
      });
    });
  });
}

// Reuses the local development database string only as launch configuration; the app still runs non-Development.
function readDevelopmentConnectionString() {
  const filePath = path.join(rootDir, 'appsettings.Development.json');
  if (!fs.existsSync(filePath)) {
    return '';
  }

  const json = fs.readFileSync(filePath, 'utf8').replace(/\/\/.*$/gm, '');
  return JSON.parse(json).ConnectionStrings?.DefaultConnection ?? '';
}

async function signIn(page, baseUrl) {
  await page.goto(`${baseUrl}/Identity/Account/Login?ReturnUrl=${encodeURIComponent(editorPath())}`, { waitUntil: 'domcontentloaded' });
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.includes('/Identity/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

async function expectMountedEditor(page) {
  const app = page.locator('#trip-editor-app');
  await expect(app).toBeVisible();
  await expect(app.locator('.trip-editor-workspace')).toBeVisible();
  await expect(app.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
}

function editorPath() {
  return `/User/Trip/Edit/${config.tripId}`;
}

function collectRequestedUrls(page) {
  const requestedUrls = [];
  page.on('request', request => requestedUrls.push(request.url()));
  return requestedUrls;
}

function isTripEditorProductionAsset(url) {
  return /\/vite\/trip-editor\/(?:assets\/|manifest\.json)/i.test(url);
}

async function expectDocumentAssetsServed(context, page, selector, label) {
  const urls = await page.locator(selector).evaluateAll(elements => elements.map(element => element.getAttribute('href') || element.getAttribute('src')).filter(Boolean));
  expect(urls.length, `${label} should be referenced by the published page.`).toBeGreaterThan(0);
  for (const url of urls) {
    await expectServedAsset(context, url, label);
  }
}

async function expectManifestAssetsServed(context) {
  const buildOutputDir = path.join(publishDir, 'wwwroot', 'vite', 'trip-editor');
  const manifest = readTripEditorManifest(buildOutputDir);
  const entry = manifest[tripEditorEntryKey];
  expect(entry, 'Published manifest should contain the Trip Editor entry script.').toBeTruthy();

  await expectServedAsset(context, `/vite/trip-editor/${entry.file}`, 'manifest Trip Editor script');
  for (const css of entry.css ?? []) {
    await expectServedAsset(context, `/vite/trip-editor/${css}`, 'manifest Trip Editor stylesheet');
  }
}

async function expectServedAsset(context, assetUrl, label) {
  const response = await context.request.get(assetUrl);
  expect(response.status(), `${label} ${assetUrl} should return 200.`).toBe(200);
  const body = await response.body();
  expect(body.length, `${label} ${assetUrl} should be non-empty.`).toBeGreaterThan(0);
}

async function expectNonEmptyUrl(url, label) {
  const response = await fetch(url);
  expect(response.status, `${label} should return 200.`).toBe(200);
  const body = await response.text();
  expect(body.length, `${label} should be non-empty.`).toBeGreaterThan(0);
}

function runCommand(command, args, label) {
  console.log(`[published] ${label}`);
  const normalized = normalizeCommand(command, args);
  const result = spawnSync(normalized.command, normalized.args, {
    cwd: rootDir,
    env: process.env,
    stdio: 'inherit',
    shell: false
  });

  if (result.status !== 0) {
    throw new Error(`${label} failed with exit code ${result.status}.`);
  }
}

function startProcess(command, args, extraEnv, name, cwd = rootDir) {
  const logPath = path.join(logsDir, `${name}.log`);
  const logStream = fs.createWriteStream(logPath, { flags: 'a' });
  console.log(`[server] Starting ${name}; log: ${path.relative(rootDir, logPath)}`);
  const normalized = normalizeCommand(command, args);
  const child = spawn(normalized.command, normalized.args, {
    cwd,
    env: { ...process.env, ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: false,
    windowsHide: true
  });

  const processInfo = {
    child,
    name,
    logStream,
    logPath,
    stdoutTail: '',
    stderrTail: ''
  };

  child.stdout.on('data', chunk => {
    processInfo.stdoutTail = appendTail(processInfo.stdoutTail, chunk);
  });
  child.stderr.on('data', chunk => {
    processInfo.stderrTail = appendTail(processInfo.stderrTail, chunk);
  });
  child.stdout.pipe(logStream);
  child.stderr.pipe(logStream);
  startedProcesses.push(processInfo);
  return processInfo;
}

// Runs Windows .cmd shims through cmd.exe so npm scripts launch reliably from Node.
function normalizeCommand(command, args) {
  if (isWindows && command.toLowerCase().endsWith('.cmd')) {
    return { command: 'cmd.exe', args: ['/d', '/s', '/c', command, ...args] };
  }

  return { command, args };
}

async function waitForUrl(url, label, processInfo = null) {
  const deadline = Date.now() + 120_000;
  while (Date.now() < deadline) {
    if (processInfo) {
      assertProcessRunning(processInfo, label);
    }

    if (await urlResponds(url)) {
      console.log(`[server] ${label} is responding at ${url}.`);
      return;
    }

    await delay(1000);
  }

  throw new Error(`${label} did not respond at ${url} within 120 seconds.`);
}

async function urlResponds(url) {
  try {
    const response = await fetch(url, { redirect: 'manual' });
    return response.status >= 200 && response.status < 500;
  } catch {
    return false;
  }
}

async function stopStartedProcesses() {
  for (const { child, name, logStream } of startedProcesses.splice(0).reverse()) {
    if (child.exitCode === null && child.pid) {
      console.log(`[server] Stopping ${name}.`);
      if (isWindows) {
        spawnSync('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore' });
      } else {
        child.kill('SIGTERM');
      }
    }

    logStream.end();
  }
}

function assertProcessRunning(processInfo, label) {
  if (processInfo.child.exitCode === null && processInfo.child.signalCode === null) {
    return;
  }

  throw new Error(
    [
      `${label} exited before smoke verification completed.`,
      `Exit code: ${processInfo.child.exitCode ?? 'none'}; signal: ${processInfo.child.signalCode ?? 'none'}.`,
      `Log: ${path.relative(rootDir, processInfo.logPath)}`,
      formatTail('stdout', processInfo.stdoutTail),
      formatTail('stderr', processInfo.stderrTail)
    ].join('\n')
  );
}

function appendTail(current, chunk) {
  const next = `${current}${chunk.toString('utf8')}`;
  return next.length > 8000 ? next.slice(-8000) : next;
}

function formatTail(label, value) {
  const trimmed = value.trim();
  return trimmed ? `${label} tail:\n${trimmed}` : `${label} tail: <empty>`;
}

function safeRemoveDirectory(targetDir) {
  const resolved = path.resolve(targetDir);
  const allowedRoot = path.resolve(localDir);
  if (resolved === allowedRoot || !resolved.startsWith(`${allowedRoot}${path.sep}`)) {
    throw new Error(`Refusing to remove directory outside .local: ${resolved}`);
  }

  fs.rmSync(resolved, { recursive: true, force: true });
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
