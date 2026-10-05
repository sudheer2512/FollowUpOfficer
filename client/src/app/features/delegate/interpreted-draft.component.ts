import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { ResolvedDraft } from '../../core/models/view.models';
import { formatDateOnly, toTimeInput } from '../../core/util/dates';
import { humanize } from '../../core/util/humanize';
import { ConfidencePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';

interface DraftField {
  key: string;
  label: string;
  value: string;
  icon: string;
}

/**
 * What the AI understood from a natural-language / voice command: one chip per extracted field, the provider and
 * confidence, parser notes, and warnings for names it could not match to a person.
 */
@Component({
  selector: 'fu-interpreted-draft',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, HumanizePipe, ConfidencePipe],
  template: `
    <section class="interp" aria-label="Interpreted command">
      <header class="head">
        <fu-icon name="sparkles" [size]="15" />
        <h3>Interpreted</h3>
        <span class="chip sm outline">{{ provider() }}</span>
        <span class="xsmall" [class]="'xsmall conf text-' + confidenceTone()">{{ resolved().draft.confidence | confidence }} confidence</span>
        <span class="spacer"></span>
        <button type="button" class="btn btn-ghost btn-icon btn-xs" aria-label="Hide the interpretation" (click)="dismissed.emit()">
          <fu-icon name="x" [size]="14" />
        </button>
      </header>

      <dl class="fields">
        @for (f of fields(); track f.key) {
          <div class="field-chip">
            <dt><fu-icon [name]="f.icon" [size]="12" /> {{ f.label }}</dt>
            <dd>{{ f.value }}</dd>
          </div>
        }
      </dl>

      @if (unresolved().length) {
        <div class="unresolved">
          @for (name of unresolved(); track name) {
            <button type="button" class="chip tone-warning" (click)="pickPerson.emit(name)">
              <fu-icon name="alert-triangle" [size]="12" /> Couldn't match ‘{{ name }}’ — pick a person
            </button>
          }
        </div>
      }

      @if (notes().length) {
        <ul class="notes">
          @for (n of notes(); track $index) {
            <li>
              <fu-icon name="info" [size]="12" />
              <span>
                @if (n.field) {
                  <strong>{{ n.field | humanize }}:</strong>
                }
                {{ n.message }}
              </span>
            </li>
          }
        </ul>
      }

      <p class="foot xsmall muted">
        {{ resolved().draft.isRecurring ? 'Recognised as a recurring task — the recurring form below has been filled in.' : 'The form below has been filled in — adjust anything before delegating.' }}
      </p>
    </section>
  `,
  styles: `
    :host { display: block; }
    .interp { border: 1px solid color-mix(in srgb, var(--accent) 30%, var(--border)); background: color-mix(in srgb, var(--accent-soft) 45%, var(--surface)); border-radius: var(--radius); padding: 10px 12px 8px; }
    .head { display: flex; align-items: center; gap: 8px; color: var(--accent-text); }
    .head h3 { font-size: 13.5px; font-weight: 600; margin: 0; }
    .conf { font-weight: 500; }
    .text-neutral { color: var(--muted); }
    .fields { display: flex; flex-wrap: wrap; gap: 6px; margin: 8px 0 0; }
    .field-chip { display: inline-flex; align-items: baseline; gap: 6px; max-width: 100%; padding: 3px 8px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); font-size: 12.5px; min-width: 0; }
    .field-chip dt { color: var(--muted); display: inline-flex; align-items: center; gap: 4px; white-space: nowrap; }
    .field-chip dd { margin: 0; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .unresolved { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .unresolved .chip { cursor: pointer; border: 0; }
    .notes { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 3px; font-size: 12.5px; color: var(--text-2); }
    .notes li { display: flex; gap: 6px; align-items: flex-start; }
    .notes fu-icon { margin-top: 3px; color: var(--muted); }
    .foot { margin: 8px 0 0; }
  `,
})
export class InterpretedDraftComponent {
  readonly resolved = input.required<ResolvedDraft>();
  /** An unresolved name was clicked: focus the person picker. */
  readonly pickPerson = output<string>();
  readonly dismissed = output<void>();

  protected readonly provider = computed(() => {
    const p = (this.resolved().provider ?? '').trim();
    if (!p) return 'AI';
    return /^ai\b/i.test(p) ? p : `AI: ${p}`;
  });

  protected readonly confidenceTone = computed(() => {
    const c = this.resolved().draft.confidence ?? 0;
    const v = c <= 1 ? c : c / 100;
    return v >= 0.75 ? 'good' : v >= 0.5 ? 'warning' : 'danger';
  });

  protected readonly unresolved = computed(() => this.resolved().draft.unresolvedNames ?? []);
  protected readonly notes = computed(() => this.resolved().draft.notes ?? []);

  protected readonly fields = computed<DraftField[]>(() => {
    const r = this.resolved();
    const d = r.draft;
    const out: DraftField[] = [];
    const add = (key: string, label: string, value: string | null | undefined, icon: string) => {
      if (value && value.trim()) out.push({ key, label, value: value.trim(), icon });
    };
    const primaryId = r.primaryAssignee?.id ?? d.primaryAssigneeId;
    add('title', 'Task', d.title, 'tasks');
    add('primary', 'Responsible', r.primaryAssignee?.name, 'user');
    add(
      'assignees',
      'Also',
      (r.assignees ?? [])
        .filter((p) => p.id !== primaryId)
        .map((p) => p.name)
        .join(', '),
      'users',
    );
    add('teams', (r.teams ?? []).length > 1 ? 'Teams' : 'Team', (r.teams ?? []).map((t) => t.name).join(', '), 'users');
    add('department', 'Department', r.department?.name, 'briefcase');
    add('project', 'Project', r.project?.name, 'folder');
    add('start', 'Starts', formatDateOnly(d.startDate, { withWeekday: true }), 'calendar');
    const dueTime = toTimeInput(d.dueTime);
    add('due', 'Due', d.dueDate ? `${formatDateOnly(d.dueDate, { withWeekday: true })}${dueTime ? ' ' + dueTime : ''}` : '', 'clock');
    add('followUp', 'Follow-up', r.followUpText, 'repeat');
    add('recurrence', 'Repeats', r.recurrenceText, 'rotate');
    add('dueRule', 'Due rule', r.dueRuleText, 'calendar-clock');
    add('priority', 'Priority', humanize(d.priority), 'flag');
    add('criticality', 'Criticality', humanize(d.criticality), 'alert-circle');
    if (d.notifyCeoOnDelay) add('notify', 'Tell me if delayed', 'Yes', 'bell');
    if (d.requiresApproval) add('approval', 'Approval', 'Required', 'check-square');
    return out;
  });
}
