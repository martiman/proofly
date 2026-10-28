import type { operations } from '../generated/schema'
import { api, command, read, type ApiQuery, type ApiResult, type CommandOptions, type ReadOptions } from './core'

export const getEvidencePack = (runId: string, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/evidence-pack', { params: { path: { runId } }, signal: o?.signal }))
export const generateEvidencePack = (runId: string, o?: CommandOptions) =>
  command<ApiResult<'generateEvidencePack'>>(null, o, (h) =>
    api().POST('/runs/{runId}/evidence-pack', { params: { path: { runId }, header: { 'Idempotency-Key': h['Idempotency-Key'] } }, signal: o?.signal }))
/** The pack is audited server-side. Returns the zip and the suggested file name (Content-Disposition). */
export async function downloadEvidencePack(runId: string, o?: ReadOptions): Promise<{ blob: Blob; filename?: string }> {
  const r = await api().GET('/runs/{runId}/evidence-pack/download', { params: { path: { runId } }, parseAs: 'blob', signal: o?.signal })
  const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(r.response.headers.get('Content-Disposition') ?? '')
  return { blob: r.data as Blob, filename: m ? decodeURIComponent(m[1]!) : undefined }
}
export const listAuditEvents = (query: ApiQuery<'listAuditEvents'> = {}, o?: ReadOptions) =>
  read(api().GET('/audit-events', { params: { query }, signal: o?.signal }))
export type LineageSubjectKind = operations['getLineage']['parameters']['path']['subjectKind']
export const getLineage = (subjectKind: LineageSubjectKind, subjectId: string, query: ApiQuery<'getLineage'> = {}, o?: ReadOptions) =>
  read(api().GET('/lineage/{subjectKind}/{subjectId}', { params: { path: { subjectKind, subjectId }, query }, signal: o?.signal }))
