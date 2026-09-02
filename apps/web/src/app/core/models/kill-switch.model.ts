/** Mirrors the shape of `Akshaya.Modules.Trading.Application.KillSwitch`'s state, per tenant. */
export interface KillSwitchState {
  readonly isEngaged: boolean;
  readonly engagedBy?: string;
  readonly engagedAt?: string;
  readonly reason?: string;
}
