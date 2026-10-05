import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { tap } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { EscalationPolicyDto, EscalationPolicyInput, EscalationStepDto } from '../../../core/models/admin.models';
import { Guid } from '../../../core/models/common.models';
import {
  CRITICALITY_VALUES,
  ESCALATION_TARGET_VALUES,
  ESCALATION_TRIGGER_VALUES,
  EscalationTrigger,
  NOTIFY_CHANNEL_VALUES,
  THRESHOLD_UNIT_VALUES,
  ThresholdUnit,
} from '../../../core/models/enums';
import { LookupsStore } from '../../../core/state/lookups.store';
import { parseFlags, toFlags } from '../../../core/util/flags';
import { loader } from '../../../core/util/loader';
import { HumanizePipe } from '../../../shared/pipes/format.pipes';
import { DialogService } from '../../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { checkedOf, formView, inOrder, textOrNull, toggleInList } from '../shared/form-view';
import { describeStep } from './step-text';

type StepGroup = FormGroup<{
  id: FormControl<Guid | null>;
  trigger: FormControl<EscalationTrigger>;
  threshold: FormControl<number>;
  unit: FormControl<ThresholdUnit>;
  targets: FormControl<string[]>;
  channels: FormControl<string[]>;
  level: FormControl<number>;
  raiseCeoAttention: FormControl<boolean>;
  description: FormControl<string>;
}>;

const LEVELS = [1, 2, 3, 4, 5];
/** Quick tokens for "applies to": the default policy marker plus every criticality. */
const APPLIES_TO_TOKENS = ['Default', ...CRITICALITY_VALUES];

function splitList(value: string): string[] {
  return value
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

/** /admin/escalation — escalation policies and their ordered steps. */
@Component({
  selector: 'fu-escalation-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HumanizePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  templateUrl: './escalation.page.html',
  styleUrl: './escalation.page.css',
})
export class EscalationPage {
  private readonly api = inject(AdminApi);
  private readonly lookups = inject(LookupsStore);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly triggers = ESCALATION_TRIGGER_VALUES;
  protected readonly units = THRESHOLD_UNIT_VALUES;
  protected readonly targetValues = ESCALATION_TARGET_VALUES;
  protected readonly channelValues = NOTIFY_CHANNEL_VALUES;
  protected readonly levels = LEVELS;
  protected readonly appliesToTokens = APPLIES_TO_TOKENS;

  protected readonly data = loader<EscalationPolicyDto[]>();
  protected readonly policies = computed(() =>
    (this.data.value() ?? [])
      .map((p) => ({ ...p, steps: p.steps ?? [] }))
      .sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || (a.name ?? '').localeCompare(b.name ?? '')),
  );
  protected readonly selectedId = signal<Guid | 'new' | null>(null);
  protected readonly selected = computed(() => {
    const id = this.selectedId();
    return id && id !== 'new' ? (this.policies().find((p) => p.id === id) ?? null) : null;
  });
  protected readonly saving = signal(false);

  protected readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    description: [''],
    appliesTo: ['Default', [Validators.required]],
    isDefault: [false],
    isActive: [true],
    steps: this.fb.array<StepGroup>([]),
  });
  protected readonly state = formView(this.form);

  protected get steps(): FormArray<StepGroup> {
    return this.form.controls.steps;
  }

  /** A generated sentence for every step, recomputed on each edit. */
  protected readonly summaries = computed(() =>
    this.state().value.steps.map((s) =>
      describeStep({
        trigger: s.trigger,
        threshold: Number(s.threshold),
        unit: s.unit,
        targets: inOrder(ESCALATION_TARGET_VALUES, s.targets),
        channels: inOrder(NOTIFY_CHANNEL_VALUES, s.channels),
        level: s.level,
        raiseCeoAttention: s.raiseCeoAttention,
      }),
    ),
  );

  protected readonly appliesToSelected = computed(() => new Set(splitList(this.state().value.appliesTo).map((t) => t.toLowerCase())));

  /** Steps that would never reach anyone. */
  protected readonly stepsWithoutTargets = computed(() =>
    this.state()
      .value.steps.map((s, i) => (s.targets.length ? -1 : i))
      .filter((i) => i >= 0),
  );

  constructor() {
    this.reload();
  }

  protected reload(silent = false): void {
    this.data.load(this.api.escalationPolicies().pipe(tap((list) => this.ensureSelection(list ?? []))), { silent });
  }

  private ensureSelection(list: EscalationPolicyDto[]): void {
    const current = this.selectedId();
    if (current === 'new' || (current && list.some((p) => p.id === current))) return;
    const pick = list.find((p) => p.isDefault) ?? list[0];
    if (pick) this.load(pick);
    else this.selectedId.set(null);
  }

  /** Asks before throwing away unsaved edits. */
  private async canLeave(): Promise<boolean> {
    if (!this.form.dirty) return true;
    return this.dialog.confirm({
      title: 'Discard changes?',
      message: 'This policy has unsaved changes. Discard them?',
      confirmText: 'Discard',
      danger: true,
    });
  }

  protected async open(p: EscalationPolicyDto): Promise<void> {
    if (p.id === this.selectedId() || !(await this.canLeave())) return;
    this.load(p);
  }

  protected async openNew(): Promise<void> {
    if (!(await this.canLeave())) return;
    this.selectedId.set('new');
    this.resetForm({
      name: '',
      description: '',
      appliesTo: 'Default',
      isDefault: false,
      isActive: true,
      steps: [
        this.defaultStep(1, { trigger: 'NoResponse', threshold: 24, unit: 'Hours', targets: 'Assignee', level: 1 }),
        this.defaultStep(2, { trigger: 'Overdue', threshold: 1, unit: 'WorkingDays', targets: 'TeamLeader', level: 2 }),
        this.defaultStep(3, { trigger: 'Overdue', threshold: 3, unit: 'WorkingDays', targets: 'DepartmentHead, Ceo', level: 3, raiseCeoAttention: true }),
      ],
    });
  }

  private load(p: EscalationPolicyDto): void {
    this.selectedId.set(p.id);
    this.resetForm({
      name: p.name ?? '',
      description: p.description ?? '',
      appliesTo: p.appliesTo ?? '',
      isDefault: p.isDefault ?? false,
      isActive: p.isActive ?? true,
      steps: [...(p.steps ?? [])].sort((a, b) => (a.order ?? 0) - (b.order ?? 0)),
    });
  }

  private resetForm(v: Omit<EscalationPolicyInput, 'description'> & { description: string }): void {
    this.steps.clear({ emitEvent: false });
    for (const s of v.steps) this.steps.push(this.createStep(s), { emitEvent: false });
    this.form.reset({ name: v.name, description: v.description, appliesTo: v.appliesTo, isDefault: v.isDefault, isActive: v.isActive, steps: this.steps.getRawValue() });
  }

  private defaultStep(order: number, partial: Partial<EscalationStepDto>): EscalationStepDto {
    return {
      id: null,
      order,
      trigger: 'Overdue',
      threshold: 1,
      unit: 'WorkingDays',
      targets: 'TeamLeader',
      channels: 'Email, InApp',
      level: Math.min(5, order),
      raiseCeoAttention: false,
      description: null,
      ...partial,
    };
  }

  private createStep(s: EscalationStepDto): StepGroup {
    return this.fb.group({
      id: [s.id ?? (null as Guid | null)],
      trigger: [s.trigger ?? ('Overdue' as EscalationTrigger)],
      threshold: [s.threshold ?? 1, [Validators.required, Validators.min(0)]],
      unit: [s.unit ?? ('WorkingDays' as ThresholdUnit)],
      targets: [parseFlags(s.targets)],
      channels: [parseFlags(s.channels)],
      level: [s.level ?? 1, [Validators.min(1), Validators.max(5)]],
      raiseCeoAttention: [s.raiseCeoAttention ?? false],
      description: [s.description ?? ''],
    });
  }

  protected addStep(): void {
    const last = this.steps.length ? this.steps.at(this.steps.length - 1).getRawValue() : null;
    const order = this.steps.length + 1;
    this.steps.push(
      this.createStep(
        this.defaultStep(order, {
          trigger: last?.trigger ?? 'Overdue',
          threshold: last ? Number(last.threshold) + 1 : 1,
          unit: last?.unit ?? 'WorkingDays',
          channels: last ? toFlags(last.channels) : 'Email, InApp',
          level: Math.min(5, (last?.level ?? 0) + 1),
        }),
      ),
    );
    this.form.markAsDirty();
  }

  protected removeStep(i: number): void {
    this.steps.removeAt(i);
    this.form.markAsDirty();
  }

  /** Reorders a step; the order number is always its position (index + 1). */
  protected moveStep(i: number, delta: -1 | 1): void {
    const j = i + delta;
    if (j < 0 || j >= this.steps.length) return;
    const control = this.steps.at(i);
    this.steps.removeAt(i, { emitEvent: false });
    this.steps.insert(j, control);
    this.form.markAsDirty();
  }

  protected toggleIn(control: FormControl<string[]>, value: string, event: Event): void {
    toggleInList(control, value, checkedOf(event));
  }

  protected isApplied(token: string): boolean {
    return this.appliesToSelected().has(token.toLowerCase());
  }

  /** Adds or removes a token in the comma-separated "applies to" text. */
  protected toggleAppliesTo(token: string): void {
    const control = this.form.controls.appliesTo;
    const list = splitList(control.value);
    const exists = list.some((t) => t.toLowerCase() === token.toLowerCase());
    const next = exists ? list.filter((t) => t.toLowerCase() !== token.toLowerCase()) : [...list, token];
    control.setValue(next.join(', '));
    control.markAsDirty();
  }

  protected discard(): void {
    const p = this.selected();
    if (p) {
      this.load(p);
      return;
    }
    this.form.markAsPristine();
    this.selectedId.set(null);
    this.ensureSelection(this.policies());
  }

  protected save(): void {
    if (this.saving()) return;
    if (this.form.invalid || this.stepsWithoutTargets().length) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const input: EscalationPolicyInput = {
      name: v.name.trim(),
      description: textOrNull(v.description),
      appliesTo: splitList(v.appliesTo).join(', '),
      isDefault: v.isDefault,
      isActive: v.isActive,
      steps: v.steps.map((s, i) => ({
        id: s.id,
        order: i + 1,
        trigger: s.trigger,
        threshold: Number(s.threshold) || 0,
        unit: s.unit,
        targets: toFlags(inOrder(ESCALATION_TARGET_VALUES, s.targets)),
        channels: toFlags(inOrder(NOTIFY_CHANNEL_VALUES, s.channels)),
        level: Number(s.level) || 1,
        raiseCeoAttention: s.raiseCeoAttention,
        description: textOrNull(s.description),
      })),
    };
    const id = this.selectedId();
    const request = id && id !== 'new' ? this.api.updateEscalationPolicy(id, input) : this.api.createEscalationPolicy(input);
    this.saving.set(true);
    request.subscribe({
      next: (dto) => {
        this.saving.set(false);
        this.toast.success(id === 'new' ? `Policy “${input.name}” created` : `Policy “${input.name}” saved`);
        if (dto?.id) {
          this.load(dto);
        } else {
          // No body returned: let the reload pick the policy again.
          this.form.markAsPristine();
          if (id === 'new') this.selectedId.set(null);
        }
        // Default flags may have moved between policies.
        this.reload(true);
        this.lookups.refresh().subscribe({ error: () => undefined });
      },
      error: () => this.saving.set(false),
    });
  }
}
