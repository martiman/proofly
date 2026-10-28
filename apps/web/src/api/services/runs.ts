import { api, command, read, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export const getMe = (o?: ReadOptions) => read(api().GET('/me', { signal: o?.signal }))
export const listLegalEntities = (o?: ReadOptions) => read(api().GET('/legal-entities', { signal: o?.signal }))

export const listRuns = (query: ApiQuery<'listRuns'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs', { params: { query }, signal: o?.signal }))
/** Single run. Its ETag is cached for `requestRunReprocess`. */
export const getRun = (runId: string, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}', { params: { path: { runId } }, signal: o?.signal }))
export const getControlRoom = (runId: string, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/control-room', { params: { path: { runId } }, signal: o?.signal }))
export const compareRuns = (runId: string, query: ApiQuery<'compareRuns'>, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/compare', { params: { path: { runId }, query }, signal: o?.signal }))
/** Needs the run's ETag: call `getRun` first or pass `ifMatch`. */
export const requestRunReprocess = (runId: string, body: ApiBody<'requestRunReprocess'>, o?: CommandOptions) =>
  command<ApiResult<'requestRunReprocess'>>(`/runs/${runId}`, o, (h) =>
    api().POST('/runs/{runId}/reprocess', { params: { path: { runId }, header: h }, body, signal: o?.signal }))
