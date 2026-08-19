import { useEffect, useState } from 'react'

export function useBufferedImageSource(identity: string, requestedUrl: string, onError: () => void) {
  const [displayed, setDisplayed] = useState({ identity, url: requestedUrl })

  useEffect(() => {
    if (displayed.identity !== identity) {
      setDisplayed({ identity, url: requestedUrl })
      return
    }
    if (displayed.url === requestedUrl) return

    let cancelled = false
    const image = new window.Image()
    image.onload = () => {
      if (!cancelled) setDisplayed({ identity, url: requestedUrl })
    }
    image.onerror = () => {
      if (!cancelled) onError()
    }
    image.src = requestedUrl
    return () => { cancelled = true }
  }, [displayed.identity, displayed.url, identity, onError, requestedUrl])

  return displayed.identity === identity ? displayed.url : requestedUrl
}
