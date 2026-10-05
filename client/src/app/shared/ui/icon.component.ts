import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ICONS, IconName } from './icons';

/**
 * Inline SVG icon. Decorative by default (aria-hidden); pass `label` to expose it to assistive tech.
 *   <fu-icon name="bell" [size]="18" />
 */
@Component({
  selector: 'fu-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
  host: {
    '[innerHTML]': 'svg()',
    '[attr.aria-hidden]': 'label() ? null : "true"',
    '[attr.role]': 'label() ? "img" : null',
    '[attr.aria-label]': 'label() || null',
    '[style.width.px]': 'size()',
    '[style.height.px]': 'size()',
    style: 'display:inline-flex;flex:none;line-height:0',
  },
})
export class IconComponent {
  private readonly sanitizer = inject(DomSanitizer);

  readonly name = input.required<IconName | string>();
  readonly size = input<number>(16);
  readonly stroke = input<number>(1.8);
  readonly label = input<string>('');

  /** Static, trusted markup from ICONS only — never user input. */
  protected readonly svg = computed<SafeHtml>(() => {
    const body = (ICONS as Record<string, string>)[this.name()] ?? ICONS['help'];
    const s = this.size();
    return this.sanitizer.bypassSecurityTrustHtml(
      `<svg xmlns="http://www.w3.org/2000/svg" width="${s}" height="${s}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="${this.stroke()}" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`,
    );
  });
}
