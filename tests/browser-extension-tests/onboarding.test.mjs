import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  completeOnboarding,
  installationPlan,
  needsOnboarding,
  normalizeOnboardingState
} from '../../src/AudioSourceMixer.BrowserExtension/onboarding/onboarding-policy.js';

test('fresh install opens the extension-owned welcome page once', () => {
  const plan = installationPlan({ reason: 'install' }, null, '1.0.0');
  assert.equal(plan.openWelcome, true);
  assert.equal(plan.state.status, 'pending');
  assert.equal(needsOnboarding(plan.state), true);
});

test('browser update and development reload never force onboarding open', () => {
  for (const reason of ['update', 'chrome_update', 'shared_module_update']) {
    const plan = installationPlan({ reason }, { status: 'pending' }, '1.0.0');
    assert.equal(plan.openWelcome, false);
  }
  assert.equal(installationPlan(undefined, undefined, '1.0.0').openWelcome, false);
});

test('completed and never states survive installs and schema normalization', () => {
  const completed = completeOnboarding({}, '1.0.0');
  assert.deepEqual(normalizeOnboardingState(completed), completed);
  assert.equal(installationPlan({ reason: 'install' }, completed, '1.0.0').openWelcome, false);
  const never = completeOnboarding(completed, '1.0.0', true);
  assert.equal(never.status, 'never');
  assert.equal(needsOnboarding(never), false);
  assert.equal(installationPlan({ reason: 'install' }, never, '1.0.0').openWelcome, false);
});

test('legacy or malformed state migrates to a safe pending schema', () => {
  assert.deepEqual(normalizeOnboardingState({ status: 'unexpected', completedVersion: 12 }), {
    schemaVersion: 1, status: 'pending', completedVersion: null
  });
});

test('welcome page keeps the detailed guide local and uses packaged MV3 scripts', async () => {
  const base = new URL('../../src/AudioSourceMixer.BrowserExtension/onboarding/', import.meta.url);
  const html = await readFile(new URL('welcome.html', base), 'utf8');
  const script = await readFile(new URL('welcome.js', base), 'utf8');
  assert.match(html, /id="detailed-guide"[\s\S]*hidden/);
  assert.match(html, /<script type="module" src="welcome\.js"><\/script>/);
  assert.doesNotMatch(html, /<script(?:\s[^>]*)?>\s*[^<\s]/);
  assert.match(script, /details\.hidden = false/);
  assert.doesNotMatch(script, /openOptionsPage|eval\s*\(|new Function/);
});
