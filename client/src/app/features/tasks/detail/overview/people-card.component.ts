import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AssigneeRole } from '../../../../core/models/enums';
import { AssigneeDto, TaskDetail } from '../../../../core/models/task.models';
import { AvatarComponent, PersonComponent } from '../../../../shared/domain/indicators';
import { IconComponent } from '../../../../shared/ui/icon.component';

interface AssigneeGroup {
  role: AssigneeRole;
  label: string;
  members: AssigneeDto[];
}

const ROLE_LABELS: [AssigneeRole, string][] = [
  ['Additional', 'Also assigned'],
  ['TeamLeader', 'Team leader'],
  ['Supporting', 'Supporting'],
];

/** Who is involved: responsible person, other assignees by role, team, accountable manager, delegators, followers. */
@Component({
  selector: 'fu-people-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PersonComponent, AvatarComponent, IconComponent],
  template: `
    @let d = detail();
    <section class="card">
      <header class="card-header"><h2>People</h2></header>
      <div class="card-body">
        <dl class="kv">
          <dt>Responsible</dt>
          <dd><fu-person [person]="d.summary.primaryAssignee" [name]="d.summary.teamName" [showTitle]="true" /></dd>
          @for (g of groups(); track g.role) {
            <dt>{{ g.label }}</dt>
            <dd class="people">
              @for (a of g.members; track a.id) {
                <fu-person [name]="a.employeeName ?? a.teamName" />
              }
            </dd>
          }
          @if (d.team) {
            <dt>Team</dt>
            <dd>{{ d.team.name }}</dd>
          }
          <dt>Accountable</dt>
          <dd><fu-person [person]="d.accountableManager" emptyText="—" [showTitle]="true" /></dd>
          <dt>Delegated by</dt>
          <dd><fu-person [person]="d.summary.delegator" emptyText="—" [showTitle]="true" /></dd>
          @if (showOriginalDelegator()) {
            <dt>Originally from</dt>
            <dd><fu-person [person]="d.originalDelegator" [showTitle]="true" /></dd>
          }
          <dt>Followers</dt>
          <dd>
            <div class="followers">
              @for (f of d.followers; track f.id) {
                <fu-avatar [name]="f.name" size="sm" />
              } @empty {
                <span class="muted small">None</span>
              }
              <button type="button" class="btn btn-xs" [disabled]="followBusy()" [attr.aria-pressed]="following()" (click)="toggleFollow.emit()">
                <fu-icon [name]="following() ? 'eye-off' : 'eye'" [size]="13" /> {{ following() ? 'Unfollow' : 'Follow' }}
              </button>
            </div>
          </dd>
        </dl>
      </div>
    </section>
  `,
  styles: `
    .people { display: flex; flex-direction: column; gap: 6px; }
    .followers { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; }
    .followers .btn { margin-left: 6px; }
  `,
})
export class PeopleCardComponent {
  readonly detail = input.required<TaskDetail>();
  readonly following = input(false);
  readonly followBusy = input(false);
  readonly toggleFollow = output<void>();

  /** Non-primary assignees grouped by role (the primary is shown as "Responsible"). */
  protected readonly groups = computed<AssigneeGroup[]>(() => {
    const assignees = this.detail().assignees;
    return ROLE_LABELS.map(([role, label]) => ({ role, label, members: assignees.filter((a) => a.role === role) })).filter((g) => g.members.length > 0);
  });

  protected readonly showOriginalDelegator = computed(() => {
    const d = this.detail();
    return !!d.originalDelegator && d.originalDelegator.id !== d.summary.delegator?.id;
  });
}
