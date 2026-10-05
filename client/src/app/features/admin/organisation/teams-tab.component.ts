import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminApi } from '../../../core/api/admin.api';
import { TeamDto, TeamInput, TeamMemberDto } from '../../../core/models/admin.models';
import { Guid } from '../../../core/models/common.models';
import { LookupsStore } from '../../../core/state/lookups.store';
import { loader } from '../../../core/util/loader';
import { AvatarComponent, PersonComponent } from '../../../shared/domain/indicators';
import { LookupSelectComponent } from '../../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../../shared/forms/person-picker.component';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ModalComponent } from '../../../shared/ui/modal.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { formView, textOrNull, valueOf } from '../shared/form-view';

const MAX_AVATARS = 5;

/** Teams table (leader, deputy, members, email DL, Teams channel) with a create / edit modal. */
@Component({
  selector: 'fu-teams-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    AvatarComponent,
    PersonComponent,
    LookupSelectComponent,
    PersonPickerComponent,
    EmptyStateComponent,
    IconComponent,
    ModalComponent,
    SkeletonComponent,
  ],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Teams</h2>
        <span class="count-pill">{{ rows().length }}</span>
        <span class="spacer"></span>
        <input class="input input-sm search" type="search" placeholder="Filter teams…" aria-label="Filter teams" [value]="query()" (input)="query.set(value($event))" />
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh teams" (click)="reload()">
          <fu-icon name="refresh" />
        </button>
        <button type="button" class="btn btn-primary btn-sm" (click)="openNew()"><fu-icon name="plus" /> New team</button>
      </header>
      <div class="card-body flush">
        @if (data.loading() && !data.value()) {
          <fu-skeleton variant="table" [rows]="5" />
        } @else if (data.error()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!rows().length) {
          <fu-empty icon="users" [title]="query() ? 'No matching teams' : 'No teams yet'" [message]="query() ? 'Try a different filter.' : 'Create a team to group people under a leader.'" />
        } @else {
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th>Team</th>
                  <th>Department</th>
                  <th>Leader</th>
                  <th>Deputy</th>
                  <th>Members</th>
                  <th>Email DL</th>
                  <th>Teams channel</th>
                  <th>Status</th>
                  <th><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (t of rows(); track t.id) {
                  <tr class="clickable" tabindex="0" [attr.aria-label]="'Edit ' + t.name" (click)="openEdit(t)" (keydown.enter)="openEdit(t)">
                    <td class="strong">{{ t.name }}</td>
                    <td class="muted">{{ t.department?.name ?? '—' }}</td>
                    <td><fu-person [person]="t.leader" emptyText="—" /></td>
                    <td><fu-person [person]="t.deputyLeader" emptyText="—" /></td>
                    <td>
                      <div class="row gap-4">
                        <span class="num small strong">{{ t.members.length }}</span>
                        <span class="stack-avatars">
                          @for (m of avatarsOf(t); track m.employeeId) {
                            <span [attr.title]="m.name + ' · ' + m.role"><fu-avatar [name]="m.name" size="sm" /></span>
                          }
                        </span>
                        @if (t.members.length > maxAvatars) {
                          <span class="xsmall muted">+{{ t.members.length - maxAvatars }}</span>
                        }
                      </div>
                    </td>
                    <td class="small">{{ t.email || '—' }}</td>
                    <td class="small">
                      @if (t.teamsChannelName || t.teamsChannelId) {
                        <div># {{ t.teamsChannelName || 'Channel' }}</div>
                        <div class="mono xsmall muted truncate ids" [attr.title]="'Team ' + (t.teamsTeamId ?? '—') + ' · Channel ' + (t.teamsChannelId ?? '—')">
                          {{ t.teamsTeamId ?? '—' }} / {{ t.teamsChannelId ?? '—' }}
                        </div>
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td>
                      <span class="badge" [class.tone-good]="t.isActive">{{ t.isActive ? 'Active' : 'Inactive' }}</span>
                    </td>
                    <td class="actions">
                      <button type="button" class="btn btn-ghost btn-icon btn-xs" [attr.aria-label]="'Edit ' + t.name" (click)="openEdit(t); $event.stopPropagation()">
                        <fu-icon name="edit" [size]="14" />
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </section>

    @if (editing(); as e) {
      <fu-modal [title]="e === 'new' ? 'New team' : 'Edit team'" [subtitle]="e === 'new' ? '' : e.name" size="lg" [dismissOnScrim]="false" (closed)="close()">
        <form [formGroup]="form" class="form-grid" (ngSubmit)="save()" id="team-form" novalidate>
          <div class="field">
            <label for="team-name" class="req">Name</label>
            <input id="team-name" class="input" formControlName="name" maxlength="120" />
            @if (form.controls.name.touched && form.controls.name.invalid) {
              <span class="error">A team name is required.</span>
            }
          </div>
          <div class="field">
            <label for="team-dep">Department</label>
            <fu-lookup-select kind="department" formControlName="departmentId" selectId="team-dep" emptyLabel="No department" />
          </div>
          <div class="field">
            <label for="team-leader">Leader</label>
            <fu-person-picker formControlName="leaderId" inputId="team-leader" />
          </div>
          <div class="field">
            <label for="team-deputy">Deputy leader</label>
            <fu-person-picker formControlName="deputyLeaderId" inputId="team-deputy" [excludeIds]="leaderExclusion()" />
          </div>
          <div class="field full">
            <label for="team-members">Members</label>
            <fu-person-picker formControlName="memberIds" inputId="team-members" [multiple]="true" placeholder="Add members…" />
            @if (currentMembers().length) {
              <div class="roles">
                <span class="hint">Current roles:</span>
                @for (m of currentMembers(); track m.employeeId) {
                  <span class="chip sm" [class.tone-accent]="m.role === 'Leader'" [class.tone-info]="m.role === 'Deputy'">
                    {{ m.name }}{{ m.role !== 'Member' ? ' · ' + m.role : '' }}
                  </span>
                }
              </div>
            }
          </div>
          <div class="field full">
            <label for="team-email">Email distribution list</label>
            <input id="team-email" class="input" type="email" formControlName="email" placeholder="finance-team@contoso.com" />
            @if (form.controls.email.touched && form.controls.email.invalid) {
              <span class="error">Enter a valid email address.</span>
            }
          </div>
          <div class="field">
            <label for="team-teams-id">Microsoft Teams team id</label>
            <input id="team-teams-id" class="input mono" formControlName="teamsTeamId" autocomplete="off" />
          </div>
          <div class="field">
            <label for="team-channel-id">Channel id</label>
            <input id="team-channel-id" class="input mono" formControlName="teamsChannelId" autocomplete="off" />
          </div>
          <div class="field full">
            <label for="team-channel-name">Channel name</label>
            <input id="team-channel-name" class="input" formControlName="teamsChannelName" placeholder="General" />
            <span class="hint">Messages in this channel are imported and matched to the team's tasks.</span>
          </div>
          <div class="field full">
            <label class="switch"><input type="checkbox" formControlName="isActive" /> Active</label>
          </div>
        </form>
        <ng-container modal-footer>
          <button type="button" class="btn" (click)="close()">Cancel</button>
          <button type="submit" form="team-form" class="btn btn-primary" [disabled]="saving()">
            {{ saving() ? 'Saving…' : e === 'new' ? 'Create team' : 'Save changes' }}
          </button>
        </ng-container>
      </fu-modal>
    }
  `,
  styles: `
    .search { width: 200px; }
    .stack-avatars { display: inline-flex; gap: 2px; }
    .ids { max-width: 220px; }
    .roles { display: flex; flex-wrap: wrap; gap: 4px 6px; align-items: center; margin-top: 6px; }
  `,
})
export class TeamsTabComponent {
  private readonly api = inject(AdminApi);
  private readonly lookups = inject(LookupsStore);
  private readonly toast = inject(ToastService);

  protected readonly maxAvatars = MAX_AVATARS;
  protected readonly data = loader<TeamDto[]>();
  protected readonly query = signal('');
  protected readonly editing = signal<TeamDto | 'new' | null>(null);
  protected readonly saving = signal(false);

  protected readonly rows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return (this.data.value() ?? [])
      .map((t) => ({ ...t, members: t.members ?? [] }))
      .filter(
        (t) =>
          !q ||
          (t.name ?? '').toLowerCase().includes(q) ||
          (t.department?.name ?? '').toLowerCase().includes(q) ||
          (t.leader?.name ?? '').toLowerCase().includes(q) ||
          (t.email ?? '').toLowerCase().includes(q),
      )
      .sort((a, b) => (a.name ?? '').localeCompare(b.name ?? ''));
  });

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    departmentId: [null as Guid | null],
    leaderId: [null as Guid | null],
    deputyLeaderId: [null as Guid | null],
    memberIds: [[] as Guid[]],
    email: ['', [Validators.email]],
    teamsTeamId: [''],
    teamsChannelId: [''],
    teamsChannelName: [''],
    isActive: [true],
  });
  private readonly formState = formView(this.form);

  /** The leader cannot also be the deputy. */
  protected readonly leaderExclusion = computed(() => {
    const leader = this.formState().value.leaderId;
    return leader ? [leader] : [];
  });

  protected readonly currentMembers = computed<TeamMemberDto[]>(() => {
    const e = this.editing();
    return e && e !== 'new' ? (e.members ?? []) : [];
  });

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.teams());
  }

  protected value(event: Event): string {
    return valueOf(event);
  }

  protected avatarsOf(t: TeamDto): TeamMemberDto[] {
    return t.members.slice(0, MAX_AVATARS);
  }

  protected openNew(): void {
    this.form.reset({
      name: '',
      departmentId: null,
      leaderId: null,
      deputyLeaderId: null,
      memberIds: [],
      email: '',
      teamsTeamId: '',
      teamsChannelId: '',
      teamsChannelName: '',
      isActive: true,
    });
    this.editing.set('new');
  }

  protected openEdit(t: TeamDto): void {
    this.form.reset({
      name: t.name ?? '',
      departmentId: t.department?.id ?? null,
      leaderId: t.leader?.id ?? null,
      deputyLeaderId: t.deputyLeader?.id ?? null,
      memberIds: (t.members ?? []).map((m) => m.employeeId),
      email: t.email ?? '',
      teamsTeamId: t.teamsTeamId ?? '',
      teamsChannelId: t.teamsChannelId ?? '',
      teamsChannelName: t.teamsChannelName ?? '',
      isActive: t.isActive ?? true,
    });
    this.editing.set(t);
  }

  protected close(): void {
    this.editing.set(null);
  }

  protected save(): void {
    const e = this.editing();
    if (!e || this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const input: TeamInput = {
      name: v.name.trim(),
      departmentId: v.departmentId,
      leaderId: v.leaderId,
      deputyLeaderId: v.deputyLeaderId,
      email: textOrNull(v.email),
      teamsTeamId: textOrNull(v.teamsTeamId),
      teamsChannelId: textOrNull(v.teamsChannelId),
      teamsChannelName: textOrNull(v.teamsChannelName),
      isActive: v.isActive,
      memberIds: [...new Set(v.memberIds ?? [])],
    };
    this.saving.set(true);
    const request = e === 'new' ? this.api.createTeam(input) : this.api.updateTeam(e.id, input);
    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success(e === 'new' ? `Team “${input.name}” created` : `Team “${input.name}” saved`);
        this.close();
        this.reload();
        this.lookups.refresh().subscribe({ error: () => undefined });
      },
      error: () => this.saving.set(false),
    });
  }
}
