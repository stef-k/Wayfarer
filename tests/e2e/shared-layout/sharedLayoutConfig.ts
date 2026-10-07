type SharedLayoutE2EConfig = {
  username: string;
  password: string;
};

const configKeys = ['WAYFARER_E2E_USERNAME', 'WAYFARER_E2E_PASSWORD'] as const;
type ConfigKey = (typeof configKeys)[number];

// Mutation coverage accepts only the supervisor's run-owned credentials, never a human runbook.
export function loadSharedLayoutConfig(): SharedLayoutE2EConfig {
  if (process.env.WAYFARER_E2E_MANAGED !== '1' || !process.env.WAYFARER_E2E_RUN_ID) {
    throw new Error('Shared-layout mutations require the managed browser supervisor.');
  }
  const values = Object.fromEntries(configKeys.map(key => [key, process.env[key] || ''])) as Record<ConfigKey, string>;
  const missing = configKeys.filter(key => !values[key].trim());
  if (missing.length > 0) {
    throw new Error(`Missing managed shared-layout configuration: ${missing.join(', ')}.`);
  }

  return { username: values.WAYFARER_E2E_USERNAME.trim(), password: values.WAYFARER_E2E_PASSWORD };
}
