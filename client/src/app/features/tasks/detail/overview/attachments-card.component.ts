import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { TasksApi } from '../../../../core/api/tasks.api';
import { TaskDetail } from '../../../../core/models/task.models';
import { AgoPipe, FileSizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ToastService } from '../../../../shared/ui/toast.service';

/** Files on the task (download links) with an "Attach file" upload for people who work on it. */
@Component({
  selector: 'fu-attachments-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, AgoPipe, FileSizePipe],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Attachments</h2>
        <span class="count-pill">{{ detail().attachments.length }}</span>
        <span class="spacer"></span>
        @if (canAttach()) {
          <span class="btn btn-xs btn-ghost file-pick" [attr.aria-disabled]="uploading()">
            <fu-icon [name]="uploading() ? 'upload' : 'paperclip'" [size]="13" /> {{ uploading() ? 'Uploading…' : 'Attach file' }}
            <input type="file" aria-label="Attach a file to this task" [disabled]="uploading()" (change)="upload($any($event.target))" />
          </span>
        }
      </header>
      <div class="card-body flush list">
        @for (a of detail().attachments; track a.id) {
          <a class="list-row" [href]="api.attachmentUrl(a.id)" target="_blank" rel="noopener">
            <fu-icon name="file" [size]="16" class="muted" />
            <div class="main">
              <div class="title truncate">{{ a.fileName }}</div>
              <div class="meta">
                <span>{{ a.sizeBytes | fileSize }}</span>
                @if (a.uploadedBy) {
                  <span>{{ a.uploadedBy.name }}</span>
                }
                <span>{{ a.uploadedAt | ago }}</span>
                @if (a.isEvidence) {
                  <span class="chip sm tone-good">Evidence</span>
                }
              </div>
            </div>
            <fu-icon name="download" [size]="15" class="muted" />
          </a>
        } @empty {
          <div class="muted small pad">No files yet.</div>
        }
      </div>
    </section>
  `,
  styles: `
    .pad { padding: 14px 16px; }
    .file-pick { position: relative; overflow: hidden; }
    .file-pick input { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .file-pick:focus-within { box-shadow: var(--focus); }
  `,
})
export class AttachmentsCardComponent {
  protected readonly api = inject(TasksApi);
  private readonly toast = inject(ToastService);

  readonly detail = input.required<TaskDetail>();
  /** An attachment was added; the page refreshes the task. */
  readonly reload = output<void>();

  protected readonly uploading = signal(false);
  protected readonly canAttach = computed(() => {
    const p = this.detail().permissions;
    return p.canManage || p.canUpdate || p.canComment;
  });

  protected upload(input: HTMLInputElement): void {
    const file = input.files?.[0];
    input.value = '';
    if (!file || this.uploading()) return;
    this.uploading.set(true);
    this.api.uploadAttachment(file, { taskId: this.detail().summary.id, isEvidence: false }).subscribe({
      next: (att) => {
        this.uploading.set(false);
        this.toast.success(`${att.fileName || file.name} attached`);
        this.reload.emit();
      },
      error: () => this.uploading.set(false),
    });
  }
}
