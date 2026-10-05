import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { problemMessage } from '../../core/http/problem';
import { AgoPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { AskAnswerComponent } from './ask-answer.component';
import { AskTurn, SUGGESTED_QUESTIONS, loadAskHistory, saveAskHistory } from './ask-history';

/**
 * Conversational Q&A over delegated work (/ask, optionally /ask?q=… to ask once on arrival).
 * The conversation is kept for the browser session (sessionStorage) and rendered oldest → newest;
 * the newest answer scrolls into view.
 */
@Component({
  selector: 'fu-ask-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, SkeletonComponent, AskAnswerComponent, AgoPipe, DateTimePipe],
  templateUrl: './ask.page.html',
  styleUrl: './ask.page.css',
})
export class AskPage {
  private readonly api = inject(ExecutiveApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** `?q=` — asked once, then removed from the URL so a refresh does not ask again. */
  readonly q = input<string | undefined>();

  protected readonly suggestions = SUGGESTED_QUESTIONS;
  protected readonly history = signal<AskTurn[]>(loadAskHistory());
  protected readonly draft = signal('');
  protected readonly pending = computed(() => this.history().some((t) => t.state === 'pending'));
  /** Short screen-reader status for the newest turn (the answer card itself is not a live region). */
  protected readonly announcement = computed(() => {
    const last = this.history().at(-1);
    if (!last) return '';
    if (last.state === 'pending') return 'Working on an answer…';
    if (last.state === 'error') return last.error ?? 'The question could not be answered.';
    return last.answer?.answer ?? '';
  });

  private readonly input = viewChild<ElementRef<HTMLInputElement>>('askInput');
  private seq = this.history().reduce((max, t) => Math.max(max, t.id), 0);

  constructor() {
    effect(() => saveAskHistory(this.history()));

    effect(() => {
      const q = (this.q() ?? '').trim();
      if (!q) return;
      untracked(() => {
        this.ask(q);
        void this.router.navigate([], { queryParams: { q: null }, queryParamsHandling: 'merge', replaceUrl: true });
      });
    });

    // Returning to the page: show the latest exchange rather than the oldest.
    afterNextRender(() => {
      const last = this.history().at(-1);
      if (last) this.scrollNow(last.id, 'auto');
    });
  }

  protected submit(e: Event): void {
    e.preventDefault();
    this.ask(this.draft());
  }

  /** Adds a question to the conversation and asks the server. Ignored while another answer is pending. */
  protected ask(question: string): void {
    const text = question.trim();
    if (!text || this.pending()) return;
    const id = ++this.seq;
    this.history.update((h) => [...h, { id, question: text, askedAt: new Date().toISOString(), state: 'pending', answer: null, error: null }]);
    this.draft.set('');
    this.run(id, text);
  }

  protected retry(turn: AskTurn): void {
    if (this.pending()) return;
    this.patch(turn.id, { state: 'pending', error: null });
    this.run(turn.id, turn.question);
  }

  /** An attention action inside an answer changed server state: re-ask silently and refresh that answer. */
  protected refresh(turn: AskTurn): void {
    this.api
      .ask({ question: turn.question })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (answer) => this.patch(turn.id, { answer }), error: () => undefined });
  }

  protected clear(): void {
    if (this.pending()) return;
    this.history.set([]);
    this.focusInput();
  }

  private run(id: number, question: string): void {
    this.scrollToTurn(id);
    this.api
      .ask({ question })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (answer) => {
          this.patch(id, { state: 'done', answer, error: null });
          this.scrollToTurn(id);
          this.focusInput();
        },
        error: (e: unknown) => {
          // The interceptor already toasted the failure; keep a retryable inline state.
          this.patch(id, { state: 'error', error: problemMessage(e) });
          this.focusInput();
        },
      });
  }

  private patch(id: number, change: Partial<AskTurn>): void {
    this.history.update((h) => h.map((t) => (t.id === id ? { ...t, ...change } : t)));
  }

  /** Scrolls a turn into view once the pending DOM update has rendered. */
  private scrollToTurn(id: number): void {
    afterNextRender(() => this.scrollNow(id, 'smooth'), { injector: this.injector });
  }

  private scrollNow(id: number, behavior: ScrollBehavior): void {
    const el = this.host.nativeElement.querySelector(`#ask-turn-${id}`);
    const reduced = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    el?.scrollIntoView({ behavior: reduced ? 'auto' : behavior, block: 'start' });
  }

  private focusInput(): void {
    afterNextRender(() => this.input()?.nativeElement.focus({ preventScroll: true }), { injector: this.injector });
  }
}
