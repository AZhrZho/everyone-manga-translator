const defaultMirror = 'https://npmmirror.com/mirrors/electron/'

process.env.ELECTRON_MIRROR ??= defaultMirror

if ((process.env.HTTPS_PROXY || process.env.HTTP_PROXY) && !process.env.ELECTRON_GET_USE_PROXY) {
  process.env.ELECTRON_GET_USE_PROXY = 'true'
}

console.info(`Electron download source: ${process.env.ELECTRON_MIRROR}`)
await import('../node_modules/electron/install.js')
