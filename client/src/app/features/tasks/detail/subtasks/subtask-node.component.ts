import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SubtaskNode } from '../../../../core/models/task.models';
import { DeadlineTextComponent, HealthDotComponent, PersonComponent, StatusBadgeComponent } from '../../../../shared/domain/indicators';
import { IconComponent } from '../../../../shared/ui/icon.component';

/** One row of the subtask tree plus (recursively) its children. Indentation follows the nesting level. */
@Component({
  selector: 'fu-subtask-node',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, StatusBadgeComponent, HealthDotComponent, DeadlineTextComponent, PersonComponent],
  template: `
    @let n = node();
    <div class="node" role="treeitem" [attr.aria-level]="level() + 1" [attr.aria-expanded]="hasChildren() ? expanded() : null">
      <div class="row-line" [style.padding-left.px]="12 + level() * 22">
        @if (hasChildren()) {
          <button
            type="button"
            class="btn btn-ghost btn-icon btn-xs"
            [attr.aria-label]="(expanded() ? 'Collapse ' : 'Expand ') + n.number"
            (click)="expanded.set(!expanded())"
          >
            <fu-icon [name]="expanded() ? 'chevron-down' : 'chevron-right'" [size]="14" />
          </button>
        } @else {
          <span class="toggle-gap" aria-hidden="true"></span>
        }
        <fu-health [value]="n.health" [showLabel]="false" />
        <a class="ttl" [routerLink]="['/tasks', n.id]">
          <span class="task-number">{{ n.number }}</span>
          <span class="truncate">{{ n.title }}</span>
        </a>
        @if (n.isBlocked) {
          <span class="chip sm tone-danger">Blocked</span>
        }
        @if (hasChildren()) {
          <span class="xsmall muted nowrap">{{ n.children.length }} sub</span>
        }
        <span class="spacer"></span>
        <span class="cell status"><fu-status [status]="n.status" [text]="n.displayStatus" /></span>
        <span class="cell prog" [attr.title]="n.progressPercent + '% complete'">
          <span class="progress"><span [style.width.%]="n.progressPercent"></span></span>
          <span class="num xsmall muted">{{ n.progressPercent }}%</span>
        </span>
        <span class="cell who"><fu-person [person]="n.assignee" /></span>
        <span class="cell due"><fu-deadline [text]="n.deadlineText" /></span>
      </div>
      @if (hasChildren() && expanded()) {
        <div role="group">
          @for (c of n.children; track c.id) {
            <fu-subtask-node [node]="c" [level]="level() + 1" />
          }
        </div>
      }
    </div>
  `,
  styles: `
    :host { display: block; }
    .row-line { display: flex; align-items: center; gap: 8px; padding: 8px 16px 8px 12px; border-bottom: 1px solid var(--border); min-width: 0; }
    .row-line:hover { background: var(--surface-hover); }
    .toggle-gap { width: 24px; flex: none; }
    .ttl { display: inline-flex; align-items: center; gap: 8px; min-width: 0; color: var(--text); font-weight: 500; }
    .cell { flex: none; display: inline-flex; align-items: center; }
    .status { width: 150px; }
    .prog { width: 96px; gap: 6px; }
    .prog .progress { width: 56px; }
    .who { width: 160px; min-width: 0; overflow: hidden; }
    .due { width: 130px; justify-content: flex-end; }
    @media (max-width: 1000px) { .prog, .who { display: none; } }
    @media (max-width: 640px) { .status { width: auto; } .due { display: none; } }
  `,
})
export class SubtaskNodeComponent {
  readonly node = input.required<SubtaskNode>();
  readonly level = input(0);

  protected readonly hasChildren = computed(() => (this.node().children ?? []).length > 0);
  /** The first two levels start expanded; deeper ones start collapsed. */
  protected readonly expanded = linkedSignal(() => this.level() < 2);
}
