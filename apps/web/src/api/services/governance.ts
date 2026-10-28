import { api, command, read, type ApiBody, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export const getSettings = (query: ApiQuery<'getSettings'> = {}, o?: ReadOptions) =>
  read(api().GET('/settings', { params: { query }, signal: o?.signal }))
export const listConfigChanges = (query: ApiQuery<'listConfigChanges'> = {}, o?: ReadOptions) =>
  read(api().GET('/settings/changes', { params: { query }, signal: o?.signal }))
export const getConfigChange = (changeId: string, o?: ReadOptions) =>
  read(api().GET('/settings/changes/{changeId}', { params: { path: { changeId } }, signal: o?.signal }))
export const proposeConfigChange = (body: ApiBody<'proposeConfigChange'>, o?: CommandOptions) =>
  command<ApiResult<'proposeConfigChange'>>(null, o, (h) =>
    api().POST('/settings/changes', { params: { header: h }, body, signal: o?.signal }), (c) => `/settings/changes/${c.id}`)
export const decideConfigChange = (changeId: string, body: ApiBody<'decideConfigChange'>, o?: CommandOptions) =>
  command<ApiResult<'decideConfigChange'>>(`/settings/changes/${changeId}`, o, (h) =>
    api().POST('/settings/changes/{changeId}/decision', { params: { path: { changeId }, header: h }, body, signal: o?.signal }))
export const listApprovals = (query: ApiQuery<'listApprovals'> = {}, o?: ReadOptions) =>
  read(api().GET('/approvals', { params: { query }, signal: o?.signal }))
