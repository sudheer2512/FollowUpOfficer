import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { problemMessage } from '../../core/http/problem';
import { DemoUser } from '../../core/models/admin.models';
import { ThemeService } from '../../core/state/theme.service';
import { AvatarComponent } from '../../shared/domain/indicators';
import { IconComponent } from '../../shared/ui/icon.component';

const DEMO_PASSWORD = 'FollowUp@2026';

/**
 * Sign-in: username / password (cookie session). In demo mode, persona cards fill the username.
 * "Sign in with Microsoft" is shown when Entra ID is enabled; interactive Entra sign-in is configured in
 * Entra ID (the API also accepts bearer tokens), so the button is informational here.
 */
@Component({
  selector: 'fu-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, IconComponent, AvatarComponent],
  template: `
    <div class="wrap">
      <button type="button" class="btn btn-ghost btn-icon theme" (click)="theme.toggle()" [attr.aria-label]="'Theme: ' + theme.preference()">
        <fu-icon [name]="theme.effective() === 'dark' ? 'moon' : 'sun'" />
      </button>
      <div class="panel">
        <section class="intro">
          <div class="logo" aria-hidden="true">
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5" /></svg>
          </div>
          <h1>FollowUp Officer</h1>
          <p class="tagline">The CEO should manage exceptions, not chase people for updates.</p>
          <ul class="points">
            <li><fu-icon name="target" [size]="16" /> One ranked list of what needs your attention</li>
            <li><fu-icon name="repeat" [size]="16" /> Automatic, intelligent follow-up on every delegated task</li>
            <li><fu-icon name="messages" [size]="16" /> Email and Teams replies linked to tasks with AI</li>
            <li><fu-icon name="flag" [size]="16" /> Promises tracked — and missed ones surfaced</li>
          </ul>
        </section>

        <section class="form-side">
          <h2>Sign in</h2>
          <p class="muted small">Use your FollowUp Officer account.</p>

          <form [formGroup]="form" (ngSubmit)="submit()" class="stack mt-16" novalidate>
            <div class="field">
              <label for="login-user">Username</label>
              <input id="login-user" class="input" formControlName="userName" autocomplete="username" autofocus />
            </div>
            <div class="field">
              <label for="login-pass">Password</label>
              <input id="login-pass" class="input" type="password" formControlName="password" autocomplete="current-password" />
            </div>
            @if (error()) {
              <div class="alert tone-danger" role="alert"><fu-icon name="alert-circle" /> {{ error() }}</div>
            }
            <button type="submit" class="btn btn-primary btn-lg btn-block" [disabled]="busy() || form.invalid">
              {{ busy() ? 'Signing in…' : 'Sign in' }}
            </button>
          </form>

          @if (config()?.entraEnabled) {
            <div class="or"><span>or</span></div>
            <button type="button" class="btn btn-block btn-lg" disabled title="Configured in Entra ID">
              <svg width="16" height="16" viewBox="0 0 21 21" aria-hidden="true"><rect x="1" y="1" width="9" height="9" fill="#f25022"/><rect x="11" y="1" width="9" height="9" fill="#7fba00"/><rect x="1" y="11" width="9" height="9" fill="#00a4ef"/><rect x="11" y="11" width="9" height="9" fill="#ffb900"/></svg>
              Sign in with Microsoft
            </button>
            <p class="xsmall muted center mt-4">Microsoft sign-in is configured in Entra ID.</p>
          }

          @if (config()?.demoMode && config()!.demoUsers.length) {
            <div class="demo">
              <div class="section-title">Demo personas</div>
              <p class="xsmall muted">Click a persona to fill the username. Password: <code>{{ demoPassword }}</code></p>
              <div class="personas">
                @for (u of config()!.demoUsers; track u.userName) {
                  <button type="button" class="persona" [class.on]="form.value.userName === u.userName" (click)="pick(u)">
                    <fu-avatar [name]="u.name" />
                    <span class="who">
                      <span class="nm truncate">{{ u.name }}</span>
                      <span class="tt truncate">{{ u.title }}</span>
                    </span>
                    <span class="chip sm">{{ u.role }}</span>
                  </button>
                }
              </div>
            </div>
          }
        </section>
      </div>
    </div>
  `,
  styles: `
    .wrap { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 24px 16px; background:
      radial-gradient(1200px 600px at 10% -10%, color-mix(in srgb, var(--accent) 12%, transparent), transparent 60%), var(--bg); position: relative; }
    .theme { position: absolute; top: 14px; right: 14px; }
    .panel { width: min(980px, 100%); display: grid; grid-template-columns: 1fr 1fr; background: var(--surface); border: 1px solid var(--border); border-radius: 16px; box-shadow: var(--shadow-lg); overflow: hidden; }
    .intro { padding: 40px; background: linear-gradient(160deg, var(--accent), color-mix(in srgb, var(--accent) 70%, #000)); color: #fff; display: flex; flex-direction: column; gap: 12px; }
    .intro h1 { color: #fff; font-size: 26px; margin-top: 8px; }
    .logo { width: 44px; height: 44px; border-radius: 12px; background: rgba(255,255,255,.16); display: flex; align-items: center; justify-content: center; }
    .tagline { font-size: 16px; opacity: .92; line-height: 1.5; }
    .points { list-style: none; margin: 16px 0 0; padding: 0; display: flex; flex-direction: column; gap: 12px; font-size: 14px; opacity: .95; }
    .points li { display: flex; gap: 10px; align-items: center; }
    .form-side { padding: 40px; min-width: 0; }
    .or { display: flex; align-items: center; gap: 10px; color: var(--subtle); font-size: 12px; margin: 16px 0; }
    .or::before, .or::after { content: ''; flex: 1; height: 1px; background: var(--border); }
    .center { text-align: center; }
    .demo { margin-top: 24px; padding-top: 16px; border-top: 1px solid var(--border); }
    .personas { display: grid; gap: 6px; margin-top: 8px; max-height: 300px; overflow-y: auto; }
    .persona { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); cursor: pointer; text-align: left; }
    .persona:hover { border-color: var(--border-strong); background: var(--surface-hover); }
    .persona.on { border-color: var(--accent); background: var(--accent-soft); }
    .who { display: flex; flex-direction: column; flex: 1; min-width: 0; line-height: 1.25; }
    .nm { font-weight: 500; }
    .tt { font-size: 12px; color: var(--muted); }
    @media (max-width: 820px) { .panel { grid-template-columns: 1fr; } .intro { padding: 28px; } .points { display: none; } .form-side { padding: 28px; } }
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly theme = inject(ThemeService);

  protected readonly demoPassword = DEMO_PASSWORD;
  protected readonly config = toSignal(this.auth.config());
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    userName: ['', Validators.required],
    password: ['', Validators.required],
  });

  protected pick(u: DemoUser): void {
    this.form.patchValue({ userName: u.userName });
    this.error.set(null);
    setTimeout(() => document.getElementById('login-pass')?.focus());
  }

  protected submit(): void {
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(this.form.getRawValue()).subscribe({
      next: (me) => {
        this.busy.set(false);
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        const safe = returnUrl && returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : null;
        void this.router.navigateByUrl(safe ?? (me.isExecutive ? '/' : '/me'));
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(problemMessage(e) === 'Your session has ended. Please sign in again.' ? 'Incorrect username or password.' : problemMessage(e));
      },
    });
  }
}
