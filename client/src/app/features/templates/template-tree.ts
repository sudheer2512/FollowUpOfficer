/**
 * Template item tree helpers (pure, unit-tested in template-tree.spec.ts).
 *
 * The editor works on drafts that reference each other by a client-side `uid` instead of by key, so renaming a key never
 * breaks a parent or dependency link. Keys are resolved again when saving. References loaded from the server that match
 * no key are kept as "dangling" values and reported until the user fixes them.
 */
import { Criticality } from '../../core/models/enums';
import { FollowUpSchedule, TemplateItem } from '../../core/models/rules.models';

export interface ItemDraft {
  uid: number;
  key: string;
  title: string;
  description: string;
  /** '' = no role. */
  assigneeRole: string;
  dueOffsetWorkingDays: number;
  /** '' = inherit the template criticality. */
  criticality: Criticality | '';
  parentUid: number | null;
  dependsOnUids: number[];
  /** Parent key from the server that matched no item. */
  danglingParent: string | null;
  /** Depends-on keys from the server that matched no item. */
  danglingDeps: string[];
  /** Per-item follow-up from the server (not edited here, preserved on save). */
  followUp: FollowUpSchedule | null;
}

export interface TreeRow {
  index: number;
  depth: number;
}

export interface ItemIssues {
  key?: string;
  title?: string;
  parent?: string;
  dependsOn?: string;
  assigneeRole?: string;
  dueOffset?: string;
}

export interface TemplateValidation {
  /** Blocking problems per item (same order as the drafts). */
  issues: ItemIssues[];
  /** Non-blocking hints per item (null when none). */
  warnings: (string | null)[];
  errorCount: number;
  valid: boolean;
}

type Linked = Pick<ItemDraft, 'uid' | 'parentUid'>;

/** True when following the parent chain from `uid` comes back to it. */
function inParentCycle(uid: number, parentOf: Map<number, number | null>): boolean {
  const seen = new Set<number>();
  let p = parentOf.get(uid) ?? null;
  while (p !== null) {
    if (p === uid) return true;
    if (seen.has(p)) return false; // a loop further up that does not include this item
    seen.add(p);
    p = parentOf.get(p) ?? null;
  }
  return false;
}

/** Parent used for display: null when the parent is missing, the item itself, or part of a loop through the item. */
function effectiveParents(items: readonly Linked[]): Map<number, number | null> {
  const uids = new Set(items.map((i) => i.uid));
  const parentOf = new Map<number, number | null>(
    items.map((i) => [i.uid, i.parentUid !== null && i.parentUid !== i.uid && uids.has(i.parentUid) ? i.parentUid : null]),
  );
  const effective = new Map<number, number | null>();
  for (const i of items) effective.set(i.uid, inParentCycle(i.uid, parentOf) ? null : (parentOf.get(i.uid) ?? null));
  return effective;
}

/**
 * Display order for the tree: every parent followed by its children (depth-first), siblings in their original order.
 * Items whose parent is missing or that sit in a parent loop are shown as roots.
 */
export function treeOrder(items: readonly Linked[]): TreeRow[] {
  const parent = effectiveParents(items);
  const children = new Map<number | null, number[]>();
  items.forEach((item, index) => {
    const p = parent.get(item.uid) ?? null;
    children.set(p, [...(children.get(p) ?? []), index]);
  });

  const rows: TreeRow[] = [];
  const visited = new Set<number>();
  const visit = (index: number, depth: number) => {
    if (visited.has(index)) return;
    visited.add(index);
    rows.push({ index, depth });
    for (const child of children.get(items[index].uid) ?? []) visit(child, depth + 1);
  };
  for (const root of children.get(null) ?? []) visit(root, 0);
  // Defensive: anything unreachable is still shown.
  items.forEach((_, index) => visit(index, 0));
  return rows;
}

/** uids of every item below `uid` in the tree (children, grandchildren…). */
export function descendantUids(items: readonly Linked[], uid: number): Set<number> {
  const parent = effectiveParents(items);
  const result = new Set<number>();
  let frontier = [uid];
  while (frontier.length) {
    const next: number[] = [];
    for (const item of items) {
      const p = parent.get(item.uid) ?? null;
      if (p !== null && frontier.includes(p) && !result.has(item.uid) && item.uid !== uid) {
        result.add(item.uid);
        next.push(item.uid);
      }
    }
    frontier = next;
  }
  return result;
}

/** True when `uid` can reach itself through depends-on links. */
function inDependencyCycle(uid: number, depsOf: Map<number, number[]>): boolean {
  const seen = new Set<number>();
  const stack = [...(depsOf.get(uid) ?? [])];
  while (stack.length) {
    const d = stack.pop()!;
    if (d === uid) return true;
    if (seen.has(d)) continue;
    seen.add(d);
    stack.push(...(depsOf.get(d) ?? []));
  }
  return false;
}

/**
 * Validates the drafts: unique non-empty keys, titles, parents (exist, not itself, no loops), dependencies (exist, not
 * itself, no circular chains), assignee roles from the role list and non-negative whole-day offsets.
 * Warns (non-blocking) when an item is due before one of its dependencies.
 */
export function validateTemplateItems(items: readonly ItemDraft[], roles: readonly string[]): TemplateValidation {
  const keyCounts = new Map<string, number>();
  for (const i of items) {
    const k = i.key.trim();
    if (k) keyCounts.set(k, (keyCounts.get(k) ?? 0) + 1);
  }
  const byUid = new Map(items.map((i) => [i.uid, i]));
  const rawParent = new Map<number, number | null>(items.map((i) => [i.uid, i.parentUid]));
  const depsOf = new Map(items.map((i) => [i.uid, i.dependsOnUids.filter((d) => byUid.has(d))]));
  const roleSet = new Set(roles.map((r) => r.trim()));

  const issues: ItemIssues[] = [];
  const warnings: (string | null)[] = [];
  let errorCount = 0;

  for (const item of items) {
    const it: ItemIssues = {};
    const key = item.key.trim();
    if (!key) it.key = 'Key is required.';
    else if ((keyCounts.get(key) ?? 0) > 1) it.key = `Key “${key}” is used more than once.`;

    if (!item.title.trim()) it.title = 'Title is required.';

    if (item.danglingParent) it.parent = `No item has the key “${item.danglingParent}”.`;
    else if (item.parentUid !== null) {
      if (item.parentUid === item.uid) it.parent = 'An item cannot be its own parent.';
      else if (!byUid.has(item.parentUid)) it.parent = 'The parent item no longer exists.';
      else if (inParentCycle(item.uid, rawParent)) it.parent = 'This parent creates a loop: an item cannot sit under its own descendant.';
    }

    if (item.danglingDeps.length) it.dependsOn = `Unknown key${item.danglingDeps.length > 1 ? 's' : ''}: ${item.danglingDeps.join(', ')}.`;
    else if (item.dependsOnUids.includes(item.uid)) it.dependsOn = 'An item cannot depend on itself.';
    else if (item.dependsOnUids.some((d) => !byUid.has(d))) it.dependsOn = 'A dependency no longer exists.';
    else if (inDependencyCycle(item.uid, depsOf)) it.dependsOn = 'Circular dependency: these items wait for each other.';

    const role = item.assigneeRole.trim();
    if (role && !roleSet.has(role)) it.assigneeRole = `Role “${role}” is not in the roles list.`;

    const offset = item.dueOffsetWorkingDays;
    if (!Number.isInteger(offset) || offset < 0) it.dueOffset = 'Use a whole number of working days (0 or more).';

    const count = Object.keys(it).length;
    errorCount += count;
    issues.push(it);

    const laterDep = item.dependsOnUids
      .map((d) => byUid.get(d))
      .find((d): d is ItemDraft => !!d && d.dueOffsetWorkingDays > item.dueOffsetWorkingDays);
    warnings.push(
      laterDep ? `Due before its dependency “${laterDep.key.trim() || laterDep.title}” (day ${laterDep.dueOffsetWorkingDays}).` : null,
    );
  }

  return { issues, warnings, errorCount, valid: errorCount === 0 };
}

/** Server items → drafts. `firstUid` lets callers keep uids unique across reloads. */
export function draftsFromItems(items: readonly TemplateItem[] | null | undefined, firstUid = 1): ItemDraft[] {
  const list = items ?? [];
  const uidByKey = new Map<string, number>();
  list.forEach((item, i) => {
    const k = (item.key ?? '').trim();
    if (k && !uidByKey.has(k)) uidByKey.set(k, firstUid + i);
  });

  return list.map((item, i) => {
    const parentKey = (item.parentKey ?? '').trim();
    const parentUid = parentKey ? (uidByKey.get(parentKey) ?? null) : null;
    const deps = (item.dependsOn ?? []).map((d) => d.trim()).filter(Boolean);
    return {
      uid: firstUid + i,
      key: item.key ?? '',
      title: item.title ?? '',
      description: item.description ?? '',
      assigneeRole: item.assigneeRole ?? '',
      dueOffsetWorkingDays: item.dueOffsetWorkingDays ?? 0,
      criticality: item.criticality ?? '',
      parentUid,
      dependsOnUids: deps.map((d) => uidByKey.get(d)).filter((u): u is number => u !== undefined),
      danglingParent: parentKey && parentUid === null ? parentKey : null,
      danglingDeps: deps.filter((d) => !uidByKey.has(d)),
      followUp: item.followUp ?? null,
    };
  });
}

/** Drafts → server items (references resolved to trimmed keys). Call only after validation passed. */
export function itemsFromDrafts(drafts: readonly ItemDraft[]): TemplateItem[] {
  const keyOf = new Map(drafts.map((d) => [d.uid, d.key.trim()]));
  return drafts.map((d) => ({
    key: d.key.trim(),
    parentKey: d.parentUid !== null ? (keyOf.get(d.parentUid) ?? null) : null,
    title: d.title.trim(),
    description: d.description.trim() || null,
    assigneeRole: d.assigneeRole.trim() || null,
    dueOffsetWorkingDays: d.dueOffsetWorkingDays,
    criticality: d.criticality || null,
    dependsOn: [...new Set(d.dependsOnUids.map((u) => keyOf.get(u)).filter((k): k is string => !!k))],
    followUp: d.followUp,
  }));
}

/** A key not used by any draft: "T3" for roots, "T1.2" for the second child of "T1". */
export function suggestKey(drafts: readonly ItemDraft[], parent: ItemDraft | null): string {
  const used = new Set(drafts.map((d) => d.key.trim()));
  const base = parent ? `${parent.key.trim() || 'T'}.` : 'T';
  let n = parent ? drafts.filter((d) => d.parentUid === parent.uid).length + 1 : drafts.filter((d) => d.parentUid === null).length + 1;
  while (used.has(`${base}${n}`)) n++;
  return `${base}${n}`;
}

/** Latest due offset across the items ("due within K working days"). */
export function maxDueOffset(items: readonly Pick<ItemDraft, 'dueOffsetWorkingDays'>[]): number {
  return items.reduce((max, i) => (Number.isFinite(i.dueOffsetWorkingDays) ? Math.max(max, i.dueOffsetWorkingDays) : max), 0);
}
