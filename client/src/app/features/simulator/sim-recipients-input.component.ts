import { ChangeDetectionStrategy, Component, computed, input, model, signal } from '@angular/core';
import { IconComponent } from '../../shared/ui/icon.component';
import { eventValue } from './sim-feedback';
import { SimUser, userAddress } from './simulator.models';

let recipientsSeq = 0;

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * To / Cc field: chips of addresses, typed free-form or picked from the tenant directory (datalist).
 * Enter, comma or leaving the field adds the typed address; Backspace on an empty field removes the last one.
 */
@Component({
  selector: 'fu-sim-recipients',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    <div class="box">
      @for (a of addresses(); track a) {
        <span class="chip sm" [attr.title]="a">
          {{ nameOf(a) }}
          <button type="button" class="x" [attr.aria-label]="'Remove ' + a" (click)="remove(a)"><fu-icon name="x" [size]="11" /></button>
        </span>
      }
      <input
        class="q"
        type="text"
        autocomplete="off"
        [id]="inputId()"
        [attr.list]="listId"
        [attr.aria-label]="ariaLabel()"
        [placeholder]="addresses().length ? '' : placeholder()"
        [value]="text()"
        (input)="onInput($event)"
        (keydown)="onKey($event)"
        (blur)="commit($event)"
      />
      <datalist [id]="listId">
        @for (u of options(); track u.id) {
          <option [value]="address(u)">{{ u.displayName }}</option>
        }
      </datalist>
    </div>
    @if (error()) {
      <span class="error">{{ error() }}</span>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .box { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; min-height: 34px; padding: 3px 6px; border: 1px solid var(--border-strong); border-radius: var(--radius-sm); background: var(--surface); }
    .box:focus-within { border-color: var(--accent); box-shadow: var(--focus); }
    .q { flex: 1 1 160px; min-width: 120px; border: 0; outline: none; background: transparent; height: 26px; padding: 0 4px; font-size: 13.5px; }
    .error { display: block; margin-top: 4px; font-size: 12px; color: var(--danger); }
  `,
})
export class SimRecipientsInputComponent {
  readonly addresses = model<string[]>([]);
  readonly users = input<SimUser[]>([]);
  readonly inputId = input(`fu-sim-rcpt-${++recipientsSeq}`);
  readonly ariaLabel = input('Recipients');
  readonly placeholder = input('Name or address…');

  protected readonly listId = `fu-sim-rcpt-list-${recipientsSeq}`;
  protected readonly text = signal('');
  protected readonly error = signal<string | null>(null);

  private readonly byAddress = computed(() => new Map(this.users().map((u) => [userAddress(u).toLowerCase(), u])));
  protected readonly options = computed(() => {
    const chosen = new Set(this.addresses().map((a) => a.toLowerCase()));
    return this.users().filter((u) => userAddress(u) && !chosen.has(userAddress(u).toLowerCase()));
  });

  protected address(u: SimUser): string {
    return userAddress(u);
  }

  protected nameOf(address: string): string {
    return this.byAddress().get(address.toLowerCase())?.displayName ?? address;
  }

  protected onInput(event: Event): void {
    const value = eventValue(event);
    this.error.set(null);
    // Picking from the datalist fills the whole address at once — add it straight away.
    if (this.byAddress().has(value.trim().toLowerCase())) {
      this.text.set(value);
      this.commit(event);
      return;
    }
    this.text.set(value);
  }

  protected onKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === ',' || event.key === ';') {
      if (this.text().trim()) {
        event.preventDefault();
        this.commit(event);
      }
    } else if (event.key === 'Backspace' && !this.text() && this.addresses().length) {
      this.addresses.update((list) => list.slice(0, -1));
    }
  }

  /** An address as typed, or the address of the one tenant user whose name matches (exactly or by prefix). */
  private resolve(part: string): string | null {
    if (EMAIL_RE.test(part)) return part;
    const q = part.toLowerCase();
    const exact = this.users().filter((u) => u.displayName.toLowerCase() === q);
    const matches = exact.length ? exact : this.users().filter((u) => u.displayName.toLowerCase().startsWith(q));
    return matches.length === 1 && userAddress(matches[0]) ? userAddress(matches[0]) : null;
  }

  /**
   * Adds the typed text as one or more recipients ("a@x.com; Asha Rao" works too). The field is written
   * directly because the text signal can return to the value Angular last rendered within one tick.
   */
  protected commit(event: Event): void {
    const parts = this.text()
      .split(/[;,]+/)
      .map((p) => p.trim())
      .filter((p) => p.length > 0);
    if (!parts.length) return;
    const resolved = parts.map((p) => ({ part: p, address: this.resolve(p) }));
    const invalid = resolved.filter((r) => !r.address).map((r) => r.part);
    const valid = resolved.map((r) => r.address).filter((a): a is string => !!a);
    if (valid.length) {
      const existing = new Set(this.addresses().map((a) => a.toLowerCase()));
      this.addresses.update((list) => [...list, ...valid.filter((v) => !existing.has(v.toLowerCase()))]);
    }
    this.text.set(invalid.join(', '));
    const field = event.target as HTMLInputElement | null;
    if (field) field.value = this.text();
    this.error.set(invalid.length ? `Not a tenant user or email address: ${invalid.join(', ')}` : null);
  }

  protected remove(address: string): void {
    this.addresses.update((list) => list.filter((a) => a !== address));
  }
}
