import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { IconComponent } from './icon.component';

/** "1–50 of 213" with previous / next. */
@Component({
  selector: 'fu-paginator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `<nav class="row small muted" aria-label="Pagination">
    <span class="num">{{ from() }}–{{ to() }} of {{ total() }}</span>
    <span class="spacer"></span>
    <button type="button" class="btn btn-sm btn-icon" aria-label="Previous page" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">
      <fu-icon name="chevron-left" />
    </button>
    <span class="num">Page {{ page() }} / {{ pages() }}</span>
    <button type="button" class="btn btn-sm btn-icon" aria-label="Next page" [disabled]="page() >= pages()" (click)="pageChange.emit(page() + 1)">
      <fu-icon name="chevron-right" />
    </button>
  </nav>`,
  host: { style: 'display:block' },
})
export class PaginatorComponent {
  readonly page = input(1);
  readonly pageSize = input(50);
  readonly total = input(0);
  readonly pageChange = output<number>();

  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / Math.max(1, this.pageSize()))));
  protected readonly from = computed(() => (this.total() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1));
  protected readonly to = computed(() => Math.min(this.total(), this.page() * this.pageSize()));
}
