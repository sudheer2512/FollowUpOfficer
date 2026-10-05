import { FormBuilder } from '@angular/forms';
import { DEFAULT_DUE_RULE, DEFAULT_RECURRENCE_RULE } from '../../core/models/rules.models';
import { ResolvedDraft } from '../../core/models/view.models';
import {
  TaskFormValue,
  createDependencyRow,
  createLinkRow,
  createTaskForm,
  recurringOverridesFromDraft,
  taskPatchFromDraft,
  taskProblems,
  taskRequestFromForm,
} from './delegate-form.model';

const fb = () => new FormBuilder().nonNullable;

function formValue(patch: Partial<TaskFormValue>, configure?: (form: ReturnType<typeof createTaskForm>) => void): TaskFormValue {
  const form = createTaskForm(fb());
  configure?.(form);
  form.patchValue(patch);
  return form.getRawValue();
}

const ctx = { isDraft: false, source: 'Manual' as const, attachmentIds: [] };

describe('taskRequestFromForm', () => {
  it('maps people, dates and controls the way POST /api/tasks expects', () => {
    const req = taskRequestFromForm(
      formValue({
        title: '  Finish dealer CRM screens ',
        primaryAssigneeId: 'rahul',
        additionalAssigneeIds: ['meera', 'rahul'],
        teamIds: [],
        includeTeamLeader: true,
        dueTime: '17:30',
        customerImpact: false,
        customerName: 'Acme',
        financialImpact: 250000,
      }),
      { ...ctx, attachmentIds: ['a1'] },
    );
    expect(req.title).toBe('Finish dealer CRM screens');
    expect(req.additionalAssigneeIds).toEqual(['meera']);
    expect(req.includeTeamLeader).toBe(false);
    expect(req.dueTime).toBeNull();
    expect(req.customerName).toBeNull();
    expect(req.financialImpact).toBe(250000);
    expect(req.requiresAcknowledgement).toBeNull();
    expect(req.attachmentIds).toEqual(['a1']);
    expect(req.followUp?.frequency).toBe('EveryNDays');
  });

  it('keeps the due time with a due date and maps links and dependencies', () => {
    const fbn = fb();
    const req = taskRequestFromForm(
      formValue({ title: 'Launch', dueDate: '2026-10-09', dueTime: '17:30' }, (form) => {
        form.controls.links.push(createLinkRow(fbn, { url: ' https://example.com/spec ' }));
        form.controls.links.push(createLinkRow(fbn, { title: 'Empty', url: '' }));
        form.controls.dependencies.push(createDependencyRow(fbn, { taskId: 't-42', label: 'TASK-42 · API' }));
        const external = createDependencyRow(fbn);
        external.patchValue({ externalDescription: 'Vendor API keys', externalParty: 'Vendor' });
        form.controls.dependencies.push(external);
        form.controls.dependencies.push(createDependencyRow(fbn));
      }),
      ctx,
    );
    expect(req.dueTime).toBe('17:30:00');
    expect(req.links).toEqual([{ title: 'https://example.com/spec', url: 'https://example.com/spec' }]);
    expect(req.dependencies).toEqual([
      { taskId: 't-42', type: 'FinishToStart' },
      { type: 'External', externalDescription: 'Vendor API keys', externalParty: 'Vendor', expectedBy: null },
    ]);
  });
});

describe('taskProblems', () => {
  it('needs a title, and a person or team unless saving a draft', () => {
    expect(taskProblems(formValue({}), true)).toEqual(['Give the task a title.']);
    expect(taskProblems(formValue({ title: 'X' }), false)).toHaveLength(1);
    expect(taskProblems(formValue({ title: 'X', teamIds: ['dev'] }), false)).toEqual([]);
    expect(taskProblems(formValue({ title: 'X' }), true)).toEqual([]);
  });

  it('flags contradictory dates', () => {
    const v = formValue({ title: 'X', primaryAssigneeId: 'p', dueDate: '2026-10-09', hardDeadline: '2026-10-08' });
    expect(taskProblems(v, false)).toEqual(['The hard deadline is before the due date.']);
  });
});

describe('draft conversions', () => {
  const resolved = {
    draft: {
      title: 'Finish the dealer CRM screens',
      primaryAssigneeId: 'rahul',
      assigneeIds: ['rahul', 'meera'],
      teamIds: ['dev'],
      dueDate: '2026-10-09',
      dueTime: '18:00:00',
      priority: 'High',
      criticality: 'Important',
      notifyCeoOnDelay: true,
      requiresApproval: false,
      isRecurring: false,
      recurrence: { ...DEFAULT_RECURRENCE_RULE, frequency: 'Weekly', daysOfWeek: ['Monday'] },
      recurrenceDue: { ...DEFAULT_DUE_RULE, kind: 'WeekdayOfSameWeek' },
      confidence: 0.82,
      notes: [],
      unresolvedNames: [],
      needsConfirmation: true,
    },
    primaryAssignee: { id: 'rahul', name: 'Rahul' },
    assignees: [{ id: 'meera', name: 'Meera' }],
    teams: [{ id: 'dev', name: 'Development' }],
    workload: [],
    provider: 'Heuristic',
  } as unknown as ResolvedDraft;

  it('fills the task form without touching fields the draft does not mention', () => {
    const patch = taskPatchFromDraft(resolved);
    expect(patch.additionalAssigneeIds).toEqual(['meera']);
    expect(patch.teamIds).toEqual(['dev']);
    expect(patch.dueTime).toBe('18:00');
    expect(patch.notifyCeoOnDelay).toBe(true);
    expect('projectId' in patch).toBe(false);
    expect('description' in patch).toBe(false);
    expect('requiresApproval' in patch).toBe(false);
  });

  it('maps a recurring draft onto the recurring definition', () => {
    const overrides = recurringOverridesFromDraft(resolved);
    expect(overrides.titleTemplate).toBe('Finish the dealer CRM screens');
    expect(overrides.teamId).toBe('dev');
    expect(overrides.rule?.frequency).toBe('Weekly');
    expect(overrides.due?.kind).toBe('WeekdayOfSameWeek');
  });
});
