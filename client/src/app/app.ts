import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { DialogHostComponent } from './shared/ui/dialog-host.component';
import { ToastHostComponent } from './shared/ui/toast-host.component';

/** Root: the routed page (login or the signed-in shell) plus the global toast and dialog hosts. */
@Component({
  selector: 'fu-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, ToastHostComponent, DialogHostComponent],
  template: `<router-outlet />
    <fu-toast-host />
    <fu-dialog-host />`,
})
export class App {}
