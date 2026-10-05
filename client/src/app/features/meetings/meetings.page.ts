import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { MeetingDto } from '../../core/models/admin.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { loader } from '../../core/util/loader';
import { AvatarComponent, PersonComponent } from '../../shared/domain/indicators';
import { DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { actionItemCounts } from './meeting-util';

const MAX_AVATARS = 4;

/** /meetings — meetings with their decisions and action items; newest first. */
@Component({
  selector: 'fu-meetings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PersonComponent, AvatarComponent, EmptyStateComponent, SkeletonComponent, IconComponent, DateTimePipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="titles">
          <h1>Meetings</h1>
          <div class="subtitle">Decisions and action items — turned into tracked, followed-up tasks.</div>
        </div>
        <div class="actions">
          <div class="input-with-icon search">
            <fu-icon name="search" [size]="15" />
            <input class="input" type="search" placeholder="Search meetings…" aria-label="Search meetings" [value]="query()" (input)="query.set($any($event.target).value)" />
          </div>
          <a class="btn btn-primary" routerLink="/meetings/new"><fu-icon name="plus" [size]="15" /> New meeting</a>
        </div>
      </div>

      <section class="card">
        @if (data.loading() && !data.value()) {
          <div class="card-body"><fu-skeleton variant="table" [rows]="6" /></div>
        } @else if (data.error() && !data.value()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" />
              <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!rows().length) {
          <fu-empty icon="users" [title]="query() ? 'No matching meetings' : 'No meetings yet'" [message]="query() ? 'Try another search.' : 'Record a meeting to capture decisions and create follow-up tasks from its action items.'">
            @if (!query()) {
              <a class="btn btn-primary btn-sm mt-8" routerLink="/meetings/new"><fu-icon name="plus" [size]="14" /> New meeting</a>
            }
          </fu-empty>
        } @else {
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th>Meeting</th>
                  <th>Held</th>
                  <th>Organizer</th>
                  <th>Participants</th>
                  <th>Project</th>
                  <th>Action items</th>
                </tr>
              </thead>
              <tbody>
                @for (r of rows(); track r.meeting.id) {
                  <tr class="clickable" tabindex="0" [attr.aria-label]="r.meeting.title" (click)="open(r.meeting, $event)" (keydown.enter)="open(r.meeting, $event)">
                    <td class="title-cell">
                      <a class="strong" [routerLink]="['/meetings', r.meeting.id]" tabindex="-1">{{ r.meeting.title }}</a>
                      @if (r.meeting.location) {
                        <div class="xsmall muted truncate">{{ r.meeting.location }}</div>
                      }
                    </td>
                    <td class="nowrap small">{{ r.meeting.heldAt | dateTime: 'short' }}</td>
                    <td><fu-person [person]="r.meeting.organizer" emptyText="—" /></td>
                    <td>
                      <span class="avatars" [attr.title]="r.participantNames">
                        @for (p of r.shown; track p.id) {
                          <fu-avatar [name]="p.name" size="sm" />
                        }
                        @if (r.extra > 0) {
                          <span class="more xsmall">+{{ r.extra }}</span>
                        }
                        @if (!r.shown.length) {
                          <span class="muted">—</span>
                        }
                      </span>
                    </td>
                    <td class="small">{{ r.meeting.project?.name ?? '—' }}</td>
                    <td class="nowrap small">
                      <span class="strong num">{{ r.counts.total }}</span>
                      <span class="muted"> · </span><span class="num" [class.text-warning]="r.counts.open > 0">{{ r.counts.open }} open</span>
                      <span class="muted"> · </span><span class="num" [class.text-good]="r.counts.withTask > 0">{{ r.counts.withTask }} tasks</span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .search { width: 240px; }
    .title-cell { min-width: 220px; max-width: 380px; }
    .title-cell a { color: var(--text); }
    .avatars { display: inline-flex; align-items: center; }
    .avatars > fu-avatar { border-radius: 50%; box-shadow: 0 0 0 2px var(--surface); }
    .avatars > fu-avatar + fu-avatar { margin-left: -6px; }
    .more { margin-left: 6px; color: var(--muted); font-weight: 600; }
    @media (max-width: 700px) { .search { width: 100%; } }
  `,
})
export class MeetingsPage {
  private readonly api = inject(PlanningApi);
  private readonly router = inject(Router);

  protected readonly data = loader<MeetingDto[]>();
  protected readonly query = signal('');

  protected readonly rows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return (this.data.value() ?? [])
      .filter(
        (m) =>
          !q ||
          [m.title, m.location, m.notes, m.organizer?.name, m.project?.name, ...(m.participants ?? []).map((p) => p.name)].some((s) =>
            (s ?? '').toLowerCase().includes(q),
          ),
      )
      .slice()
      .sort((a, b) => (b.heldAt ?? '').localeCompare(a.heldAt ?? ''))
      .map((meeting) => {
        const participants = meeting.participants ?? [];
        return {
          meeting,
          shown: participants.slice(0, MAX_AVATARS),
          extra: Math.max(0, participants.length - MAX_AVATARS),
          participantNames: participants.map((p) => p.name).join(', '),
          counts: actionItemCounts(meeting.actionItems),
        };
      });
  });

  constructor() {
    this.reload();
    refreshOn(['task.changed', 'task.created'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.data.load(this.api.meetings(), { silent });
  }

  protected open(m: MeetingDto, e: Event): void {
    if (e.type === 'click' && (e.target as HTMLElement).closest('a')) return; // the link handles it
    void this.router.navigate(['/meetings', m.id]);
  }
}
