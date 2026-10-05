/**
 * Microsoft 365 simulator (/api/m365-sim/*) — every DTO shape the simulator console uses, in one file.
 *
 * The emulator's JSON is not final, so responses are read as `unknown` and turned into the clean view types below
 * by the pure normalisers at the end of this file. They accept both flat shapes ({ fromUserId, text }) and
 * Microsoft Graph-like nested shapes ({ from: { user: { id, displayName } }, body: { content } }), and collections
 * as bare arrays, `{ value: [] }` (Graph) or `{ items: [] }`. Adjust the field lists here when the API settles.
 */

// ───────────────────────────── View types (normalised) ─────────────────────────────

/** A directory user of the emulated tenant (Graph `user`). */
export interface SimUser {
  id: string;
  displayName: string;
  mail: string | null;
  userPrincipalName: string | null;
  jobTitle: string | null;
  department: string | null;
}

/** An email address with an optional display name (Graph `recipient.emailAddress`). */
export interface SimAddress {
  name: string | null;
  address: string;
}

export type SimMailFolder = 'inbox' | 'sentitems';

/** A message in a mailbox folder (Graph `message`). */
export interface SimMailMessage {
  id: string;
  internetMessageId: string | null;
  conversationId: string | null;
  subject: string;
  bodyPreview: string;
  body: string;
  from: SimAddress | null;
  toRecipients: SimAddress[];
  ccRecipients: SimAddress[];
  receivedDateTime: string | null;
  sentDateTime: string | null;
  /** Internet-Message-Id of the message this one replies to (threading). */
  inReplyTo: string | null;
}

export interface SimChatMember {
  userId: string | null;
  displayName: string | null;
}

/** A Teams chat (Graph `chat`): oneOnOne, group or meeting. */
export interface SimChat {
  id: string;
  topic: string | null;
  chatType: string | null;
  members: SimChatMember[];
}

/** A chat or channel message (Graph `chatMessage`). Channel replies carry `replyToId`. */
export interface SimChatMessage {
  id: string;
  fromUserId: string | null;
  fromName: string | null;
  text: string;
  createdDateTime: string | null;
  replyToId: string | null;
}

/** A channel of a team (flattened from teams → channels when the API nests them). */
export interface SimChannel {
  teamId: string;
  teamName: string | null;
  channelId: string;
  displayName: string;
}

/** One entry of the emulator's activity feed (requests served, faults injected, messages posted…). */
export interface SimActivity {
  id: string;
  at: string | null;
  kind: string;
  summary: string;
  details: string | null;
  actor: string | null;
}

/** Fault injection switches (mirrors TenantFaults in FollowUp.Core/Abstractions/DemoTenant.cs). */
export interface SimFaults {
  outage: boolean;
  /** 0–100: share of Graph calls answered with 429 + Retry-After. */
  throttlePercent: number;
  authFailure: boolean;
  latencyMs: number;
}

// ───────────────────────────── Request bodies (docs/API.md) ─────────────────────────────

/** POST mail */
export interface SimSendMailRequest {
  from: string;
  to: string[];
  cc: string[];
  subject: string;
  body: string;
  inReplyToMessageId?: string | null;
}

/** POST chats/{id}/messages */
export interface SimPostChatMessageRequest {
  fromUserId: string;
  text: string;
}

/** POST chats/one-on-one */
export interface SimOneOnOneRequest {
  userA: string;
  userB: string;
}

/** POST teams/{t}/channels/{c}/messages */
export interface SimPostChannelMessageRequest {
  fromUserId: string;
  text: string;
  replyToId?: string | null;
}

// ───────────────────────────── Normalisers ─────────────────────────────

type Rec = Record<string, unknown>;

export function isRecord(x: unknown): x is Rec {
  return typeof x === 'object' && x !== null && !Array.isArray(x);
}

/** Collections arrive as arrays, `{ value: [] }` (Graph) or `{ items: [] }`. Anything else → []. */
export function asArray(x: unknown): unknown[] {
  if (Array.isArray(x)) return x;
  if (isRecord(x)) {
    if (Array.isArray(x['value'])) return x['value'];
    if (Array.isArray(x['items'])) return x['items'];
  }
  return [];
}

/** Strings (and numbers) as trimmed non-empty text, else null. */
export function str(x: unknown): string | null {
  if (typeof x === 'string') return x.trim().length ? x : null;
  if (typeof x === 'number' && Number.isFinite(x)) return String(x);
  return null;
}

/** First non-empty string among `keys` of a record. */
function pick(x: unknown, ...keys: string[]): string | null {
  if (!isRecord(x)) return null;
  for (const k of keys) {
    const v = str(x[k]);
    if (v !== null) return v;
  }
  return null;
}

function child(x: unknown, key: string): unknown {
  return isRecord(x) ? x[key] : undefined;
}

function bool(x: unknown): boolean {
  return x === true || x === 'true' || x === 1;
}

function num(x: unknown, fallback: number): number {
  const n = typeof x === 'number' ? x : typeof x === 'string' ? Number(x) : NaN;
  return Number.isFinite(n) ? n : fallback;
}

/**
 * A display name from a string or a Graph-like party:
 * "Asha", { displayName }, { name }, { user: { displayName } }, { emailAddress: { name } }, { application: { displayName } }.
 */
export function displayNameOf(x: unknown): string | null {
  if (typeof x === 'string') return str(x);
  return (
    pick(x, 'displayName', 'name', 'fromName', 'senderName') ??
    pick(child(x, 'user'), 'displayName', 'name') ??
    pick(child(x, 'emailAddress'), 'name') ??
    pick(child(x, 'application'), 'displayName')
  );
}

/** An email address from a string or { address } / { emailAddress: { address } } / { mail } / { userPrincipalName }. */
export function addressOf(x: unknown): string | null {
  if (typeof x === 'string') return str(x);
  return pick(x, 'address', 'mail', 'email', 'userPrincipalName') ?? pick(child(x, 'emailAddress'), 'address');
}

/** A user id from { id } / { userId } / { user: { id } }. */
export function userIdOf(x: unknown): string | null {
  return pick(x, 'userId', 'id') ?? pick(child(x, 'user'), 'id');
}

const ENTITIES: Record<string, string> = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'", '&nbsp;': ' ' };

/** Graph bodies may be HTML; turn them into readable plain text (line breaks kept). */
export function htmlToText(html: string): string {
  if (!/<[a-z!/][\s\S]*>/i.test(html)) return html;
  return html
    .replace(/<\s*br\s*\/?>/gi, '\n')
    .replace(/<\/\s*(p|div|li|tr|h[1-6])\s*>/gi, '\n')
    .replace(/<[^>]+>/g, '')
    .replace(/&(amp|lt|gt|quot|#39|nbsp);/g, (m) => ENTITIES[m] ?? m)
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

/** Text of a body from a string, { content }, { body: string | { content } } or { text }. */
export function bodyTextOf(x: unknown): string {
  if (typeof x === 'string') return htmlToText(x);
  if (!isRecord(x)) return '';
  const direct = pick(x, 'content', 'text');
  if (direct !== null) return htmlToText(direct);
  const body = x['body'];
  return body !== undefined && body !== x ? bodyTextOf(body) : '';
}

function addressFrom(x: unknown): SimAddress | null {
  const address = addressOf(x);
  if (!address) return null;
  const name = displayNameOf(x);
  return { address, name: name && name !== address ? name : null };
}

/** Recipients from an array, a Graph `{ value }` list or a comma / semicolon separated string. */
function recipients(x: unknown): SimAddress[] {
  const list = typeof x === 'string' ? x.split(/[;,]/) : asArray(x);
  return list.map(addressFrom).filter((a): a is SimAddress => a !== null);
}

export function normaliseUser(x: unknown): SimUser | null {
  const id = pick(x, 'id', 'userId');
  if (!id) return null;
  const mail = pick(x, 'mail', 'email');
  const upn = pick(x, 'userPrincipalName');
  return {
    id,
    displayName: pick(x, 'displayName', 'name') ?? mail ?? upn ?? id,
    mail,
    userPrincipalName: upn,
    jobTitle: pick(x, 'jobTitle', 'title'),
    department: pick(x, 'department'),
  };
}

export function normaliseMail(x: unknown): SimMailMessage | null {
  const id = pick(x, 'id', 'messageId', 'internetMessageId');
  if (!id) return null;
  const body = bodyTextOf(isRecord(x) ? (x['body'] ?? x['content'] ?? x['text']) : x);
  return {
    id,
    internetMessageId: pick(x, 'internetMessageId'),
    conversationId: pick(x, 'conversationId'),
    subject: pick(x, 'subject') ?? '',
    bodyPreview: pick(x, 'bodyPreview', 'preview') ?? body.replace(/\s+/g, ' ').slice(0, 180),
    body,
    from: addressFrom(child(x, 'from') ?? child(x, 'sender') ?? pick(x, 'fromAddress')),
    toRecipients: recipients(child(x, 'toRecipients') ?? child(x, 'to')),
    ccRecipients: recipients(child(x, 'ccRecipients') ?? child(x, 'cc')),
    receivedDateTime: pick(x, 'receivedDateTime', 'receivedAt'),
    sentDateTime: pick(x, 'sentDateTime', 'sentAt', 'createdDateTime'),
    inReplyTo: pick(x, 'inReplyTo', 'inReplyToInternetMessageId', 'inReplyToMessageId'),
  };
}

function normaliseMember(x: unknown): SimChatMember | null {
  if (typeof x === 'string') return { userId: x, displayName: null };
  const userId = userIdOf(x);
  const displayName = displayNameOf(x);
  return userId || displayName ? { userId, displayName } : null;
}

export function normaliseChat(x: unknown): SimChat | null {
  const id = pick(x, 'id', 'chatId');
  if (!id) return null;
  const members = asArray(child(x, 'members') ?? child(x, 'memberIds'))
    .map(normaliseMember)
    .filter((m): m is SimChatMember => m !== null);
  return { id, topic: pick(x, 'topic', 'title'), chatType: pick(x, 'chatType', 'type'), members };
}

export function normaliseChatMessage(x: unknown, parentId: string | null = null): SimChatMessage | null {
  const id = pick(x, 'id', 'messageId');
  if (!id) return null;
  const from = child(x, 'from');
  return {
    id,
    fromUserId: pick(x, 'fromUserId', 'senderId') ?? userIdOf(from),
    fromName: pick(x, 'fromName', 'senderName') ?? displayNameOf(from),
    text: bodyTextOf(isRecord(x) ? (x['body'] ?? x['text'] ?? x['content']) : x),
    createdDateTime: pick(x, 'createdDateTime', 'createdAt', 'sentAt', 'at'),
    replyToId: pick(x, 'replyToId', 'parentId') ?? parentId,
  };
}

/** Channel messages may come flat (with replyToId) or as roots carrying nested `replies`; both become one flat list. */
export function normaliseChannelMessages(x: unknown): SimChatMessage[] {
  const out: SimChatMessage[] = [];
  for (const raw of asArray(x)) {
    const root = normaliseChatMessage(raw);
    if (!root) continue;
    out.push(root);
    for (const reply of asArray(child(raw, 'replies'))) {
      const r = normaliseChatMessage(reply, root.id);
      if (r) out.push(r);
    }
  }
  return out;
}

/** `GET channels` may list channels flat ({ teamId, channelId… }) or as teams with nested `channels`. */
export function normaliseChannels(x: unknown): SimChannel[] {
  const out: SimChannel[] = [];
  for (const raw of asArray(x)) {
    const nested = asArray(child(raw, 'channels'));
    if (nested.length) {
      const teamId = pick(raw, 'id', 'teamId');
      if (!teamId) continue;
      const teamName = pick(raw, 'displayName', 'name', 'teamName');
      for (const c of nested) {
        const channelId = pick(c, 'id', 'channelId');
        if (channelId) out.push({ teamId, teamName, channelId, displayName: pick(c, 'displayName', 'name') ?? channelId });
      }
      continue;
    }
    const team = child(raw, 'team');
    const teamId = pick(raw, 'teamId') ?? pick(team, 'id');
    const channelId = pick(raw, 'channelId', 'id');
    if (!teamId || !channelId) continue;
    out.push({
      teamId,
      teamName: pick(raw, 'teamName', 'teamDisplayName') ?? pick(team, 'displayName', 'name'),
      channelId,
      displayName: pick(raw, 'displayName', 'channelName', 'name') ?? channelId,
    });
  }
  return out;
}

export function normaliseActivity(x: unknown, index: number): SimActivity {
  if (typeof x === 'string') return { id: `a${index}`, at: null, kind: 'Activity', summary: x, details: null, actor: null };
  const summary = pick(x, 'summary', 'text', 'description', 'message', 'title') ?? (bodyTextOf(child(x, 'body')) || '');
  return {
    id: pick(x, 'id') ?? `a${index}`,
    at: pick(x, 'at', 'timestamp', 'createdDateTime', 'time', 'occurredAt'),
    kind: pick(x, 'kind', 'type', 'activityType', 'category') ?? 'Activity',
    summary: summary || '(no summary)',
    details: pick(x, 'details', 'detail', 'path', 'url'),
    actor: displayNameOf(child(x, 'actor') ?? child(x, 'user') ?? child(x, 'from')),
  };
}

export function normaliseFaults(x: unknown): SimFaults {
  return {
    outage: bool(child(x, 'outage') ?? child(x, 'isOutage')),
    throttlePercent: Math.max(0, Math.min(100, Math.round(num(child(x, 'throttlePercent') ?? child(x, 'throttle'), 0)))),
    authFailure: bool(child(x, 'authFailure') ?? child(x, 'authFailures')),
    latencyMs: Math.max(0, Math.round(num(child(x, 'latencyMs') ?? child(x, 'latency'), 0))),
  };
}

/** The id from a create response: a bare string, { id } or { chatId }. */
export function idOf(x: unknown): string | null {
  return typeof x === 'string' ? str(x) : pick(x, 'id', 'chatId', 'messageId');
}

/** Best address for a user (mail, else UPN). */
export function userAddress(u: SimUser): string {
  return u.mail ?? u.userPrincipalName ?? '';
}
