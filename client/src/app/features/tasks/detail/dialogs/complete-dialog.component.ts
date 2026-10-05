import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { EVIDENCE_KIND_VALUES, EvidenceKind } from '../../../../core/models/enums';
import { CompleteRequest, EvidenceInput } from '../../../../core/models/task.models';
import { todayDateOnly } from '../../../../core/util/dates';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { FileSizePipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { evidenceIcon } from '../task-detail.util';
import { TaskDialogBase } from './task-dialog-base';

type EvidenceRow = FormGroup<{
  /** Client-only key linking the row to its upload state. */
  rowId: FormControl<string>;
  kind: FormControl<EvidenceKind>;
  title: FormControl<string>;
  url: FormControl<string>;
  reference: FormControl<string>;
  attachmentId: FormControl<string | null>;
}>;

interface UploadState {
  status: 'uploading' | 'done' | 'failed';
  fileName: string;
  size?: number;
}

const URL_PATTERN = /^(https?:\/\/|mailto:)\S+$/i;

/**
 * Mark Complete → POST /api/tasks/{id}/complete with completion date, note and evidence. Evidence files upload
 * immediately (POST /api/attachments, isEvidence) so the submission only carries attachment ids.
 */
@Component({
  selector: 'fu-complete-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, IconComponent, HumanizePipe, FileSizePipe],
  templateUrl: './complete-dialog.component.html',
  styles: `
    .ev-row { border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px; display: grid; gap: 10px; grid-template-columns: 170px minmax(0, 1fr) auto; align-items: end; }
    .ev-row .wide { grid-column: 1 / 3; }
    .ev-row .rm { align-self: start; }
    .file-line { display: flex; align-items: center; gap: 8px; font-size: 12.5px; min-width: 0; }
    .file-pick { position: relative; overflow: hidden; }
    .file-pick input { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .file-pick:focus-within { box-shadow: var(--focus); }
    @media (max-width: 600px) { .ev-row { grid-template-columns: minmax(0, 1fr) auto; } .ev-row .wide { grid-column: 1 / -1; } }
  `,
})
export class CompleteDialogComponent extends TaskDialogBase {
  private readonly fb = inject(FormBuilder).nonNullable;
  private rowSeq = 0;

  protected readonly kinds = EVIDENCE_KIND_VALUES;
  protected readonly today = todayDateOnly();
  /** Upload state per evidence row (signals, so async upload results re-render the zoneless view). */
  protected readonly uploads = signal<Record<string, UploadState>>({});
  protected readonly uploading = computed(() => Object.values(this.uploads()).some((u) => u.status === 'uploading'));

  protected readonly form = this.fb.group({
    completionDate: [todayDateOnly(), Validators.required],
    note: ['', Validators.maxLength(4000)],
    evidence: this.fb.array<EvidenceRow>([]),
    forceWithOpenSubtasks: [false],
  });

  protected openSubtasks(): number {
    return this.detail().summary.openSubtaskCount ?? 0;
  }

  protected canSubmit(): boolean {
    const blockedBySubtasks = this.openSubtasks() > 0 && !this.form.controls.forceWithOpenSubtasks.value;
    return this.form.valid && !this.uploading() && !blockedBySubtasks && !this.busy();
  }

  protected addRow(kind: EvidenceKind = 'Document'): void {
    this.form.controls.evidence.push(
      this.fb.group({
        rowId: [`ev-${++this.rowSeq}`],
        kind: [kind],
        title: ['', [Validators.required, Validators.maxLength(200)]],
        url: ['', Validators.pattern(URL_PATTERN)],
        reference: ['', Validators.maxLength(200)],
        attachmentId: this.fb.control<string | null>(null),
      }),
    );
  }

  protected removeRow(index: number): void {
    const row = this.form.controls.evidence.at(index);
    const id = row.controls.rowId.value;
    this.uploads.update((all) => {
      const next = { ...all };
      delete next[id];
      return next;
    });
    this.form.controls.evidence.removeAt(index);
  }

  protected upload(row: EvidenceRow, input: HTMLInputElement): void {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    const id = row.controls.rowId.value;
    if (!row.controls.title.value.trim()) row.controls.title.setValue(file.name);
    this.setUpload(id, { status: 'uploading', fileName: file.name, size: file.size });
    this.api.uploadAttachment(file, { taskId: this.taskId(), isEvidence: true }).subscribe({
      next: (att) => {
        row.controls.attachmentId.setValue(att.id);
        this.setUpload(id, { status: 'done', fileName: att.fileName || file.name, size: att.sizeBytes ?? file.size });
      },
      error: () => {
        row.controls.attachmentId.setValue(null);
        this.setUpload(id, { status: 'failed', fileName: file.name });
      },
    });
  }

  protected clearFile(row: EvidenceRow): void {
    row.controls.attachmentId.setValue(null);
    const id = row.controls.rowId.value;
    this.uploads.update((all) => {
      const next = { ...all };
      delete next[id];
      return next;
    });
  }

  protected icon(kind: EvidenceKind): string {
    return evidenceIcon(kind);
  }

  protected submit(): void {
    if (!this.canSubmit()) return;
    const v = this.form.getRawValue();
    const evidence: EvidenceInput[] = v.evidence.map((e) => ({
      kind: e.kind,
      title: e.title.trim(),
      url: e.url.trim() || null,
      reference: e.reference.trim() || null,
      attachmentId: e.attachmentId,
    }));
    const req: CompleteRequest = {
      completionDate: v.completionDate,
      note: v.note.trim() || null,
      evidence,
      forceWithOpenSubtasks: this.openSubtasks() > 0 && v.forceWithOpenSubtasks,
    };
    this.run(this.api.complete(this.taskId(), req), this.detail().requiresApproval ? 'Completion submitted for approval' : 'Task marked complete');
  }

  private setUpload(id: string, state: UploadState): void {
    this.uploads.update((all) => ({ ...all, [id]: state }));
  }
}
