import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DelegateLauncher } from '../../core/state/delegate-launcher';
import { ModalComponent } from '../../shared/ui/modal.component';

/** STUB — replaced by the delegate feature implementation. */
@Component({
  selector: 'fu-delegate-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModalComponent],
  template: `<fu-modal title="Delegate" (closed)="launcher.close()"><p>Coming soon</p></fu-modal>`,
})
export class DelegateDialogComponent {
  protected readonly launcher = inject(DelegateLauncher);
}
