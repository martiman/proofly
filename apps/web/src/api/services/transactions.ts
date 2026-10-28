import { api, read, type ApiQuery, type ReadOptions } from './core'

export const listTransactions = (runId: string, query: ApiQuery<'listTransactions'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/transactions', { params: { path: { runId }, query }, signal: o?.signal }))
export const getTransaction = (runId: string, transactionId: string, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/transactions/{transactionId}', { params: { path: { runId, transactionId } }, signal: o?.signal }))
export const getTransactionLineage = (runId: string, transactionId: string, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/transactions/{transactionId}/lineage', { params: { path: { runId, transactionId } }, signal: o?.signal }))
export const listAccounts = (runId: string, query: ApiQuery<'listAccounts'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/accounts', { params: { path: { runId }, query }, signal: o?.signal }))
export const getAccountLedger = (runId: string, accountId: string, query: ApiQuery<'getAccountLedger'> = {}, o?: ReadOptions) =>
  read(api().GET('/runs/{runId}/accounts/{accountId}/ledger', { params: { path: { runId, accountId }, query }, signal: o?.signal }))
