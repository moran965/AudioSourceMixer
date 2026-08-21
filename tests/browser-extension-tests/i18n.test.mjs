import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, access } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { normalizeLanguage } from '../../src/AudioSourceMixer.BrowserExtension/shared/i18n.js';

const extensionRoot = new URL('../../src/AudioSourceMixer.BrowserExtension/', import.meta.url);

async function read(relativePath) {
  return readFile(new URL(relativePath, extensionRoot), 'utf8');
}

async function messages(locale) {
  return JSON.parse(await read(`_locales/${locale}/messages.json`));
}

test('extension locales have identical, non-empty message keys', async () => {
  const [english, chinese] = await Promise.all([messages('en'), messages('zh_CN')]);
  const englishKeys = Object.keys(english).sort();
  const chineseKeys = Object.keys(chinese).sort();
  assert.deepEqual(englishKeys, chineseKeys);
  assert.ok(englishKeys.length >= 100);
  for (const key of englishKeys) {
    assert.equal(typeof english[key].message, 'string', `English ${key}`);
    assert.equal(typeof chinese[key].message, 'string', `Chinese ${key}`);
    assert.ok(english[key].message.trim(), `English ${key}`);
    assert.ok(chinese[key].message.trim(), `Chinese ${key}`);
  }
});

test('manifest is localized and every declared icon exists', async () => {
  const manifest = JSON.parse(await read('manifest.json'));
  const chinese = await messages('zh_CN');
  assert.equal(manifest.default_locale, 'zh_CN');
  for (const value of [manifest.name, manifest.description, manifest.action.default_title]) {
    const match = /^__MSG_([A-Za-z0-9_]+)__$/.exec(value);
    assert.ok(match, `${value} is not a localized manifest value`);
    assert.ok(chinese[match[1]]?.message, `${match[1]} is missing`);
  }
  for (const path of new Set([...Object.values(manifest.icons), ...Object.values(manifest.action.default_icon)]))
    await access(fileURLToPath(new URL(path, extensionRoot)));
});

test('localized pages reference defined keys and contain no inline scripts', async () => {
  const english = await messages('en');
  for (const path of ['onboarding/welcome.html', 'output-authorization/authorize.html']) {
    const html = await read(path);
    const keys = [...html.matchAll(/data-i18n(?:-title|-aria-label)?="([A-Za-z0-9_]+)"/g)].map((match) => match[1]);
    assert.ok(keys.length > 0, `${path} has no localized elements`);
    for (const key of keys) assert.ok(english[key]?.message, `${path} references missing key ${key}`);
    assert.doesNotMatch(html, /<script(?![^>]*\bsrc=)[^>]*>/iu);
  }
});

test('JavaScript localization calls reference defined keys', async () => {
  const english = await messages('en');
  const paths = [
    'onboarding/welcome.js',
    'output-authorization/authorize.js',
    'service-worker/service-worker.js'
  ];
  for (const path of paths) {
    const code = await read(path);
    const keys = [
      ...code.matchAll(/\bi18n\.t\(['"]([A-Za-z0-9_]+)['"]/g),
      ...code.matchAll(/\blocalizedMessage\(['"]([A-Za-z0-9_]+)['"]/g)
    ].map((match) => match[1]);
    for (const key of keys) assert.ok(english[key]?.message, `${path} references missing key ${key}`);
  }
});

test('extension language defaults by browser UI and only accepts supported values', () => {
  assert.equal(normalizeLanguage(undefined, 'zh-TW'), 'zh-CN');
  assert.equal(normalizeLanguage(undefined, 'en-GB'), 'en-US');
  assert.equal(normalizeLanguage('zh-CN', 'en-US'), 'zh-CN');
  assert.equal(normalizeLanguage('en-US', 'zh-CN'), 'en-US');
  assert.equal(normalizeLanguage('fr-FR', 'fr-FR'), 'en-US');
});

test('runtime scripts use stable protocol codes instead of localized status text', async () => {
  const paths = [
    'service-worker/service-worker.js',
    'offscreen/offscreen.js',
    'output-authorization/mappings.js'
  ];
  for (const path of paths) {
    const code = await read(path);
    assert.doesNotMatch(code, /[\p{Script=Han}]/u, `${path} contains a localized protocol string`);
  }
  const offscreen = await read('offscreen/offscreen.js');
  assert.match(offscreen, /outputStatus = 'setSinkFailed'/);
  assert.match(offscreen, /error = 'set-sink-failed'/);
});
