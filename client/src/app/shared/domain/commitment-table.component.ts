import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CommitmentStatus, CommunicationSource } from '../../core/models/enums';
import { CommitmentDto } from '../../core/models/task.models';
import { DateOnlyPipe, HumanizePipe } from '../pipes/format.pipes';
import { EmptyStateComponent } from '../ui/empty-state.component';
import { IconComponent } from '../ui/icon.component';
import { SkeletonComponent } from '../ui/skeleton.component';
import { PersonComponent } from './indicators';

export function commitmentStatusTone(s: CommitmentStatus | string): string {
  switch (s) {
    case 'Met':
      return 'good';
    case 'Missed':
      return 'danger';
    case 'Open':
      return 'info';
    default:
      return 'neutral';
  }
}

export function sourceIcon(s: CommunicationSource | string): string {
  switch (s) {
    case 'Email':
      return 'mail';
    case 'TeamsChat':
      return 'message';
    case 'TeamsChannel':
      return 'hash';
    case 'Meeting':
      return 'users';
    case 'Comment':
      return 'messages';
    case 'Acknowledgement':
      return 'user-check';
    default:
      return 'edit';
  }
}

/**
 * Commitments ("We will complete this by Friday") with the person, task, date, original text, source and status.
 *   <fu-commitment-table [commitments]="list" />
 */
@Component({
  selector: 'fu-commitment-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, PersonComponent, EmptyStateComponent, SkeletonComponent, DateOnlyPipe, HumanizePipe],
  template: `
    @if (loading() && !commitments().length) {
      <fu-skeleton variant="table" [rows]="5" />
    } @else if (!commitments().length) {
      <fu-empty icon="flag" [title]="emptyTitle()" [message]="emptyMessage()" [compact]="compact()" />
    } @else {
      <div class="table-wrap">
        <table class="table" [class.compact]="compact()">
          <thead>
            <tr>
              <th>Person</th>
              <th>Task</th>
              <th>Commitment date</th>
              @if (!compact()) {
                <th>Original text</th>
              }
              <th>Source</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            @for (c of visible(); track c.id) {
              <tr>
                <td><fu-person [person]="c.person" emptyText="—" /></td>
                <td class="task">
                  <a [routerLink]="['/tasks', c.taskId]"><span class="task-number">{{ c.taskNumber }}</span></a>
                  <div class="truncate small" [attr.title]="c.taskTitle">{{ c.taskTitle }}</div>
                </td>
                <td class="nowrap" [class.text-danger]="c.status === 'Missed'" [class.strong]="c.status === 'Missed'">{{ c.commitmentDate | dateOnly: 'weekday' }}</td>
                @if (!compact()) {
                  <td class="quote">
                    <q class="clamp-2" [attr.title]="c.originalText">{{ c.originalText }}</q>
                  </td>
                }
                <td class="nowrap small">
                  <span class="row gap-4">
                    <fu-icon [name]="srcIcon(c.source)" [size]="14" class="muted" />
                    @if (c.sourceMessageId) {
                      <a routerLink="/communications" [queryParams]="{ message: c.sourceMessageId }">{{ c.source | humanize }}</a>
                    } @else {
                      {{ c.source | humanize }}
                    }
                  </span>
                </td>
                <td><span class="badge tone-{{ tone(c.status) }}">{{ c.status }}</span></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      @if (limit() && commitments().length > limit()!) {
        <div class="small muted more">+ {{ commitments().length - limit()! }} more</div>
      }
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .task { max-width: 260px; }
    .quote { max-width: 360px; color: var(--text-2); font-style: italic; }
    q { quotes: '“' '”'; }
    .more { padding: 8px 16px; border-top: 1px solid var(--border); }
  `,
})
export class CommitmentTableComponent {
  readonly commitments = input<CommitmentDto[]>([]);
  readonly loading = input(false);
  readonly compact = input(false);
  readonly limit = input<number | null>(null);
  readonly emptyTitle = input('No commitments');
  readonly emptyMessage = input('Promises such as “we will finish by Friday” appear here when they are detected.');

  protected readonly visible = computed(() => {
    const l = this.limit();
    return l ? this.commitments().slice(0, l) : this.commitments();
  });

  protected tone(s: CommitmentStatus): string {
    return commitmentStatusTone(s);
  }

  protected srcIcon(s: CommunicationSource): string {
    return sourceIcon(s);
  }
}
