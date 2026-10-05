import { ChangeDetectionStrategy, Component, input, model } from '@angular/core';

export interface TabItem {
  key: string;
  label: string;
  count?: number | null;
  /** Tone of the count pill: 'danger' | 'warning' | 'accent'. */
  tone?: string;
}

/**
 * Accessible tab bar (arrow keys move between tabs). Bind the active key two-way:
 *   <fu-tabs [tabs]="tabs" [(active)]="tab" />  then  @switch (tab()) { … }
 */
@Component({
  selector: 'fu-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="tabs" role="tablist" [attr.aria-label]="ariaLabel()">
    @for (t of tabs(); track t.key; let i = $index) {
      <button
        type="button"
        role="tab"
        class="tab"
        [class.active]="t.key === active()"
        [attr.aria-selected]="t.key === active()"
        [attr.tabindex]="t.key === active() ? 0 : -1"
        (click)="active.set(t.key)"
        (keydown)="onKey($event, i)"
      >
        {{ t.label }}
        @if (t.count !== undefined && t.count !== null) {
          <span class="count-pill" [class]="t.tone ?? ''">{{ t.count }}</span>
        }
      </button>
    }
  </div>`,
  host: { style: 'display:block' },
})
export class TabsComponent {
  readonly tabs = input.required<TabItem[]>();
  readonly active = model<string>('');
  readonly ariaLabel = input('Sections');

  protected onKey(e: KeyboardEvent, i: number): void {
    const list = this.tabs();
    let next = -1;
    if (e.key === 'ArrowRight') next = (i + 1) % list.length;
    else if (e.key === 'ArrowLeft') next = (i - 1 + list.length) % list.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = list.length - 1;
    if (next < 0) return;
    e.preventDefault();
    this.active.set(list[next].key);
    const buttons = (e.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLButtonElement>('button[role=tab]');
    buttons?.[next]?.focus();
  }
}
