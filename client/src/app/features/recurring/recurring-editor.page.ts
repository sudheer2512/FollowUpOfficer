import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { tap } from 'rxjs';
import { RecurringApi } from '../../core/api/recurring.api';
import { DateOnly, Guid } from '../../core/models/common.models';
import { CLOSED_STATUSES, ChangeScope } from '../../core/models/enums';
import { RecurringDefinitionDto, RecurringDefinitionInput, RecurringInstanceDto, RecurringUpdateRequest } from '../../core/models/view.models';
import { formatDateOnly, todayDateOnly } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalComponent } from '../../shared/ui/modal.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { rawValueSignal } from './form-signal';
import { RecurringFormComponent } from './recurring-form.component';
import {
  createRecurringForm,
  emptyRecurringInput,
  inputFromDto,
  patchRecurringForm,
  recurringInputFromForm,
  recurringProblems,
} from './recurring-form.model';

interface ScopeOption {
  value: ChangeScope;
  label: string;
  description: string;
}

const SCOPE_OPTIONS: ScopeOption[] = [
  {
    value: 'ThisInstance',
    label: 'This instance',
    description: 'Only one generated task takes the new priority, criticality and responsible person. The schedule stays as it is.',
  },
  {
    value: 'ThisAndFuture',
    label: 'This and future',
    description: 'Occurrences from the chosen date use the new settings. Earlier occurrences and their history keep the old ones.',
  },
  {
    value: 'EntireSeries',
    label: 'Entire series',
    description: 'The whole series changes, including the open tasks it already created.',
  },
];

/**
 * /recurring/new and /recurring/:id/edit — the recurring-definition form with a live preview.
 * Saving an existing definition asks for the scope (this instance / this and future / entire series).
 */
@Component({
  selector: 'fu-recurring-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, RecurringFormComponent, ModalComponent, DateInputComponent, IconComponent, SkeletonComponent],
  templateUrl: './recurring-editor.page.html',
  styles: `
    .grow { flex: 1; min-width: 0; }
    .problems { margin: 0; padding-left: 18px; }
    .scopes { display: flex; flex-direction: column; gap: 8px; }
    .scope { display: flex; gap: 10px; align-items: flex-start; padding: 10px 12px; border: 1px solid var(--border); border-radius: var(--radius-sm); cursor: pointer; }
    .scope.on { border-color: var(--accent); background: var(--accent-soft); }
    .scope.disabled { opacity: .55; cursor: not-allowed; }
    .scope input[type='radio'] { margin-top: 3px; accent-color: var(--accent); flex: none; }
    .scope-body { flex: 1; min-width: 0; }
    .desc { display: block; margin-top: 2px; }
    .scope-extra { margin-top: 8px; max-width: 320px; }
  `,
})
export class RecurringEditorPage {
  private readonly api = inject(RecurringApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder).nonNullable;

  /** Route param (absent on /recurring/new). */
  readonly id = input<string>();

  protected readonly form = createRecurringForm(this.fb);
  protected readonly existing = loader<RecurringDefinitionDto>();
  protected readonly isEdit = computed(() => !!this.id());
  protected readonly busy = signal(false);
  protected readonly problems = signal<string[]>([]);

  // ── Scope dialog ──
  protected readonly scopeOptions = SCOPE_OPTIONS;
  protected readonly scopeOpen = signal(false);
  protected readonly scopeForm = this.fb.group({
    scope: this.fb.control<ChangeScope>('EntireSeries'),
    fromDate: this.fb.control<DateOnly | null>(todayDateOnly()),
    instanceId: this.fb.control<Guid | null>(null),
  });
  protected readonly scopeValue = rawValueSignal(this.scopeForm);
  /** Generated occurrences whose task is still open — the candidates for a "this instance" change. */
  protected readonly openInstances = loader<RecurringInstanceDto[]>([]);
  protected readonly openList = computed(() => this.onlyOpen(this.openInstances.value()));
  /** The validated input waiting for the scope choice. */
  private pendingInput: RecurringDefinitionInput | null = null;

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  protected load(id = this.id()): void {
    this.problems.set([]);
    if (!id) {
      patchRecurringForm(this.form, emptyRecurringInput());
      return;
    }
    this.existing.load(this.api.get(id).pipe(tap((dto) => patchRecurringForm(this.form, inputFromDto(dto)))));
  }

  protected cancelLink(): unknown[] {
    const id = this.id();
    return id ? ['/recurring', id] : ['/recurring'];
  }

  protected save(): void {
    if (this.busy()) return;
    const input = recurringInputFromForm(this.form.getRawValue());
    const problems = recurringProblems(input);
    this.problems.set(problems);
    if (problems.length) {
      this.form.markAllAsTouched();
      return;
    }
    const id = this.id();
    if (id) {
      this.pendingInput = input;
      this.openScopeDialog(id);
      return;
    }
    this.busy.set(true);
    this.api.create(input).subscribe({
      next: (dto) => {
        this.form.markAsPristine();
        this.toast.success(`Recurring task “${dto?.titleTemplate ?? input.titleTemplate}” created`);
        void this.router.navigate(dto?.id ? ['/recurring', dto.id] : ['/recurring']);
      },
      error: () => this.busy.set(false),
    });
  }

  private openScopeDialog(id: Guid): void {
    this.scopeForm.setValue({ scope: 'EntireSeries', fromDate: todayDateOnly(), instanceId: null });
    this.openInstances.load(this.api.instances({ definitionId: id }).pipe(
      tap((list) => {
        const open = this.onlyOpen(list);
        if (open.length) this.scopeForm.controls.instanceId.setValue(open[0].id);
      }),
    ));
    this.scopeOpen.set(true);
  }

  private onlyOpen(list: RecurringInstanceDto[] | null | undefined): RecurringInstanceDto[] {
    return (list ?? [])
      .filter((i) => !!i.taskId && !CLOSED_STATUSES.includes(i.taskStatus ?? 'Completed'))
      .sort((a, b) => b.nominalDate.localeCompare(a.nominalDate));
  }

  protected instanceLabel(i: RecurringInstanceDto): string {
    return `${i.taskNumber ?? i.occurrenceKey} · ${i.occurrenceKey} · due ${formatDateOnly(i.dueDate) || '—'}`;
  }

  protected confirmScope(): void {
    const id = this.id();
    const input = this.pendingInput;
    if (!id || !input || this.busy()) return;
    const { scope, fromDate, instanceId } = this.scopeForm.getRawValue();
    if (scope === 'ThisInstance' && !instanceId) return;
    const req: RecurringUpdateRequest = {
      definition: input,
      scope,
      fromInstanceId: scope === 'ThisInstance' ? instanceId : null,
      fromDate: scope === 'ThisAndFuture' ? (fromDate ?? todayDateOnly()) : null,
    };
    this.busy.set(true);
    this.api.update(id, req).subscribe({
      next: (dto) => {
        this.scopeOpen.set(false);
        this.form.markAsPristine();
        this.toast.success(
          scope === 'ThisAndFuture'
            ? `Changes apply from ${formatDateOnly(req.fromDate)}`
            : scope === 'ThisInstance'
              ? 'The selected occurrence was updated'
              : 'Recurring task updated',
        );
        // A "this and future" change splits the series: the response is the new version.
        void this.router.navigate(['/recurring', dto?.id ?? id]);
      },
      error: () => this.busy.set(false),
    });
  }
}
