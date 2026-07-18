import fs from 'node:fs';
import path from 'node:path';

type SharedLayoutE2EConfig = {
  username: string;
  password: string;
};

const configKeys = ['WAYFARER_E2E_USERNAME', 'WAYFARER_E2E_PASSWORD'] as const;
type ConfigKey = (typeof configKeys)[number];

// Reads ignored local credentials only for the authenticated checks and never prints them.
export function loadSharedLayoutConfig(): SharedLayoutE2EConfig {
  const localConfig = readLocalManualVerification();
  const values = Object.fromEntries(configKeys.map(key => [key, process.env[key] || localConfig[key] || ''])) as Record<ConfigKey, string>;
  const missing = configKeys.filter(key => !values[key].trim());
  if (missing.length > 0) {
    throw new Error(`Missing shared-layout E2E configuration: ${missing.join(', ')}. Set WAYFARER_E2E_* or use .local/manual-verification.md.`);
  }

  return { username: values.WAYFARER_E2E_USERNAME.trim(), password: values.WAYFARER_E2E_PASSWORD };
}

function readLocalManualVerification(): Partial<Record<ConfigKey, string>> {
  const filePath = path.resolve(process.cwd(), '.local', 'manual-verification.md');
  if (!fs.existsSync(filePath)) return {};

  const result: Partial<Record<ConfigKey, string>> = {};
  for (const line of fs.readFileSync(filePath, 'utf8').split(/\r?\n/)) {
    const envMatch = line.match(/^\s*(?:\$env:)?(WAYFARER_E2E_[A-Z_]+)\s*=\s*['"`]?([^'"`\r\n]+)['"`]?\s*$/);
    if (envMatch && configKeys.includes(envMatch[1] as ConfigKey)) result[envMatch[1] as ConfigKey] = envMatch[2].trim();
    const labelMatch = line.match(/^\s*(Username|Password):\s*(.+?)\s*$/i);
    if (!labelMatch) continue;
    const value = labelMatch[2].trim().replace(/`/g, '');
    if (/^Username$/i.test(labelMatch[1])) result.WAYFARER_E2E_USERNAME = value;
    if (/^Password$/i.test(labelMatch[1])) result.WAYFARER_E2E_PASSWORD = value;
  }
  return result;
}
