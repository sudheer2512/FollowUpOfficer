import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { TasksApi } from '../../../core/api/tasks.api';
import { EvidenceDto } from '../../../core/models/task.models';
import { HumanizePipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';
import { evidenceIcon } from './task-detail.util';

/** Completion evidence: kind icon, title, link / attachment download / reference. */
@Component({
  selector: 'fu-evidence-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, HumanizePipe],
  template: `
    @if (!evidence().length) {
      <div class="muted small">No evidence attached.</div>
    } @else {
      <ul class="ev">
        @for (e of evidence(); track e.id) {
          <li>
            <span class="ic"><fu-icon [name]="icon(e.kind)" [size]="14" /></span>
            <span class="main">
              @if (e.url) {
                <a [href]="e.url" target="_blank" rel="noopener noreferrer">{{ e.title || e.url }} <fu-icon name="external" [size]="11" style="display:inline-flex" /></a>
              } @else if (e.attachmentId) {
                <a [href]="api.attachmentUrl(e.attachmentId)" target="_blank" rel="noopener">{{ e.title || 'Attachment' }} <fu-icon name="download" [size]="11" style="display:inline-flex" /></a>
              } @else {
                <span>{{ e.title }}</span>
              }
              @if (e.reference) {
                <span class="muted small">· {{ e.reference }}</span>
              }
            </span>
            <span class="chip sm">{{ e.kind | humanize }}</span>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    :host { display: block; }
    .ev { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
    .ev li { display: flex; align-items: center; gap: 8px; min-width: 0; font-size: 13px; }
    .ic { width: 24px; height: 24px; border-radius: 6px; background: var(--bg-sunken); color: var(--muted); display: inline-flex; align-items: center; justify-content: center; flex: none; }
    .main { flex: 1; min-width: 0; overflow-wrap: anywhere; }
  `,
})
export class EvidenceListComponent {
  protected readonly api = inject(TasksApi);
  readonly evidence = input<EvidenceDto[]>([]);

  protected icon(kind: string): string {
    return evidenceIcon(kind);
  }
}
