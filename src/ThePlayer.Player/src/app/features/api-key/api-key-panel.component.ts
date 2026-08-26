import { Component, inject, signal } from '@angular/core';
import { ApiKeyService } from '../../core/api-key.service';

/**
 * Asks for the key, but only once the server has actually refused something.
 *
 * A local deployment demands nothing, so showing a credential box on every install would be asking
 * most people for something that does not exist. The server is the only reliable source of the
 * answer — it says `401` or it does not — so the panel waits to be told.
 */
@Component({
  selector: 'app-api-key-panel',
  standalone: true,
  template: `
    @if (service.required()) {
      <section class="panel">
        <h2>This server needs a key</h2>

        @if (service.key(); as current) {
          <p class="have">
            A key is stored{{ saved() ? ' — saved' : '' }}. If it is the wrong one, replace it.
          </p>
        }

        <form (submit)="save($event)">
          <input
            type="password"
            name="key"
            autocomplete="off"
            placeholder="API key"
            [value]="draft()"
            (input)="draft.set($any($event.target).value)" />

          <button type="submit" [disabled]="!draft().trim()">Save</button>
          @if (service.key()) {
            <button type="button" class="ghost" (click)="forget()">Forget</button>
          }
        </form>

        <p class="note">
          Kept in this browser's local storage, so it survives a reload. Anything with script access
          to this page can read it — the same exposure as a session cookie without <code>HttpOnly</code>.
        </p>
      </section>
    }
  `,
  styles: [`
    .panel { border: 1px solid #f5c26b; background: #fffaf0; border-radius: 8px;
             padding: 0.75rem 1rem; }
    h2 { font-size: 0.95rem; margin: 0 0 0.5rem; }
    form { display: flex; gap: 0.4rem; }
    input { flex: 1; min-width: 0; padding: 0.35rem 0.5rem; border: 1px solid #d0d5dd;
            border-radius: 6px; font: inherit; }
    button { padding: 0.35rem 0.7rem; border-radius: 6px; border: 1px solid #b54708;
             background: #b54708; color: #fff; font: inherit; cursor: pointer; }
    button:disabled { opacity: 0.5; cursor: default; }
    button.ghost { background: none; color: #b54708; }
    .have { margin: 0 0 0.5rem; font-size: 0.85rem; color: #475467; }
    .note { margin: 0.5rem 0 0; font-size: 0.78rem; color: #475467; line-height: 1.35; }
    code { font-size: 0.95em; }
  `],
})
export class ApiKeyPanelComponent {
  protected readonly service = inject(ApiKeyService);
  protected readonly draft = signal('');
  protected readonly saved = signal(false);

  protected save(event: Event): void {
    event.preventDefault();

    const key = this.draft().trim();
    if (!key) {
      return;
    }

    this.service.set(key);
    this.draft.set('');
    this.saved.set(true);
  }

  protected forget(): void {
    this.service.clear();
    this.saved.set(false);
  }
}
