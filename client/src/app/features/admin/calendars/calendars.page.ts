import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { tap } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { CalendarDto, CalendarInput, HolidayDto, HolidayInput } from '../../../core/models/admin.models';
import { DateOnly, Guid } from '../../../core/models/common.models';
import { HOLIDAY_KIND_VALUES, HolidayKind, WEEKDAYS_MONDAY_FIRST } from '../../../core/models/enums';
import { toTimeInput, toTimeOnly } from '../../../core/util/dates';
import { loader } from '../../../core/util/loader';
import { DateInputComponent } from '../../../shared/forms/date-input.component';
import { DateOnlyPipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { DialogService } from '../../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { checkedOf, formView, inOrder, textOrNull, toggleInList, valueOf } from '../shared/form-view';

/** Suggestions for the time-zone field (IANA ids); any valid id may be typed. */
const COMMON_TIME_ZONES = [
  'Asia/Kolkata',
  'Asia/Dubai',
  'Asia/Singapore',
  'Asia/Riyadh',
  'Europe/London',
  'Europe/Berlin',
  'America/New_York',
  'America/Chicago',
  'America/Los_Angeles',
  'Australia/Sydney',
  'UTC',
];

const KIND_TONE: Record<HolidayKind, string> = { Public: 'tone-accent', Company: 'tone-info', Regional: '' };

function parseWeekend(value: string | null | undefined): string[] {
  return (value ?? '')
    .split(',')
    .map((d) => d.trim())
    .filter((d) => d.length > 0);
}

function minutesOf(time: string): number | null {
  const m = /^(\d{1,2}):(\d{2})/.exec(time);
  return m ? Number(m[1]) * 60 + Number(m[2]) : null;
}

/** /admin/calendars — working calendars (weekend, hours, time zone) and their holidays. */
@Component({
  selector: 'fu-calendars-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, DateInputComponent, DateOnlyPipe, HumanizePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  templateUrl: './calendars.page.html',
  styleUrl: './calendars.page.css',
})
export class CalendarsPage {
  private readonly api = inject(AdminApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly days = WEEKDAYS_MONDAY_FIRST;
  protected readonly kinds = HOLIDAY_KIND_VALUES;
  protected readonly timeZones = COMMON_TIME_ZONES;

  protected readonly data = loader<CalendarDto[]>();
  protected readonly calendars = computed(() =>
    (this.data.value() ?? [])
      .map((c) => ({ ...c, holidays: c.holidays ?? [] }))
      .sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || (a.name ?? '').localeCompare(b.name ?? '')),
  );
  protected readonly selectedId = signal<Guid | 'new' | null>(null);
  protected readonly selected = computed(() => {
    const id = this.selectedId();
    return id && id !== 'new' ? (this.calendars().find((c) => c.id === id) ?? null) : null;
  });
  protected readonly saving = signal(false);
  protected readonly holidayBusy = signal(false);
  protected readonly year = signal<number | 'all'>(new Date().getFullYear());

  protected readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    region: [''],
    timeZoneId: ['Asia/Kolkata', [Validators.required]],
    weekendDays: [['Saturday', 'Sunday'] as string[]],
    workdayStart: ['09:00', [Validators.required]],
    workdayEnd: ['18:00', [Validators.required]],
    isDefault: [false],
  });
  protected readonly state = formView(this.form);

  protected readonly holidayForm = this.fb.group({
    date: [null as DateOnly | null, [Validators.required]],
    name: ['', [Validators.required, Validators.maxLength(120)]],
    kind: ['Public' as HolidayKind],
  });
  protected readonly holidayState = formView(this.holidayForm);

  /** Validation that spans fields: at least one working day, and the day must end after it starts. */
  protected readonly scheduleError = computed(() => {
    const v = this.state().value;
    if (v.weekendDays.length >= 7) return 'At least one day must be a working day.';
    const start = minutesOf(v.workdayStart);
    const end = minutesOf(v.workdayEnd);
    if (start !== null && end !== null && end <= start) return 'The working day must end after it starts.';
    return null;
  });

  protected readonly workingHours = computed(() => {
    const v = this.state().value;
    const start = minutesOf(v.workdayStart);
    const end = minutesOf(v.workdayEnd);
    if (start === null || end === null || end <= start) return '';
    const mins = end - start;
    return `${Math.floor(mins / 60)} h${mins % 60 ? ` ${mins % 60} min` : ''} per working day`;
  });

  protected readonly years = computed(() => {
    const set = new Set<number>([new Date().getFullYear()]);
    for (const h of this.selected()?.holidays ?? []) {
      const y = Number((h.date ?? '').slice(0, 4));
      if (y) set.add(y);
    }
    return [...set].sort((a, b) => a - b);
  });

  protected readonly holidays = computed(() => {
    const y = this.year();
    return [...(this.selected()?.holidays ?? [])]
      .filter((h) => y === 'all' || (h.date ?? '').startsWith(`${y}-`))
      .sort((a, b) => (a.date ?? '').localeCompare(b.date ?? ''));
  });

  constructor() {
    this.reload();
  }

  protected reload(silent = false): void {
    this.data.load(this.api.calendars().pipe(tap((list) => this.ensureSelection(list ?? []))), { silent });
  }

  /** Keeps the current selection after a reload, or opens the default (else first) calendar. */
  private ensureSelection(list: CalendarDto[]): void {
    const current = this.selectedId();
    if (current === 'new' || (current && list.some((c) => c.id === current))) return;
    const pick = list.find((c) => c.isDefault) ?? list[0];
    if (pick) this.open(pick);
    else this.selectedId.set(null);
  }

  /** Asks before throwing away unsaved edits. */
  private async canLeave(): Promise<boolean> {
    if (!this.form.dirty) return true;
    return this.dialog.confirm({
      title: 'Discard changes?',
      message: 'This calendar has unsaved changes. Discard them?',
      confirmText: 'Discard',
      danger: true,
    });
  }

  protected async select(c: CalendarDto): Promise<void> {
    if (c.id === this.selectedId() || !(await this.canLeave())) return;
    this.open(c);
  }

  private open(c: CalendarDto): void {
    this.selectedId.set(c.id);
    this.resetForm(c);
    this.year.set(new Date().getFullYear());
  }

  protected async openNew(): Promise<void> {
    if (!(await this.canLeave())) return;
    this.selectedId.set('new');
    this.form.reset({
      name: '',
      region: '',
      timeZoneId: 'Asia/Kolkata',
      weekendDays: ['Saturday', 'Sunday'],
      workdayStart: '09:00',
      workdayEnd: '18:00',
      isDefault: this.calendars().length === 0,
    });
  }

  private resetForm(c: CalendarDto): void {
    this.form.reset({
      name: c.name ?? '',
      region: c.region ?? '',
      timeZoneId: c.timeZoneId ?? 'UTC',
      weekendDays: parseWeekend(c.weekendDays),
      workdayStart: toTimeInput(c.workdayStart) || '09:00',
      workdayEnd: toTimeInput(c.workdayEnd) || '18:00',
      isDefault: c.isDefault ?? false,
    });
  }

  protected discard(): void {
    const c = this.selected();
    if (c) {
      this.resetForm(c);
      return;
    }
    // Cancelling a new calendar returns to the default one.
    this.form.markAsPristine();
    this.selectedId.set(null);
    this.ensureSelection(this.calendars());
  }

  protected isWeekend(day: string): boolean {
    return this.state().value.weekendDays.includes(day);
  }

  protected toggleDay(day: string, event: Event): void {
    toggleInList(this.form.controls.weekendDays, day, checkedOf(event));
  }

  protected setYear(event: Event): void {
    const v = valueOf(event);
    this.year.set(v === 'all' ? 'all' : Number(v));
  }

  protected kindTone(kind: HolidayKind | string): string {
    return KIND_TONE[kind as HolidayKind] ?? '';
  }

  protected save(): void {
    if (this.saving()) return;
    if (this.form.invalid || this.scheduleError()) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const input: CalendarInput = {
      name: v.name.trim(),
      region: textOrNull(v.region),
      timeZoneId: v.timeZoneId.trim(),
      weekendDays: inOrder(WEEKDAYS_MONDAY_FIRST, v.weekendDays).join(','),
      workdayStart: toTimeOnly(v.workdayStart) ?? '09:00:00',
      workdayEnd: toTimeOnly(v.workdayEnd) ?? '18:00:00',
      isDefault: v.isDefault,
    };
    const id = this.selectedId();
    const request = id && id !== 'new' ? this.api.updateCalendar(id, input) : this.api.createCalendar(input);
    this.saving.set(true);
    request.subscribe({
      next: (dto) => {
        this.saving.set(false);
        this.toast.success(id === 'new' ? `Calendar “${input.name}” created` : `Calendar “${input.name}” saved`);
        if (dto?.id) {
          this.selectedId.set(dto.id);
          this.resetForm(dto);
        } else {
          // No body returned: let the reload pick the calendar again.
          this.form.markAsPristine();
          if (id === 'new') this.selectedId.set(null);
        }
        // Making one calendar the default clears the flag on the others, so refresh the whole list.
        this.reload(true);
      },
      error: () => this.saving.set(false),
    });
  }

  protected addHoliday(): void {
    const c = this.selected();
    if (!c || this.holidayBusy()) return;
    if (this.holidayForm.invalid) {
      this.holidayForm.markAllAsTouched();
      return;
    }
    const v = this.holidayForm.getRawValue();
    const input: HolidayInput = { date: v.date ?? '', name: v.name.trim(), kind: v.kind };
    this.holidayBusy.set(true);
    this.api.addHoliday(c.id, input).subscribe({
      next: (dto) => {
        this.holidayBusy.set(false);
        this.applyCalendar(dto);
        this.toast.success(`${input.name} added`);
        this.holidayForm.reset({ date: null, name: '', kind: v.kind });
        const y = Number(input.date.slice(0, 4));
        if (y && this.year() !== 'all') this.year.set(y);
      },
      error: () => this.holidayBusy.set(false),
    });
  }

  protected async removeHoliday(h: HolidayDto): Promise<void> {
    const c = this.selected();
    if (!c) return;
    const ok = await this.dialog.confirm({
      title: 'Remove holiday',
      message: `Remove “${h.name}” from ${c.name}? Due dates and follow-ups that skip holidays will treat that day as a working day.`,
      confirmText: 'Remove',
      danger: true,
    });
    if (!ok) return;
    this.holidayBusy.set(true);
    this.api.removeHoliday(c.id, h.id).subscribe({
      next: (dto) => {
        this.holidayBusy.set(false);
        this.applyCalendar(dto);
        this.toast.success(`${h.name} removed`);
      },
      error: () => this.holidayBusy.set(false),
    });
  }

  /** Holiday endpoints return the updated calendar; fall back to a reload if the body is empty. */
  private applyCalendar(dto: CalendarDto | null | undefined): void {
    if (dto?.id) this.data.update((list) => (list ?? []).map((c) => (c.id === dto.id ? dto : c)));
    else this.reload(true);
  }
}
