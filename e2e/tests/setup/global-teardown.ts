import path from 'path';
import { unlinkSync } from 'fs';
import { dockerManager } from '../../helpers/docker-manager.js';
import { SeedrApiClient } from '../../helpers/seedr-api-client.js';

// process.env is already populated by playwright.config.ts .env loading

const STATE_FILE = path.join(process.cwd(), '.test-state.json');

export default async function globalTeardown() {
  console.log('[teardown] Cleaning Seedr account (safety net)...');

  const seedrEmail = process.env.SEEDR_EMAIL;
  const seedrPassword = process.env.SEEDR_PASSWORD;

  if (seedrEmail && seedrPassword) {
    const seedr = new SeedrApiClient(seedrEmail, seedrPassword);
    await seedr.cleanupAll();
  }

  console.log('[teardown] Destroying Docker resources...');
  try {
    dockerManager.destroy();
  } catch (err) {
    console.warn('[teardown] Docker cleanup error:', err);
  }

  try {
    unlinkSync(STATE_FILE);
  } catch {
    // may not exist
  }

  console.log('[teardown] Global teardown complete.');
}
