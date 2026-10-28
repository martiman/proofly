import { api, command, read, rememberItemEtags, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export async function listExceptions(runId: string, query: ApiQuery<'listExceptions'> = {}, o?: ReadOptions) {
  const page = await read(api().GET('/runs/{runId}/exceptions', { params: { path: { runId }, query }, signal: o?.signal }))
  rememberItemEtags(page.items, (e) => `/exceptions/${e.id}`)
  return page
}
export const getExceptionsSummary = (runId: string, query: ApiQuery<'getExceptionsSummary'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/exceptions/summary', { params: { path: { runId }, query }, signal: o?.signal }))
/** Single exception with its ETag cached for the exception commands below. */
export const getException = (exceptionId: string, o?: ReadOptions) =>
  read(api().GET('/exceptions/{exceptionId}', { params: { path: { exceptionId } }, signal: o?.signal }))

/** Needs no If-Match; each item carries its own `etag` (partial success is a 207, inspect `results`). */
export const bulkAssignExceptions = (runId: string, body: ApiBody<'bulkAssignExceptions'>, o?: CommandOptions) =>
  command<ApiResult<'bulkAssignExceptions'>>(null, o, (h) =>
    api().POST('/runs/{runId}/exceptions/bulk-assign', { params: { path: { runId }, header: h }, body, signal: o?.signal }))

export async function listExceptionViews(o?: ReadOptions) {
  const views = await read(api().GET('/exception-views', { signal: o?.signal }))
  return views
}
export const createExceptionView = (body: ApiBody<'createExceptionView'>, o?: CommandOptions) =>
  command<ApiResult<'createExceptionView'>>(null, o, (h) =>
    api().POST('/exception-views', { params: { header: h }, body, signal: o?.signal }))
/** `ifMatch` is the view's `etag` from the list (the contract has no single-view GET). */
export const deleteExceptionView = (viewId: string, o?: CommandOptions & { ifMatch: string }) =>
  command<void>(`/exception-views/${viewId}`, o, (h) =>
    api().DELETE('/exception-views/{viewId}', { params: { path: { viewId }, header: { 'If-Match': h['If-Match'] } }, signal: o?.signal }))

export const assignException = (exceptionId: string, body: ApiBody<'assignException'>, o?: CommandOptions) =>
  command<ApiResult<'assignException'>>(`/exceptions/${exceptionId}`, o, (h) =>
    api().POST('/exceptions/{exceptionId}/assign', { params: { path: { exceptionId }, header: h }, body, signal: o?.signal }))
export const addExceptionNote = (exceptionId: string, body: ApiBody<'addExceptionNote'>, o?: CommandOptions) =>
  command<ApiResult<'addExceptionNote'>>(`/exceptions/${exceptionId}`, o, (h) =>
    api().POST('/exceptions/{exceptionId}/notes', { params: { path: { exceptionId }, header: h }, body, signal: o?.signal }))
export const requestExceptionEvidence = (exceptionId: string, body: ApiBody<'requestExceptionEvidence'>, o?: CommandOptions) =>
  command<ApiResult<'requestExceptionEvidence'>>(`/exceptions/${exceptionId}`, o, (h) =>
    api().POST('/exceptions/{exceptionId}/request-evidence', { params: { path: { exceptionId }, header: h }, body, signal: o?.signal }))
export const proposeResolution = (exceptionId: string, body: ApiBody<'proposeResolution'>, o?: CommandOptions) =>
  command<ApiResult<'proposeResolution'>>(`/exceptions/${exceptionId}`, o, (h) =>
    api().POST('/exceptions/{exceptionId}/resolution-proposals', { params: { path: { exceptionId }, header: h }, body, signal: o?.signal }))
export const decideResolution = (exceptionId: string, proposalId: string, body: ApiBody<'decideResolution'>, o?: CommandOptions) =>
  command<ApiResult<'decideResolution'>>(`/exceptions/${exceptionId}`, o, (h) =>
    api().POST('/exceptions/{exceptionId}/resolution-proposals/{proposalId}/decision', { params: { path: { exceptionId, proposalId }, header: h }, body, signal: o?.signal }))
export const reopenException = (exceptionId: string, body: ApiBody<'reopenException'>, o?: CommandOptions) =>
  command<ApiResult<'reopenException'>>(`/exceptions/${exceptionId}`, o, (h) =>
    api().POST('/exceptions/{exceptionId}/reopen', { params: { path: { exceptionId }, header: h }, body, signal: o?.signal }))
