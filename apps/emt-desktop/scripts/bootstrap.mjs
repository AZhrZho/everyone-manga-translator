import { spawnSync } from 'node:child_process'

const npmCli = process.env.npm_execpath
const command = npmCli ? process.execPath : (process.platform === 'win32' ? 'npm.cmd' : 'npm')
const args = npmCli ? [npmCli, 'install'] : ['install']
const environment = {
  ...process.env,
  ELECTRON_MIRROR: process.env.ELECTRON_MIRROR ?? 'https://npmmirror.com/mirrors/electron/',
}

if ((environment.HTTPS_PROXY || environment.HTTP_PROXY) && !environment.ELECTRON_GET_USE_PROXY) {
  environment.ELECTRON_GET_USE_PROXY = 'true'
}

console.info(`Installing EMT desktop dependencies with Electron mirror: ${environment.ELECTRON_MIRROR}`)
const result = spawnSync(command, args, { env: environment, stdio: 'inherit', windowsHide: true })
process.exit(result.status ?? 1)
