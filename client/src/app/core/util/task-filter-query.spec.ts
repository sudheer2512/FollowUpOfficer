import { TaskFilter } from '../models/task.models';
import { activeFilterCount, filterToQuery, isEmptyFilter, queryToFilter, toQueryBody } from './task-filter-query';

describe('task filter ↔ query string', () => {
  it('parses server drilldown keys exactly as TaskFilter property names', () => {
    const f = queryToFilter({ bucket: 'Overdue', minDaysOverdue: '5', departmentId: 'dep-1', open: 'true' });
    expect(f.bucket).toBe('Overdue');
    expect(f.minDaysOverdue).toBe(5);
    expect(f.departmentId).toBe('dep-1');
    expect(f.open).toBe(true);
    expect(f.page).toBe(1);
    expect(f.pageSize).toBe(50);
  });

  it('splits comma-separated lists and accepts repeated keys', () => {
    expect(queryToFilter({ statuses: 'InProgress,Blocked' }).statuses).toEqual(['InProgress', 'Blocked']);
    expect(queryToFilter({ health: ['Critical', 'AtRisk'] }).health).toEqual(['Critical', 'AtRisk']);
  });

  it('accepts singular aliases for list keys', () => {
    const f = queryToFilter({ status: 'Blocked', priority: 'Urgent', criticality: 'Critical,BusinessCritical' });
    expect(f.statuses).toEqual(['Blocked']);
    expect(f.priorities).toEqual(['Urgent']);
    expect(f.criticalities).toEqual(['Critical', 'BusinessCritical']);
  });

  it('parses booleans leniently and ignores junk', () => {
    const f = queryToFilter({ blocked: '1', recurring: 'no', noResponse: 'maybe', minDaysOverdue: 'abc' });
    expect(f.blocked).toBe(true);
    expect(f.recurring).toBe(false);
    expect(f.noResponse).toBeUndefined();
    expect(f.minDaysOverdue).toBeUndefined();
  });

  it('ignores unknown keys', () => {
    const f = queryToFilter({ tab: 'overview', text: 'crm' }) as Record<string, unknown>;
    expect(f['tab']).toBeUndefined();
    expect(f['text']).toBe('crm');
  });

  it('serialises a filter compactly (no empty values, default paging omitted)', () => {
    const q = filterToQuery({
      text: '  dealer crm ',
      scope: 'delegated',
      statuses: ['InProgress', 'Blocked'],
      priorities: [],
      waitingForCeo: true,
      overdue: false,
      tag: '',
      page: 1,
      pageSize: 50,
    });
    expect(q).toEqual({ text: 'dealer crm', scope: 'delegated', statuses: 'InProgress,Blocked', waitingForCeo: 'true', overdue: 'false' });
  });

  it('keeps non-default paging', () => {
    expect(filterToQuery({ page: 3, pageSize: 25 })).toEqual({ page: '3', pageSize: '25' });
  });

  it('round-trips', () => {
    const original: TaskFilter = {
      text: 'gst',
      scope: 'team',
      statuses: ['Assigned', 'InProgress'],
      open: true,
      assignedToId: 'p-1',
      teamId: 't-1',
      criticalities: ['Critical'],
      health: ['AtRisk'],
      bucket: 'DueThisWeek',
      dueFrom: '2026-10-01',
      dueTo: '2026-10-31',
      minDaysOverdue: 3,
      recurring: true,
      blocked: false,
      topLevelOnly: true,
      sort: 'due',
      page: 2,
      pageSize: 50,
    };
    expect(queryToFilter(filterToQuery(original))).toEqual(original);
  });

  it('counts active criteria excluding text, sort and paging', () => {
    const f: TaskFilter = { text: 'x', sort: 'due', page: 4, blocked: true, statuses: ['Blocked'] };
    expect(activeFilterCount(f)).toBe(2);
    expect(isEmptyFilter({ sort: 'attention', page: 2 })).toBe(true);
    expect(isEmptyFilter({ blocked: true })).toBe(false);
  });

  it('builds a clean POST body with paging defaults', () => {
    expect(toQueryBody({ text: '', statuses: [], blocked: true, assignedToId: null })).toEqual({ blocked: true, page: 1, pageSize: 50 });
  });
});
