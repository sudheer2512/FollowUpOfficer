import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { formatDateTime } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { AgoPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { MailDraft, SimComposeModalComponent } from './sim-compose-modal.component';
import { eventValue, userLabel } from './sim-feedback';
import { SimulatorApi } from './simulator.api';
import { SimAddress, SimMailFolder, SimMailMessage, SimUser, userAddress } from './simulator.models';

function addressLabel(a: SimAddress | null | undefined): string {
  return a ? (a.name ?? a.address) : '';
}

/** "> " quoted original for replies. */
function quote(m: SimMailMessage): string {
  const sent = formatDateTime(m.sentDateTime ?? m.receivedDateTime);
  const header = `From: ${addressLabel(m.from)}${m.from?.name ? ` <${m.from.address}>` : ''}\nSent: ${sent}\nSubject: ${m.subject}`;
  const body = m.body
    .split('\n')
    .map((line) => `> ${line}`)
    .join('\n');
  return `\n\n-----\n${header}\n\n${body}`;
}

/** Mail tab: browse any mailbox's Inbox / Sent Items, read a message, compose or reply as any user. */
@Component({
  selector: 'fu-sim-mail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, EmptyStateComponent, IconComponent, SkeletonComponent, SimComposeModalComponent],
  templateUrl: './sim-mail.component.html',
  styleUrl: './sim-pane.css',
})
export class SimMailComponent {
  private readonly api = inject(SimulatorApi);

  readonly users = input<SimUser[]>([]);

  protected readonly mailboxes = computed(() => this.users().filter((u) => !!userAddress(u)));
  /** The mailbox being viewed; defaults to the first user and survives directory reloads. */
  protected readonly mailbox = linkedSignal<SimUser[], string>({
    source: this.mailboxes,
    computation: (list, previous) =>
      previous && list.some((u) => userAddress(u) === previous.value) ? previous.value : list[0] ? userAddress(list[0]) : '',
  });
  protected readonly folder = signal<SimMailFolder>('inbox');
  protected readonly messages = loader<SimMailMessage[]>();
  protected readonly rows = computed(() =>
    [...(this.messages.value() ?? [])].sort((a, b) =>
      (b.receivedDateTime ?? b.sentDateTime ?? '').localeCompare(a.receivedDateTime ?? a.sentDateTime ?? ''),
    ),
  );
  protected readonly selectedId = signal<string | null>(null);
  protected readonly selected = computed(() => this.rows().find((m) => m.id === this.selectedId()) ?? null);
  protected readonly draft = signal<MailDraft | null>(null);

  constructor() {
    effect(() => {
      const box = this.mailbox();
      const folder = this.folder();
      untracked(() => {
        this.selectedId.set(null);
        if (box) this.messages.load(this.api.messages(box, folder));
        else this.messages.set([]);
      });
    });
  }

  protected refresh(): void {
    const box = this.mailbox();
    if (box) this.messages.load(this.api.messages(box, this.folder()), { silent: true });
  }

  protected setMailbox(event: Event): void {
    this.mailbox.set(eventValue(event));
  }

  protected label(u: SimUser): string {
    return userLabel(u);
  }

  protected address(u: SimUser): string {
    return userAddress(u);
  }

  /** "Asha Rao <asha@contoso.com>" (or just the address). */
  protected fullAddress(a: SimAddress | null | undefined): string {
    if (!a) return '';
    return a.name ? `${a.name} <${a.address}>` : a.address;
  }

  protected recipientsText(list: SimAddress[]): string {
    return list.map((a) => this.fullAddress(a)).join(', ');
  }

  /** List line: the sender in the inbox, the recipients in Sent Items. */
  protected counterpart(m: SimMailMessage): string {
    if (this.folder() === 'sentitems') {
      const names = m.toRecipients.map((a) => addressLabel(a)).join(', ');
      return names ? `To: ${names}` : 'To: —';
    }
    return addressLabel(m.from) || 'Unknown sender';
  }

  protected compose(): void {
    this.draft.set({ from: this.mailbox(), to: [], cc: [], subject: '', body: '', inReplyToMessageId: null });
  }

  /** Reply (or reply all) from the mailbox being viewed, threaded onto the selected message. */
  protected reply(all: boolean): void {
    const m = this.selected();
    if (!m) return;
    const me = this.mailbox().toLowerCase();
    const sentByMe = (m.from?.address ?? '').toLowerCase() === me || this.folder() === 'sentitems';
    const primary = sentByMe ? m.toRecipients.map((a) => a.address) : m.from ? [m.from.address] : [];
    const others = all ? [...m.toRecipients, ...m.ccRecipients].map((a) => a.address) : [];
    const unique = (list: string[]) => [...new Set(list.filter((a) => a && a.toLowerCase() !== me))];
    const to = unique(primary);
    this.draft.set({
      from: this.mailbox(),
      to,
      cc: unique(others).filter((a) => !to.includes(a)),
      subject: /^re:/i.test(m.subject) ? m.subject : `RE: ${m.subject}`,
      body: quote(m),
      inReplyToMessageId: m.id,
    });
  }

  protected onSent(): void {
    this.draft.set(null);
    this.refresh();
  }
}
