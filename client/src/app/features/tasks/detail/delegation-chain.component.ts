import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PersonRef } from '../../../core/models/common.models';
import { DelegationDto } from '../../../core/models/task.models';
import { AvatarComponent } from '../../../shared/domain/indicators';
import { DateTimePipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';

interface ChainStep {
  person: PersonRef;
  /** The hand-over that brought the task to this person (absent for the originator). */
  delegation: DelegationDto | null;
}

/**
 * Who delegated to whom: CEO → CTO → Manager → Developer.
 * `compact` renders a single wrapped line of names (used in the Reassign dialog); otherwise a vertical stepper with
 * dates and delegation notes.
 */
@Component({
  selector: 'fu-delegation-chain',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarComponent, IconComponent, DateTimePipe],
  template: `
    @if (!steps().length) {
      <div class="muted small">No delegation recorded.</div>
    } @else if (compact()) {
      <ol class="line" aria-label="Delegation chain">
        @for (s of steps(); track $index; let last = $last) {
          <li [class.current]="last">
            <fu-avatar [name]="s.person.name" size="sm" />
            <span>{{ s.person.name }}</span>
            @if (!last) {
              <fu-icon name="arrow-right" [size]="13" class="muted" />
            }
          </li>
        }
      </ol>
    } @else {
      <ol class="steps" aria-label="Delegation chain">
        @for (s of steps(); track $index; let last = $last) {
          <li [class.current]="last">
            <span class="node"><fu-avatar [name]="s.person.name" size="sm" /></span>
            <div class="body">
              <div class="who">
                <span class="strong">{{ s.person.name }}</span>
                @if (s.person.title) {
                  <span class="muted small">{{ s.person.title }}</span>
                }
                @if (last) {
                  <span class="chip sm tone-accent">Responsible now</span>
                }
              </div>
              @if (s.delegation) {
                <div class="xsmall muted">Delegated {{ s.delegation.delegatedAt | dateTime: 'short' }}</div>
                @if (s.delegation.note) {
                  <div class="note small">“{{ s.delegation.note }}”</div>
                }
              } @else {
                <div class="xsmall muted">Originator</div>
              }
            </div>
          </li>
        }
      </ol>
    }
  `,
  styles: `
    :host { display: block; }
    .line { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 6px; align-items: center; font-size: 13px; }
    .line li { display: inline-flex; align-items: center; gap: 6px; }
    .line li.current span { font-weight: 600; }
    .steps { list-style: none; margin: 0; padding: 0; }
    .steps li { position: relative; display: flex; gap: 10px; padding-bottom: 14px; }
    .steps li::before { content: ''; position: absolute; left: 11px; top: 26px; bottom: 2px; width: 1px; background: var(--border); }
    .steps li:last-child { padding-bottom: 0; }
    .steps li:last-child::before { display: none; }
    .node { flex: none; }
    .body { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .who { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .note { color: var(--text-2); font-style: italic; }
  `,
})
export class DelegationChainComponent {
  readonly chain = input<DelegationDto[]>([]);
  readonly compact = input(false);

  /** People in hand-over order: the first delegator, then each recipient. */
  protected readonly steps = computed<ChainStep[]>(() => {
    const ordered = [...this.chain()].sort((a, b) => a.sequence - b.sequence);
    if (!ordered.length) return [];
    const steps: ChainStep[] = [];
    const first = ordered[0];
    if (first.from) steps.push({ person: first.from, delegation: null });
    for (const d of ordered) steps.push({ person: d.to, delegation: d });
    return steps;
  });
}
