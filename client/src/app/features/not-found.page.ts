import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { EmptyStateComponent } from '../shared/ui/empty-state.component';

@Component({
  selector: 'fu-not-found-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, EmptyStateComponent],
  template: `<div class="page page-narrow">
    <div class="card">
      <fu-empty icon="help" title="Page not found" message="The page you were looking for does not exist or has moved.">
        <a class="btn btn-primary mt-8" routerLink="/">Go to the start page</a>
      </fu-empty>
    </div>
  </div>`,
})
export class NotFoundPage {}
