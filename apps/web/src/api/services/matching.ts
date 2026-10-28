import { api, command, read, rememberItemEtags, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export const getMatchingWorkbench = (runId: string, query: ApiQuery<'getMatchingWorkbench'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/matching/workbench', { params: { path: { runId }, query }, signal: o?.signal }))
export async function listMatchGroups(runId: string, query: ApiQuery<'listMatchGroups'> = {}, o?: ReadOptions) {
  const page = await read(api().GET('/runs/{runId}/match-groups', { params: { path: { runId }, query }, signal: o?.signal }))
  rememberItemEtags(page.items, (g) => `/runs/${runId}/match-groups/${g.id}`)
  return page
}
export const proposeManualMatch = (runId: string, body: ApiBody<'proposeManualMatch'>, o?: CommandOptions) =>
  command<ApiResult<'proposeManualMatch'>>(null, o, (h) =>
    api().POST('/runs/{runId}/match-groups', { params: { path: { runId }, header: h }, body, signal: o?.signal }), (g) => `/runs/${runId}/match-groups/${g.id}`)
/** `If-Match` comes from the group's `etag` (list item or propose response). */
export const decideManualMatch = (runId: string, groupId: string, body: ApiBody<'decideManualMatch'>, o?: CommandOptions) =>
  command<ApiResult<'decideManualMatch'>>(`/runs/${runId}/match-groups/${groupId}`, o, (h) =>
    api().POST('/runs/{runId}/match-groups/{groupId}/decision', { params: { path: { runId, groupId }, header: h }, body, signal: o?.signal }))
