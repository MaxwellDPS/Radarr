interface DownloadClientSchema {
  id: number;
  implementation: string;
  implementationName: string;
  configContract: string;
  fields: Array<{
    name: string;
    label: string;
    type: string;
    value: unknown;
  }>;
  name?: string;
  enable?: boolean;
  protocol?: string;
}

interface DownloadClientConfig {
  implementation: string;
  implementationName: string;
  configContract: string;
  name: string;
  enable: boolean;
  fields: Array<{ name: string; value: unknown }>;
  protocol?: string;
  priority?: number;
}

interface DownloadClientResponse {
  id: number;
  name: string;
  implementation: string;
  enable: boolean;
}

interface RootFolderResponse {
  id: number;
  path: string;
}

interface MovieLookupResult {
  tmdbId: number;
  title: string;
  year: number;
  images: unknown[];
  [key: string]: unknown;
}

interface MovieResponse {
  id: number;
  tmdbId: number;
  title: string;
  [key: string]: unknown;
}

interface QueueItem {
  id: number;
  downloadId: string;
  title: string;
  status: string;
  downloadClient: string;
  [key: string]: unknown;
}

interface QueueResponse {
  page: number;
  pageSize: number;
  totalRecords: number;
  records: QueueItem[];
}

interface ReleasePushBody {
  title: string;
  magnetUrl?: string;
  downloadUrl?: string;
  protocol: string;
  publishDate: string;
  size?: number;
  downloadClientId?: number;
  downloadClient?: string;
}

interface QualityProfile {
  id: number;
  name: string;
  [key: string]: unknown;
}

export class RadarrApiClient {
  private baseUrl: string;
  private apiKey: string;

  constructor(baseUrl: string, apiKey: string) {
    this.baseUrl = baseUrl.replace(/\/$/, '');
    this.apiKey = apiKey;
  }

  private async request<T>(
    method: string,
    path: string,
    body?: unknown
  ): Promise<T> {
    const url = `${this.baseUrl}/api/v3${path}`;
    const headers: Record<string, string> = {
      'X-Api-Key': this.apiKey,
      Accept: 'application/json',
    };

    const init: RequestInit = { method, headers };

    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    const res = await fetch(url, init);

    if (!res.ok) {
      const text = await res.text().catch(() => '');
      throw new Error(
        `Radarr API ${method} ${path} returned ${res.status}: ${text}`
      );
    }

    const text = await res.text();
    return text ? (JSON.parse(text) as T) : ({} as T);
  }

  async getDownloadClientSchemas(): Promise<DownloadClientSchema[]> {
    return this.request<DownloadClientSchema[]>(
      'GET',
      '/downloadclient/schema'
    );
  }

  async testDownloadClient(
    config: DownloadClientConfig
  ): Promise<{ isValid: boolean; validationFailures: unknown[] }> {
    const res = await fetch(
      `${this.baseUrl}/api/v3/downloadclient/test`,
      {
        method: 'POST',
        headers: {
          'X-Api-Key': this.apiKey,
          'Content-Type': 'application/json',
          Accept: 'application/json',
        },
        body: JSON.stringify(config),
      }
    );

    const text = await res.text().catch(() => '');
    const parsed = text ? JSON.parse(text) : {};

    return {
      isValid: res.ok,
      validationFailures: parsed.validationFailures ?? parsed ?? [],
    };
  }

  async createDownloadClient(
    config: DownloadClientConfig
  ): Promise<DownloadClientResponse> {
    return this.request<DownloadClientResponse>(
      'POST',
      '/downloadclient',
      config
    );
  }

  async deleteDownloadClient(id: number): Promise<void> {
    await this.request<void>('DELETE', `/downloadclient/${id}`);
  }

  async addRootFolder(path: string): Promise<RootFolderResponse> {
    return this.request<RootFolderResponse>('POST', '/rootfolder', { path });
  }

  async getRootFolders(): Promise<RootFolderResponse[]> {
    return this.request<RootFolderResponse[]>('GET', '/rootfolder');
  }

  async lookupMovie(term: string): Promise<MovieLookupResult[]> {
    return this.request<MovieLookupResult[]>(
      'GET',
      `/movie/lookup?term=${encodeURIComponent(term)}`
    );
  }

  async addMovie(movie: {
    tmdbId: number;
    title: string;
    qualityProfileId: number;
    rootFolderPath: string;
    monitored: boolean;
    addOptions: { searchForMovie: boolean };
    [key: string]: unknown;
  }): Promise<MovieResponse> {
    return this.request<MovieResponse>('POST', '/movie', movie);
  }

  async deleteMovie(
    id: number,
    deleteFiles = true
  ): Promise<void> {
    await this.request<void>(
      'DELETE',
      `/movie/${id}?deleteFiles=${deleteFiles}`
    );
  }

  async pushRelease(release: ReleasePushBody): Promise<unknown[]> {
    return this.request<unknown[]>('POST', '/release/push', release);
  }

  async getQueue(
    page = 1,
    pageSize = 50
  ): Promise<QueueResponse> {
    return this.request<QueueResponse>(
      'GET',
      `/queue?page=${page}&pageSize=${pageSize}&includeUnknownMovieItems=true`
    );
  }

  async getQualityProfiles(): Promise<QualityProfile[]> {
    return this.request<QualityProfile[]>('GET', '/qualityprofile');
  }

  async getSystemStatus(): Promise<{ startupPath: string; version: string }> {
    return this.request<{ startupPath: string; version: string }>(
      'GET',
      '/system/status'
    );
  }

  async getHostConfig(): Promise<Record<string, unknown>> {
    return this.request<Record<string, unknown>>('GET', '/config/host');
  }

  async updateHostConfig(
    config: Record<string, unknown>
  ): Promise<Record<string, unknown>> {
    return this.request<Record<string, unknown>>(
      'PUT',
      '/config/host',
      config
    );
  }
}
