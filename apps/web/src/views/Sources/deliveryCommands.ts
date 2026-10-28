import { retryDeliveryIngestion, markDeliverySuperseded } from '../../api/services'
import { useCommand } from '../../api/composables'

/** Retry/supersede commands for a delivery. If-Match comes from the ETag captured by `getSourceDelivery`. */
export function useDeliveryCommands() {
  const retry = useCommand((a: { deliveryId: string; reason: string }, o) => retryDeliveryIngestion(a.deliveryId, { reason: a.reason }, o))
  const supersede = useCommand((a: { deliveryId: string; supersededByDeliveryId: string; reason: string }, o) =>
    markDeliverySuperseded(a.deliveryId, { supersededByDeliveryId: a.supersededByDeliveryId, reason: a.reason }, o))
  return { retry, supersede }
}
