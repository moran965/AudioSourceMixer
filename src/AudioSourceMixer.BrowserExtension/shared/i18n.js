export const UI_LANGUAGE_KEY = 'uiLanguage';
export const CHINESE_LANGUAGE = 'zh-CN';
export const ENGLISH_LANGUAGE = 'en-US';

export function normalizeLanguage(value, browserLanguage = chrome.i18n.getUILanguage()) {
  if (value === CHINESE_LANGUAGE || value === ENGLISH_LANGUAGE) return value;
  return String(browserLanguage || '').toLowerCase().startsWith('zh') ? CHINESE_LANGUAGE : ENGLISH_LANGUAGE;
}

export async function createI18n(onChanged = null) {
  const stored = await chrome.storage.local.get(UI_LANGUAGE_KEY);
  let language = normalizeLanguage(stored[UI_LANGUAGE_KEY]);
  let messages = await loadMessages(language);

  const api = {
    get language() { return language; },
    t(key, substitutions = []) {
      const entry = messages[key];
      if (!entry?.message) return chrome.i18n.getMessage(key, substitutions) || 'Text unavailable';
      const values = Array.isArray(substitutions) ? substitutions : [substitutions];
      return entry.message.replace(/\$(\d+)/g, (_, index) => String(values[Number(index) - 1] ?? '')).replace(/\$\$/g, '$');
    },
    async setLanguage(value) {
      language = normalizeLanguage(value);
      messages = await loadMessages(language);
      await chrome.storage.local.set({ [UI_LANGUAGE_KEY]: language });
      applyDocument(api);
      if (onChanged) await onChanged(api);
    }
  };

  applyDocument(api);
  const selector = document.querySelector('#languageSelect');
  if (selector) {
    selector.value = language;
    selector.addEventListener('change', () => api.setLanguage(selector.value).catch((error) =>
      console.error('[Audio Source Mixer] language switch failed', error)));
  }
  return api;
}

export async function message(key, substitutions = []) {
  const stored = await chrome.storage.local.get(UI_LANGUAGE_KEY);
  const language = normalizeLanguage(stored[UI_LANGUAGE_KEY]);
  const messages = await loadMessages(language);
  const entry = messages[key];
  if (!entry?.message) return chrome.i18n.getMessage(key, substitutions) || 'Text unavailable';
  const values = Array.isArray(substitutions) ? substitutions : [substitutions];
  return entry.message.replace(/\$(\d+)/g, (_, index) => String(values[Number(index) - 1] ?? '')).replace(/\$\$/g, '$');
}

function applyDocument(i18n) {
  document.documentElement.lang = i18n.language;
  for (const element of document.querySelectorAll('[data-i18n]')) element.textContent = i18n.t(element.dataset.i18n);
  for (const element of document.querySelectorAll('[data-i18n-title]')) element.title = i18n.t(element.dataset.i18nTitle);
  for (const element of document.querySelectorAll('[data-i18n-aria-label]'))
    element.setAttribute('aria-label', i18n.t(element.dataset.i18nAriaLabel));
}

async function loadMessages(language) {
  const locale = language === CHINESE_LANGUAGE ? 'zh_CN' : 'en';
  const response = await fetch(chrome.runtime.getURL(`_locales/${locale}/messages.json`));
  if (!response.ok) throw new Error(`Could not load extension locale ${locale}: HTTP ${response.status}`);
  return response.json();
}
