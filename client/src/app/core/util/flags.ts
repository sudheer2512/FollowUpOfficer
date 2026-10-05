/**
 * Helpers for [Flags] enums, which the API serialises as comma lists ("TeamLeader, Ceo").
 * "None" (or an empty string) means no flags.
 */
export function parseFlags(value: string | null | undefined): string[] {
  if (!value) return [];
  return value
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0 && s !== 'None' && !/^\d+$/.test(s));
}

export function toFlags(values: readonly string[] | null | undefined): string {
  const list = (values ?? []).filter((v) => v && v !== 'None');
  return list.length ? list.join(', ') : 'None';
}

export function hasFlag(value: string | null | undefined, flag: string): boolean {
  return parseFlags(value).includes(flag);
}

export function toggleFlag(value: string | null | undefined, flag: string, on: boolean): string {
  const set = new Set(parseFlags(value));
  if (on) set.add(flag);
  else set.delete(flag);
  return toFlags([...set]);
}
