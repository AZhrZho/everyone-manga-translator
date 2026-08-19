import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { AlertTriangle, CircleHelp, Info, X } from 'lucide-react'
import type { ReactNode } from 'react'
import type { Locale } from './i18n'
import { dialogService, type ActiveDialogRequest } from './dialogService'

export function DialogProvider({ children, locale, theme }: { children: ReactNode; locale: Locale; theme: 'dark' | 'light' }) {
  const request = useSyncExternalStore(dialogService.subscribe, dialogService.getCurrent, dialogService.getCurrent)
  return (
    <>
      {children}
      {request && <DialogModal key={request.id} request={request} locale={locale} theme={theme} />}
    </>
  )
}

function DialogModal({ request, locale, theme }: { request: ActiveDialogRequest; locale: Locale; theme: 'dark' | 'light' }) {
  const { kind, options } = request
  const [value, setValue] = useState(options.defaultValue ?? '')
  const dialogRef = useRef<HTMLElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const confirmRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    if (kind === 'prompt') inputRef.current?.focus()
    else confirmRef.current?.focus()
    return () => previousFocus?.focus()
  }, [kind])

  const isEnglish = locale === 'en-US'
  const title = options.title ?? (kind === 'prompt' ? (isEnglish ? 'Enter value' : '输入内容') : kind === 'alert' ? (isEnglish ? 'Notice' : '提示') : (isEnglish ? 'Confirm action' : '确认操作'))
  const confirmLabel = options.confirmLabel ?? (kind === 'prompt' ? (isEnglish ? 'Save' : '保存') : kind === 'alert' ? (isEnglish ? 'OK' : '知道了') : (isEnglish ? 'Confirm' : '确认'))
  const cancelLabel = options.cancelLabel ?? (isEnglish ? 'Cancel' : '取消')

  function cancel() {
    dialogService.settle(kind === 'prompt' ? null : false)
  }

  function confirm() {
    dialogService.settle(kind === 'prompt' ? value : true)
  }

  return (
    <div
      className={`emt-dialog-backdrop theme-${theme}`}
      role="presentation"
      onMouseDown={event => {
        if (event.target === event.currentTarget && kind !== 'alert') cancel()
      }}
      onKeyDown={event => {
        if (event.key === 'Escape') { event.preventDefault(); cancel() }
        if (event.key === 'Enter' && kind !== 'alert' && event.target !== inputRef.current) { event.preventDefault(); confirm() }
        if (event.key === 'Tab') {
          const focusable = [...(dialogRef.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])') ?? [])]
          if (focusable.length === 0) return
          const first = focusable[0]
          const last = focusable[focusable.length - 1]
          if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
          else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
        }
      }}
    >
      <section ref={dialogRef} className={`emt-dialog ${options.destructive ? 'destructive' : ''}`} role="dialog" aria-modal="true" aria-labelledby={`emt-dialog-title-${request.id}`}>
        <header className="emt-dialog-header">
          <span className="emt-dialog-icon">{options.destructive || kind === 'alert' ? <AlertTriangle size={18}/> : kind === 'prompt' ? <Info size={18}/> : <CircleHelp size={18}/>}</span>
          <h2 id={`emt-dialog-title-${request.id}`}>{title}</h2>
          <button className="emt-dialog-close" onClick={cancel} aria-label={isEnglish ? 'Close' : '关闭'}><X size={16}/></button>
        </header>
        <div className="emt-dialog-body">
          <p className="emt-dialog-message">{options.message}</p>
          {options.detail && <p className="emt-dialog-detail">{options.detail}</p>}
          {kind === 'prompt' && <label className="emt-dialog-input-label"><span>{options.inputLabel ?? (isEnglish ? 'Value' : '内容')}</span><input ref={inputRef} value={value} placeholder={options.inputPlaceholder} onChange={event => setValue(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); confirm() } }}/></label>}
        </div>
        <footer className="emt-dialog-actions">
          {kind !== 'alert' && <button onClick={cancel}>{cancelLabel}</button>}
          <button ref={confirmRef} className={options.destructive ? 'danger' : 'primary'} onClick={confirm}>{confirmLabel}</button>
        </footer>
      </section>
    </div>
  )
}
