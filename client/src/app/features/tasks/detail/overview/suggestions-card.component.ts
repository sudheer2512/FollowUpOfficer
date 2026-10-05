import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TasksApi } from '../../../../core/api/tasks.api';
import { SuggestionDto, TaskDetail } from '../../../../core/models/task.models';
import { AgoPipe, ConfidencePipe, DateOnlyPipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ToastService } from '../../../../shared/ui/toast.service';

/**
 * AI suggestions waiting for a human decision (from emails, Teams and comments): what the AI proposes, how confident it
 * is and why. Accept applies the change server-side; Dismiss records the rejection. Both return the updated task.
 */
@Component({
  selector: 'fu-suggestions-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, HumanizePipe, ConfidencePipe, DateOnlyPipe, AgoPipe],
  template: `
    <section class="card ai">
      <header class="card-header">
        <fu-icon name="sparkles" [size]="16" class="text-accent" />
        <h2>AI suggestions</h2>
        <span class="count-pill accent">{{ pending().length }}</span>
        <span class="spacer"></span>
        @if (resolvedCount()) {
          <span class="xsmall muted">{{ resolvedCount() }} earlier handled</span>
        }
      </header>
      <div class="card-body flush list">
        @for (s of pending(); track s.id) {
          <div class="list-row sug">
            <div class="main stack">
              <div class="row wrap">
                <span class="strong">{{ s.kind | humanize }}</span>
                @if (s.suggestedStatus) {
                  <span class="chip sm tone-accent">→ {{ s.suggestedStatus | humanize }}</span>
                }
                @if (s.suggestedDate) {
                  <span class="chip sm tone-accent">{{ s.suggestedDate | dateOnly: 'weekday' }}</span>
                }
                <span class="chip sm" [class.tone-good]="s.confidence >= 0.8" [class.tone-warning]="s.confidence < 0.6" [attr.title]="'Confidence ' + (s.confidence | confidence)">
                  {{ s.confidence | confidence }} confident
                </span>
              </div>
              @if (s.detail) {
                <div class="small">{{ s.detail }}</div>
              }
              <div class="small muted why">{{ s.explanation }}</div>
              <div class="xsmall muted row gap-4">
                {{ s.createdAt | ago }}
                @if (s.messageId) {
                  · <a routerLink="/communications" [queryParams]="{ message: s.messageId }">View source message</a>
                }
              </div>
            </div>
            @if (canDecide()) {
              <div class="acts">
                <button type="button" class="btn btn-sm btn-primary" [disabled]="busyId() === s.id" (click)="decide(s, true)"><fu-icon name="check" [size]="14" /> Accept</button>
                <button type="button" class="btn btn-sm btn-ghost" [disabled]="busyId() === s.id" (click)="decide(s, false)">Dismiss</button>
              </div>
            }
          </div>
        }
      </div>
    </section>
  `,
  styles: `
    .ai { border-color: color-mix(in srgb, var(--accent) 35%, var(--border)); }
    .sug { align-items: flex-start; }
    .why { font-style: italic; }
    .acts { display: flex; flex-direction: column; gap: 6px; }
    @media (max-width: 600px) { .sug { flex-direction: column; } .acts { flex-direction: row; } }
  `,
})
export class SuggestionsCardComponent {
  private readonly api = inject(TasksApi);
  private readonly toast = inject(ToastService);

  readonly detail = input.required<TaskDetail>();
  readonly updated = output<TaskDetail>();

  protected readonly busyId = signal<string | null>(null);
  protected readonly pending = computed(() => this.detail().suggestions.filter((s) => s.status === 'Pending'));
  protected readonly resolvedCount = computed(() => this.detail().suggestions.length - this.pending().length);
  protected readonly canDecide = computed(() => {
    const p = this.detail().permissions;
    return p.canManage || p.canUpdate;
  });

  protected decide(s: SuggestionDto, accept: boolean): void {
    this.busyId.set(s.id);
    const command = accept ? this.api.acceptSuggestion(s.id) : this.api.dismissSuggestion(s.id);
    command.subscribe({
      next: (d) => {
        this.busyId.set(null);
        this.toast.success(accept ? 'Suggestion applied' : 'Suggestion dismissed');
        this.updated.emit(d);
      },
      error: () => this.busyId.set(null),
    });
  }
}
