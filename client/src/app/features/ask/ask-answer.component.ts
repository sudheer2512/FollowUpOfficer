import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AskAnswer } from '../../core/models/view.models';
import { confidencePercent } from '../../core/util/humanize';
import { CommitmentTableComponent } from '../../shared/domain/commitment-table.component';
import { RecurringInstanceTableComponent } from '../../shared/domain/recurring-instance-table.component';
import { TaskTableComponent } from '../../shared/domain/task-table.component';
import { HumanizePipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { AttentionListComponent } from '../attention/attention-list.component';

const INSTANCE_LIMIT = 10;

/**
 * One answer in the Ask conversation: the answer sentence, how the question was understood (intent + confidence),
 * the AI provider, then the result lists that are non-empty and a "View all" drill-down.
 *   <fu-ask-answer [answer]="turn.answer" (changed)="refresh(turn)" />
 */
@Component({
  selector: 'fu-ask-answer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, HumanizePipe, TaskTableComponent, CommitmentTableComponent, RecurringInstanceTableComponent, AttentionListComponent],
  template: `
    <section class="card answer" aria-label="Answer">
      <div class="card-body">
        <div class="head">
          <span class="ai-mark" aria-hidden="true"><fu-icon name="sparkles" [size]="16" /></span>
          <p class="sentence">{{ answer().answer || 'No answer was returned for this question.' }}</p>
        </div>
        <div class="meta">
          @if (interpretation()) {
            <span class="muted">Understood as: {{ interpretation() }}</span>
          }
          @if (intent()) {
            <span class="chip sm tone-accent" title="Question type">{{ intent() | humanize }}</span>
          }
          @if (confidence()) {
            <span class="xsmall muted" title="How confident the interpretation is">{{ confidence() }} confidence</span>
          }
          <span class="xsmall subtle">AI: {{ answer().provider || 'Heuristic' }}</span>
        </div>
      </div>

      @if (tasks().length) {
        <div class="result">
          <div class="result-head"><fu-icon name="tasks" [size]="14" class="muted" /><h3>Tasks</h3><span class="count-pill">{{ tasks().length }}</span></div>
          <fu-task-table [tasks]="tasks()" [compact]="true" [limit]="10" />
        </div>
      }
      @if (commitments().length) {
        <div class="result">
          <div class="result-head"><fu-icon name="flag" [size]="14" class="muted" /><h3>Commitments</h3><span class="count-pill">{{ commitments().length }}</span></div>
          <fu-commitment-table [commitments]="commitments()" [compact]="true" [limit]="10" />
        </div>
      }
      @if (instances().length) {
        <div class="result">
          <div class="result-head"><fu-icon name="repeat" [size]="14" class="muted" /><h3>Recurring occurrences</h3><span class="count-pill">{{ instances().length }}</span></div>
          <fu-instance-table [instances]="visibleInstances()" [showDefinition]="true" />
          @if (instances().length > visibleInstances().length) {
            <div class="more small muted">+ {{ instances().length - visibleInstances().length }} more</div>
          }
        </div>
      }
      @if (attention().length) {
        <div class="result">
          <div class="result-head"><fu-icon name="target" [size]="14" class="muted" /><h3>Needs attention</h3><span class="count-pill">{{ attention().length }}</span></div>
          <fu-attention-list [items]="attention()" [compact]="true" [limit]="8" (changed)="changed.emit()" />
        </div>
      }

      @if (answer().drilldown; as d) {
        <div class="card-footer">
          <span class="spacer"></span>
          <a class="btn btn-sm" [routerLink]="d.route" [queryParams]="d.query">View all <fu-icon name="arrow-right" [size]="14" /></a>
        </div>
      }
    </section>
  `,
  styles: `
    :host { display: block; }
    .head { display: flex; gap: 12px; align-items: flex-start; }
    .ai-mark { width: 30px; height: 30px; border-radius: 9px; background: var(--accent-soft); color: var(--accent-text); display: flex; align-items: center; justify-content: center; flex: none; }
    .sentence { margin: 4px 0 0; font-size: 15.5px; line-height: 1.5; font-weight: 500; color: var(--text); }
    .meta { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 10px 0 0 42px; font-size: 12.5px; }
    .result { border-top: 1px solid var(--border); }
    .result-head { display: flex; align-items: center; gap: 8px; padding: 10px 16px 6px; }
    .result-head h3 { font-size: 13px; }
    .result fu-attention-list { display: block; }
    .more { padding: 8px 16px; border-top: 1px solid var(--border); }
    @media (max-width: 640px) { .meta { margin-left: 0; } }
  `,
})
export class AskAnswerComponent {
  readonly answer = input.required<AskAnswer>();
  /** An attention action changed server state; the host re-asks the question silently. */
  readonly changed = output<void>();

  protected readonly interpretation = computed(() => this.answer().interpretation || this.answer().query?.interpretation || '');
  protected readonly intent = computed(() => this.answer().query?.intent ?? null);
  protected readonly confidence = computed(() => {
    const c = this.answer().query?.confidence;
    return typeof c === 'number' && c > 0 ? confidencePercent(c) : '';
  });
  protected readonly tasks = computed(() => this.answer().tasks ?? []);
  protected readonly commitments = computed(() => this.answer().commitments ?? []);
  protected readonly instances = computed(() => this.answer().instances ?? []);
  protected readonly visibleInstances = computed(() => this.instances().slice(0, INSTANCE_LIMIT));
  protected readonly attention = computed(() => this.answer().attention ?? []);
}
