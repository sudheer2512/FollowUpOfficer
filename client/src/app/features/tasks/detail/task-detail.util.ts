/**
 * Small pure helpers for the task detail page: DTO normalisation (the backend is still evolving, so arrays may be
 * missing), status predicates, and derived facts the header, banners and tabs share.
 */
import { Guid } from '../../../core/models/common.models';
import { CLOSED_STATUSES, EvidenceKind, WorkStatus } from '../../../core/models/enums';
import { NO_FOLLOW_UP } from '../../../core/models/rules.models';
import { ApprovalDto, ExtensionDto, SubtaskNode, SubmissionDto, TaskDetail, TaskPermissions } from '../../../core/models/task.models';

const NO_PERMISSIONS: TaskPermissions = {
  canView: true,
  canManage: false,
  canUpdate: false,
  canComplete: false,
  canAcknowledge: false,
  canApprove: false,
  canComment: false,
  canDecideExtension: false,
};

function normaliseNodes(nodes: SubtaskNode[] | null | undefined): SubtaskNode[] {
  return (nodes ?? []).map((n) => ({ ...n, children: normaliseNodes(n.children) }));
}

/** Fills every collection and nested record the UI reads, so templates never have to null-check arrays. */
export function normaliseDetail(d: TaskDetail): TaskDetail {
  return {
    ...d,
    summary: { ...d.summary, tags: d.summary?.tags ?? [] },
    links: d.links ?? [],
    attentionFactors: d.attentionFactors ?? [],
    followUp: {
      ...d.followUp,
      description: d.followUp?.description ?? '',
      remindersSinceResponse: d.followUp?.remindersSinceResponse ?? 0,
      schedule: d.followUp?.schedule ?? NO_FOLLOW_UP,
    },
    assignees: d.assignees ?? [],
    followers: d.followers ?? [],
    subtasks: normaliseNodes(d.subtasks),
    dependencies: d.dependencies ?? [],
    commitments: d.commitments ?? [],
    approvals: d.approvals ?? [],
    extensions: d.extensions ?? [],
    suggestions: d.suggestions ?? [],
    delegationChain: d.delegationChain ?? [],
    attachments: d.attachments ?? [],
    submissions: (d.submissions ?? []).map((s) => ({ ...s, evidence: s.evidence ?? [] })),
    escalations: d.escalations ?? [],
    messages: d.messages ?? [],
    timeline: d.timeline ?? [],
    permissions: { ...NO_PERMISSIONS, ...d.permissions },
  };
}

/** Completed by the assignee and waiting for someone to confirm. */
export function isAwaitingApproval(status: WorkStatus): boolean {
  return status === 'CompletedByAssignee' || status === 'CompletionAwaitingApproval';
}

export function isClosed(status: WorkStatus): boolean {
  return CLOSED_STATUSES.includes(status);
}

/** Statuses from which a manager may reopen the task. */
export function isReopenable(status: WorkStatus): boolean {
  return isClosed(status) || isAwaitingApproval(status);
}

/** Total number of nodes in a subtask tree. */
export function countSubtasks(nodes: SubtaskNode[]): number {
  return nodes.reduce((n, node) => n + 1 + countSubtasks(node.children ?? []), 0);
}

/** The approval step the current user can decide right now, if any. */
export function pendingApprovalForMe(d: TaskDetail): ApprovalDto | null {
  return d.approvals.find((a) => a.canDecide && a.decision === 'Pending') ?? null;
}

/** True when the approval banner should show (an approval step is mine, or I approve and the task awaits approval). */
export function needsMyApproval(d: TaskDetail): boolean {
  return !!pendingApprovalForMe(d) || (d.permissions.canApprove && isAwaitingApproval(d.summary.status));
}

/** Extension requests waiting for the current user's decision. */
export function decidableExtensions(d: TaskDetail): ExtensionDto[] {
  return d.extensions.filter((e) => e.canDecide && e.status === 'Pending');
}

/** The most recent completion submission (highest round, then latest). */
export function latestSubmission(d: TaskDetail): SubmissionDto | null {
  if (!d.submissions.length) return null;
  return [...d.submissions].sort((a, b) => b.round - a.round || b.submittedAt.localeCompare(a.submittedAt))[0];
}

export function isFollowing(d: TaskDetail, me: Guid | null): boolean {
  return !!me && d.followers.some((f) => f.id === me);
}

export function canAcknowledgeNow(d: TaskDetail): boolean {
  return d.permissions.canAcknowledge && d.acknowledgement === 'Pending';
}

export function evidenceIcon(kind: EvidenceKind | string): string {
  switch (kind) {
    case 'Url':
      return 'link';
    case 'GitCommit':
      return 'git-branch';
    case 'Screenshot':
      return 'eye';
    case 'TestResult':
      return 'check-square';
    case 'CustomerConfirmation':
      return 'user-check';
    case 'Report':
      return 'chart';
    case 'Document':
      return 'file';
    default:
      return 'paperclip';
  }
}

export function channelIcon(channel: string | null | undefined): string {
  switch (channel) {
    case 'Email':
      return 'mail';
    case 'TeamsChannel':
      return 'hash';
    case 'TeamsChat':
      return 'message';
    default:
      return 'messages';
  }
}

/** Whole days since an ISO instant (0 when missing or in the future). */
export function daysSince(iso: string | null | undefined, now: Date = new Date()): number {
  if (!iso) return 0;
  const t = new Date(iso).getTime();
  if (isNaN(t)) return 0;
  return Math.max(0, Math.floor((now.getTime() - t) / 86_400_000));
}
