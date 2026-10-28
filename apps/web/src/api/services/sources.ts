import { api, command, read, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export const listSourceDeliveries = (runId: string, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/deliveries', { params: { path: { runId } }, signal: o?.signal }))
export const getSourceDelivery = (deliveryId: string, o?: ReadOptions) =>
  read(api().GET('/deliveries/{deliveryId}', { params: { path: { deliveryId } }, signal: o?.signal }))
export const getDeliveryProcessingHistory = (deliveryId: string, o?: ReadOptions) =>
  read(api().GET('/deliveries/{deliveryId}/processing-history', { params: { path: { deliveryId } }, signal: o?.signal }))
export const listDeliveryRecords = (deliveryId: string, query: ApiQuery<'listDeliveryRecords'> = {}, o?: ReadOptions) =>
  read(api().GET('/deliveries/{deliveryId}/records', { params: { path: { deliveryId }, query }, signal: o?.signal }))
export const getSourceRecord = (recordId: string, o?: ReadOptions) =>
  read(api().GET('/source-records/{recordId}', { params: { path: { recordId } }, signal: o?.signal }))
/** Needs the delivery's ETag (from `getSourceDelivery`) or `ifMatch`. */
export const retryDeliveryIngestion = (deliveryId: string, body: ApiBody<'retryDeliveryIngestion'>, o?: CommandOptions) =>
  command<ApiResult<'retryDeliveryIngestion'>>(`/deliveries/${deliveryId}`, o, (h) =>
    api().POST('/deliveries/{deliveryId}/retry-ingestion', { params: { path: { deliveryId }, header: h }, body, signal: o?.signal }))
export const markDeliverySuperseded = (deliveryId: string, body: ApiBody<'markDeliverySuperseded'>, o?: CommandOptions) =>
  command<ApiResult<'markDeliverySuperseded'>>(`/deliveries/${deliveryId}`, o, (h) =>
    api().POST('/deliveries/{deliveryId}/mark-superseded', { params: { path: { deliveryId }, header: h }, body, signal: o?.signal }))
