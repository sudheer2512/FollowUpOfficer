import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { TemplateDto, TemplateInput } from '../../core/models/admin.models';
import { Guid } from '../../core/models/common.models';
import { APPROVER_ROLE_VALUES, ApproverRole, CRITICALITY_VALUES, Criticality, TASK_PRIORITY_VALUES, TaskPriority } from '../../core/models/enums';
import { FollowUpSchedule } from '../../core/models/rules.models';
import { LookupsStore } from '../../core/state/lookups.store';
import { pluralize } from '../../core/util/humanize';
import { loader } from '../../core/util/loader';
import { FollowUpPickerComponent, scheduleForPreset } from '../../shared/forms/follow-up-picker.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { HumanizePipe } from '../../shared/pipes/format.pipes';
import { DialogService } from '../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { InstantiateTemplateDialogComponent } from './instantiate-dialog.component';
import {
  ItemDraft,
  ItemIssues,
  descendantUids,
  draftsFromItems,
  itemsFromDrafts,
  maxDueOffset,
  suggestKey,
  treeOrder,
  validateTemplateItems,
} from './template-tree';

const DEFAULT_ROLES = ['Owner'];

/**
 * Issue messages to show under an item. "Required" messages for an untouched empty key / title wait until the first
 * save attempt; relational problems (duplicates, loops, unknown keys) show immediately.
 */
function visibleMessages(issues: ItemIssues, item: ItemDraft, showRequired: boolean): string[] {
  return [
    showRequired || item.key.trim() ? issues.key : undefined,
    showRequired || item.title.trim() ? issues.title : undefined,
    issues.parent,
    issues.dependsOn,
    issues.assigneeRole,
    issues.dueOffset,
  ].filter((m): m is string => !!m);
}

/**
 * /templates/new and /templates/:id — template settings (reactive form), role placeholders, approval flow and the items
 * tree editor. Items reference each other by a client uid (see template-tree.ts), are rendered as a tree and validated
 * inline; saving is blocked until the tree is valid.
 */
@Component({
  selector: 'fu-template-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    LookupSelectComponent,
    FollowUpPickerComponent,
    InstantiateTemplateDialogComponent,
    EmptyStateComponent,
    SkeletonComponent,
    IconComponent,
    HumanizePipe,
  ],
  templateUrl: './template-editor.page.html',
  styleUrl: './template-editor.page.css',
  host: { '(window:beforeunload)': 'onBeforeUnload($event)' },
})
export class TemplateEditorPage {
  private readonly api = inject(PlanningApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly lookups = inject(LookupsStore);

  /** Route parameter :id (absent on /templates/new). */
  readonly id = input<Guid | undefined>(undefined);

  protected readonly priorities = TASK_PRIORITY_VALUES;
  protected readonly criticalities = CRITICALITY_VALUES;
  protected readonly data = loader<TemplateDto>();
  protected readonly isNew = computed(() => !this.id());
  protected readonly saving = signal(false);
  protected readonly submitted = signal(false);
  protected readonly instantiateOpen = signal(false);

  protected readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    category: ['', [Validators.required, Validators.maxLength(100)]],
    description: [''],
    priority: this.fb.control<TaskPriority>('Normal'),
    criticality: this.fb.control<Criticality>('Normal'),
    requiresApproval: [false],
    escalationPolicyId: this.fb.control<Guid | null>(null),
    followUp: this.fb.control<FollowUpSchedule>(scheduleForPreset('every2')),
    isActive: [true],
  });

  // ── Lists edited outside the reactive form ──
  protected readonly roles = signal<string[]>([]);
  protected readonly roleInput = signal('');
  protected readonly approvalFlow = signal<ApproverRole[]>([]);
  protected readonly items = signal<ItemDraft[]>([]);
  private nextUid = 1;

  private readonly formDirty = signal(false);
  private readonly listsDirty = signal(false);
  protected readonly dirty = computed(() => this.formDirty() || this.listsDirty());
  protected readonly requiresApproval = signal(false);

  protected readonly availableApprovers = computed(() => APPROVER_ROLE_VALUES.filter((r) => !this.approvalFlow().includes(r)));
  protected readonly categorySuggestions = computed(() => this.lookups.categories().map((c) => c.name));

  protected readonly validation = computed(() => validateTemplateItems(this.items(), this.roles()));
  /** Items in tree order with their depth, issues, warning and the choices for their parent / depends-on selects. */
  protected readonly rows = computed(() => {
    const items = this.items();
    const v = this.validation();
    const showRequired = this.submitted();
    return treeOrder(items).map(({ index, depth }) => {
      const item = items[index];
      const below = descendantUids(items, item.uid);
      const issues = v.issues[index] ?? {};
      return {
        item,
        depth,
        issues,
        messages: visibleMessages(issues, item, showRequired),
        warning: v.warnings[index] ?? null,
        parentOptions: items.filter((o) => o.uid !== item.uid && !below.has(o.uid)),
        dependencyOptions: items.filter((o) => o.uid !== item.uid && !item.dependsOnUids.includes(o.uid)),
        dependencies: item.dependsOnUids.map((u) => items.find((o) => o.uid === u)).filter((o): o is ItemDraft => !!o),
      };
    });
  });
  protected readonly previewText = computed(() => {
    const items = this.items();
    return `${pluralize(items.length, 'item')} · ${pluralize(this.roles().length, 'role')} · due within ${pluralize(maxDueOffset(items), 'working day')}`;
  });

  constructor() {
    this.lookups.load();
    effect(() => {
      const id = this.id();
      untracked(() => {
        if (id) this.data.load(this.api.template(id));
        else this.resetForNew();
      });
    });
    effect(() => {
      const t = this.data.value();
      if (t) untracked(() => this.applyTemplate(t));
    });
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.formDirty.set(this.form.dirty);
      this.requiresApproval.set(this.form.controls.requiresApproval.value);
    });
  }

  protected reload(): void {
    const id = this.id();
    if (id) this.data.load(this.api.template(id));
  }

  // ── Roles ──
  protected addRole(): void {
    const role = this.roleInput().trim();
    if (!role) return;
    if (this.roles().some((r) => r.toLowerCase() === role.toLowerCase())) {
      this.toast.info(`“${role}” is already a role.`);
      return;
    }
    this.roles.update((list) => [...list, role]);
    this.roleInput.set('');
    this.listsDirty.set(true);
  }

  protected onRoleKey(e: KeyboardEvent): void {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault();
      this.addRole();
    }
  }

  protected removeRole(role: string): void {
    this.roles.update((list) => list.filter((r) => r !== role));
    this.listsDirty.set(true);
  }

  protected roleUsage(role: string): number {
    return this.items().filter((i) => i.assigneeRole.trim() === role).length;
  }

  // ── Approval flow ──
  protected addApprover(select: HTMLSelectElement): void {
    const role = select.value as ApproverRole;
    select.value = '';
    if (!role || this.approvalFlow().includes(role)) return;
    this.approvalFlow.update((list) => [...list, role]);
    this.listsDirty.set(true);
  }

  protected moveApprover(index: number, delta: -1 | 1): void {
    const list = [...this.approvalFlow()];
    const target = index + delta;
    if (target < 0 || target >= list.length) return;
    [list[index], list[target]] = [list[target], list[index]];
    this.approvalFlow.set(list);
    this.listsDirty.set(true);
  }

  protected removeApprover(index: number): void {
    this.approvalFlow.update((list) => list.filter((_, i) => i !== index));
    this.listsDirty.set(true);
  }

  // ── Items ──
  protected addItem(parent: ItemDraft | null = null, focus = true): void {
    const items = this.items();
    const draft: ItemDraft = {
      uid: this.nextUid++,
      key: suggestKey(items, parent),
      title: '',
      description: '',
      assigneeRole: parent?.assigneeRole ?? this.roles()[0] ?? '',
      dueOffsetWorkingDays: parent?.dueOffsetWorkingDays ?? 0,
      criticality: '',
      parentUid: parent?.uid ?? null,
      dependsOnUids: [],
      danglingParent: null,
      danglingDeps: [],
      followUp: null,
    };
    this.items.set([...items, draft]);
    this.listsDirty.set(true);
    if (focus) setTimeout(() => document.getElementById(`ti-title-${draft.uid}`)?.focus());
  }

  protected async removeItem(item: ItemDraft): Promise<void> {
    const children = this.items().filter((i) => i.parentUid === item.uid).length;
    if (children) {
      const ok = await this.dialog.confirm({
        title: 'Remove item',
        message: `“${item.title || item.key}” has ${pluralize(children, 'child item')}. They move up one level.`,
        confirmText: 'Remove',
        danger: true,
      });
      if (!ok) return;
    }
    this.items.update((list) =>
      list
        .filter((i) => i.uid !== item.uid)
        .map((i) => ({
          ...i,
          parentUid: i.parentUid === item.uid ? item.parentUid : i.parentUid,
          dependsOnUids: i.dependsOnUids.filter((d) => d !== item.uid),
        })),
    );
    this.listsDirty.set(true);
  }

  protected patchItem(uid: number, changes: Partial<ItemDraft>): void {
    this.items.update((list) => list.map((i) => (i.uid === uid ? { ...i, ...changes } : i)));
    this.listsDirty.set(true);
  }

  protected setParent(item: ItemDraft, value: string): void {
    this.patchItem(item.uid, { parentUid: value ? Number(value) : null, danglingParent: null });
  }

  protected setOffset(item: ItemDraft, value: string): void {
    const n = Number(value);
    this.patchItem(item.uid, { dueOffsetWorkingDays: value.trim() === '' || isNaN(n) ? 0 : n });
  }

  protected addDependency(item: ItemDraft, select: HTMLSelectElement): void {
    const uid = Number(select.value);
    select.value = '';
    if (!uid || item.dependsOnUids.includes(uid)) return;
    this.patchItem(item.uid, { dependsOnUids: [...item.dependsOnUids, uid] });
  }

  protected removeDependency(item: ItemDraft, uid: number): void {
    this.patchItem(item.uid, { dependsOnUids: item.dependsOnUids.filter((d) => d !== uid) });
  }

  protected clearDanglingDeps(item: ItemDraft): void {
    this.patchItem(item.uid, { danglingDeps: [] });
  }

  protected depLabel(d: ItemDraft): string {
    return d.key.trim() || d.title.trim() || 'Untitled item';
  }

  // ── Save / instantiate ──
  protected save(): void {
    if (this.saving()) return;
    this.submitted.set(true);
    if (this.form.invalid || !this.validation().valid) {
      this.form.markAllAsTouched();
      this.toast.warning(
        this.validation().valid ? 'Please complete the highlighted fields.' : `Fix ${pluralize(this.validation().errorCount, 'problem')} in the items before saving.`,
      );
      return;
    }
    const v = this.form.getRawValue();
    const input: TemplateInput = {
      name: v.name.trim(),
      category: v.category.trim(),
      description: v.description.trim() || null,
      priority: v.priority,
      criticality: v.criticality,
      requiresApproval: v.requiresApproval,
      escalationPolicyId: v.escalationPolicyId,
      followUp: v.followUp,
      roles: this.roles(),
      items: itemsFromDrafts(this.items()),
      approvalFlow: this.approvalFlow(),
      isActive: v.isActive,
    };
    const id = this.id();
    this.saving.set(true);
    (id ? this.api.updateTemplate(id, input) : this.api.createTemplate(input)).subscribe({
      next: (t) => {
        this.saving.set(false);
        this.toast.success(id ? 'Template saved' : `Template “${input.name}” created`);
        if (!id) {
          this.markClean();
          void this.router.navigate(['/templates', t.id], { replaceUrl: true });
          return;
        }
        this.data.set(t);
      },
      error: () => this.saving.set(false),
    });
  }

  protected openInstantiate(): void {
    if (this.dirty()) {
      this.toast.info('Save your changes before instantiating the template.');
      return;
    }
    this.instantiateOpen.set(true);
  }

  protected onBeforeUnload(e: BeforeUnloadEvent): void {
    if (this.dirty()) e.preventDefault();
  }

  // ── internals ──

  private resetForNew(): void {
    this.form.reset({
      name: '',
      category: '',
      description: '',
      priority: 'Normal',
      criticality: 'Normal',
      requiresApproval: false,
      escalationPolicyId: null,
      followUp: scheduleForPreset('every2'),
      isActive: true,
    });
    this.roles.set([...DEFAULT_ROLES]);
    this.approvalFlow.set([]);
    this.items.set([]);
    this.addItem(null, false);
    this.submitted.set(false);
    this.markClean();
  }

  private applyTemplate(t: TemplateDto): void {
    this.form.reset({
      name: t.name ?? '',
      category: t.category ?? '',
      description: t.description ?? '',
      priority: t.priority ?? 'Normal',
      criticality: t.criticality ?? 'Normal',
      requiresApproval: t.requiresApproval ?? false,
      escalationPolicyId: t.escalationPolicyId ?? null,
      followUp: t.followUp ?? scheduleForPreset('none'),
      isActive: t.isActive ?? true,
    });
    this.roles.set([...(t.roles ?? [])]);
    this.approvalFlow.set([...(t.approvalFlow ?? [])]);
    const drafts = draftsFromItems(t.items, this.nextUid);
    this.nextUid += drafts.length;
    this.items.set(drafts);
    this.markClean();
  }

  private markClean(): void {
    this.form.markAsPristine();
    this.formDirty.set(false);
    this.listsDirty.set(false);
    this.requiresApproval.set(this.form.controls.requiresApproval.value);
  }
}
