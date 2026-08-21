import { ONBOARDING_KEY, completeOnboarding } from './onboarding-policy.js';
import { createI18n } from '../shared/i18n.js';

const status = document.querySelector('#status');
const details = document.querySelector('#detailed-guide');
const detailsButton = document.querySelector('#details');
const version = chrome.runtime.getManifest().version;
const i18n = await createI18n();

async function finish(never) {
  const stored = await chrome.storage.local.get(ONBOARDING_KEY);
  await chrome.storage.local.set({
    [ONBOARDING_KEY]: completeOnboarding(stored[ONBOARDING_KEY], version, never)
  });
  status.textContent = i18n.t(never ? 'welcomeNeverSaved' : 'welcomeSaved');
}

document.querySelector('#complete').addEventListener('click', () => finish(false).catch(showError));
document.querySelector('#never').addEventListener('click', () => finish(true).catch(showError));
detailsButton.addEventListener('click', () => {
  details.hidden = false;
  detailsButton.setAttribute('aria-expanded', 'true');
  detailsButton.hidden = true;
  details.focus();
});

function showError(error) {
  console.error('[Audio Source Mixer] onboarding state save failed', error);
  status.textContent = i18n.t('welcomeSaveFailed', error instanceof Error ? error.message : String(error));
}
