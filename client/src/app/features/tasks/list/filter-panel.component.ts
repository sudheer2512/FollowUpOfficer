import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { Guid } from '../../../core/models/common.models';
import {
  CRITICALITY_VALUES,
  Criticality,
  DeadlineBucket,
  FILTER_BUCKET_VALUES,
  TASK_HEALTH_VALUES,
  TASK_PRIORITY_VALUES,
  TaskHealth,
  TaskPriority,
  WORK_STATUS_VALUES,
  WorkStatus,
} from '../../../core/models/enums';
import { TaskFilter } from '../../../core/models/task.models';
import { LookupsStore } from '../../../core/state/lookups.store';
import { healthTone } from '../../../core/util/tone';
import { DateInputComponent } from '../../../shared/forms/date-input.component';
import { LookupSelectComponent } from '../../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../../shared/forms/person-picker.component';
import { TeamPickerComponent } from '../../../shared/forms/team-picker.component';
import { HumanizePipe } from '../../../shared/pipes/format.pipes';

type ListKey = 'statuses' | 'priorities' | 'criticalities' | 'health';
type ToggleKey = 'recurring' | 'blocked' | 'waitingForCeo' | 'noResponse' | 'awaitingApproval' | 'topLevelOnly';
/** Criteria edited through ControlValueAccessor pickers (synced from the URL, emitted on change). */
type PickerKey = 'assignedToId' | 'assignedById' | 'teamId' | 'departmentId' | 'projectId' | 'dueFrom' | 'dueTo';

const PICKER_KEYS: PickerKey[] = ['assignedToId', 'assignedById', 'teamId', 'departmentId', 'projectId', 'dueFrom', 'dueTo'];

/** A one-key TaskFilter patch (typed alternative to a computed-key object literal). */
function patchOf<K extends keyof TaskFilter>(key: K, value: TaskFilter[K] | undefined): Partial<TaskFilter> {
  const patch: Partial<TaskFilter> = {};
  patch[key] = value;
  return patch;
}

/**
 * Every TaskFilter criterion as form controls. Stateless with respect to the filter: it renders `filter` (parsed from the
 * URL by the page) and emits `patch` with the changed keys; `undefined` removes a criterion.
 */
@Component({
  selector: 'fu-task-filter-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, PersonPickerComponent, TeamPickerComponent, LookupSelectComponent, DateInputComponent, HumanizePipe],
  templateUrl: './filter-panel.component.html',
  styleUrl: './filter-panel.component.css',
})
export class FilterPanelComponent {
  private readonly lookups = inject(LookupsStore);

  readonly filter = input.required<TaskFilter>();
  readonly patch = output<Partial<TaskFilter>>();

  protected readonly statusOptions = WORK_STATUS_VALUES;
  protected readonly priorityOptions = TASK_PRIORITY_VALUES;
  protected readonly criticalityOptions = CRITICALITY_VALUES;
  protected readonly healthOptions = TASK_HEALTH_VALUES;
  protected readonly bucketOptions = FILTER_BUCKET_VALUES;
  protected readonly toggles: { key: ToggleKey; label: string }[] = [
    { key: 'recurring', label: 'Recurring tasks' },
    { key: 'blocked', label: 'Blocked' },
    { key: 'waitingForCeo', label: 'Waiting for CEO' },
    { key: 'noResponse', label: 'No response' },
    { key: 'awaitingApproval', label: 'Awaiting approval' },
    { key: 'topLevelOnly', label: 'Top-level tasks only' },
  ];
  protected readonly tags = this.lookups.tags;

  protected readonly controls: Record<PickerKey, FormControl<string | null>> = {
    assignedToId: new FormControl<Guid | null>(null),
    assignedById: new FormControl<Guid | null>(null),
    teamId: new FormControl<Guid | null>(null),
    departmentId: new FormControl<Guid | null>(null),
    projectId: new FormControl<Guid | null>(null),
    dueFrom: new FormControl<string | null>(null),
    dueTo: new FormControl<string | null>(null),
  };

  /** Selected values per list criterion, as sets for O(1) lookups in the template. */
  protected readonly selected = computed(() => {
    const f = this.filter();
    return {
      statuses: new Set<string>(f.statuses ?? []),
      priorities: new Set<string>(f.priorities ?? []),
      criticalities: new Set<string>(f.criticalities ?? []),
      health: new Set<string>(f.health ?? []),
    };
  });

  constructor() {
    this.lookups.load();
    const destroyRef = inject(DestroyRef);

    // URL → pickers. emitEvent:false so syncing never echoes back as a change.
    effect(() => {
      const f = this.filter();
      untracked(() => {
        for (const key of PICKER_KEYS) {
          const value = (f[key] as string | null | undefined) ?? null;
          if (this.controls[key].value !== value) this.controls[key].setValue(value, { emitEvent: false });
        }
      });
    });

    // Pickers → patch.
    for (const key of PICKER_KEYS) {
      this.controls[key].valueChanges.pipe(takeUntilDestroyed(destroyRef)).subscribe((v) => this.patch.emit(patchOf(key, v || undefined)));
    }
  }

  protected toggleList(key: ListKey, value: WorkStatus | TaskPriority | Criticality | TaskHealth): void {
    const current = [...this.selected()[key]];
    const next = current.includes(value) ? current.filter((v) => v !== value) : [...current, value];
    this.patch.emit(patchOf(key, (next.length ? next : undefined) as TaskFilter[ListKey]));
  }

  protected setBucket(bucket: DeadlineBucket): void {
    this.patch.emit({ bucket: this.filter().bucket === bucket ? undefined : bucket });
  }

  protected setToggle(key: ToggleKey, on: boolean): void {
    this.patch.emit(patchOf(key, on ? true : undefined));
  }

  protected setMinDaysOverdue(raw: string): void {
    const n = Number(raw);
    this.patch.emit({ minDaysOverdue: raw === '' || isNaN(n) || n < 0 ? undefined : Math.trunc(n) });
  }

  protected setTag(raw: string): void {
    const tag = raw.trim().replace(/^#/, '');
    this.patch.emit({ tag: tag || undefined });
  }

  protected healthTone(h: TaskHealth): string {
    return healthTone(h);
  }
}
