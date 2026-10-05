import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { SystemStore } from '../../core/state/system.store';
import { loader } from '../../core/util/loader';
import { IconComponent } from '../../shared/ui/icon.component';
import { TabItem, TabsComponent } from '../../shared/ui/tabs.component';
import { SimActivityComponent } from './sim-activity.component';
import { SimChannelsComponent } from './sim-channels.component';
import { SimChatsComponent } from './sim-chats.component';
import { SimFaultsComponent } from './sim-faults.component';
import { SimMailComponent } from './sim-mail.component';
import { SimulatorApi } from './simulator.api';
import { SimUser } from './simulator.models';

type SimTab = 'mail' | 'chats' | 'channels' | 'activity' | 'faults';

const TABS: TabItem[] = [
  { key: 'mail', label: 'Mail' },
  { key: 'chats', label: 'Teams chats' },
  { key: 'channels', label: 'Channels' },
  { key: 'activity', label: 'Activity feed' },
  { key: 'faults', label: 'Fault injection' },
];

function asTab(value: string | null | undefined): SimTab {
  return value === 'chats' || value === 'channels' || value === 'activity' || value === 'faults' ? value : 'mail';
}

/**
 * /simulator — console for the emulated Microsoft 365 tenant (Emulator mode). FollowUp Officer reaches the
 * emulator through the real Microsoft Graph client, so mail and Teams messages posted here flow through the
 * normal sync jobs, and injected faults exercise retries, checkpoints and resyncs. Tab synced to `?tab=`.
 */
@Component({
  selector: 'fu-simulator-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, TabsComponent, SimMailComponent, SimChatsComponent, SimChannelsComponent, SimActivityComponent, SimFaultsComponent],
  templateUrl: './simulator.page.html',
  styleUrl: './simulator.page.css',
})
export class SimulatorPage {
  private readonly api = inject(SimulatorApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly system = inject(SystemStore);

  /** `?tab=` via router component-input binding. */
  readonly tab = input<string | undefined>();

  protected readonly tabs = TABS;
  protected readonly active = computed(() => asTab(this.tab()));
  protected readonly users = loader<SimUser[]>();
  protected readonly userList = computed(() => this.users.value() ?? []);

  constructor() {
    if (!this.system.info()) this.system.load();
    this.reloadUsers();
  }

  protected reloadUsers(): void {
    this.users.load(this.api.users());
  }

  protected select(key: string): void {
    const tab = asTab(key);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: tab === 'mail' ? null : tab },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
