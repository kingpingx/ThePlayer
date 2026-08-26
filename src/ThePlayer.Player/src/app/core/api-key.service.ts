import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'theplayer.apiKey';

/**
 * The key a hosted deployment demands, remembered between visits.
 *
 * Held in `localStorage` rather than in memory, because the alternative is retyping it on every
 * reload and the alternative to *that* is people picking a short key. It is a bearer credential in
 * a browser either way — anything with script access to this origin can read it, which is the same
 * exposure a session cookie without `HttpOnly` would have.
 *
 * A local deployment demands nothing and never asks; the panel only appears once the server has
 * actually answered `401`.
 */
@Injectable({ providedIn: 'root' })
export class ApiKeyService {
  /** The key in hand, or null. */
  readonly key = signal<string | null>(read());

  /** Set once the server has refused something, so the UI can ask rather than guess. */
  readonly required = signal(false);

  set(key: string): void {
    const trimmed = key.trim();
    this.key.set(trimmed || null);

    try {
      if (trimmed) {
        localStorage.setItem(STORAGE_KEY, trimmed);
      } else {
        localStorage.removeItem(STORAGE_KEY);
      }
    } catch {
      // Private browsing, or storage disabled. The key still works for this session.
    }
  }

  clear(): void {
    this.set('');
  }

  /** Headers for a `fetch`, which is the only place a header can actually be set. */
  headers(): Record<string, string> {
    const key = this.key();
    return key ? { 'X-Api-Key': key } : {};
  }

  /**
   * Appends the key to a URL, for `EventSource` and `WebSocket`.
   *
   * Neither can set a header, which is the whole reason the server accepts a query parameter at
   * all. It is the weaker form — query strings reach proxy logs and browser history — so it is used
   * only where there is no header to use instead.
   */
  withKey(url: string): string {
    const key = this.key();
    if (!key) {
      return url;
    }

    const separator = url.includes('?') ? '&' : '?';
    return `${url}${separator}key=${encodeURIComponent(key)}`;
  }
}

function read(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}
