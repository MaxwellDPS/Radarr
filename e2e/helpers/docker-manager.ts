import { execSync } from 'child_process';
import os from 'os';
import { XMLParser } from 'fast-xml-parser';

function getComposeDir(): string {
  return process.cwd();
}

/** Detect the .NET RID for the current host architecture */
function getDotnetRid(): string {
  const arch = os.arch(); // 'arm64' or 'x64'
  return arch === 'arm64' ? 'linux-musl-arm64' : 'linux-musl-x64';
}

/** Environment variables for compose commands, including the auto-detected RID */
function getEnv(): NodeJS.ProcessEnv {
  return { ...process.env, DOTNET_RID: getDotnetRid() };
}

/** Run a command and capture its output */
function run(cmd: string, timeoutMs = 120_000): string {
  return execSync(cmd, {
    cwd: getComposeDir(),
    timeout: timeoutMs,
    encoding: 'utf-8',
    maxBuffer: 50 * 1024 * 1024, // 50 MB
    env: getEnv(),
    stdio: ['pipe', 'pipe', 'pipe'],
  }).trim();
}

/** Run a command with output streamed to the console (for long-running builds) */
function runStreaming(cmd: string, timeoutMs = 120_000): void {
  execSync(cmd, {
    cwd: getComposeDir(),
    timeout: timeoutMs,
    env: getEnv(),
    stdio: 'inherit',
  });
}

export const dockerManager = {
  build(timeoutMs = 600_000): void {
    runStreaming('docker compose build', timeoutMs);
  },

  start(): void {
    runStreaming('docker compose up -d');
  },

  async waitForHealthy(timeoutMs = 120_000): Promise<void> {
    const start = Date.now();
    const interval = 3_000;

    while (Date.now() - start < timeoutMs) {
      try {
        const res = await fetch('http://localhost:7878/ping');
        if (res.ok) return;
      } catch {
        // not ready yet
      }
      await new Promise((r) => setTimeout(r, interval));
    }

    throw new Error(`Radarr did not become healthy within ${timeoutMs}ms`);
  },

  getApiKey(): string {
    const xml = run(
      'docker compose exec -T radarr cat /config/radarr/config.xml'
    );
    const parser = new XMLParser();
    const parsed = parser.parse(xml);
    const apiKey = parsed?.Config?.ApiKey;

    if (!apiKey) {
      throw new Error(
        `Could not extract ApiKey from config.xml. Parsed: ${JSON.stringify(parsed)}`
      );
    }

    return apiKey as string;
  },

  exec(cmd: string, timeoutMs = 60_000): string {
    return run(`docker compose exec -T radarr ${cmd}`, timeoutMs);
  },

  destroy(): void {
    runStreaming('docker compose down -v', 60_000);
  },
};
