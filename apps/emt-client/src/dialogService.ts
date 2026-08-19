export type DialogKind = 'confirm' | 'alert' | 'prompt'

export type DialogOptions = {
  title?: string
  message: string
  detail?: string
  confirmLabel?: string
  cancelLabel?: string
  inputLabel?: string
  inputPlaceholder?: string
  defaultValue?: string
  destructive?: boolean
}

type DialogRequest = {
  id: number
  kind: DialogKind
  options: DialogOptions
  resolve: (value: boolean | string | null | void) => void
}

export type ActiveDialogRequest = DialogRequest

class DialogService {
  private sequence = 0
  private queue: DialogRequest[] = []
  private readonly listeners = new Set<() => void>()

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  getCurrent = (): DialogRequest | null => this.queue[0] ?? null

  private notify() {
    this.listeners.forEach(listener => listener())
  }

  private enqueue(kind: DialogKind, options: DialogOptions) {
    return new Promise<boolean | string | null | void>(resolve => {
      this.queue.push({ id: ++this.sequence, kind, options, resolve })
      this.notify()
    })
  }

  confirm(options: DialogOptions | string) {
    const normalized = typeof options === 'string' ? { message: options } : options
    return this.enqueue('confirm', normalized).then(value => value === true)
  }

  alert(options: DialogOptions | string) {
    const normalized = typeof options === 'string' ? { message: options } : options
    return this.enqueue('alert', normalized).then(() => undefined)
  }

  prompt(options: DialogOptions | string) {
    const normalized = typeof options === 'string' ? { message: options } : options
    return this.enqueue('prompt', normalized).then(value => typeof value === 'string' ? value : null)
  }

  settle(value: boolean | string | null | void) {
    const request = this.queue.shift()
    if (!request) return
    request.resolve(value)
    this.notify()
  }
}

export const dialogService = new DialogService()
