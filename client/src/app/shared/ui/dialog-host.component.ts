import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DialogService } from './dialog.service';
import { ModalComponent } from './modal.component';

/** Renders DialogService confirm / prompt requests. Mounted once in the root component. */
@Component({
  selector: 'fu-dialog-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModalComponent, FormsModule],
  template: `
    @if (dialog.current(); as req) {
      <fu-modal [title]="req.options.title ?? (req.kind === 'confirm' ? 'Please confirm' : '')" size="sm" (closed)="dialog.settle(null)">
        <form id="fu-dialog-form" (ngSubmit)="submit()" class="stack">
          @if (req.options.message) {
            <p class="pre" style="margin:0">{{ req.options.message }}</p>
          }
          @if (req.kind === 'prompt') {
            <div class="field">
              @if (req.options.label) {
                <label for="fu-dialog-input" [class.req]="req.options.required">{{ req.options.label }}</label>
              }
              @if (req.options.multiline) {
                <textarea id="fu-dialog-input" class="textarea" name="value" rows="4" [(ngModel)]="value" [placeholder]="req.options.placeholder ?? ''" autofocus></textarea>
              } @else {
                <input id="fu-dialog-input" class="input" name="value" [type]="req.options.inputType ?? 'text'" [(ngModel)]="value" [placeholder]="req.options.placeholder ?? ''" autofocus />
              }
            </div>
          }
        </form>
        <ng-container modal-footer>
          <button type="button" class="btn" (click)="dialog.settle(null)">{{ req.options.cancelText ?? 'Cancel' }}</button>
          <button
            type="submit"
            form="fu-dialog-form"
            class="btn"
            [class.btn-primary]="!req.options.danger"
            [class.btn-danger]="req.options.danger"
            [class.solid]="req.options.danger"
            [disabled]="req.kind === 'prompt' && req.options.required && !value().trim()"
          >
            {{ req.options.confirmText ?? (req.kind === 'confirm' ? 'Confirm' : 'OK') }}
          </button>
        </ng-container>
      </fu-modal>
    }
  `,
})
export class DialogHostComponent {
  protected readonly dialog = inject(DialogService);
  protected readonly value = signal('');

  constructor() {
    effect(() => {
      const req = this.dialog.current();
      this.value.set(req?.options.value ?? '');
    });
  }

  protected submit(): void {
    const req = this.dialog.current();
    if (!req) return;
    if (req.kind === 'confirm') this.dialog.settle(true);
    else if (!req.options.required || this.value().trim()) this.dialog.settle(this.value());
  }
}
