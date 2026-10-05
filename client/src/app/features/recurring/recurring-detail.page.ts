import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, firstValueFrom, of } from 'rxjs';
import { RecurringApi } from '../../core/api/recurring.api';
import { DateOnly } from '../../core/models/common.models';
import { OCCURRENCE_OUTCOME_VALUES, OccurrenceOutcome } from '../../core/models/enums';
import { OccurrencePreview, RecurringDefinitionDto, RecurringInstanceDto } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { addDays, formatDateOnly, todayDateOnly } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { CriticalityChipComponent, PersonComponent, PriorityChipComponent } from '../../shared/domain/indicators';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { DateOnlyPipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { DialogService } from '../../shared/ui/dialog.service';
import { DropdownComponent } from '../../shared/ui/dropdown.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalComponent } from '../../shared/ui/modal.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { OccurrenceHistoryComponent } from './occurrence-history.component';
import { RecurrencePreviewComponent } from './recurrence-preview.component';
import { barWidth, complianceTone, stateTone } from './recurring-display';
import { CATCH_UP_TEXT, HOLIDAY_HANDLING_TEXT, previewRequestOf } from './recurring-form.model';

/**
 * /recurring/:id — one recurring definition: schedule, people, compliance, the next occurrences and the full
 * occurrence history, with Pause / Resume / Skip next / End / Edit.
 */
@Component({
  selector: 'fu-recurring-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    ReactiveFormsModule,
    IconComponent,
    SkeletonComponent,
    EmptyStateComponent,
    ModalComponent,
    DropdownComponent,
    DateInputComponent,
    PersonComponent,
    PriorityChipComponent,
    CriticalityChipComponent,
    HumanizePipe,
    DateOnlyPipe,
    RecurrencePreviewComponent,
    OccurrenceHistoryComponent,
  ],
  templateUrl: './recurring-detail.page.html',
  styleUrl: './recurring-detail.page.css',
})
export class RecurringDetailPage {
  private readonly api = inject(RecurringApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(DialogService);
  private readonly fb = inject(FormBuilder).nonNullable;

  /** Route param. */
  readonly id = input<string>();

  protected readonly def = loader<RecurringDefinitionDto>();
  protected readonly instances = loader<RecurringInstanceDto[]>();
  protected readonly busy = signal(false);

  protected readonly holidayText = HOLIDAY_HANDLING_TEXT;
  protected readonly catchUpText = CATCH_UP_TEXT;
  protected readonly outcomes = OCCURRENCE_OUTCOME_VALUES;
  protected readonly outcomeFilter = signal<OccurrenceOutcome | ''>('');

  protected readonly view = computed(() => {
    const d = this.def.value();
    if (!d) return null;
    return {
      d,
      stateTone: stateTone(d.state),
      hasHistory: (d.generatedCount ?? 0) > 0,
      compliance: Math.round(d.compliancePercent ?? 0),
      complianceTone: complianceTone(d.compliancePercent),
      bar: barWidth(d.compliancePercent),
      additional: d.additionalAssignees ?? [],
      checklist: d.checklist ?? [],
      stats: {
        generated: d.generatedCount ?? 0,
        onTime: d.completedOnTime ?? 0,
        late: d.completedLate ?? 0,
        missed: d.missedCount ?? 0,
        open: d.openInstances ?? 0,
      },
    };
  });

  /** Next five occurrences of the stored rule (none once the series has ended). */
  protected readonly nextRequest = computed(() => {
    const d = this.def.value();
    if (!d || d.state === 'Ended') return null;
    return previewRequestOf(
      { rule: d.rule, due: d.due, holidayHandling: d.holidayHandling, startDate: d.startDate, endDate: d.endDate, titleTemplate: d.titleTemplate },
      5,
    );
  });

  protected readonly outcomeCounts = computed(() => {
    const counts: Partial<Record<string, number>> = {};
    for (const i of this.instances.value() ?? []) counts[i.outcome] = (counts[i.outcome] ?? 0) + 1;
    return counts;
  });

  protected readonly filteredInstances = computed(() => {
    const f = this.outcomeFilter();
    const list = [...(this.instances.value() ?? [])].sort((a, b) => (b.nominalDate ?? '').localeCompare(a.nominalDate ?? ''));
    return f ? list.filter((i) => i.outcome === f) : list;
  });

  // ── Pause dialog ──
  protected readonly pauseOpen = signal(false);
  protected readonly pauseForm = this.fb.group({
    reason: this.fb.control(''),
    resumeOn: this.fb.control<DateOnly | null>(null),
  });
  protected readonly tomorrow = addDays(todayDateOnly(), 1);

  constructor() {
    effect(() => {
      this.id();
      untracked(() => this.reload());
    });
    refreshOn(['task.changed', 'task.created'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    const id = this.id();
    if (!id) return;
    this.def.load(this.api.get(id), { silent });
    this.loadInstances(silent);
  }

  protected loadInstances(silent = false): void {
    const id = this.id();
    if (id) this.instances.load(this.api.instances({ definitionId: id }), { silent });
  }

  protected openPause(): void {
    this.pauseForm.reset({ reason: '', resumeOn: null });
    this.pauseOpen.set(true);
  }

  protected confirmPause(): void {
    const id = this.id();
    if (!id || this.busy()) return;
    const v = this.pauseForm.getRawValue();
    this.busy.set(true);
    this.api.pause(id, { reason: v.reason.trim() || null, resumeOn: v.resumeOn || null }).subscribe({
      next: (dto) => {
        this.afterAction(dto);
        this.pauseOpen.set(false);
        this.toast.success(v.resumeOn ? `Paused until ${formatDateOnly(v.resumeOn)}` : 'Recurring task paused');
      },
      error: () => this.busy.set(false),
    });
  }

  protected resume(): void {
    const id = this.id();
    if (!id || this.busy()) return;
    this.busy.set(true);
    this.api.resume(id).subscribe({
      next: (dto) => {
        this.afterAction(dto);
        this.toast.success('Recurring task resumed');
      },
      error: () => this.busy.set(false),
    });
  }

  /** Finds the next occurrence not yet processed, confirms it (with an optional reason) and records the skip. */
  protected async skipNext(): Promise<void> {
    const d = this.def.value();
    const id = this.id();
    if (!d || !id || this.busy()) return;
    const req = previewRequestOf(
      { rule: d.rule, due: d.due, holidayHandling: d.holidayHandling, startDate: d.startDate, endDate: d.endDate, titleTemplate: d.titleTemplate },
      4,
    );
    if (!req) return;
    this.busy.set(true);
    const upcoming = await firstValueFrom(this.api.preview(req).pipe(catchError(() => of([] as OccurrencePreview[]))));
    this.busy.set(false);
    const processed = d.lastProcessedOccurrence ?? '';
    const next = (upcoming ?? []).find((o) => o.nominalDate > processed);
    if (!next) {
      this.toast.info('There is no upcoming occurrence to skip.');
      return;
    }
    const when = formatDateOnly(next.date ?? next.nominalDate, { withWeekday: true });
    const reason = await this.dialogs.prompt({
      title: 'Skip the next occurrence?',
      message: `“${next.title || d.titleTemplate}” on ${when} (${next.key}) will not be created.\nLater occurrences are not affected.`,
      label: 'Reason (optional)',
      multiline: true,
      confirmText: 'Skip occurrence',
    });
    if (reason === null) return;
    this.busy.set(true);
    this.api.skip(id, { nominalDate: next.nominalDate, reason: reason.trim() || null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success(`Occurrence ${next.key} skipped`);
        this.reload(true);
      },
      error: () => this.busy.set(false),
    });
  }

  protected async end(): Promise<void> {
    const id = this.id();
    if (!id || this.busy()) return;
    const reason = await this.dialogs.prompt({
      title: 'End this recurring task?',
      message: 'No further occurrences will be created. Tasks already generated stay open and the history is kept.',
      label: 'Reason',
      multiline: true,
      required: true,
      confirmText: 'End series',
      danger: true,
    });
    if (reason === null) return;
    this.busy.set(true);
    this.api.end(id, reason.trim()).subscribe({
      next: (dto) => {
        this.afterAction(dto);
        this.toast.success('Recurring task ended');
      },
      error: () => this.busy.set(false),
    });
  }

  private afterAction(dto: RecurringDefinitionDto | null | undefined): void {
    this.busy.set(false);
    if (dto?.id) this.def.set(dto);
    else this.reload(true);
    this.loadInstances(true);
  }
}
