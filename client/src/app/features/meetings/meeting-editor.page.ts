import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormArray, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { SessionState } from '../../core/auth/session-state';
import { ActionItemDto, MeetingDto, MeetingInput } from '../../core/models/admin.models';
import { DateOnly, Guid } from '../../core/models/common.models';
import { CRITICALITY_VALUES, Criticality } from '../../core/models/enums';
import { FollowUpSchedule } from '../../core/models/rules.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { fromLocalDateTimeInput, toLocalDateTimeInput } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { StatusBadgeComponent } from '../../shared/domain/indicators';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { FollowUpPickerComponent, scheduleForPreset } from '../../shared/forms/follow-up-picker.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../shared/forms/person-picker.component';
import { DateOnlyPipe, DateTimePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { DialogService } from '../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { actionItemCounts, actionStatusTone, taskCandidates } from './meeting-util';

type ActionRowForm = FormGroup<{
  id: FormControl<Guid | null>;
  decision: FormControl<string>;
  actionText: FormControl<string>;
  ownerId: FormControl<Guid | null>;
  dueDate: FormControl<DateOnly | null>;
  criticality: FormControl<Criticality>;
}>;

/** "Now", rounded down to the hour, as a datetime-local value. */
function defaultHeldAt(): string {
  const d = new Date();
  d.setMinutes(0, 0, 0);
  return toLocalDateTimeInput(d.toISOString());
}

/**
 * /meetings/new and /meetings/:id — meeting details plus a "Decisions & action items" grid (FormArray).
 * For saved meetings a side panel turns open action items into delegated tasks with a follow-up schedule and lists the
 * tasks that were created.
 */
@Component({
  selector: 'fu-meeting-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    PersonPickerComponent,
    LookupSelectComponent,
    DateInputComponent,
    FollowUpPickerComponent,
    StatusBadgeComponent,
    EmptyStateComponent,
    SkeletonComponent,
    IconComponent,
    HumanizePipe,
    DateOnlyPipe,
    DateTimePipe,
  ],
  templateUrl: './meeting-editor.page.html',
  styleUrl: './meeting-editor.page.css',
  host: { '(window:beforeunload)': 'onBeforeUnload($event)' },
})
export class MeetingEditorPage {
  private readonly api = inject(PlanningApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly session = inject(SessionState);
  private readonly fb = inject(FormBuilder).nonNullable;

  /** Route parameter :id (absent on /meetings/new). */
  readonly id = input<Guid | undefined>(undefined);

  protected readonly criticalities = CRITICALITY_VALUES;
  protected readonly data = loader<MeetingDto>();
  protected readonly isNew = computed(() => !this.id());
  protected readonly meeting = computed(() => this.data.value() ?? null);
  protected readonly counts = computed(() => actionItemCounts(this.meeting()?.actionItems));

  protected readonly saving = signal(false);
  protected readonly creating = signal(false);
  protected readonly dirty = signal(false);
  protected readonly submitted = signal(false);
  /** Action items whose task was created by the last "Create tasks" run. */
  protected readonly created = signal<ActionItemDto[]>([]);
  /** Mirrors the FormArray controls so the zoneless view re-renders when rows are added or removed. */
  protected readonly rows = signal<ActionRowForm[]>([]);

  protected readonly form = this.fb.group({
    title: ['', [Validators.required, Validators.maxLength(300)]],
    heldAt: [defaultHeldAt(), Validators.required],
    organizerId: this.fb.control<Guid | null>(null),
    location: [''],
    participantIds: this.fb.control<Guid[]>([]),
    projectId: this.fb.control<Guid | null>(null),
    notes: [''],
    actionItems: new FormArray<ActionRowForm>([]),
  });

  // ── Create tasks from action items ──
  protected readonly candidates = computed(() => taskCandidates(this.meeting()?.actionItems));
  /** Selected candidates: new candidates start checked; a refresh keeps the user's earlier choices. */
  protected readonly selected = linkedSignal<ActionItemDto[], Set<Guid>>({
    source: this.candidates,
    computation: (candidates, previous) => {
      if (!previous) return new Set(candidates.map((a) => a.id));
      const known = new Set(previous.source.map((a) => a.id));
      return new Set(candidates.filter((a) => previous.value.has(a.id) || !known.has(a.id)).map((a) => a.id));
    },
  });
  protected readonly allSelected = computed(() => this.candidates().length > 0 && this.selected().size === this.candidates().length);
  protected readonly someSelected = computed(() => this.selected().size > 0 && !this.allSelected());
  protected readonly unownedSelected = computed(() => this.candidates().filter((a) => this.selected().has(a.id) && !a.owner).length);
  protected readonly followUp = new FormControl<FollowUpSchedule>(scheduleForPreset('every2'), { nonNullable: true });

  private readonly itemsById = computed(() => new Map((this.meeting()?.actionItems ?? []).map((a) => [a.id, a])));

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => {
        if (id) this.data.load(this.api.meeting(id));
        else this.resetForNew();
      });
    });
    effect(() => {
      const m = this.data.value();
      if (m) untracked(() => this.applyMeeting(m));
    });
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.dirty.set(this.form.dirty));
    // Keep task statuses of converted action items fresh, but never overwrite the user's unsaved edits.
    refreshOn(['task.changed'], () => {
      const id = this.id();
      if (id && !this.dirty() && !this.saving() && !this.creating()) this.data.load(this.api.meeting(id), { silent: true });
    });
  }

  protected reload(): void {
    const id = this.id();
    if (id) this.data.load(this.api.meeting(id));
  }

  /** The saved action item behind a row (for its task link and status). */
  protected existing(row: ActionRowForm): ActionItemDto | undefined {
    const id = row.controls.id.value;
    return id ? this.itemsById().get(id) : undefined;
  }

  protected statusTone(item: ActionItemDto): string {
    return actionStatusTone(item.status);
  }

  protected addRow(): void {
    const array = this.form.controls.actionItems;
    array.push(this.row());
    this.syncRows();
    this.markDirty();
    const index = array.length - 1;
    setTimeout(() => document.getElementById(`mt-act-${index}`)?.focus());
  }

  protected async removeRow(index: number): Promise<void> {
    const row = this.rows()[index];
    if (!row) return;
    const item = this.existing(row);
    if (item?.taskId) {
      const ok = await this.dialog.confirm({
        title: 'Remove action item',
        message: `Remove this action item from the meeting? Task ${item.taskNumber ?? ''} is not affected and stays open.`,
        confirmText: 'Remove',
        danger: true,
      });
      if (!ok) return;
    }
    this.form.controls.actionItems.removeAt(index);
    this.syncRows();
    this.markDirty();
  }

  protected toggle(id: Guid, on: boolean): void {
    this.selected.update((s) => {
      const next = new Set(s);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  protected toggleAll(on: boolean): void {
    this.selected.set(new Set(on ? this.candidates().map((a) => a.id) : []));
  }

  protected save(): void {
    this.persist();
  }

  protected async createTasks(): Promise<void> {
    if (!this.selected().size || this.creating() || this.saving()) return;
    if (this.dirty()) {
      const ok = await this.dialog.confirm({
        title: 'Unsaved changes',
        message: 'This meeting has unsaved changes. Save them first, then create the tasks?',
        confirmText: 'Save and create tasks',
      });
      if (ok) this.persist((m) => this.runCreate(m));
      return;
    }
    const m = this.meeting();
    if (m) this.runCreate(m);
  }

  protected onBeforeUnload(e: BeforeUnloadEvent): void {
    if (this.dirty()) e.preventDefault();
  }

  // ── internals ──

  private row(a?: ActionItemDto): ActionRowForm {
    return this.fb.group({
      id: this.fb.control<Guid | null>(a?.id ?? null),
      decision: [a?.decision ?? ''],
      actionText: [a?.actionText ?? '', [Validators.required, Validators.maxLength(1000)]],
      ownerId: this.fb.control<Guid | null>(a?.owner?.id ?? null),
      dueDate: this.fb.control<DateOnly | null>(a?.dueDate ?? null),
      criticality: this.fb.control<Criticality>(a?.criticality ?? 'Normal'),
    });
  }

  private syncRows(): void {
    this.rows.set([...this.form.controls.actionItems.controls]);
  }

  private markDirty(): void {
    this.form.markAsDirty();
    this.dirty.set(true);
  }

  private resetForNew(): void {
    const array = this.form.controls.actionItems;
    array.clear({ emitEvent: false });
    array.push(this.row(), { emitEvent: false });
    this.form.reset(
      {
        title: '',
        heldAt: defaultHeldAt(),
        organizerId: this.session.employeeId(),
        location: '',
        participantIds: [],
        projectId: null,
        notes: '',
      },
      { emitEvent: false },
    );
    this.syncRows();
    this.submitted.set(false);
    this.dirty.set(false);
  }

  private applyMeeting(m: MeetingDto): void {
    const array = this.form.controls.actionItems;
    array.clear({ emitEvent: false });
    for (const a of m.actionItems ?? []) array.push(this.row(a), { emitEvent: false });
    this.form.patchValue(
      {
        title: m.title ?? '',
        heldAt: toLocalDateTimeInput(m.heldAt) || defaultHeldAt(),
        organizerId: m.organizer?.id ?? null,
        location: m.location ?? '',
        participantIds: (m.participants ?? []).map((p) => p.id),
        projectId: m.project?.id ?? null,
        notes: m.notes ?? '',
      },
      { emitEvent: false },
    );
    this.form.markAsPristine();
    this.syncRows();
    this.dirty.set(false);
  }

  /** Rows the user added but left completely empty are dropped instead of blocking the save. */
  private dropBlankRows(): void {
    const array = this.form.controls.actionItems;
    for (let i = array.length - 1; i >= 0; i--) {
      const v = array.at(i).getRawValue();
      if (!v.id && !v.actionText.trim() && !v.decision.trim()) array.removeAt(i, { emitEvent: false });
    }
    this.syncRows();
  }

  private toInput(): MeetingInput | null {
    const v = this.form.getRawValue();
    const heldAt = fromLocalDateTimeInput(v.heldAt);
    if (!heldAt) return null;
    return {
      title: v.title.trim(),
      heldAt,
      organizerId: v.organizerId,
      location: v.location.trim() || null,
      notes: v.notes.trim() || null,
      projectId: v.projectId,
      participantIds: v.participantIds ?? [],
      actionItems: v.actionItems.map((a) => ({
        id: a.id,
        decision: a.decision.trim() || null,
        actionText: a.actionText.trim(),
        ownerId: a.ownerId,
        dueDate: a.dueDate || null,
        criticality: a.criticality,
      })),
    };
  }

  /** Creates or updates the meeting; `after` runs with the saved meeting (existing meetings only). */
  private persist(after?: (m: MeetingDto) => void): void {
    if (this.saving()) return;
    this.submitted.set(true);
    this.dropBlankRows();
    const input = this.form.valid ? this.toInput() : null;
    if (!input) {
      this.form.markAllAsTouched();
      this.toast.warning('Please complete the highlighted fields.');
      return;
    }
    const id = this.id();
    this.saving.set(true);
    (id ? this.api.updateMeeting(id, input) : this.api.createMeeting(input)).subscribe({
      next: (m) => {
        this.saving.set(false);
        this.toast.success(id ? 'Meeting saved' : 'Meeting created');
        if (!id) {
          this.form.markAsPristine();
          this.dirty.set(false);
          void this.router.navigate(['/meetings', m.id], { replaceUrl: true });
          return;
        }
        this.data.set(m);
        after?.(m);
      },
      error: () => this.saving.set(false),
    });
  }

  private runCreate(before: MeetingDto): void {
    const id = this.id();
    if (!id) return;
    const valid = new Set(taskCandidates(before.actionItems).map((a) => a.id));
    const actionItemIds = [...this.selected()].filter((x) => valid.has(x));
    if (!actionItemIds.length) return;
    const hadTask = new Set((before.actionItems ?? []).filter((a) => a.taskId).map((a) => a.id));

    this.creating.set(true);
    this.api.createActionTasks(id, { actionItemIds, followUp: this.followUp.value }).subscribe({
      next: (m) => {
        this.creating.set(false);
        const created = (m?.actionItems ?? []).filter((a) => !!a.taskId && !hadTask.has(a.id));
        this.created.set(created);
        if (m) this.data.set(m);
        this.toast.success(created.length === 1 ? '1 task created' : `${created.length} tasks created`);
      },
      error: () => this.creating.set(false),
    });
  }
}
