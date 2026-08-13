import { ONBOARDING_KEY, completeOnboarding } from './onboarding-policy.js';

const status = document.querySelector('#status');
const details = document.querySelector('#detailed-guide');
const detailsButton = document.querySelector('#details');
const version = chrome.runtime.getManifest().version;

async function finish(never) {
  const stored = await chrome.storage.local.get(ONBOARDING_KEY);
  await chrome.storage.local.set({
    [ONBOARDING_KEY]: completeOnboarding(stored[ONBOARDING_KEY], version, never)
  });
  status.textContent = never ? '已关闭后续引导提示。' : '设置已保存，可以关闭此页面并点击扩展图标。';
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
  console.error('[Audio Source Mixer] 无法保存引导状态', error);
  status.textContent = `无法保存：${error instanceof Error ? error.message : String(error)}`;
}
