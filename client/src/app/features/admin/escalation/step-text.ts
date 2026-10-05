import { humanize } from '../../../core/util/humanize';

/** The parts of an escalation step needed to describe it in one sentence. */
export interface StepSummaryInput {
  trigger: string;
  threshold: number;
  unit: string;
  targets: readonly string[];
  channels: readonly string[];
  level: number;
  raiseCeoAttention: boolean;
}

/** "2 working days", "1 hour", "3 days". */
export function describeAmount(threshold: number, unit: string): string {
  const n = Number.isFinite(threshold) ? Math.max(0, threshold) : 0;
  const label = unit === 'Hours' ? 'hour' : unit === 'WorkingDays' ? 'working day' : 'day';
  return `${n} ${label}${n === 1 ? '' : 's'}`;
}

/** When the step fires, e.g. "2 working days overdue", "No response for 24 hours". */
export function describeTrigger(trigger: string, threshold: number, unit: string): string {
  const amount = describeAmount(threshold, unit);
  switch (trigger) {
    case 'BeforeDue':
      return `${amount} before due`;
    case 'OnDue':
      return threshold > 0 ? `${amount} after the due time` : 'On the due date';
    case 'Overdue':
      return `${amount} overdue`;
    case 'NoResponse':
      return `No response for ${amount}`;
    case 'AcknowledgementPending':
      return `Not acknowledged after ${amount}`;
    case 'CommitmentMissed':
      return threshold > 0 ? `Commitment missed by ${amount}` : 'Commitment missed';
    case 'Blocked':
      return `Blocked for ${amount}`;
    default:
      return `${humanize(trigger)} after ${amount}`;
  }
}

/**
 * One human sentence per step:
 * "2 working days overdue → Team leader, Department head via Email, Teams · level 2 · raises CEO attention".
 */
export function describeStep(step: StepSummaryInput): string {
  const targets = step.targets.length ? step.targets.map((t) => humanize(t)).join(', ') : 'no one yet';
  const channels = step.channels.length ? ` via ${step.channels.map((c) => humanize(c)).join(', ')}` : '';
  const parts = [`${describeTrigger(step.trigger, step.threshold, step.unit)} → ${targets}${channels}`, `level ${step.level}`];
  if (step.raiseCeoAttention) parts.push('raises CEO attention');
  return parts.join(' · ');
}
