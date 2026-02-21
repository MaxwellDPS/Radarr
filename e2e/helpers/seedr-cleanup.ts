import { readFileSync } from 'fs';
import path from 'path';
import { SeedrApiClient } from './seedr-api-client.js';

// Load .env manually
try {
  const envPath = path.join(process.cwd(), '.env');
  const envContent = readFileSync(envPath, 'utf-8');
  for (const line of envContent.split('\n')) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const eqIdx = trimmed.indexOf('=');
    if (eqIdx === -1) continue;
    const key = trimmed.slice(0, eqIdx).trim();
    const value = trimmed.slice(eqIdx + 1).trim();
    if (!process.env[key]) {
      process.env[key] = value;
    }
  }
} catch {
  // env vars may be set externally
}

async function main() {
  const email = process.env.SEEDR_EMAIL;
  const password = process.env.SEEDR_PASSWORD;

  if (!email || !password) {
    console.error('SEEDR_EMAIL and SEEDR_PASSWORD must be set in e2e/.env');
    process.exit(1);
  }

  const seedr = new SeedrApiClient(email, password);

  console.log('Cleaning all Seedr content...');
  await seedr.cleanupAll();
  console.log('Done.');
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
