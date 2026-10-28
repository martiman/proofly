import type { components } from '../generated/schema'

export type PageInfo = components['schemas']['PageInfo']
export interface Page<T> { items: T[]; page: PageInfo }
export interface PageQuery { limit?: number; cursor?: string; includeTotal?: boolean }
export type PageFetcher<T> = (query: PageQuery, signal?: AbortSignal) => Promise<Page<T>>

export const hasNext = (page: PageInfo): boolean => page.hasMore && !!page.nextCursor

/** Async iterator over pages following `page.nextCursor`. Stops on the last page or after `maxPages` (safety valve). */
export async function* iteratePages<T>(fetchPage: PageFetcher<T>, opts: { limit?: number; includeTotal?: boolean; signal?: AbortSignal; maxPages?: number } = {}): AsyncGenerator<Page<T>> {
  let cursor: string | undefined
  for (let n = 0; n < (opts.maxPages ?? 1000); n++) {
    const p = await fetchPage({ limit: opts.limit, cursor, includeTotal: n === 0 ? opts.includeTotal : undefined }, opts.signal)
    yield p
    if (!hasNext(p.page)) return
    cursor = p.page.nextCursor!
  }
}

/** Async iterator over single items across pages. */
export async function* iterateItems<T>(fetchPage: PageFetcher<T>, opts: Parameters<typeof iteratePages>[1] = {}): AsyncGenerator<T> {
  for await (const p of iteratePages(fetchPage, opts)) yield* p.items
}
