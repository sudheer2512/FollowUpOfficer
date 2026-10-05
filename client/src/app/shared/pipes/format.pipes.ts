import { Pipe, PipeTransform } from '@angular/core';
import { formatDateOnly, formatDateTime, relativeTime } from '../../core/util/dates';
import { confidencePercent, fileSize, humanize, money } from '../../core/util/humanize';

/** "InProgress" → "In progress". */
@Pipe({ name: 'humanize' })
export class HumanizePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return humanize(value);
  }
}

/** "2026-10-06" → "6 Oct 2026" (`'short'` drops the current year, `'weekday'` prefixes the day). */
@Pipe({ name: 'dateOnly' })
export class DateOnlyPipe implements PipeTransform {
  transform(value: string | null | undefined, style: 'default' | 'short' | 'weekday' = 'default'): string {
    return formatDateOnly(value, { short: style !== 'default', withWeekday: style === 'weekday' });
  }
}

/** ISO UTC → "6 Oct 2026, 14:05" local (`'short'` drops the current year). */
@Pipe({ name: 'dateTime' })
export class DateTimePipe implements PipeTransform {
  transform(value: string | null | undefined, style: 'default' | 'short' = 'default'): string {
    return formatDateTime(value, { short: style === 'short' });
  }
}

/** ISO UTC → "3h ago" / "in 2 days". */
@Pipe({ name: 'ago' })
export class AgoPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return relativeTime(value);
  }
}

/** 0–1 → "82%". */
@Pipe({ name: 'confidence' })
export class ConfidencePipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return confidencePercent(value);
  }
}

@Pipe({ name: 'fileSize' })
export class FileSizePipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return fileSize(value);
  }
}

@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return money(value);
  }
}

export const FORMAT_PIPES = [HumanizePipe, DateOnlyPipe, DateTimePipe, AgoPipe, ConfidencePipe, FileSizePipe, MoneyPipe] as const;
