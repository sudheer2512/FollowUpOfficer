import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { tap } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { RuntimeSettings } from '../../../core/models/admin.models';
import { CRITICALITY_VALUES, Criticality } from '../../../core/models/enums';
import { toTimeInput, toTimeOnly } from '../../../core/util/dates';
import { loader } from '../../../core/util/loader';
import { HumanizePipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { formView } from '../shared/form-view';

/** 0–1 → whole percent. */
function toPercent(value: number | null | undefined, fallback: number): number {
  if (value === null || value === undefined || isNaN(value)) return fallback;
  return Math.round((value <= 1 ? value * 100 : value) * 10) / 10;
}

/** Whole percent → 0–1 with two decimals. */
function fromPercent(value: number): number {
  return Math.round(Math.max(0, Math.min(100, Number(value) || 0))) / 100;
}

/** /admin/settings — runtime-editable thresholds (RuntimeSettings), applied without a restart. */
@Component({
  selector: 'fu-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HumanizePipe, IconComponent, SkeletonComponent],
  templateUrl: './settings.page.html',
  styleUrl: './settings.page.css',
})
export class SettingsPage {
  private readonly api = inject(AdminApi);
  private readonly toast = inject(ToastService);

  protected readonly criticalities = CRITICALITY_VALUES;
  protected readonly data = loader<RuntimeSettings>();
  protected readonly saving = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    severelyOverdueDays: [7, [Validators.required, Validators.min(1), Validators.max(365)]],
    recentUpdateWindowHours: [48, [Validators.required, Validators.min(1), Validators.max(720)]],
    maxRemindersPerRecipientPerDay: [5, [Validators.required, Validators.min(1), Validators.max(100)]],
    reminderExpiryHours: [24, [Validators.required, Validators.min(1), Validators.max(720)]],
    repeatedNonResponseReminders: [3, [Validators.required, Validators.min(1), Validators.max(50)]],
    acknowledgementFrom: ['Important' as Criticality],
    autoAttachPercent: [85, [Validators.required, Validators.min(0), Validators.max(100)]],
    suggestPercent: [60, [Validators.required, Validators.min(0), Validators.max(100)]],
    dailyDigestEnabled: [true],
    dailyDigestTime: ['08:00', [Validators.required]],
    weeklyReviewEnabled: [true],
    calendarSyncEnabled: [false],
  });
  protected readonly state = formView(this.form);

  /** The AI must be more certain to auto-attach than to merely suggest. */
  protected readonly confidenceError = computed(() => {
    const v = this.state().value;
    return Number(v.suggestPercent) >= Number(v.autoAttachPercent) ? 'The suggest threshold must be lower than the auto-attach threshold.' : null;
  });

  /** Relative widths of the ignore / suggest / auto-attach bands under the sliders. */
  protected readonly bands = computed(() => {
    const v = this.state().value;
    const suggest = Math.max(0, Math.min(100, Number(v.suggestPercent) || 0));
    const attach = Math.max(suggest, Math.min(100, Number(v.autoAttachPercent) || 0));
    return { ignore: suggest, suggest: attach - suggest, attach: 100 - attach };
  });

  protected readonly canSave = computed(() => this.state().dirty && this.state().valid && !this.confidenceError() && !this.saving());

  constructor() {
    // The digest time only matters while the digest is on.
    this.form.controls.dailyDigestEnabled.valueChanges.pipe(takeUntilDestroyed()).subscribe((on) => {
      const time = this.form.controls.dailyDigestTime;
      if (on) time.enable();
      else time.disable();
    });
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.settings().pipe(tap((s) => this.apply(s))));
  }

  protected reset(): void {
    const s = this.data.value();
    if (s) this.apply(s);
  }

  private apply(s: RuntimeSettings | null | undefined): void {
    if (!s) return;
    this.form.reset({
      severelyOverdueDays: s.severelyOverdueDays ?? 7,
      recentUpdateWindowHours: s.recentUpdateWindowHours ?? 48,
      maxRemindersPerRecipientPerDay: s.maxRemindersPerRecipientPerDay ?? 5,
      reminderExpiryHours: s.reminderExpiryHours ?? 24,
      repeatedNonResponseReminders: s.repeatedNonResponseReminders ?? 3,
      acknowledgementFrom: s.acknowledgementFrom ?? 'Important',
      autoAttachPercent: toPercent(s.autoAttachConfidence, 85),
      suggestPercent: toPercent(s.suggestConfidence, 60),
      dailyDigestEnabled: s.dailyDigestEnabled ?? true,
      dailyDigestTime: toTimeInput(s.dailyDigestTime) || '08:00',
      weeklyReviewEnabled: s.weeklyReviewEnabled ?? true,
      calendarSyncEnabled: s.calendarSyncEnabled ?? false,
    });
  }

  protected save(): void {
    if (!this.canSave()) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const settings: RuntimeSettings = {
      severelyOverdueDays: Number(v.severelyOverdueDays),
      recentUpdateWindowHours: Number(v.recentUpdateWindowHours),
      maxRemindersPerRecipientPerDay: Number(v.maxRemindersPerRecipientPerDay),
      reminderExpiryHours: Number(v.reminderExpiryHours),
      repeatedNonResponseReminders: Number(v.repeatedNonResponseReminders),
      acknowledgementFrom: v.acknowledgementFrom,
      autoAttachConfidence: fromPercent(v.autoAttachPercent),
      suggestConfidence: fromPercent(v.suggestPercent),
      dailyDigestEnabled: v.dailyDigestEnabled,
      dailyDigestTime: toTimeOnly(v.dailyDigestTime) ?? '08:00:00',
      weeklyReviewEnabled: v.weeklyReviewEnabled,
      calendarSyncEnabled: v.calendarSyncEnabled,
    };
    this.saving.set(true);
    this.api.saveSettings(settings).subscribe({
      next: (saved) => {
        this.saving.set(false);
        const value = saved ?? settings;
        this.data.set(value);
        this.apply(value);
        this.toast.success('Settings saved — they apply immediately');
      },
      error: () => this.saving.set(false),
    });
  }
}
