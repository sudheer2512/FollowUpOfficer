import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TaskDetail } from '../../../core/models/task.models';
import { CommitmentTableComponent } from '../../../shared/domain/commitment-table.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { DelegationChainComponent } from './delegation-chain.component';
import { AttachmentsCardComponent } from './overview/attachments-card.component';
import { DatesCardComponent } from './overview/dates-card.component';
import { DependenciesCardComponent } from './overview/dependencies-card.component';
import { DetailsCardComponent } from './overview/details-card.component';
import { EscalationsCardComponent } from './overview/escalations-card.component';
import { ExtensionsCardComponent } from './overview/extensions-card.component';
import { FollowUpCardComponent } from './overview/follow-up-card.component';
import { PeopleCardComponent } from './overview/people-card.component';
import { SuggestionsCardComponent } from './overview/suggestions-card.component';
import { DialogRequest } from './task-actions';
import { isClosed } from './task-detail.util';

/**
 * Overview tab: what the task is and where it stands. Main column — AI suggestions, details, dependencies, commitments,
 * extensions, escalations, attachments; side column — people, dates, follow-up, delegation chain.
 * Sections with nothing to show (and nothing the user could add) are left out to keep the page calm.
 */
@Component({
  selector: 'fu-overview-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IconComponent,
    CommitmentTableComponent,
    DelegationChainComponent,
    SuggestionsCardComponent,
    DetailsCardComponent,
    DependenciesCardComponent,
    ExtensionsCardComponent,
    EscalationsCardComponent,
    AttachmentsCardComponent,
    PeopleCardComponent,
    DatesCardComponent,
    FollowUpCardComponent,
  ],
  template: `
    @let d = detail();
    <div class="overview">
      <div class="col">
        @if (hasPendingSuggestions()) {
          <fu-suggestions-card [detail]="d" (updated)="updated.emit($event)" />
        }
        <fu-details-card [detail]="d" />
        @if (d.dependencies.length || canWork()) {
          <fu-dependencies-card [detail]="d" (updated)="updated.emit($event)" (open)="open.emit($event)" />
        }
        @if (d.commitments.length || canWork()) {
          <section class="card">
            <header class="card-header">
              <h2>Commitments</h2>
              <span class="count-pill">{{ d.commitments.length }}</span>
              <span class="spacer"></span>
              @if (canWork()) {
                <button type="button" class="btn btn-xs btn-ghost" (click)="open.emit({ kind: 'commitment' })"><fu-icon name="plus" [size]="13" /> Add</button>
              }
            </header>
            <div class="card-body flush">
              <fu-commitment-table [commitments]="d.commitments" [compact]="true" emptyMessage="Promises like “I’ll send it by Friday” are picked up from replies and comments." />
            </div>
          </section>
        }
        @if (d.extensions.length) {
          <fu-extensions-card [detail]="d" (open)="open.emit($event)" />
        }
        @if (d.escalations.length) {
          <fu-escalations-card [detail]="d" />
        }
        <fu-attachments-card [detail]="d" (reload)="reload.emit()" />
      </div>

      <div class="col">
        <fu-people-card [detail]="d" [following]="following()" [followBusy]="busy()" (toggleFollow)="toggleFollow.emit()" />
        <fu-dates-card [detail]="d" />
        <fu-follow-up-card [detail]="d" [busy]="busy()" (open)="open.emit($event)" (resume)="resume.emit()" />
        <section class="card">
          <header class="card-header"><h2>Delegation chain</h2></header>
          <div class="card-body">
            <fu-delegation-chain [chain]="d.delegationChain" />
          </div>
        </section>
      </div>
    </div>
  `,
  styles: `
    .overview { display: grid; grid-template-columns: minmax(0, 3fr) minmax(0, 2fr); gap: 16px; align-items: start; }
    .col { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
    @media (max-width: 1100px) { .overview { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class OverviewTabComponent {
  readonly detail = input.required<TaskDetail>();
  readonly following = input(false);
  readonly busy = input(false);

  readonly updated = output<TaskDetail>();
  readonly reload = output<void>();
  readonly open = output<DialogRequest>();
  readonly toggleFollow = output<void>();
  readonly resume = output<void>();

  protected readonly hasPendingSuggestions = computed(() => this.detail().suggestions.some((s) => s.status === 'Pending'));
  /** The user works on or manages this open task, so empty sections still offer "Add". */
  protected readonly canWork = computed(() => {
    const d = this.detail();
    return (d.permissions.canManage || d.permissions.canUpdate) && !isClosed(d.summary.status);
  });
}
