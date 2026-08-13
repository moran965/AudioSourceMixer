export const ONBOARDING_KEY = 'browserOnboarding';
export const ONBOARDING_SCHEMA_VERSION = 1;

export function normalizeOnboardingState(value) {
  const status = value?.status;
  return {
    schemaVersion: ONBOARDING_SCHEMA_VERSION,
    status: status === 'completed' || status === 'never' ? status : 'pending',
    completedVersion: typeof value?.completedVersion === 'string' ? value.completedVersion : null
  };
}

export function installationPlan(details, storedState, extensionVersion) {
  const state = normalizeOnboardingState(storedState);
  if (details?.reason !== 'install') return { state, openWelcome: false };
  return { state, openWelcome: state.status === 'pending', extensionVersion };
}

export function completeOnboarding(storedState, extensionVersion, never = false) {
  return {
    ...normalizeOnboardingState(storedState),
    status: never ? 'never' : 'completed',
    completedVersion: extensionVersion
  };
}

export function needsOnboarding(storedState) {
  return normalizeOnboardingState(storedState).status === 'pending';
}
