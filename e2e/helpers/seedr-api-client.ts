interface SeedrUser {
  account: {
    username: string;
    email: string;
    space_used: number;
    space_max: number;
  };
}

interface SeedrFolder {
  id: number;
  name: string;
  size: number;
}

interface SeedrFile {
  id: number;
  name: string;
  size: number;
  folder_id: number;
}

interface SeedrTransfer {
  id: number;
  name: string;
  progress: number;
  size: number;
  hash: string;
}

interface SeedrFolderContents {
  id: number;
  name: string;
  folders: SeedrFolder[];
  files: SeedrFile[];
  torrents: SeedrTransfer[];
  space_used: number;
  space_max: number;
}

export class SeedrApiClient {
  private baseUrl = 'https://www.seedr.cc/rest';
  private authHeader: string;

  constructor(email: string, password: string) {
    this.authHeader =
      'Basic ' + Buffer.from(`${email}:${password}`).toString('base64');
  }

  private async request<T>(
    method: string,
    path: string,
    body?: URLSearchParams
  ): Promise<T> {
    const url = `${this.baseUrl}${path}`;
    const headers: Record<string, string> = {
      Authorization: this.authHeader,
      Accept: 'application/json',
    };

    const init: RequestInit = { method, headers };

    if (body) {
      headers['Content-Type'] = 'application/x-www-form-urlencoded';
      init.body = body.toString();
    }

    const res = await fetch(url, init);

    if (!res.ok) {
      const text = await res.text().catch(() => '');
      throw new Error(
        `Seedr API ${method} ${path} returned ${res.status}: ${text}`
      );
    }

    return res.json() as Promise<T>;
  }

  async getUser(): Promise<SeedrUser> {
    return this.request<SeedrUser>('GET', '/user');
  }

  async getFolderContents(folderId?: number): Promise<SeedrFolderContents> {
    const path = folderId ? `/folder/${folderId}` : '/folder';
    return this.request<SeedrFolderContents>('GET', path);
  }

  async deleteFolder(folderId: number): Promise<void> {
    await this.request<unknown>('DELETE', `/folder/${folderId}`);
  }

  async deleteFile(fileId: number): Promise<void> {
    await this.request<unknown>('DELETE', `/file/${fileId}`);
  }

  async deleteTransfer(transferId: number): Promise<void> {
    await this.request<unknown>('DELETE', `/torrent/${transferId}`);
  }

  async cleanupAll(): Promise<void> {
    try {
      const contents = await this.getFolderContents();

      for (const transfer of contents.torrents ?? []) {
        try {
          await this.deleteTransfer(transfer.id);
        } catch {
          // best effort
        }
      }

      for (const folder of contents.folders ?? []) {
        try {
          await this.deleteFolder(folder.id);
        } catch {
          // best effort
        }
      }

      for (const file of contents.files ?? []) {
        try {
          await this.deleteFile(file.id);
        } catch {
          // best effort
        }
      }
    } catch {
      // best effort — account may already be clean
    }
  }
}
