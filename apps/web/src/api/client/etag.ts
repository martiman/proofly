/**
 * Strong ETag store. `resources` maps a resource key (the GET path relative to the API base, for example
 * `/exceptions/F-019`) to its last seen ETag; mutations read it for `If-Match`. `bodies` backs 304 handling per full URL.
 */
export class EtagCache {
  private readonly resources = new Map<string, string>()
  private readonly bodies = new Map<string, { etag: string; body: string }>()
  private readonly max: number
  constructor(max = 500) { this.max = max }

  get(resourceKey: string): string | undefined { return this.resources.get(resourceKey) }
  set(resourceKey: string, etag: string | null | undefined): void {
    if (!etag) return
    this.resources.delete(resourceKey)
    this.resources.set(resourceKey, etag)
    this.trim(this.resources)
  }
  delete(resourceKey: string): void { this.resources.delete(resourceKey) }

  getBody(url: string): { etag: string; body: string } | undefined { return this.bodies.get(url) }
  setBody(url: string, etag: string, body: string): void {
    this.bodies.delete(url)
    this.bodies.set(url, { etag, body })
    this.trim(this.bodies)
  }

  clear(): void { this.resources.clear(); this.bodies.clear() }
  private trim(m: Map<string, unknown>) {
    while (m.size > this.max) m.delete(m.keys().next().value as string)
  }
}
