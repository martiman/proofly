import { api, command, read, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

/** Run sign-off state. Its ETag is cached for submit/return/approve. */
export const getRunSignOff = (runId: string, query: ApiQuery<'getRunSignOff'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/sign-off', { params: { path: { runId }, query }, signal: o?.signal }))
export const listRunOpenMatters = (runId: string, query: ApiQuery<'listRunOpenMatters'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/sign-off/open-matters', { params: { path: { runId }, query }, signal: o?.signal }))
export const submitRunForSignOff = (runId: string, body: ApiBody<'submitRunForSignOff'>, o?: CommandOptions) =>
  command<ApiResult<'submitRunForSignOff'>>(`/runs/${runId}/sign-off`, o, (h) =>
    api().POST('/runs/{runId}/sign-off/submit', { params: { path: { runId }, header: h }, body, signal: o?.signal }))
export const returnRun = (runId: string, body: ApiBody<'returnRun'>, o?: CommandOptions) =>
  command<ApiResult<'returnRun'>>(`/runs/${runId}/sign-off`, o, (h) =>
    api().POST('/runs/{runId}/sign-off/return', { params: { path: { runId }, header: h }, body, signal: o?.signal }))
export const signOffRun = (runId: string, body: ApiBody<'signOffRun'>, o?: CommandOptions) =>
  command<ApiResult<'signOffRun'>>(`/runs/${runId}/sign-off`, o, (h) =>
    api().POST('/runs/{runId}/sign-off/approve', { params: { path: { runId }, header: h }, body, signal: o?.signal }))
