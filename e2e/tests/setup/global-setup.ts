import { writeFileSync } from 'fs';
import path from 'path';
import { dockerManager } from '../../helpers/docker-manager.js';
import { RadarrApiClient } from '../../helpers/radarr-api-client.js';
import { SeedrApiClient } from '../../helpers/seedr-api-client.js';
import { pollUntil } from '../../helpers/wait-helpers.js';

// process.env is already populated by playwright.config.ts .env loading

const STATE_FILE = path.join(process.cwd(), '.test-state.json');

export default async function globalSetup() {
  const seedrEmail = process.env.SEEDR_EMAIL;
  const seedrPassword = process.env.SEEDR_PASSWORD;

  if (!seedrEmail || !seedrPassword) {
    throw new Error(
      'SEEDR_EMAIL and SEEDR_PASSWORD must be set in e2e/.env'
    );
  }

  console.log('[setup] Cleaning up previous Docker resources...');
  try {
    dockerManager.destroy();
  } catch {
    // may not exist yet
  }

  console.log('[setup] Cleaning Seedr account...');
  const seedr = new SeedrApiClient(seedrEmail, seedrPassword);
  await seedr.cleanupAll();

  console.log('[setup] Building Docker image...');
  dockerManager.build(600_000);

  console.log('[setup] Starting container...');
  dockerManager.start();

  console.log('[setup] Waiting for Radarr to become healthy...');
  await dockerManager.waitForHealthy(120_000);

  console.log('[setup] Extracting API key...');
  // Give Radarr a moment to write config.xml
  await new Promise((r) => setTimeout(r, 5_000));
  const apiKey = dockerManager.getApiKey();
  console.log(`[setup] API key: ${apiKey.substring(0, 4)}...`);

  const baseUrl = 'http://localhost:7878';
  const radarr = new RadarrApiClient(baseUrl, apiKey);

  console.log('[setup] Waiting for Radarr API to be fully ready...');
  await pollUntil(
    () => radarr.getSystemStatus(),
    (status) => !!status.version,
    { timeoutMs: 60_000, description: 'Radarr system status' }
  );

  console.log('[setup] Configuring authentication...');
  const hostConfig = await radarr.getHostConfig();
  await radarr.updateHostConfig({
    ...hostConfig,
    authenticationMethod: 'forms',
    authenticationRequired: 'disabledForLocalAddresses',
    username: 'admin',
    password: 'admin',
    passwordConfirmation: 'admin',
  });

  console.log('[setup] Adding root folder /movies...');
  const existingFolders = await radarr.getRootFolders();

  if (!existingFolders.some((f) => f.path === '/movies')) {
    await radarr.addRootFolder('/movies');
  }

  // Write state for tests to consume
  const state = { baseUrl, apiKey };
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));

  console.log('[setup] Global setup complete.');
}
