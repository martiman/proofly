import { api, command, read, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export const getSafeguardingPosition = (runId: string, query: ApiQuery<'getSafeguardingPosition'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/safeguarding', { params: { path: { runId }, query }, signal: o?.signal }))
export const listSafeguardingLines = (runId: string, currency: string, query: ApiQuery<'listSafeguardingLines'>, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/safeguarding/{currency}/lines', { params: { path: { runId, currency }, query }, signal: o?.signal }))
/** Adjustments carry no `etag` in the list: `decideAdjustment` uses the ETag of `proposeAdjustment`'s response or an explicit `ifMatch`. */
export const listAdjustments = (runId: string, query: ApiQuery<'listAdjustments'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/safeguarding/adjustments', { params: { path: { runId }, query }, signal: o?.signal }))
export const proposeAdjustment = (runId: string, body: ApiBody<'proposeAdjustment'>, o?: CommandOptions) =>
  command<ApiResult<'proposeAdjustment'>>(null, o, (h) =>
    api().POST('/runs/{runId}/safeguarding/adjustments', { params: { path: { runId }, header: h }, body, signal: o?.signal }), (a) => `/safeguarding/adjustments/${a.id}`)
export const decideAdjustment = (adjustmentId: string, body: ApiBody<'decideAdjustment'>, o?: CommandOptions) =>
  command<ApiResult<'decideAdjustment'>>(`/safeguarding/adjustments/${adjustmentId}`, o, (h) =>
    api().POST('/safeguarding/adjustments/{adjustmentId}/decision', { params: { path: { adjustmentId }, header: h }, body, signal: o?.signal }))
