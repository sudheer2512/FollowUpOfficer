import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TaskDetail } from '../../../../core/models/task.models';
import { PersonComponent } from '../../../../shared/domain/indicators';
import { HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';

interface TextBlock {
  label: string;
  text: string;
}

/** What the task is: description, objective, expected result, deliverable, success criteria, links and classification. */
@Component({
  selector: 'fu-details-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, PersonComponent, HumanizePipe],
  template: `
    @let d = detail();
    <section class="card">
      <header class="card-header"><h2>Details</h2></header>
      <div class="card-body stack-lg">
        @for (b of blocks(); track b.label) {
          <div>
            <h3 class="section-title">{{ b.label }}</h3>
            <div class="pre text">{{ b.text }}</div>
          </div>
        } @empty {
          <div class="muted small">No description, objective or success criteria were given.</div>
        }

        @if (d.links.length) {
          <div>
            <h3 class="section-title">Links</h3>
            <ul class="links">
              @for (l of d.links; track l.url) {
                <li>
                  <fu-icon name="link" [size]="14" class="muted" />
                  <a [href]="l.url" target="_blank" rel="noopener noreferrer">{{ l.title || l.url }}</a>
                  <fu-icon name="external" [size]="11" class="muted" />
                </li>
              }
            </ul>
          </div>
        }

        <dl class="kv small">
          @if (d.project) {
            <dt>Project</dt>
            <dd><a [routerLink]="['/projects', d.project.id]">{{ d.project.name }}</a></dd>
          }
          @if (d.category) {
            <dt>Category</dt>
            <dd>{{ d.category.name }}</dd>
          }
          @if (d.department) {
            <dt>Department</dt>
            <dd>{{ d.department.name }}</dd>
          }
          <dt>Approval</dt>
          <dd>{{ d.requiresApproval ? 'Completion must be approved' : 'Not required' }}</dd>
          <dt>Created</dt>
          <dd class="row gap-4">
            {{ d.source | humanize }}
            @if (d.createdBy) {
              <span class="muted">by</span> <fu-person [person]="d.createdBy" />
            }
          </dd>
        </dl>
      </div>
    </section>
  `,
  styles: `
    .text { color: var(--text-2); overflow-wrap: anywhere; }
    .section-title { margin-bottom: 4px; }
    .links { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
    .links li { display: flex; align-items: center; gap: 6px; min-width: 0; overflow-wrap: anywhere; }
  `,
})
export class DetailsCardComponent {
  readonly detail = input.required<TaskDetail>();

  protected readonly blocks = computed<TextBlock[]>(() => {
    const d = this.detail();
    const all: [string, string | null | undefined][] = [
      ['Description', d.description],
      ['Objective', d.objective],
      ['Expected result', d.expectedResult],
      ['Deliverable', d.deliverable],
      ['Success criteria', d.successCriteria],
    ];
    return all.filter(([, text]) => !!text?.trim()).map(([label, text]) => ({ label, text: text!.trim() }));
  });
}
