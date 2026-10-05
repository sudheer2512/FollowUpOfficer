import { ChangeDetectionStrategy, Component, DestroyRef, afterNextRender, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { catchError, debounceTime, filter, forkJoin, map, merge, of, switchMap, tap } from 'rxjs';
import { RecurringApi } from '../../core/api/recurring.api';
import { TasksApi } from '../../core/api/tasks.api';
import { Guid } from '../../core/models/common.models';
import { CRITICALITY_VALUES, TASK_PRIORITY_VALUES, TASK_VISIBILITY_VALUES, TaskSource, TaskVisibility } from '../../core/models/enums';
import { AttachmentDto, WorkloadDto } from '../../core/models/task.models';
import { ResolvedDraft } from '../../core/models/view.models';
import { DelegateLaunchOptions, DelegateLauncher } from '../../core/state/delegate-launcher';
import { LookupsStore } from '../../core/state/lookups.store';
import { formatDateOnly } from '../../core/util/dates';
import { humanize, pluralize } from '../../core/util/humanize';
import { describeFollowUp } from '../../core/util/schedule-text';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { FollowUpPickerComponent } from '../../shared/forms/follow-up-picker.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../shared/forms/person-picker.component';
import { TagInputComponent } from '../../shared/forms/tag-input.component';
import { TeamPickerComponent } from '../../shared/forms/team-picker.component';
import { FileSizePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { DialogService } from '../../shared/ui/dialog.service';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalComponent } from '../../shared/ui/modal.component';
import { ToastService } from '../../shared/ui/toast.service';
import { rawValueSignal } from '../recurring/form-signal';
import { RecurringFormComponent } from '../recurring/recurring-form.component';
import {
  createRecurringForm,
  patchRecurringForm,
  recurringInputFromForm,
  recurringProblems,
} from '../recurring/recurring-form.model';
import {
  assigneeIdsOf,
  createLinkRow,
  createTaskForm,
  recurringOverridesFromDraft,
  recurringOverridesFromTask,
  taskPatchFromDraft,
  taskPatchFromRecurring,
  taskProblems,
  taskRequestFromForm,
} from './delegate-form.model';
import { InterpretedDraftComponent } from './interpreted-draft.component';
import { createSpeechCapture } from './speech-capture';
import { TaskDependenciesComponent } from './task-dependencies.component';
import { WorkloadPanelComponent } from './workload-panel.component';

type Mode = 'task' | 'recurring';
type SectionKey = 'details' | 'people' | 'classification' | 'dates' | 'followUp' | 'controls' | 'dependencies' | 'links' | 'attachments';

const EXAMPLE_COMMAND =
  'Ask Rahul and the development team to finish the dealer CRM screens by Friday. Follow up every two days and tell me if it gets delayed.';

const VISIBILITY_HINT: Partial<Record<TaskVisibility, string>> = {
  TeamOnly: 'Visible to the assigned teams and their managers.',
  ManagementOnly: 'Visible to managers and executives only.',
  CeoOnly: 'Visible only to you, the CEO and the people assigned.',
  SelectedParticipants: 'Visible only to the assignees, supporting members and followers picked here.',
};

/**
 * Global Delegate dialog (top bar, shortcut N, mic button, "Create subtask"). Hosted once by the shell.
 *
 * Flow: say or type the task → POST /api/tasks/parse → the interpreted draft fills the structured form (which also
 * works on its own) → POST /api/tasks (or /subtasks, or /api/recurring when "Recurring" is on) → open the result.
 * The workload of the chosen people is shown alongside so the CEO sees who is already stretched (spec §42).
 */
@Component({
  selector: 'fu-delegate-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    ModalComponent,
    IconComponent,
    PersonPickerComponent,
    TeamPickerComponent,
    LookupSelectComponent,
    DateInputComponent,
    TagInputComponent,
    FollowUpPickerComponent,
    HumanizePipe,
    FileSizePipe,
    InterpretedDraftComponent,
    WorkloadPanelComponent,
    TaskDependenciesComponent,
    RecurringFormComponent,
  ],
  templateUrl: './delegate-dialog.component.html',
  styleUrl: './delegate-dialog.component.css',
})
export class DelegateDialogComponent {
  protected readonly launcher = inject(DelegateLauncher);
  private readonly tasksApi = inject(TasksApi);
  private readonly recurringApi = inject(RecurringApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(DialogService);
  private readonly lookups = inject(LookupsStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(FormBuilder).nonNullable;

  /** Launch options, captured once: the shell creates a fresh dialog for every launch. */
  protected readonly options: DelegateLaunchOptions = this.launcher.request() ?? {};
  protected readonly isSubtask = !!this.options.parentTaskId;
  protected readonly dialogTitle = this.isSubtask ? `Create subtask of ${this.options.parentNumber || 'the task'}` : 'Delegate a task';
  protected readonly example = EXAMPLE_COMMAND;

  protected readonly taskForm = createTaskForm(this.fb);
  protected readonly recurringForm = createRecurringForm(this.fb);
  protected readonly task = rawValueSignal(this.taskForm);
  protected readonly mode = signal<Mode>(this.options.recurring && !this.isSubtask ? 'recurring' : 'task');

  // ── Natural language & voice ──
  protected readonly nlText = signal(this.options.text ?? '');
  protected readonly parsing = signal(false);
  protected readonly resolved = signal<ResolvedDraft | null>(null);
  /** The command text came (at least partly) from the microphone. */
  private readonly usedVoice = signal(false);
  /** How the current form content was produced — becomes the task's `source`. */
  private readonly parsedFrom = signal<'none' | 'text' | 'voice'>('none');
  protected readonly speech = createSpeechCapture({
    onFinal: (text) => this.appendTranscript(text),
    onEnd: (heard) => {
      if (heard) this.interpret();
    },
  });

  // ── Workload (spec §42) ──
  protected readonly workload = signal<WorkloadDto[]>([]);
  protected readonly workloadLoading = signal(false);
  /** Key (ids|team) of the workload currently shown, to skip refetching what the draft already returned. */
  private workloadKey = '';

  // ── Attachments, submission, sections ──
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly problems = signal<string[]>([]);
  private readonly openSections = signal<ReadonlySet<SectionKey>>(new Set<SectionKey>());

  protected readonly priorities = TASK_PRIORITY_VALUES;
  protected readonly criticalities = CRITICALITY_VALUES;
  protected readonly visibilities = TASK_VISIBILITY_VALUES;

  protected readonly primaryExclude = computed(() => {
    const id = this.task().primaryAssigneeId;
    return id ? [id] : [];
  });
  protected readonly visibilityHint = computed(() => VISIBILITY_HINT[this.task().visibility] ?? '');
  protected readonly submitLabel = computed(() =>
    this.mode() === 'recurring' ? 'Create recurring task' : this.isSubtask ? 'Create subtask' : 'Delegate',
  );

  /** One-line summaries shown on collapsed sections so nothing filled in is hidden. */
  protected readonly summaries = computed(() => {
    const v = this.task();
    const details = [v.description, v.objective, v.expectedResult, v.deliverable, v.successCriteria].filter((s) => s.trim()).length;
    const people = v.supportingMemberIds.length + v.followerIds.length + (v.accountableManagerId ? 1 : 0);
    const classification = [
      this.lookups.nameIn(this.lookups.departments(), v.departmentId),
      this.lookups.nameIn(this.lookups.projects(), v.projectId),
      this.lookups.nameIn(this.lookups.categories(), v.categoryId),
      v.tags.length ? pluralize(v.tags.length, 'tag') : '',
      v.visibility !== 'Normal' ? humanize(v.visibility) : '',
    ].filter(Boolean);
    const dates = [
      v.startDate ? `Starts ${formatDateOnly(v.startDate, { short: true })}` : '',
      v.hardDeadline ? `Hard deadline ${formatDateOnly(v.hardDeadline, { short: true })}` : '',
      v.reviewDate ? `Review ${formatDateOnly(v.reviewDate, { short: true })}` : '',
    ].filter(Boolean);
    const controls = [
      v.notifyCeoOnDelay ? 'Tell me if delayed' : '',
      v.requiresApproval === true ? 'Approval required' : v.requiresApproval === false ? 'No approval' : '',
      v.requiresAcknowledgement === true ? 'Acknowledgement required' : '',
      v.customerImpact ? `Customer impact${v.customerName.trim() ? ': ' + v.customerName.trim() : ''}` : '',
      v.financialImpact ? 'Financial impact' : '',
      v.escalationPolicyId ? this.lookups.nameIn(this.lookups.escalationPolicies(), v.escalationPolicyId) || 'Escalation policy' : '',
    ].filter(Boolean);
    const links = v.links.filter((l) => l.url.trim()).length;
    return {
      details: details ? `${details} of 5 filled` : 'Description, objective, expected result, deliverable, success criteria',
      people: people ? pluralize(people, 'person', 'people') : 'Supporting members, followers, accountable manager',
      classification: classification.length ? classification.join(' · ') : 'Department, project, category, tags, visibility',
      dates: dates.length ? dates.join(' · ') : 'Start, hard deadline, review date',
      followUp: describeFollowUp(v.followUp),
      controls: controls.length ? controls.join(' · ') : 'Approval, acknowledgement, delay alert, impact, escalation',
      dependencies: v.dependencies.length ? pluralize(v.dependencies.length, 'dependency', 'dependencies') : 'None',
      links: links ? pluralize(links, 'link') : 'None',
      attachments: this.files().length ? pluralize(this.files().length, 'file') : 'None',
    };
  });

  constructor() {
    this.lookups.load();
    if (this.options.projectId) {
      this.taskForm.controls.projectId.setValue(this.options.projectId);
      this.recurringForm.controls.projectId.setValue(this.options.projectId);
    }
    this.watchWorkload();
    if (this.nlText().trim()) this.interpret();
    if (this.options.voice) afterNextRender(() => this.speech.start());
  }

  // ── Natural language ──
  protected onNlInput(value: string): void {
    this.nlText.set(value);
  }

  protected onNlKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      event.stopPropagation();
      this.interpret();
    }
  }

  private appendTranscript(text: string): void {
    this.usedVoice.set(true);
    this.nlText.update((current) => (current.trim() ? `${current.trimEnd()} ${text}` : text));
  }

  protected interpret(): void {
    const text = this.nlText().trim();
    if (!text || this.parsing()) return;
    const voice = this.usedVoice();
    this.parsing.set(true);
    this.tasksApi
      .parse({ text, voice })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.parsing.set(false);
          if (r?.draft) this.applyDraft(r, voice);
        },
        error: () => this.parsing.set(false),
      });
  }

  /** Pre-fills the task form (or the recurring form for a recurring draft) from the interpretation. */
  private applyDraft(r: ResolvedDraft, voice: boolean): void {
    this.resolved.set(r);
    this.parsedFrom.set(voice ? 'voice' : 'text');
    this.problems.set([]);
    const recurring = !!r.draft.isRecurring && !this.isSubtask;
    if (recurring) {
      const base = recurringInputFromForm(this.recurringForm.getRawValue());
      patchRecurringForm(this.recurringForm, { ...base, ...recurringOverridesFromDraft(r) });
    } else {
      this.taskForm.patchValue(taskPatchFromDraft(r));
    }
    this.mode.set(recurring ? 'recurring' : 'task');
    this.workload.set(r.workload ?? []);
    this.workloadKey = this.workloadQuery().key;
  }

  protected focusPrimary(): void {
    const id = this.mode() === 'task' ? 'dlg-primary' : 'dlg-rf-primary';
    document.getElementById(id)?.focus();
  }

  // ── Mode ──
  protected setMode(next: Mode): void {
    if (next === this.mode() || this.isSubtask) return;
    if (next === 'recurring') {
      const base = recurringInputFromForm(this.recurringForm.getRawValue());
      patchRecurringForm(this.recurringForm, { ...base, ...recurringOverridesFromTask(this.taskForm.getRawValue()) });
    } else {
      this.taskForm.patchValue(taskPatchFromRecurring(recurringInputFromForm(this.recurringForm.getRawValue())));
    }
    this.problems.set([]);
    this.mode.set(next);
  }

  // ── Sections ──
  protected isOpen(key: SectionKey): boolean {
    return this.openSections().has(key);
  }

  protected onToggle(key: SectionKey, event: Event): void {
    const open = (event.target as HTMLDetailsElement).open;
    if (open === this.isOpen(key)) return;
    this.openSections.update((set) => {
      const next = new Set(set);
      if (open) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  private openSection(key: SectionKey): void {
    if (!this.isOpen(key)) this.openSections.update((set) => new Set([...set, key]));
  }

  // ── Links & attachments ──
  protected addLink(): void {
    this.taskForm.controls.links.push(createLinkRow(this.fb));
    this.openSection('links');
  }

  protected removeLink(index: number): void {
    this.taskForm.controls.links.removeAt(index);
    this.taskForm.controls.links.markAsDirty();
  }

  protected addFiles(input: HTMLInputElement): void {
    const picked = Array.from(input.files ?? []);
    input.value = '';
    if (!picked.length) return;
    this.files.update((list) => [...list, ...picked.filter((f) => !list.some((x) => x.name === f.name && x.size === f.size))]);
  }

  protected removeFile(index: number): void {
    this.files.update((list) => list.filter((_, i) => i !== index));
  }

  // ── Workload ──
  private workloadQuery(): { ids: Guid[]; teamId: Guid | null; key: string } {
    let ids: Guid[];
    let teamId: Guid | null;
    if (this.mode() === 'task') {
      const v = this.taskForm.getRawValue();
      ids = assigneeIdsOf(v.primaryAssigneeId, v.additionalAssigneeIds);
      teamId = v.teamIds[0] ?? null;
    } else {
      const v = this.recurringForm.getRawValue();
      ids = assigneeIdsOf(v.primaryAssigneeId, v.additionalAssigneeIds);
      teamId = v.teamId;
    }
    // Named people take precedence; a team alone shows its members' load.
    const team = ids.length ? null : teamId;
    return { ids, teamId: team, key: `${ids.join(',')}|${team ?? ''}` };
  }

  /** Re-queries GET /api/workload (debounced) whenever the chosen people or team change. */
  private watchWorkload(): void {
    merge(this.taskForm.valueChanges, this.recurringForm.valueChanges, toObservable(this.mode))
      .pipe(
        map(() => this.workloadQuery()),
        debounceTime(300),
        filter((q) => q.key !== this.workloadKey),
        tap((q) => {
          this.workloadKey = q.key;
          this.workloadLoading.set(q.ids.length > 0 || !!q.teamId);
        }),
        switchMap((q) =>
          q.ids.length || q.teamId
            ? this.tasksApi.workload(q.ids, q.teamId).pipe(catchError(() => of([] as WorkloadDto[])))
            : of([] as WorkloadDto[]),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((list) => {
        this.workload.set(list ?? []);
        this.workloadLoading.set(false);
      });
  }

  // ── Submit ──
  protected onFormKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.submit(false);
    }
  }

  protected submit(asDraft: boolean): void {
    if (this.busy()) return;
    if (this.mode() === 'recurring') {
      this.submitRecurring();
      return;
    }
    const v = this.taskForm.getRawValue();
    const problems = taskProblems(v, asDraft);
    this.problems.set(problems);
    if (problems.length) {
      this.taskForm.markAllAsTouched();
      if (v.links.some((l) => l.url.trim() && !/^https?:\/\/\S+$/i.test(l.url.trim()))) this.openSection('links');
      if (v.hardDeadline || v.startDate) this.openSection('dates');
      return;
    }
    this.busy.set(true);
    const parentTaskId = this.options.parentTaskId ?? null;
    const files = this.files();
    const uploads$ = files.length ? forkJoin(files.map((f) => this.tasksApi.uploadAttachment(f))) : of([] as AttachmentDto[]);
    uploads$
      .pipe(
        switchMap((attachments) => {
          const req = taskRequestFromForm(v, {
            parentTaskId,
            isDraft: asDraft,
            source: this.source(),
            attachmentIds: (attachments ?? []).map((a) => a.id),
          });
          return parentTaskId ? this.tasksApi.createSubtask(parentTaskId, req) : this.tasksApi.create(req);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (detail) => {
          const number = detail?.summary?.number;
          const what = this.isSubtask ? 'Subtask' : 'Task';
          const label = number ? `${what} ${number}` : what;
          this.toast.success(asDraft ? `${label} saved as draft` : this.isSubtask ? `${label} created` : `${label} delegated`);
          this.finish(detail?.summary?.id ? ['/tasks', detail.summary.id] : null);
        },
        error: () => this.busy.set(false),
      });
  }

  private submitRecurring(): void {
    const input = recurringInputFromForm(this.recurringForm.getRawValue());
    const problems = recurringProblems(input);
    this.problems.set(problems);
    if (problems.length) {
      this.recurringForm.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.recurringApi
      .create(input)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (dto) => {
          this.toast.success(`Recurring task “${dto?.titleTemplate ?? input.titleTemplate}” created`);
          this.finish(dto?.id ? ['/recurring', dto.id] : ['/recurring']);
        },
        error: () => this.busy.set(false),
      });
  }

  private source(): TaskSource {
    const from = this.parsedFrom();
    return from === 'voice' ? 'Voice' : from === 'text' ? 'NaturalLanguage' : 'Manual';
  }

  private finish(route: unknown[] | null): void {
    this.speech.stop();
    this.launcher.close();
    if (route) void this.router.navigate(route);
  }

  /** Close (Esc, ×, Cancel): confirm before throwing away anything the user entered. */
  protected async requestClose(): Promise<void> {
    if (this.busy()) return;
    const typed = !!this.nlText().trim() && this.nlText() !== (this.options.text ?? '');
    const dirty = this.taskForm.dirty || this.recurringForm.dirty || this.files().length > 0 || typed;
    if (dirty) {
      const discard = await this.dialogs.confirm({
        title: 'Discard this delegation?',
        message: 'What you have entered will be lost.',
        confirmText: 'Discard',
        cancelText: 'Keep editing',
        danger: true,
      });
      if (!discard) return;
    }
    this.speech.stop();
    this.launcher.close();
  }
}
