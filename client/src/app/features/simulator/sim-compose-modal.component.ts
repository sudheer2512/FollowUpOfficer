import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { ModalComponent } from '../../shared/ui/modal.component';
import { eventValue, injectAnnounceSent, userLabel } from './sim-feedback';
import { SimRecipientsInputComponent } from './sim-recipients-input.component';
import { SimulatorApi } from './simulator.api';
import { SimUser, userAddress } from './simulator.models';

/** Initial values for the compose dialog (new message or reply). */
export interface MailDraft {
  from: string;
  to: string[];
  cc: string[];
  subject: string;
  body: string;
  /** Set when replying: the emulator threads the reply onto that message's conversation. */
  inReplyToMessageId: string | null;
}

/** Compose an email as any tenant user (POST /api/m365-sim/mail). */
@Component({
  selector: 'fu-sim-compose-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModalComponent, SimRecipientsInputComponent],
  template: `
    <fu-modal [title]="draft().inReplyToMessageId ? 'Reply' : 'New email'" subtitle="Sent inside the emulated tenant, as the chosen user" size="lg" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form class="form-grid" id="sim-compose-form" (submit)="$event.preventDefault(); send()" novalidate>
        <div class="field full">
          <label for="sim-mail-from" class="req">From</label>
          <select id="sim-mail-from" class="select" (change)="from.set(value($event))">
            @for (u of senders(); track u.id) {
              <option [value]="address(u)" [selected]="address(u) === from()">{{ label(u) }} — {{ address(u) }}</option>
            }
          </select>
        </div>
        <div class="field full">
          <label for="sim-mail-to" class="req">To</label>
          <fu-sim-recipients [(addresses)]="to" [users]="users()" inputId="sim-mail-to" ariaLabel="To" />
        </div>
        <div class="field full">
          <label for="sim-mail-cc">Cc</label>
          <fu-sim-recipients [(addresses)]="cc" [users]="users()" inputId="sim-mail-cc" ariaLabel="Cc" />
        </div>
        <div class="field full">
          <label for="sim-mail-subject">Subject</label>
          <input id="sim-mail-subject" class="input" [value]="subject()" (input)="subject.set(value($event))" />
        </div>
        <div class="field full">
          <label for="sim-mail-body">Message</label>
          <textarea id="sim-mail-body" class="textarea body" rows="10" [value]="body()" (input)="body.set(value($event))"></textarea>
          <span class="hint">Tip: mention a task number, or write “done”, “blocked” or a new date, to see how replies are linked to tasks and understood.</span>
        </div>
        @if (draft().inReplyToMessageId) {
          <div class="full xsmall muted">Threaded as a reply to message <span class="mono">{{ draft().inReplyToMessageId }}</span>.</div>
        }
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="sim-compose-form" class="btn btn-primary" [disabled]="!canSend()">
          {{ sending() ? 'Sending…' : 'Send' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
  styles: `.body { min-height: 200px; font-family: var(--font); }`,
})
export class SimComposeModalComponent implements OnInit {
  private readonly api = inject(SimulatorApi);
  private readonly announceSent = injectAnnounceSent();

  readonly draft = input.required<MailDraft>();
  readonly users = input<SimUser[]>([]);
  readonly sent = output<void>();
  readonly closed = output<void>();

  protected readonly from = signal('');
  protected readonly to = signal<string[]>([]);
  protected readonly cc = signal<string[]>([]);
  protected readonly subject = signal('');
  protected readonly body = signal('');
  protected readonly sending = signal(false);

  protected readonly senders = computed(() => this.users().filter((u) => !!userAddress(u)));
  protected readonly canSend = computed(
    () => !this.sending() && !!this.from() && this.to().length > 0 && (!!this.subject().trim() || !!this.body().trim()),
  );

  ngOnInit(): void {
    const d = this.draft();
    this.from.set(d.from || (this.senders()[0] ? userAddress(this.senders()[0]) : ''));
    this.to.set([...d.to]);
    this.cc.set([...d.cc]);
    this.subject.set(d.subject);
    this.body.set(d.body);
  }

  protected address(u: SimUser): string {
    return userAddress(u);
  }

  protected label(u: SimUser): string {
    return userLabel(u);
  }

  protected value(event: Event): string {
    return eventValue(event);
  }

  protected send(): void {
    if (!this.canSend()) return;
    this.sending.set(true);
    this.api
      .sendMail({
        from: this.from(),
        to: this.to(),
        cc: this.cc(),
        subject: this.subject().trim(),
        body: this.body(),
        inReplyToMessageId: this.draft().inReplyToMessageId,
      })
      .subscribe({
        next: () => {
          this.sending.set(false);
          this.announceSent();
          this.sent.emit();
        },
        error: () => this.sending.set(false),
      });
  }
}
