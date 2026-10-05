import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LookupsApi } from '../../../core/api/lookups.api';
import { SavedViewDto } from '../../../core/models/admin.models';
import { TaskFilter } from '../../../core/models/task.models';
import { loader } from '../../../core/util/loader';
import { DialogService } from '../../../shared/ui/dialog.service';
import { DropdownComponent } from '../../../shared/ui/dropdown.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ModalComponent } from '../../../shared/ui/modal.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { sameCriteria, viewCriteria } from './filter-chips';

/**
 * Saved task-list views (GET/POST/PUT/DELETE /api/views): apply, save the current filter, and — for the user's own
 * views — rename, share/unshare, overwrite with the current filter and delete.
 */
@Component({
  selector: 'fu-saved-views',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, DropdownComponent, IconComponent, ModalComponent],
  template: `
    <fu-dropdown [label]="active()?.name ?? 'Saved views'" icon="bookmark" align="right">
      @if (views.loading() && !views.value()) {
        <div class="menu-label">Loading views…</div>
      }
      @if (mine().length) {
        <div class="menu-label">My views</div>
        @for (v of mine(); track v.id) {
          <button type="button" role="menuitem" class="menu-item" (click)="applyView(v)">
            <fu-icon [name]="active()?.id === v.id ? 'check' : 'bookmark'" [size]="14" />
            <span class="nm truncate">{{ v.name }}</span>
            @if (v.isShared) {
              <fu-icon name="users" [size]="13" class="muted" label="Shared" />
            }
          </button>
        }
      }
      @if (shared().length) {
        <div class="menu-label">Shared with me</div>
        @for (v of shared(); track v.id) {
          <button type="button" role="menuitem" class="menu-item" (click)="applyView(v)">
            <fu-icon [name]="active()?.id === v.id ? 'check' : 'users'" [size]="14" />
            <span class="nm truncate">{{ v.name }}</span>
          </button>
        }
      }
      @if (views.value() && !mine().length && !shared().length) {
        <div class="menu-label">No saved views yet</div>
      }
      <div class="menu-sep"></div>
      <button type="button" role="menuitem" class="menu-item" (click)="openSave()"><fu-icon name="plus" [size]="14" /> Save current view…</button>
      @if (mine().length) {
        <button type="button" role="menuitem" class="menu-item" (click)="showManage.set(true)"><fu-icon name="settings" [size]="14" /> Manage views…</button>
      }
    </fu-dropdown>

    @if (showSave()) {
      <fu-modal title="Save view" subtitle="Saves the current filters, scope and sort under a name." size="sm" (closed)="showSave.set(false)">
        <form id="save-view-form" class="stack" [formGroup]="saveForm" (ngSubmit)="save()">
          <div class="field">
            <label for="sv-name" class="req">Name</label>
            <input id="sv-name" class="input" formControlName="name" maxlength="80" placeholder="e.g. Critical overdue – Finance" autofocus />
          </div>
          <label class="switch">
            <input type="checkbox" formControlName="isShared" />
            Share with everyone
          </label>
          <div class="hint">Shared views appear in everyone's “Saved views” menu. Only you can change or delete them.</div>
        </form>
        <ng-container modal-footer>
          <button type="button" class="btn" (click)="showSave.set(false)">Cancel</button>
          <button type="submit" form="save-view-form" class="btn btn-primary" [disabled]="saveForm.invalid || busy()">Save view</button>
        </ng-container>
      </fu-modal>
    }

    @if (showManage()) {
      <fu-modal title="Manage saved views" size="md" (closed)="showManage.set(false)">
        <div class="list views">
          @for (v of mine(); track v.id) {
            <div class="list-row">
              <div class="main">
                <div class="title truncate">{{ v.name }}</div>
                <div class="meta">
                  @if (active()?.id === v.id) {
                    <span class="chip sm tone-accent">Current</span>
                  }
                  <label class="switch small">
                    <input type="checkbox" [checked]="v.isShared" [disabled]="busyId() === v.id" (change)="setShared(v, $any($event.target).checked)" />
                    Shared
                  </label>
                </div>
              </div>
              <button type="button" class="btn btn-sm btn-ghost" (click)="applyView(v); showManage.set(false)">Apply</button>
              <button type="button" class="btn btn-sm btn-ghost" [disabled]="busyId() === v.id" title="Replace this view's filters with the current ones" (click)="overwrite(v)">Update</button>
              <button type="button" class="btn btn-sm btn-ghost btn-icon" [attr.aria-label]="'Rename ' + v.name" [disabled]="busyId() === v.id" (click)="rename(v)">
                <fu-icon name="edit" [size]="14" />
              </button>
              <button type="button" class="btn btn-sm btn-ghost btn-icon text-danger" [attr.aria-label]="'Delete ' + v.name" [disabled]="busyId() === v.id" (click)="remove(v)">
                <fu-icon name="trash" [size]="14" />
              </button>
            </div>
          }
        </div>
        <ng-container modal-footer>
          <button type="button" class="btn" (click)="showManage.set(false)">Done</button>
        </ng-container>
      </fu-modal>
    }
  `,
  styles: `
    .nm { flex: 1; min-width: 0; max-width: 260px; }
    .views { margin: -16px -18px; }
  `,
})
export class SavedViewsComponent {
  private readonly api = inject(LookupsApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  /** The filter currently applied on the list (from the URL). */
  readonly filter = input.required<TaskFilter>();
  readonly apply = output<TaskFilter>();

  protected readonly views = loader<SavedViewDto[]>();
  protected readonly mine = computed(() => (this.views.value() ?? []).filter((v) => v.isMine));
  protected readonly shared = computed(() => (this.views.value() ?? []).filter((v) => !v.isMine));
  /** The saved view whose criteria equal the current filter, if any. */
  protected readonly active = computed(() => (this.views.value() ?? []).find((v) => sameCriteria(v.filter ?? {}, this.filter())) ?? null);

  protected readonly showSave = signal(false);
  protected readonly showManage = signal(false);
  protected readonly busy = signal(false);
  protected readonly busyId = signal<string | null>(null);

  protected readonly saveForm = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(80)]],
    isShared: [false],
  });

  constructor() {
    this.reload();
  }

  private reload(): void {
    this.views.load(this.api.views(), { silent: !!this.views.value() });
  }

  protected applyView(v: SavedViewDto): void {
    this.apply.emit({ ...(v.filter ?? {}), page: 1 });
  }

  protected openSave(): void {
    this.saveForm.reset({ name: '', isShared: false });
    this.showSave.set(true);
  }

  protected save(): void {
    if (this.saveForm.invalid || this.busy()) return;
    const { name, isShared } = this.saveForm.getRawValue();
    this.busy.set(true);
    this.api.createView({ name: name.trim(), isShared, filter: viewCriteria(this.filter()) }).subscribe({
      next: () => {
        this.busy.set(false);
        this.showSave.set(false);
        this.toast.success(`View “${name.trim()}” saved`);
        this.reload();
      },
      error: () => this.busy.set(false),
    });
  }

  protected setShared(v: SavedViewDto, isShared: boolean): void {
    this.update(v, { name: v.name, isShared, filter: v.filter ?? {} }, isShared ? 'View shared' : 'View is now private');
  }

  protected async overwrite(v: SavedViewDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: 'Update view',
      message: `Replace the filters saved in “${v.name}” with the current ones?`,
      confirmText: 'Update view',
    });
    if (ok) this.update(v, { name: v.name, isShared: v.isShared, filter: viewCriteria(this.filter()) }, 'View updated');
  }

  protected async rename(v: SavedViewDto): Promise<void> {
    const name = await this.dialog.prompt({
      title: 'Rename view',
      message: 'Shared views are renamed for everyone.',
      label: 'Name',
      value: v.name,
      required: true,
      confirmText: 'Rename',
    });
    if (name && name.trim() && name.trim() !== v.name) {
      this.update(v, { name: name.trim(), isShared: v.isShared, filter: v.filter ?? {} }, 'View renamed');
    }
  }

  protected async remove(v: SavedViewDto): Promise<void> {
    const ok = await this.dialog.confirm({ title: 'Delete view', message: `Delete the saved view “${v.name}”?`, confirmText: 'Delete', danger: true });
    if (!ok) return;
    this.busyId.set(v.id);
    this.api.deleteView(v.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success('View deleted');
        this.reload();
      },
      error: () => this.busyId.set(null),
    });
  }

  private update(v: SavedViewDto, input: { name: string; isShared: boolean; filter: TaskFilter }, message: string): void {
    this.busyId.set(v.id);
    this.api.updateView(v.id, input).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success(message);
        this.reload();
      },
      error: () => {
        this.busyId.set(null);
        this.reload();
      },
    });
  }
}
