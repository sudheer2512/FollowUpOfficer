import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminApi } from '../../../core/api/admin.api';
import { AccountInput, EmployeeDto } from '../../../core/models/admin.models';
import { APP_ROLE_VALUES } from '../../../core/models/enums';
import { parseFlags, toFlags } from '../../../core/util/flags';
import { AgoPipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ModalComponent } from '../../../shared/ui/modal.component';
import { checkedOf, formView, inOrder, toggleInList } from '../shared/form-view';

const MIN_PASSWORD = 8;

/** What each role unlocks — shown next to the checkboxes. */
const ROLE_HINTS: Record<string, string> = {
  TeamMember: 'Receives and updates delegated tasks',
  TeamLeader: 'Team dashboard, approves the team’s completions',
  SeniorManager: 'Cross-team visibility and approvals',
  Executive: 'Workbench, attention list, reports and the monitor',
  Administrator: 'Organisation, calendars, policies and settings',
};

/** Sign-in account for an employee: username, roles, enable login and an optional password reset. */
@Component({
  selector: 'fu-employee-account-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, AgoPipe, HumanizePipe, IconComponent, ModalComponent],
  template: `
    <fu-modal [title]="employee().hasLogin ? 'Account' : 'Create login'" [subtitle]="subtitle()" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form [formGroup]="form" class="stack-lg" (ngSubmit)="save()" id="account-form" novalidate>
        @if (employee().hasLogin) {
          <div class="alert">
            <fu-icon name="info" />
            <span>
              Last sign-in: <strong>{{ employee().lastLoginAt ? (employee().lastLoginAt | ago) : 'never' }}</strong>
              @if (employee().externalUserId) {
                · linked to Microsoft account <span class="mono">{{ employee().externalUserId }}</span>
              }
            </span>
          </div>
        }
        <div class="field">
          <label for="acc-user" class="req">Username</label>
          <input id="acc-user" class="input" formControlName="userName" autocomplete="off" maxlength="80" />
          @if (form.controls.userName.touched && form.controls.userName.invalid) {
            <span class="error">A username is required.</span>
          }
        </div>

        <fieldset class="fieldset">
          <legend>Roles</legend>
          <div class="stack">
            @for (r of roleValues; track r) {
              <label class="check role">
                <input type="checkbox" [checked]="state().value.roles.includes(r)" (change)="toggleRole(r, $event)" />
                <span>
                  <span class="strong">{{ r | humanize }}</span>
                  <span class="xsmall muted"> — {{ roleHints[r] }}</span>
                </span>
              </label>
            }
          </div>
          @if (!state().value.roles.length) {
            <div class="error-text mt-8">Choose at least one role.</div>
          }
        </fieldset>

        <div class="field">
          <label class="switch"><input type="checkbox" formControlName="enabled" /> Enable login</label>
          <span class="hint">Disabled accounts keep their history but cannot sign in.</span>
        </div>

        <div class="field">
          <label for="acc-pass" [class.req]="!employee().hasLogin">{{ employee().hasLogin ? 'Reset password' : 'Initial password' }}</label>
          <div class="input-group">
            <input
              id="acc-pass"
              class="input"
              [type]="showPassword() ? 'text' : 'password'"
              formControlName="newPassword"
              autocomplete="new-password"
              [placeholder]="employee().hasLogin ? 'Leave blank to keep the current password' : 'At least ' + minPassword + ' characters'"
            />
            <button
              type="button"
              class="btn btn-icon"
              [attr.aria-label]="showPassword() ? 'Hide password' : 'Show password'"
              [attr.aria-pressed]="showPassword()"
              (click)="showPassword.set(!showPassword())"
            >
              <fu-icon [name]="showPassword() ? 'eye-off' : 'eye'" />
            </button>
          </div>
          @if (form.controls.newPassword.touched && passwordError()) {
            <span class="error">{{ passwordError() }}</span>
          } @else {
            <span class="hint">Minimum {{ minPassword }} characters. The employee should change it after signing in.</span>
          }
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="account-form" class="btn btn-primary" [disabled]="saving()">
          {{ saving() ? 'Saving…' : employee().hasLogin ? 'Save account' : 'Create login' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
  styles: `.role { align-items: flex-start; } .role input { margin-top: 2px; }`,
})
export class EmployeeAccountModalComponent implements OnInit {
  private readonly api = inject(AdminApi);

  readonly employee = input.required<EmployeeDto>();
  readonly saved = output<EmployeeDto>();
  readonly closed = output<void>();

  protected readonly roleValues = APP_ROLE_VALUES;
  protected readonly roleHints = ROLE_HINTS;
  protected readonly minPassword = MIN_PASSWORD;
  protected readonly subtitle = computed(() => [this.employee().displayName, this.employee().email].filter(Boolean).join(' · '));
  protected readonly saving = signal(false);
  protected readonly showPassword = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    userName: ['', [Validators.required, Validators.maxLength(80)]],
    roles: [['TeamMember'] as string[]],
    enabled: [true],
    newPassword: ['', [Validators.minLength(MIN_PASSWORD)]],
  });
  protected readonly state = formView(this.form);

  ngOnInit(): void {
    const e = this.employee();
    const roles = parseFlags(e.roles);
    this.form.reset({
      userName: e.userName ?? (e.email ?? '').split('@')[0] ?? '',
      roles: roles.length ? roles : ['TeamMember'],
      enabled: e.hasLogin ? e.loginEnabled : true,
      newPassword: '',
    });
  }

  /** A new login needs an initial password; a reset is optional but must meet the minimum length. */
  protected passwordError(): string | null {
    const pwd = this.state().value.newPassword;
    if (!pwd && !this.employee().hasLogin) return 'Set an initial password for the new login.';
    if (pwd && pwd.length < MIN_PASSWORD) return `Use at least ${MIN_PASSWORD} characters.`;
    return null;
  }

  protected toggleRole(role: string, event: Event): void {
    toggleInList(this.form.controls.roles, role, checkedOf(event));
  }

  protected save(): void {
    if (this.saving()) return;
    const v = this.form.getRawValue();
    if (this.form.invalid || this.passwordError() || !v.roles.length) {
      this.form.markAllAsTouched();
      return;
    }
    const input: AccountInput = {
      userName: v.userName.trim(),
      roles: toFlags(inOrder(APP_ROLE_VALUES, v.roles)),
      enabled: v.enabled,
      // Passwords are sent verbatim (never trimmed); blank means "keep the current password".
      newPassword: v.newPassword ? v.newPassword : null,
    };
    this.saving.set(true);
    this.api.updateAccount(this.employee().id, input).subscribe({
      next: (dto) => {
        this.saving.set(false);
        this.saved.emit(dto);
      },
      error: () => this.saving.set(false),
    });
  }
}
