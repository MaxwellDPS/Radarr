import { test, expect } from '@playwright/test';
import { readFileSync } from 'fs';
import path from 'path';
import { RadarrApiClient } from '../helpers/radarr-api-client.js';

const STATE_FILE = path.join(process.cwd(), '.test-state.json');

function loadState(): { baseUrl: string; apiKey: string } {
  return JSON.parse(readFileSync(STATE_FILE, 'utf-8'));
}

let radarr: RadarrApiClient;

test.describe('Seedr download client configuration', () => {
  test.beforeAll(() => {
    const state = loadState();
    radarr = new RadarrApiClient(state.baseUrl, state.apiKey);
  });

  test('Schema includes Seedr with expected fields', async () => {
    const schemas = await radarr.getDownloadClientSchemas();
    const seedrSchema = schemas.find((s) => s.implementation === 'Seedr');

    expect(seedrSchema).toBeDefined();
    expect(seedrSchema!.configContract).toBe('SeedrSettings');

    const fieldNames = seedrSchema!.fields.map((f) => f.name);
    expect(fieldNames).toContain('email');
    expect(fieldNames).toContain('password');
    expect(fieldNames).toContain('downloadDirectory');
    expect(fieldNames).toContain('deleteFromCloud');
  });

  test('Test with invalid credentials returns failure', async () => {
    const schemas = await radarr.getDownloadClientSchemas();
    const seedrSchema = schemas.find((s) => s.implementation === 'Seedr')!;

    const config = {
      implementation: 'Seedr',
      implementationName: 'Seedr',
      configContract: seedrSchema.configContract,
      name: 'Seedr-Invalid',
      enable: true,
      protocol: 'torrent',
      priority: 1,
      fields: seedrSchema.fields.map((f) => {
        switch (f.name) {
          case 'email':
            return { name: 'email', value: 'fake@invalid.example' };
          case 'password':
            return { name: 'password', value: 'wrongpassword123' };
          case 'downloadDirectory':
            return { name: 'downloadDirectory', value: '/downloads' };
          case 'deleteFromCloud':
            return { name: 'deleteFromCloud', value: true };
          default:
            return { name: f.name, value: f.value };
        }
      }),
    };

    const result = await radarr.testDownloadClient(config);
    expect(result.isValid).toBe(false);
  });

  test('Test with valid credentials returns success', async () => {
    const schemas = await radarr.getDownloadClientSchemas();
    const seedrSchema = schemas.find((s) => s.implementation === 'Seedr')!;

    const config = {
      implementation: 'Seedr',
      implementationName: 'Seedr',
      configContract: seedrSchema.configContract,
      name: 'Seedr-Valid',
      enable: true,
      protocol: 'torrent',
      priority: 1,
      fields: seedrSchema.fields.map((f) => {
        switch (f.name) {
          case 'email':
            return { name: 'email', value: process.env.SEEDR_EMAIL };
          case 'password':
            return { name: 'password', value: process.env.SEEDR_PASSWORD };
          case 'downloadDirectory':
            return { name: 'downloadDirectory', value: '/downloads' };
          case 'deleteFromCloud':
            return { name: 'deleteFromCloud', value: true };
          default:
            return { name: f.name, value: f.value };
        }
      }),
    };

    const result = await radarr.testDownloadClient(config);
    expect(result.isValid).toBe(true);
  });

  test('UI shows Seedr in Add Download Client modal', async ({ page }) => {
    await page.goto('/settings/downloadclients');

    // Wait for the page to fully load
    await page.waitForLoadState('networkidle');

    // Click the add button (the + card for adding download clients)
    const addButton = page.locator('[class*="addDownloadClient"]').first();
    await addButton.waitFor({ timeout: 15_000 });
    await addButton.click();

    // Look for Seedr in the modal
    await expect(
      page.locator('div[class*="modalContent"] >> text=Seedr').first()
    ).toBeVisible({ timeout: 10_000 });
  });
});
