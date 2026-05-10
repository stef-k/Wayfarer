import fs from 'node:fs';
import path from 'node:path';

export type TripEditorE2EConfig = {
  baseUrl: string;
  username: string;
  password: string;
  tripId: string;
};

const configKeys = [
  'WAYFARER_E2E_BASE_URL',
  'WAYFARER_E2E_USERNAME',
  'WAYFARER_E2E_PASSWORD',
  'WAYFARER_E2E_TRIP_ID'
] as const;

type ConfigKey = (typeof configKeys)[number];

// Loads Trip Editor E2E settings from environment variables, then the ignored local runbook.
export function loadTripEditorConfig(): TripEditorE2EConfig {
  const localConfig = readLocalManualVerification();
  const values = Object.fromEntries(configKeys.map(key => [key, process.env[key] || localConfig[key] || ''])) as Record<ConfigKey, string>;
  const missing = configKeys.filter(key => !values[key].trim());

  if (missing.length > 0) {
    throw new Error(
      [
        `Missing Trip Editor E2E configuration: ${missing.join(', ')}.`,
        'Set the WAYFARER_E2E_* environment variables or add simple KEY=value lines to .local/manual-verification.md.',
        'The password value is required but is never printed by this harness.'
      ].join(' ')
    );
  }

  return {
    baseUrl: values.WAYFARER_E2E_BASE_URL.trim().replace(/\/+$/, ''),
    username: values.WAYFARER_E2E_USERNAME.trim(),
    password: values.WAYFARER_E2E_PASSWORD,
    tripId: values.WAYFARER_E2E_TRIP_ID.trim()
  };
}

// Parses only explicit KEY=value or PowerShell $env:KEY='value' lines from the ignored local runbook.
function readLocalManualVerification(): Partial<Record<ConfigKey, string>> {
  const filePath = path.resolve(process.cwd(), '.local', 'manual-verification.md');
  if (!fs.existsSync(filePath)) {
    return {};
  }

  const result: Partial<Record<ConfigKey, string>> = {};
  const lines = fs.readFileSync(filePath, 'utf8').split(/\r?\n/);
  for (const line of lines) {
    const match = line.match(/^\s*(?:\$env:)?(WAYFARER_E2E_[A-Z_]+)\s*=\s*['"`]?([^'"`\r\n]+)['"`]?\s*$/);
    if (!match || !configKeys.includes(match[1] as ConfigKey)) {
      continue;
    }

    result[match[1] as ConfigKey] = match[2].trim();
  }

  return result;
}
