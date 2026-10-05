import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TaskDetail } from '../../../core/models/task.models';
import {
  CriticalityChipComponent,
  DeadlineTextComponent,
  HealthDotComponent,
  PriorityChipComponent,
  ScoreBarComponent,
  StatusBadgeComponent,
} from '../../../shared/domain/indicators';
import { HumanizePipe, MoneyPipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';

/**
 * Task header: breadcrumb (Tasks › parent › number), title, status / health / criticality / priority / deadline,
 * context chips (visibility, blocked, escalation, customer & financial impact, recurring series, meeting, project),
 * the attention score with a "why" popover, and Follow / Unfollow.
 */
@Component({
  selector: 'fu-task-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    IconComponent,
    StatusBadgeComponent,
    HealthDotComponent,
    CriticalityChipComponent,
    PriorityChipComponent,
    DeadlineTextComponent,
    ScoreBarComponent,
    HumanizePipe,
    MoneyPipe,
  ],
  template: `
    @let s = detail().summary;
    @let d = detail();
    <nav class="breadcrumb" aria-label="Breadcrumb">
      <a routerLink="/tasks">Tasks</a>
      @if (d.parent) {
        <fu-icon name="chevron-right" [size]="12" />
        <a [routerLink]="['/tasks', d.parent.id]" [attr.title]="d.parent.title"><span class="task-number">{{ d.parent.number }}</span></a>
        <span class="truncate parent">{{ d.parent.title }}</span>
      }
      <fu-icon name="chevron-right" [size]="12" />
      <span class="task-number" aria-current="page">{{ s.number }}</span>
    </nav>

    <div class="head">
      <div class="titles">
        <h1>
          @if (s.visibility !== 'Normal') {
            <fu-icon name="lock" [size]="17" class="muted lock" [label]="'Visibility: ' + (s.visibility | humanize)" [attr.title]="'Visibility: ' + (s.visibility | humanize)" />
          }
          {{ s.title }}
        </h1>
        <div class="meta">
          <fu-status [status]="s.status" [text]="s.displayStatus" />
          <fu-health [value]="s.health" />
          <fu-criticality [value]="s.criticality" />
          <fu-priority [value]="s.priority" />
          <span class="due"><fu-icon name="calendar" [size]="13" class="muted" /><fu-deadline [text]="s.deadlineText" [overdue]="s.isOverdue" [bucket]="s.bucket" /></span>
          @if (s.progressPercent > 0) {
            <span class="prog" [attr.title]="s.progressPercent + '% complete'">
              <span class="progress"><span [style.width.%]="s.progressPercent"></span></span>
              <span class="small muted num">{{ s.progressPercent }}%</span>
            </span>
          }
        </div>
        <div class="chips">
          @if (s.isBlocked) {
            <span class="chip sm tone-danger" [attr.title]="s.blockedReason ?? 'Blocked'"><fu-icon name="ban" [size]="12" /> Blocked</span>
          }
          @if (s.escalationLevel > 0) {
            <span class="chip sm tone-orange"><fu-icon name="zap" [size]="12" /> Escalated · level {{ s.escalationLevel }}</span>
          }
          @if (d.customerImpact) {
            <span class="chip sm tone-info"><fu-icon name="users" [size]="12" /> Customer impact{{ d.customerName ? ' · ' + d.customerName : '' }}</span>
          }
          @if (d.financialImpact) {
            <span class="chip sm tone-warning">Financial impact {{ d.financialImpact | money }}</span>
          }
          @if (d.notifyCeoOnDelay) {
            <span class="chip sm tone-accent"><fu-icon name="bell" [size]="12" /> CEO notified on delay</span>
          }
          @if (d.recurringDefinitionId) {
            <a class="chip sm" [routerLink]="['/recurring', d.recurringDefinitionId]">
              <fu-icon name="repeat" [size]="12" /> {{ d.recurringTitle || 'Recurring series' }}{{ d.occurrenceKey ? ' · ' + d.occurrenceKey : '' }}
            </a>
          }
          @if (d.meetingId) {
            <a class="chip sm" [routerLink]="['/meetings', d.meetingId]"><fu-icon name="users" [size]="12" /> {{ d.meetingTitle || 'Meeting' }}</a>
          }
          @if (d.project) {
            <a class="chip sm" [routerLink]="['/projects', d.project.id]"><fu-icon name="folder" [size]="12" /> {{ d.project.name }}</a>
          }
          @for (tag of s.tags; track tag) {
            <a class="tag" routerLink="/tasks" [queryParams]="{ tag }">#{{ tag }}</a>
          }
        </div>
      </div>

      <div class="side">
        <div class="score-wrap">
          <button
            type="button"
            class="btn btn-ghost score-btn"
            aria-haspopup="dialog"
            [attr.aria-expanded]="showFactors()"
            aria-label="Attention score — show the factors"
            (click)="showFactors.update((v) => !v)"
          >
            <span class="small muted">Attention</span>
            <fu-score [value]="s.attentionScore" />
            <fu-icon name="chevron-down" [size]="13" class="muted" />
          </button>
          @if (showFactors()) {
            <div class="menu factors" role="dialog" aria-label="Attention score factors" (keydown.escape)="showFactors.set(false)">
              <div class="menu-label">Why this needs attention</div>
              @for (f of d.attentionFactors; track f.code) {
                <div class="factor">
                  <span>{{ f.label }}</span>
                  <span class="num pts" [class.neg]="f.points < 0">{{ f.points > 0 ? '+' : '' }}{{ f.points }}</span>
                </div>
              } @empty {
                <div class="factor muted">Nothing unusual — routine follow-up.</div>
              }
              <div class="menu-sep"></div>
              <div class="factor strong">
                <span>Score</span>
                <span class="num">{{ s.attentionScore }} / 100</span>
              </div>
              @if (factorTotal() !== s.attentionScore && d.attentionFactors.length) {
                <div class="xsmall muted pad">Factors add up to {{ factorTotal() }}; the score is capped at 100.</div>
              }
            </div>
          }
        </div>
        <button type="button" class="btn" [attr.aria-pressed]="following()" [disabled]="followBusy()" (click)="toggleFollow.emit()">
          <fu-icon [name]="following() ? 'check' : 'eye'" [size]="15" />
          {{ following() ? 'Following' : 'Follow' }}
        </button>
      </div>
    </div>
  `,
  styles: `
    :host { display: block; margin-bottom: 14px; }
    .parent { max-width: 320px; }
    .head { display: flex; gap: 16px; align-items: flex-start; flex-wrap: wrap; }
    .titles { flex: 1 1 480px; min-width: 0; }
    h1 { display: flex; align-items: flex-start; gap: 8px; overflow-wrap: anywhere; }
    .lock { margin-top: 5px; }
    .meta { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; margin-top: 10px; }
    .due, .prog { display: inline-flex; align-items: center; gap: 6px; }
    .prog .progress { width: 70px; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 10px; }
    .chips:empty { display: none; }
    .side { display: flex; align-items: center; gap: 8px; }
    .score-wrap { position: relative; }
    .score-btn { gap: 8px; }
    .factors { right: 0; top: calc(100% + 4px); width: 300px; }
    .factor { display: flex; justify-content: space-between; gap: 12px; padding: 6px 10px; font-size: 13px; }
    .pts { color: var(--danger); font-weight: 600; }
    .pts.neg { color: var(--good); }
    .pad { padding: 0 10px 6px; }
  `,
  host: { '(document:click)': 'onDocumentClick($event)' },
})
export class TaskHeaderComponent {
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly detail = input.required<TaskDetail>();
  readonly following = input(false);
  readonly followBusy = input(false);
  readonly toggleFollow = output<void>();

  protected readonly showFactors = signal(false);
  protected readonly factorTotal = computed(() => this.detail().attentionFactors.reduce((sum, f) => sum + (f.points ?? 0), 0));

  protected onDocumentClick(e: MouseEvent): void {
    if (!this.showFactors()) return;
    const wrap = this.el.nativeElement.querySelector('.score-wrap');
    if (wrap && !wrap.contains(e.target as Node)) this.showFactors.set(false);
  }
}
