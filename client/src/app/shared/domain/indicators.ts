/**
 * Small, colour-for-meaning display components used across every page:
 * health dot, criticality / priority chips, status badge, attention score bar, deadline text, avatar, person.
 */
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PersonRef } from '../../core/models/common.models';
import { Criticality, DeadlineBucket, TaskHealth, TaskPriority } from '../../core/models/enums';
import { humanize, initials } from '../../core/util/humanize';
import { criticalityTone, healthTone, statusTone, toneOf } from '../../core/util/tone';

/** Healthy green · Watch amber · AtRisk orange · Critical red (dot + optional label). */
@Component({
  selector: 'fu-health',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="dot tone-{{ tone() }}" [class.pulse]="value() === 'Critical'"></span>
    @if (showLabel()) {
      <span class="lbl">{{ label() }}</span>
    }`,
  host: {
    class: 'fu-health',
    '[attr.title]': '"Health: " + label()',
    '[attr.aria-label]': 'showLabel() ? null : "Health: " + label()',
    style: 'display:inline-flex;align-items:center;gap:6px;font-size:12.5px;white-space:nowrap',
  },
})
export class HealthDotComponent {
  readonly value = input<TaskHealth | string | null | undefined>('Healthy');
  readonly showLabel = input(true);
  protected readonly tone = computed(() => healthTone(this.value()));
  protected readonly label = computed(() => humanize(this.value() ?? ''));
}

/** Informational grey · Normal slate · Important blue · Critical orange · BusinessCritical red. */
@Component({
  selector: 'fu-criticality',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span [class]="cls()">{{ label() }}</span>`,
  host: { style: 'display:inline-flex' },
})
export class CriticalityChipComponent {
  readonly value = input<Criticality | string | null | undefined>('Normal');
  readonly small = input(false);
  protected readonly label = computed(() => humanize(this.value() ?? 'Normal'));
  protected readonly cls = computed(() => {
    const v = this.value();
    const tone = v === 'Normal' ? 'slate' : `tone-${criticalityTone(v)}`;
    return `chip ${tone}${this.small() ? ' sm' : ''}`;
  });
}

/** Priority as a quiet chip (Urgent/High stand out slightly). */
@Component({
  selector: 'fu-priority',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span [class]="cls()">
    @if (value() === 'Urgent' || value() === 'High') {
      <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>
    }
    {{ value() }}</span>`,
  host: { style: 'display:inline-flex' },
})
export class PriorityChipComponent {
  readonly value = input<TaskPriority | string | null | undefined>('Normal');
  protected readonly cls = computed(() => {
    const v = this.value();
    const tone = v === 'Urgent' ? 'tone-danger' : v === 'High' ? 'tone-orange' : v === 'Low' ? 'outline' : 'slate';
    return `chip sm ${tone}`;
  });
}

/** Workflow / display status badge ("In Progress", "Overdue", "Blocked"…). */
@Component({
  selector: 'fu-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge tone-{{ tone() }}">{{ label() }}</span>`,
  host: { style: 'display:inline-flex' },
})
export class StatusBadgeComponent {
  /** Raw WorkStatus (used for tone when `text` is a display status). */
  readonly status = input<string | null | undefined>('');
  /** Display text (TaskListItem.displayStatus); falls back to the humanised status. */
  readonly text = input<string | null | undefined>('');
  protected readonly label = computed(() => this.text() || humanize(this.status() ?? ''));
  protected readonly tone = computed(() => {
    const t = statusTone(this.text());
    return t !== 'neutral' ? t : statusTone(this.status());
  });
}

/** Attention score 0–100 as a compact bar + number. */
@Component({
  selector: 'fu-score',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="bar" aria-hidden="true"><span [style.width.%]="pct()" [style.background]="color()"></span></span>
    <span class="v num">{{ value() ?? 0 }}</span>`,
  styles: `
    :host { display: inline-flex; align-items: center; gap: 6px; }
    .bar { display: inline-block; width: var(--score-w, 44px); height: 5px; border-radius: 999px; background: var(--bg-sunken); overflow: hidden; }
    .bar > span { display: block; height: 100%; border-radius: inherit; }
    .v { font-size: 12px; font-weight: 600; color: var(--text-2); min-width: 20px; text-align: right; }
  `,
  host: { '[attr.title]': '"Attention score " + (value() ?? 0) + " / 100"', role: 'meter', '[attr.aria-valuenow]': 'value() ?? 0', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-label': 'Attention score' },
})
export class ScoreBarComponent {
  readonly value = input<number | null | undefined>(0);
  protected readonly pct = computed(() => Math.max(0, Math.min(100, this.value() ?? 0)));
  protected readonly color = computed(() => {
    const v = this.pct();
    return v >= 75 ? 'var(--danger)' : v >= 50 ? 'var(--orange)' : v >= 25 ? 'var(--warning)' : 'var(--accent)';
  });
}

/** "Overdue by 7 days" in red, "Due today" amber, "Due in 3 days" muted. */
@Component({
  selector: 'fu-deadline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `{{ text() || '—' }}`,
  host: {
    '[class]': 'cls()',
    style: 'white-space:nowrap;font-size:12.5px',
  },
})
export class DeadlineTextComponent {
  readonly text = input<string | null | undefined>('');
  readonly overdue = input(false);
  readonly bucket = input<DeadlineBucket | string | null | undefined>(null);
  protected readonly cls = computed(() => {
    const b = this.bucket();
    const t = (this.text() ?? '').toLowerCase();
    if (this.overdue() || b === 'Overdue' || b === 'SeverelyOverdue' || t.startsWith('overdue')) return 'text-danger strong';
    if (b === 'DueToday' || t.includes('today')) return 'text-warning strong';
    if (b === 'DueTomorrow' || t.includes('tomorrow')) return 'text-warning';
    if (t.startsWith('completed') || t.startsWith('done')) return 'text-good';
    return 'muted';
  });
}

/** Initials avatar. */
@Component({
  selector: 'fu-avatar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="avatar" [class.sm]="size() === 'sm'" [class.lg]="size() === 'lg'" [style.background]="bg()" [style.color]="fg()" aria-hidden="true">{{ text() }}</span>`,
  host: { style: 'display:inline-flex', '[attr.title]': 'name()' },
})
export class AvatarComponent {
  readonly name = input<string | null | undefined>('');
  readonly size = input<'sm' | 'md' | 'lg'>('md');
  protected readonly text = computed(() => initials(this.name()));
  private readonly hue = computed(() => {
    const s = this.name() ?? '';
    let h = 0;
    for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) % 360;
    return h;
  });
  protected readonly bg = computed(() => `hsl(${this.hue()} 55% 50% / 0.16)`);
  protected readonly fg = computed(() => `hsl(${this.hue()} 50% 40%)`);
}

/** Avatar + name (+ optional title). */
@Component({
  selector: 'fu-person',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarComponent],
  template: `@if (person()?.name || name()) {
      <fu-avatar [name]="person()?.name ?? name()" [size]="size()" />
      <span class="who">
        <span class="nm truncate">{{ person()?.name ?? name() }}</span>
        @if (showTitle() && person()?.title) {
          <span class="tt truncate">{{ person()?.title }}</span>
        }
      </span>
    } @else {
      <span class="muted">{{ emptyText() }}</span>
    }`,
  styles: `
    :host { display: inline-flex; align-items: center; gap: 8px; min-width: 0; }
    .who { display: flex; flex-direction: column; min-width: 0; line-height: 1.25; }
    .nm { font-size: 13px; }
    .tt { font-size: 11.5px; color: var(--muted); }
  `,
})
export class PersonComponent {
  readonly person = input<PersonRef | null | undefined>(null);
  readonly name = input<string | null | undefined>('');
  readonly showTitle = input(false);
  readonly size = input<'sm' | 'md' | 'lg'>('sm');
  readonly emptyText = input('Unassigned');
}

/** A tone chip for arbitrary text (server tone strings accepted). */
@Component({
  selector: 'fu-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span [class]="'chip tone-' + uiTone() + (small() ? ' sm' : '')"><ng-content /></span>`,
  host: { style: 'display:inline-flex' },
})
export class ChipComponent {
  readonly tone = input<string | null | undefined>('neutral');
  readonly small = input(false);
  protected readonly uiTone = computed(() => toneOf(this.tone()));
}

export const INDICATORS = [
  HealthDotComponent,
  CriticalityChipComponent,
  PriorityChipComponent,
  StatusBadgeComponent,
  ScoreBarComponent,
  DeadlineTextComponent,
  AvatarComponent,
  PersonComponent,
  ChipComponent,
] as const;
