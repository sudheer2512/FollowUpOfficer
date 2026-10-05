import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap, tap } from 'rxjs';
import { RecurringApi } from '../../core/api/recurring.api';
import { problemMessage } from '../../core/http/problem';
import { OccurrencePreview, PreviewRequest } from '../../core/models/view.models';
import { parseDateOnly, toTimeInput } from '../../core/util/dates';
import { DateOnlyPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

interface PreviewRow {
  item: OccurrencePreview;
  /** Calendar block: the actual date, or the nominal date when skipped. */
  dow: string;
  day: number;
  month: string;
  skipped: boolean;
  shifted: boolean;
  dueTime: string;
}

/**
 * Live list of upcoming occurrences from POST /api/recurring/preview. Re-queries (debounced 400 ms) whenever the
 * request changes; holiday shifts are highlighted in amber and skipped occurrences are struck through.
 *   <fu-recurrence-preview [request]="previewRequest()" />
 */
@Component({
  selector: 'fu-recurrence-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DateOnlyPipe, DateTimePipe, EmptyStateComponent, IconComponent],
  template: `
    @if (!request()) {
      <fu-empty icon="calendar" [compact]="true" title="Nothing to preview yet" [message]="emptyHint()" />
    } @else if (!loaded() || (pending() && !rows().length)) {
      <div class="stack" aria-busy="true" aria-label="Loading preview">
        @for (i of shimmer(); track i) {
          <div class="sk-row"><div class="skeleton sk-date"></div><div class="stack grow"><div class="skeleton sk-l1"></div><div class="skeleton sk-l2"></div></div></div>
        }
      </div>
    } @else if (error()) {
      <p class="small muted quiet"><fu-icon name="info" [size]="14" /> Preview unavailable right now. {{ error() }}</p>
    } @else if (!rows().length) {
      <fu-empty icon="calendar" [compact]="true" title="No upcoming occurrences" message="Check the start and end dates, or the rule itself." />
    } @else {
      <ol class="occ" [class.stale]="pending()" aria-live="polite">
        @for (r of rows(); track r.item.key + r.item.nominalDate) {
          <li [class.skipped]="r.skipped" [class.shifted]="r.shifted">
            <div class="cal" aria-hidden="true">
              <span class="dow">{{ r.dow }}</span>
              <span class="day">{{ r.day }}</span>
              <span class="mon">{{ r.month }}</span>
            </div>
            <div class="what">
              <div class="ttl truncate" [attr.title]="r.item.title">{{ r.item.title }}</div>
              <div class="meta">
                <span class="mono">{{ r.item.key }}</span>
                @if (!r.skipped) {
                  <span>Created {{ r.item.createAt | dateTime: 'short' }}</span>
                  @if (r.item.dueDate) {
                    <span>Due {{ r.item.dueDate | dateOnly: 'weekday' }}{{ r.dueTime ? ' ' + r.dueTime : '' }}</span>
                  }
                }
              </div>
              @if (r.skipped) {
                <div class="note skip">Skipped{{ r.item.note ? ' — ' + r.item.note : '' }}</div>
              } @else if (r.shifted) {
                <div class="note shift">
                  <fu-icon name="alert-triangle" [size]="12" />
                  <span>{{ r.item.note || 'Shifted from ' + (r.item.nominalDate | dateOnly: 'weekday') }}</span>
                </div>
              }
            </div>
          </li>
        }
      </ol>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .occ { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; transition: opacity .15s; }
    .occ.stale { opacity: .55; }
    .occ li { display: flex; gap: 10px; padding: 8px 0; border-bottom: 1px solid var(--border); min-width: 0; }
    .occ li:last-child { border-bottom: 0; }
    .cal { flex: none; width: 42px; border: 1px solid var(--border); border-radius: 7px; display: flex; flex-direction: column; align-items: center; padding: 3px 0; background: var(--surface-2); line-height: 1.1; }
    .dow, .mon { font-size: 10.5px; color: var(--muted); text-transform: uppercase; letter-spacing: .03em; }
    .day { font-size: 16px; font-weight: 600; font-variant-numeric: tabular-nums; }
    .shifted .cal { border-color: color-mix(in srgb, var(--warning) 55%, transparent); background: var(--warning-soft); }
    .skipped .cal { opacity: .6; }
    .skipped .day { text-decoration: line-through; }
    .what { flex: 1; min-width: 0; }
    .ttl { font-weight: 500; font-size: 13px; }
    .skipped .ttl { color: var(--muted); }
    .meta { display: flex; flex-wrap: wrap; gap: 2px 10px; font-size: 11.5px; color: var(--muted); margin-top: 1px; }
    .meta .mono { font-size: 11px; }
    .note { font-size: 11.5px; margin-top: 3px; display: flex; gap: 4px; align-items: flex-start; }
    .note.shift { color: var(--warning); }
    .note.skip { color: var(--muted); }
    .quiet { display: flex; gap: 6px; align-items: center; margin: 0; }
    .sk-row { display: flex; gap: 10px; align-items: center; padding: 6px 0; }
    .sk-date { width: 42px; height: 40px; border-radius: 7px; flex: none; }
    .sk-l1 { height: 11px; width: 80%; }
    .sk-l2 { height: 9px; width: 55%; }
    .grow { flex: 1; gap: 6px; }
  `,
})
export class RecurrencePreviewComponent {
  private readonly api = inject(RecurringApi);

  /** null = nothing to preview (e.g. an anchor rule without anchor dates). */
  readonly request = input<PreviewRequest | null>(null);
  readonly emptyHint = input('Complete the schedule to see the upcoming occurrences.');

  protected readonly items = signal<OccurrencePreview[]>([]);
  protected readonly pending = signal(false);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly shimmer = computed(() => Array.from({ length: Math.min(this.request()?.count ?? 5, 8) }, (_, i) => i));

  protected readonly rows = computed<PreviewRow[]>(() =>
    this.items().map((item) => {
      const skipped = !item.date;
      const d = parseDateOnly(item.date ?? item.nominalDate);
      return {
        item,
        dow: d ? DAYS[d.getDay()] : '',
        day: d ? d.getDate() : 0,
        month: d ? MONTHS[d.getMonth()] : '',
        skipped,
        shifted: !skipped && item.date !== item.nominalDate,
        dueTime: toTimeInput(item.dueTime),
      };
    }),
  );

  constructor() {
    toObservable(this.request)
      .pipe(
        distinctUntilChanged((a, b) => JSON.stringify(a) === JSON.stringify(b)),
        tap((req) => this.pending.set(!!req)),
        debounceTime(400),
        switchMap((req) => {
          if (!req) return of({ items: [] as OccurrencePreview[], error: null as string | null });
          return this.api.preview(req).pipe(
            map((items) => ({ items: items ?? [], error: null as string | null })),
            catchError((e: unknown) => of({ items: [] as OccurrencePreview[], error: problemMessage(e) })),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((result) => {
        this.items.set(result.items);
        this.error.set(result.error);
        this.pending.set(false);
        this.loaded.set(true);
      });
  }
}
