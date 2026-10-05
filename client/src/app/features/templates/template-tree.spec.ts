import { TemplateItem } from '../../core/models/rules.models';
import {
  ItemDraft,
  descendantUids,
  draftsFromItems,
  itemsFromDrafts,
  maxDueOffset,
  suggestKey,
  treeOrder,
  validateTemplateItems,
} from './template-tree';

function draft(uid: number, key: string, parentUid: number | null = null, extra: Partial<ItemDraft> = {}): ItemDraft {
  return {
    uid,
    key,
    title: `Item ${key}`,
    description: '',
    assigneeRole: '',
    dueOffsetWorkingDays: 0,
    criticality: '',
    parentUid,
    dependsOnUids: [],
    danglingParent: null,
    danglingDeps: [],
    followUp: null,
    ...extra,
  };
}

function item(key: string, parentKey: string | null = null, dependsOn: string[] = []): TemplateItem {
  return { key, parentKey, title: `Item ${key}`, dueOffsetWorkingDays: 1, dependsOn };
}

describe('treeOrder', () => {
  it('places children under their parent, depth-first, keeping sibling order', () => {
    const items = [draft(1, 'A'), draft(2, 'B'), draft(3, 'A.1', 1), draft(4, 'A.1.a', 3), draft(5, 'B.1', 2)];
    expect(treeOrder(items)).toEqual([
      { index: 0, depth: 0 },
      { index: 2, depth: 1 },
      { index: 3, depth: 2 },
      { index: 1, depth: 0 },
      { index: 4, depth: 1 },
    ]);
  });

  it('shows items of a parent loop and items with a missing parent as roots', () => {
    const items = [draft(1, 'A', 2), draft(2, 'B', 1), draft(3, 'C', 99), draft(4, 'D', 1)];
    const rows = treeOrder(items);
    expect(rows).toHaveLength(4);
    expect(rows.find((r) => r.index === 0)?.depth).toBe(0);
    expect(rows.find((r) => r.index === 1)?.depth).toBe(0);
    expect(rows.find((r) => r.index === 2)?.depth).toBe(0);
    expect(rows.find((r) => r.index === 3)?.depth).toBe(1);
  });
});

describe('descendantUids', () => {
  it('collects children and grandchildren only', () => {
    const items = [draft(1, 'A'), draft(2, 'A.1', 1), draft(3, 'A.1.a', 2), draft(4, 'B')];
    expect([...descendantUids(items, 1)].sort()).toEqual([2, 3]);
    expect(descendantUids(items, 4).size).toBe(0);
  });
});

describe('validateTemplateItems', () => {
  it('accepts a well-formed tree', () => {
    const items = [draft(1, 'A', null, { assigneeRole: 'Owner' }), draft(2, 'B', 1, { dependsOnUids: [1] })];
    const v = validateTemplateItems(items, ['Owner']);
    expect(v.valid).toBe(true);
    expect(v.errorCount).toBe(0);
  });

  it('requires unique, non-empty keys and titles', () => {
    const v = validateTemplateItems([draft(1, 'A'), draft(2, ' A '), draft(3, '', null, { title: ' ' })], []);
    expect(v.issues[0].key).toContain('more than once');
    expect(v.issues[1].key).toContain('more than once');
    expect(v.issues[2].key).toBe('Key is required.');
    expect(v.issues[2].title).toBe('Title is required.');
    expect(v.valid).toBe(false);
  });

  it('rejects self-parenting, parent loops and unknown parent keys', () => {
    expect(validateTemplateItems([draft(1, 'A', 1)], []).issues[0].parent).toContain('own parent');
    const loop = validateTemplateItems([draft(1, 'A', 2), draft(2, 'B', 1)], []);
    expect(loop.issues[0].parent).toContain('loop');
    expect(loop.issues[1].parent).toContain('loop');
    expect(validateTemplateItems([draft(1, 'A', null, { danglingParent: 'Z' })], []).issues[0].parent).toContain('“Z”');
  });

  it('rejects self, unknown and circular dependencies', () => {
    expect(validateTemplateItems([draft(1, 'A', null, { dependsOnUids: [1] })], []).issues[0].dependsOn).toContain('itself');
    expect(validateTemplateItems([draft(1, 'A', null, { danglingDeps: ['X'] })], []).issues[0].dependsOn).toContain('X');
    const circular = validateTemplateItems([draft(1, 'A', null, { dependsOnUids: [2] }), draft(2, 'B', null, { dependsOnUids: [1] })], []);
    expect(circular.issues[0].dependsOn).toContain('Circular');
  });

  it('checks roles and offsets, and warns when an item is due before its dependency', () => {
    const v = validateTemplateItems(
      [
        draft(1, 'A', null, { assigneeRole: 'QA Lead', dueOffsetWorkingDays: 5 }),
        draft(2, 'B', null, { dueOffsetWorkingDays: 2, dependsOnUids: [1] }),
        draft(3, 'C', null, { dueOffsetWorkingDays: -1 }),
      ],
      ['Owner'],
    );
    expect(v.issues[0].assigneeRole).toContain('QA Lead');
    expect(v.issues[1]).toEqual({});
    expect(v.warnings[1]).toContain('“A”');
    expect(v.issues[2].dueOffset).toBeDefined();
  });
});

describe('draftsFromItems / itemsFromDrafts', () => {
  it('round-trips keys, parents and dependencies, keeping unknown references as dangling', () => {
    const drafts = draftsFromItems([item('A'), item('B', 'A', ['A']), item('C', 'Missing', ['A', 'Nope'])], 10);
    expect(drafts.map((d) => d.uid)).toEqual([10, 11, 12]);
    expect(drafts[1].parentUid).toBe(10);
    expect(drafts[1].dependsOnUids).toEqual([10]);
    expect(drafts[2].danglingParent).toBe('Missing');
    expect(drafts[2].danglingDeps).toEqual(['Nope']);

    const back = itemsFromDrafts(drafts.slice(0, 2));
    expect(back[1]).toMatchObject({ key: 'B', parentKey: 'A', dependsOn: ['A'] });
  });

  it('follows renamed keys because references are held by uid', () => {
    const drafts = draftsFromItems([item('A'), item('B', 'A', ['A'])]);
    drafts[0] = { ...drafts[0], key: 'KICKOFF' };
    expect(itemsFromDrafts(drafts)[1]).toMatchObject({ parentKey: 'KICKOFF', dependsOn: ['KICKOFF'] });
  });
});

describe('suggestKey / maxDueOffset', () => {
  it('suggests unused root and child keys', () => {
    const drafts = [draft(1, 'T1'), draft(2, 'T2'), draft(3, 'T1.1', 1)];
    expect(suggestKey(drafts, null)).toBe('T3');
    expect(suggestKey(drafts, drafts[0])).toBe('T1.2');
  });

  it('returns the latest offset', () => {
    expect(maxDueOffset([{ dueOffsetWorkingDays: 3 }, { dueOffsetWorkingDays: 12 }, { dueOffsetWorkingDays: 0 }])).toBe(12);
    expect(maxDueOffset([])).toBe(0);
  });
});
