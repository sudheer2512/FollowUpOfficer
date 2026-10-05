import { LowerCasePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LinkedTaskRef } from '../../core/models/view.models';
import { ConfidencePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { confidenceColor, confidenceValue, linkStatusTone } from './message-ui';

/**
 * One message ↔ task link: task, match method, confidence bar, explanation, signals and (for suggestions)
 * Confirm / Reject. Used by the review queue and the message drawer.
 */
@Component({
  selector: 'fu-link-suggestion',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, HumanizePipe, ConfidencePipe, LowerCasePipe],
  template: `
    <div class="head">
      <a class="task" [routerLink]="['/tasks', link().taskId]">
        <span class="task-number">{{ link().number }}</span>
        <span class="ttl truncate">{{ link().title }}</span>
      </a>
      <span class="badge tone-{{ statusTone() }}">{{ link().status }}</span>
    </div>
    <div class="meta">
      <span class="muted">Matched by {{ link().method | humanize | lowercase }}</span>
      <span class="conf" [attr.title]="'Confidence ' + (link().confidence | confidence)">
        <span class="bar" aria-hidden="true"><span [style.width.%]="pct()" [style.background]="color()"></span></span>
        <span class="num strong">{{ link().confidence | confidence }}</span>
      </span>
    </div>
    @if (link().explanation) {
      <p class="why">{{ link().explanation }}</p>
    }
    @if (signals().length) {
      <div class="signals">
        @for (s of signals(); track s) {
          <span class="chip sm outline">{{ s }}</span>
        }
      </div>
    }
    @if (link().status === 'Suggested' && actions()) {
      <div class="row acts">
        <button type="button" class="btn btn-sm btn-good" [disabled]="busy()" (click)="confirmed.emit(link())">
          <fu-icon name="check" [size]="14" /> Confirm
        </button>
        <button type="button" class="btn btn-sm btn-danger" [disabled]="busy()" (click)="rejected.emit(link())">
          <fu-icon name="x" [size]="14" /> Reject
        </button>
      </div>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 6px; padding: 10px 12px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); min-width: 0; }
    :host(.suggested) { border-style: dashed; border-color: color-mix(in srgb, var(--warning) 45%, var(--border)); }
    .head { display: flex; align-items: center; gap: 8px; min-width: 0; }
    .task { display: flex; align-items: center; gap: 6px; min-width: 0; flex: 1; color: var(--text); }
    .ttl { font-weight: 500; }
    .meta { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; font-size: 12.5px; }
    .conf { display: inline-flex; align-items: center; gap: 6px; }
    .bar { display: inline-block; width: 64px; height: 5px; border-radius: 999px; background: var(--bg-sunken); overflow: hidden; }
    .bar > span { display: block; height: 100%; border-radius: inherit; }
    .why { margin: 0; font-size: 13px; color: var(--text-2); }
    .signals { display: flex; flex-wrap: wrap; gap: 4px; }
    .acts { margin-top: 2px; }
  `,
  host: { '[class.suggested]': 'link().status === "Suggested"' },
})
export class LinkSuggestionComponent {
  readonly link = input.required<LinkedTaskRef>();
  readonly busy = input(false);
  /** Show Confirm / Reject for suggestions. */
  readonly actions = input(true);
  readonly confirmed = output<LinkedTaskRef>();
  readonly rejected = output<LinkedTaskRef>();

  protected readonly pct = computed(() => confidenceValue(this.link().confidence));
  protected readonly color = computed(() => confidenceColor(this.pct()));
  protected readonly statusTone = computed(() => linkStatusTone(this.link().status));
  protected readonly signals = computed(() => this.link().signals ?? []);
}
