import { test, expect } from '@playwright/test';
import { readFileSync } from 'fs';
import path from 'path';
import { RadarrApiClient } from '../helpers/radarr-api-client.js';
import { SeedrApiClient } from '../helpers/seedr-api-client.js';
import { pollUntil } from '../helpers/wait-helpers.js';
import { testTorrent } from '../fixtures/test-torrent.js';
import { dockerManager } from '../helpers/docker-manager.js';

const STATE_FILE = path.join(process.cwd(), '.test-state.json');

function loadState(): { baseUrl: string; apiKey: string } {
  return JSON.parse(readFileSync(STATE_FILE, 'utf-8'));
}

let radarr: RadarrApiClient;
let seedr: SeedrApiClient;
let downloadClientId: number;
let movieId: number;

test.describe.serial('Seedr download client lifecycle', () => {
  test.beforeAll(() => {
    const state = loadState();
    radarr = new RadarrApiClient(state.baseUrl, state.apiKey);

    const email = process.env.SEEDR_EMAIL!;
    const password = process.env.SEEDR_PASSWORD!;
    seedr = new SeedrApiClient(email, password);
  });

  test.afterAll(async () => {
    // Force cleanup regardless of test outcome
    try {
      await seedr.cleanupAll();
    } catch {
      // best effort
    }

    if (movieId) {
      try {
        await radarr.deleteMovie(movieId);
      } catch {
        // best effort
      }
    }

    if (downloadClientId) {
      try {
        await radarr.deleteDownloadClient(downloadClientId);
      } catch {
        // best effort
      }
    }
  });

  test('1. Configure Seedr download client via API', async () => {
    const schemas = await radarr.getDownloadClientSchemas();
    const seedrSchema = schemas.find((s) => s.implementation === 'Seedr');
    expect(seedrSchema).toBeDefined();

    const config = {
      implementation: 'Seedr',
      implementationName: 'Seedr',
      configContract: seedrSchema!.configContract,
      name: 'Seedr',
      enable: true,
      protocol: 'torrent',
      priority: 1,
      fields: seedrSchema!.fields.map((f) => {
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

    // Test the connection first
    const testResult = await radarr.testDownloadClient(config);
    expect(testResult.isValid).toBe(true);

    // Create the client
    const created = await radarr.createDownloadClient(config);
    expect(created.id).toBeGreaterThan(0);
    expect(created.name).toBe('Seedr');

    downloadClientId = created.id;
  });

  test('2. Verify Seedr client visible in UI', async ({ page }) => {
    await page.goto('/settings/downloadclients');
    await expect(page.locator('text=Seedr').first()).toBeVisible({
      timeout: 15_000,
    });
  });

  test('3. Add test movie (Big Buck Bunny)', async () => {
    // Movie lookup calls external API — retry on transient network errors
    const results = await pollUntil(
      () => radarr.lookupMovie(`tmdb:${testTorrent.tmdbId}`),
      (r) => r.length > 0,
      {
        timeoutMs: 60_000,
        intervalMs: 5_000,
        description: 'movie lookup for Big Buck Bunny',
      }
    );

    const movie = results[0];
    expect(movie.tmdbId).toBe(testTorrent.tmdbId);

    const profiles = await radarr.getQualityProfiles();
    expect(profiles.length).toBeGreaterThan(0);

    const created = await radarr.addMovie({
      tmdbId: movie.tmdbId,
      title: movie.title,
      qualityProfileId: profiles[0].id,
      rootFolderPath: '/movies',
      monitored: true,
      addOptions: { searchForMovie: false },
      images: movie.images,
      year: movie.year,
    });

    expect(created.id).toBeGreaterThan(0);
    movieId = created.id;
  });

  test('4. Push release with magnet link', async () => {
    const response = await radarr.pushRelease({
      title: testTorrent.releaseTitle,
      magnetUrl: testTorrent.magnetLink,
      protocol: 'torrent',
      publishDate: new Date().toISOString(),
      size: testTorrent.sizeBytes,
      downloadClientId,
    });

    expect(response).toBeDefined();
  });

  test('5. Verify torrent appears in Seedr cloud', async () => {
    const contents = await pollUntil(
      () => seedr.getFolderContents(),
      (c) =>
        (c.torrents?.length ?? 0) > 0 ||
        (c.folders?.length ?? 0) > 0 ||
        (c.files?.length ?? 0) > 0,
      {
        timeoutMs: 60_000,
        intervalMs: 5_000,
        description: 'Seedr transfer or folder to appear',
      }
    );

    const hasContent =
      (contents.torrents?.length ?? 0) > 0 ||
      (contents.folders?.length ?? 0) > 0 ||
      (contents.files?.length ?? 0) > 0;
    expect(hasContent).toBe(true);
  });

  test('6. Check Radarr queue shows Seedr download', async () => {
    const queue = await pollUntil(
      () => radarr.getQueue(),
      (q) =>
        q.records.some(
          (r) => r.downloadClient === 'Seedr'
        ),
      {
        timeoutMs: 60_000,
        intervalMs: 5_000,
        description: 'Seedr item in Radarr queue',
      }
    );

    const seedrItem = queue.records.find(
      (r) => r.downloadClient === 'Seedr'
    );
    expect(seedrItem).toBeDefined();
    expect(['downloading', 'completed', 'delay']).toContain(
      seedrItem!.status.toLowerCase()
    );
  });

  test('7. Validate queue in UI', async ({ page }) => {
    await page.goto('/activity/queue');
    await page.waitForLoadState('networkidle');

    // The queue should show at least one record (our Seedr download)
    // Look for the release title or any queue row content
    await expect(
      page.locator('text=Big Buck Bunny').or(page.locator('text=Big.Buck.Bunny')).first()
    ).toBeVisible({ timeout: 30_000 });
  });

  test('8. Wait for Seedr cloud transfer to complete', async () => {
    await pollUntil(
      () => seedr.getFolderContents(),
      (c) => {
        const hasTransfers = (c.torrents?.length ?? 0) > 0;
        const hasCompletedContent =
          (c.folders?.length ?? 0) > 0 || (c.files?.length ?? 0) > 0;
        // Transfer done when no active transfers and files/folders exist
        return !hasTransfers && hasCompletedContent;
      },
      {
        timeoutMs: 300_000,
        intervalMs: 10_000,
        description: 'Seedr cloud transfer to finish',
      }
    );
  });

  test('9. Wait for local file download', async () => {
    await pollUntil(
      async () => {
        try {
          const result = dockerManager.exec(
            'find /downloads -type f ! -name "*.part"'
          );
          return result
            .split('\n')
            .filter((l) => l.trim().length > 0);
        } catch {
          return [];
        }
      },
      (files) => files.length > 0,
      {
        timeoutMs: 300_000,
        intervalMs: 10_000,
        description: 'local files to appear in /downloads',
      }
    );
  });

  test('10. Verify queue completion or import', async () => {
    await pollUntil(
      () => radarr.getQueue(),
      (q) => {
        // Either queue is empty (imported) or the Seedr item is completed
        const seedrItems = q.records.filter(
          (r) => r.downloadClient === 'Seedr'
        );
        return (
          seedrItems.length === 0 ||
          seedrItems.every((r) => r.status.toLowerCase() === 'completed')
        );
      },
      {
        timeoutMs: 180_000,
        intervalMs: 10_000,
        description: 'Radarr queue to clear or complete',
      }
    );
  });

  test('11. Verify Seedr cloud cleanup (deleteFromCloud)', async () => {
    // Radarr should delete the folder it created from Seedr cloud after import.
    // We check that no active transfers remain and the folder count decreased.
    // Note: the torrent may create multiple folders (e.g., Big Buck Bunny + Sintel),
    // so we verify our specific content was removed rather than checking for zero.
    await pollUntil(
      () => seedr.getFolderContents(),
      (c) => {
        const hasTransfers = (c.torrents?.length ?? 0) > 0;
        // Check that no folder matches our test torrent's expected name patterns
        const hasBigBuckBunny = (c.folders ?? []).some(
          (f) =>
            f.name.toLowerCase().includes('big buck bunny') ||
            f.name.toLowerCase().includes('big.buck.bunny')
        );
        return !hasTransfers && !hasBigBuckBunny;
      },
      {
        timeoutMs: 180_000,
        intervalMs: 10_000,
        description: 'Big Buck Bunny folder removed from Seedr',
      }
    );
  });
});
