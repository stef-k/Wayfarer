import L from 'leaflet';

type RetryTileImage = HTMLImageElement & {
  _abortController?: AbortController | null;
};

const tileConfig = (): { burstCapacity?: number; retryAfterSeconds?: number } => window.wayfarerTileConfig ?? {};
const tilePoolSize = (): number => Math.ceil((tileConfig().burstCapacity ?? 12) * 0.75);
const jitter = (delayMs: number): number => delayMs * (0.75 + Math.random() * 0.5);
let tileFetchesInFlight = 0;
const waitingTileFetches: Array<{ resolve: (acquired: boolean) => void }> = [];

const acquireTileFetchSlot = (signal: AbortSignal): Promise<boolean> => {
  if (signal.aborted) {
    return Promise.resolve(false);
  }

  if (tileFetchesInFlight < tilePoolSize()) {
    tileFetchesInFlight += 1;
    return Promise.resolve(true);
  }

  return new Promise(resolve => {
    const entry = { resolve };
    waitingTileFetches.push(entry);
    signal.addEventListener('abort', () => {
      const index = waitingTileFetches.indexOf(entry);
      if (index !== -1) {
        waitingTileFetches.splice(index, 1);
        resolve(false);
      }
    }, { once: true });
  });
};

const releaseTileFetchSlot = (): void => {
  const next = waitingTileFetches.shift();
  if (next) {
    next.resolve(true);
    return;
  }

  tileFetchesInFlight = Math.max(0, tileFetchesInFlight - 1);
};

/**
 * Leaflet tile layer with the same retry and concurrency behavior as the legacy shared layer.
 */
class TripEditorRetryTileLayer extends L.TileLayer {
  private readonly maxRetries = 5;
  private readonly retryDelayMs = 1000;

  createTile(coords: L.Coords, done: L.DoneCallback): HTMLElement {
    const tile: RetryTileImage = document.createElement('img');
    tile.alt = '';
    tile.setAttribute('role', 'presentation');

    const controller = new AbortController();
    tile._abortController = controller;
    this.fetchWithRetry(this.getTileUrl(coords), tile, done, 0, controller.signal);
    return tile;
  }

  _removeTile(key: string): void {
    const tile = (this as unknown as { _tiles?: Record<string, { el?: RetryTileImage }> })._tiles?.[key];
    if (tile?.el?._abortController) {
      tile.el._abortController.abort();
      tile.el._abortController = null;
    }

    if (tile?.el?.src.startsWith('blob:')) {
      URL.revokeObjectURL(tile.el.src);
    }

    super._removeTile(key);
  }

  private fetchWithRetry(url: string, tile: RetryTileImage, done: L.DoneCallback, attempt: number, signal: AbortSignal): void {
    acquireTileFetchSlot(signal).then(acquired => {
      if (!acquired) return;
      if (signal.aborted) {
        releaseTileFetchSlot();
        return;
      }

      fetch(url, { signal })
        .then(response => {
          releaseTileFetchSlot();
          if (response.ok) return this.loadTileBlob(response, tile, done, signal);
          if (response.status === 503) {
            this.scheduleRetry(url, tile, done, attempt, signal, response.headers.get('Retry-After'));
            return;
          }

          done(new Error(`Tile fetch failed: ${response.status}`), tile);
        })
        .catch(error => {
          releaseTileFetchSlot();
          if (error instanceof DOMException && error.name === 'AbortError') return;
          this.scheduleRetry(url, tile, done, attempt, signal);
        });
    });
  }

  private scheduleRetry(url: string, tile: RetryTileImage, done: L.DoneCallback, attempt: number, signal: AbortSignal, retryAfterHeader?: string | null): void {
    if (attempt < this.maxRetries) {
      const retryAfterSeconds = retryAfterHeader ? Number.parseInt(retryAfterHeader, 10) : Number.NaN;
      const serverDelay = Number.isFinite(retryAfterSeconds) && retryAfterSeconds > 0
        ? retryAfterSeconds * 1000
        : this.retryDelayMs * Math.pow(2, attempt);
      const delayMs = jitter(Math.min(Math.max(serverDelay, this.retryDelayMs), 10_000));
      window.setTimeout(() => !signal.aborted && this.fetchWithRetry(url, tile, done, attempt + 1, signal), delayMs);
      return;
    }

    this.scheduleSlowRetry(url, tile, done, signal);
  }

  private scheduleSlowRetry(url: string, tile: RetryTileImage, done: L.DoneCallback, signal: AbortSignal): void {
    const retryAfterSeconds = tileConfig().retryAfterSeconds ?? 6;
    window.setTimeout(() => {
      if (!signal.aborted) this.fetchWithRetry(url, tile, done, this.maxRetries, signal);
    }, jitter(retryAfterSeconds * 3 * 1000));
  }

  private async loadTileBlob(response: Response, tile: RetryTileImage, done: L.DoneCallback, signal: AbortSignal): Promise<void> {
    const blob = await response.blob();
    if (signal.aborted) return;

    tile.onload = (): void => {
      URL.revokeObjectURL(tile.src);
      done(null, tile);
    };
    tile.onerror = (): void => {
      URL.revokeObjectURL(tile.src);
      done(new Error('Tile image decode failed'), tile);
    };
    tile.src = URL.createObjectURL(blob);
  }
}

/**
 * Creates the Trip Editor tile layer with fetch-based retry and concurrency controls.
 */
export const createTripEditorTileLayer = (tilesUrl: string, options: L.TileLayerOptions): L.TileLayer =>
  new TripEditorRetryTileLayer(tilesUrl, options);
