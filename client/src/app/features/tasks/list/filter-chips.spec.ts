import { TaskFilter } from '../../../core/models/task.models';
import { FilterNameResolver, clearCriteria, describeFilter, removeChip, sameCriteria } from './filter-chips';

const names: FilterNameResolver = {
  person: (id) => (id === 'p1' ? 'Rahul Mehta' : ''),
  team: () => 'Finance',
  department: () => 'Operations',
  project: () => 'ERP rollout',
};

describe('task list filter chips', () => {
  it('describes criteria but not scope, open toggle, sort or paging', () => {
    const f: TaskFilter = { scope: 'mine', open: true, sort: 'due', page: 3, statuses: ['Blocked'], assignedToId: 'p1', blocked: true };
    const labels = describeFilter(f, names).map((c) => c.label);
    expect(labels).toEqual(['Status: Blocked', 'Responsible: Rahul Mehta', 'Blocked']);
  });

  it('labels unknown ids and false booleans readably', () => {
    const chips = describeFilter({ assignedById: 'abcdef1234567', recurring: false }, names);
    expect(chips[0].label).toBe('Delegated by: Unknown (abcdef12)');
    expect(chips[1].label).toBe('Not recurring');
  });

  it('removes a single list entry and drops empty lists', () => {
    const f: TaskFilter = { statuses: ['Blocked', 'InProgress'], health: ['Critical'], page: 4 };
    const once = removeChip(f, { key: 'statuses', value: 'Blocked', label: '' });
    expect(once.statuses).toEqual(['InProgress']);
    expect(once.page).toBe(1);
    const twice = removeChip(once, { key: 'health', value: 'Critical', label: '' });
    expect('health' in twice).toBe(false);
  });

  it('removes scalar criteria', () => {
    expect(removeChip({ bucket: 'Overdue', tag: 'q4' }, { key: 'bucket', label: '' })).toEqual({ tag: 'q4', page: 1 });
  });

  it('clear all keeps scope, open, sort and page size', () => {
    const f: TaskFilter = { scope: 'team', open: false, sort: 'updated', pageSize: 25, text: 'x', blocked: true, page: 2 };
    expect(clearCriteria(f)).toEqual({ scope: 'team', open: false, sort: 'updated', pageSize: 25, page: 1 });
  });

  it('compares filters by meaning, ignoring paging and list order of keys', () => {
    expect(sameCriteria({ bucket: 'Overdue', open: true, page: 3 }, { open: true, bucket: 'Overdue' })).toBe(true);
    expect(sameCriteria({ bucket: 'Overdue' }, { bucket: 'DueToday' })).toBe(false);
    expect(sameCriteria({ text: '' }, {})).toBe(true);
  });
});
