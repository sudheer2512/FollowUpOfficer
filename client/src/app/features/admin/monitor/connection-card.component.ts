import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { IntegrationProbe } from '../../../core/models/admin.models';
import { AgoPipe, DateTimePipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';

/** Microsoft 365 connection probe: mode, authentication, account / tenant and permission gaps. */
@Component({
  selector: 'fu-connection-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AgoPipe, DateTimePipe, IconComponent],
  template: `
    <section class="card" aria-labelledby="mon-connection-title">
      <header class="card-header">
        <fu-icon name="plug" class="muted" />
        <h2 id="mon-connection-title">Microsoft 365 connection</h2>
        <span [class]="'badge tone-' + modeTone()">{{ mode() || 'Unknown' }}</span>
        @if (isEmulator()) {
          <a routerLink="/simulator" class="small">Open the simulator <fu-icon name="arrow-right" [size]="12" style="display:inline-flex;vertical-align:-1px" /></a>
        }
        <span class="spacer"></span>
        @if (probe(); as p) {
          <span class="xsmall muted" [attr.title]="p.checkedAt | dateTime">Checked {{ p.checkedAt | ago }}</span>
        }
      </header>
      <div class="card-body">
        @if (probe(); as p) {
          <div class="facts">
            <div class="fact">
              <span class="k">Authentication</span>
              <span class="v">
                @if (p.authenticated) {
                  <span class="badge tone-good"><fu-icon name="check-circle" [size]="13" /> Authenticated</span>
                } @else {
                  <span class="badge tone-danger"><fu-icon name="x-circle" [size]="13" /> Not authenticated</span>
                }
              </span>
            </div>
            <div class="fact">
              <span class="k">Account</span>
              <span class="v truncate" [attr.title]="p.account ?? ''">{{ p.account || '—' }}</span>
            </div>
            <div class="fact">
              <span class="k">Tenant</span>
              <span class="v mono truncate" [attr.title]="p.tenantId ?? ''">{{ p.tenantId || '—' }}</span>
            </div>
            <div class="fact">
              <span class="k">Permissions</span>
              <span class="v">
                {{ granted().length }} granted
                @if (missing().length) {
                  · <span class="text-danger strong">{{ missing().length }} missing</span>
                }
              </span>
            </div>
          </div>

          @if (p.error) {
            <div class="alert tone-danger mt-12" role="alert"><fu-icon name="alert-circle" /> <span class="pre">{{ p.error }}</span></div>
          }

          @if (missing().length) {
            <div class="mt-12">
              <div class="section-title">Missing permissions</div>
              <div class="chips">
                @for (m of missing(); track m) {
                  <span class="chip tone-danger mono">{{ m }}</span>
                }
              </div>
              <p class="xsmall muted mt-4">Grant these application permissions in Entra ID and give admin consent; syncs that need them pause until then.</p>
            </div>
          }

          @if (granted().length) {
            <details class="mt-12">
              <summary class="small">Granted permissions ({{ granted().length }})</summary>
              <div class="chips mt-8">
                @for (g of granted(); track g) {
                  <span class="chip sm outline mono">{{ g }}</span>
                }
              </div>
            </details>
          }
        } @else {
          <p class="muted small">No connection probe has run yet. It runs with the first sync after start-up.</p>
        }
      </div>
    </section>
  `,
  styles: `
    .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px 20px; }
    .fact { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
    .k { font-size: 11.5px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.05em; font-weight: 600; }
    .v { font-size: 13.5px; min-width: 0; }
    .chips { display: flex; flex-wrap: wrap; gap: 4px; }
    summary { cursor: pointer; color: var(--text-2); }
  `,
})
export class ConnectionCardComponent {
  readonly probe = input<IntegrationProbe | null | undefined>(null);
  /** MonitorOverview.mode: "Emulator", "Graph", "Disabled"… */
  readonly mode = input<string | null | undefined>('');

  protected readonly isEmulator = computed(() => (this.mode() ?? '').toLowerCase() === 'emulator');
  protected readonly modeTone = computed(() => {
    const m = (this.mode() ?? '').toLowerCase();
    if (m === 'emulator') return 'accent';
    if (m === 'graph') return this.probe()?.authenticated === false ? 'danger' : 'good';
    return 'neutral';
  });
  protected readonly granted = computed(() => this.probe()?.grantedPermissions ?? []);
  protected readonly missing = computed(() => this.probe()?.missingPermissions ?? []);
}
