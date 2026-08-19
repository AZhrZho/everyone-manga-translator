import type {
  CraftConnectedComponent,
  CraftConnectedComponents,
} from './types'

export type PixelConnectivity = 4 | 8

/** Labels a row-major binary image in the same top-left scan order as OpenCV. */
export function labelConnectedComponents(
  binary: Uint8Array,
  width: number,
  height: number,
  connectivity: PixelConnectivity,
): CraftConnectedComponents {
  validateDimensions(binary, width, height)
  if (connectivity !== 4 && connectivity !== 8) {
    throw new RangeError('Connected-component connectivity must be either 4 or 8.')
  }

  const labels = new Int32Array(binary.length)
  const queue = new Uint32Array(binary.length)
  const components: CraftConnectedComponent[] = []
  let nextLabel = 1

  for (let seed = 0; seed < binary.length; seed += 1) {
    if (binary[seed] === 0 || labels[seed] !== 0) {
      continue
    }

    let head = 0
    let tail = 0
    let left = seed % width
    let right = left
    let top = Math.floor(seed / width)
    let bottom = top
    labels[seed] = nextLabel
    queue[tail] = seed
    tail += 1

    while (head < tail) {
      const index = queue[head]
      head += 1
      const x = index % width
      const y = Math.floor(index / width)
      left = Math.min(left, x)
      right = Math.max(right, x)
      top = Math.min(top, y)
      bottom = Math.max(bottom, y)

      visitNeighbors(x, y, width, height, connectivity, (neighbor) => {
        if (binary[neighbor] !== 0 && labels[neighbor] === 0) {
          labels[neighbor] = nextLabel
          queue[tail] = neighbor
          tail += 1
        }
      })
    }

    components.push({
      label: nextLabel,
      area: tail,
      left,
      top,
      width: right - left + 1,
      height: bottom - top + 1,
      pixels: queue.slice(0, tail),
    })
    nextLabel += 1
  }

  return { labels, components, width, height }
}

export function visitNeighbors(
  x: number,
  y: number,
  width: number,
  height: number,
  connectivity: PixelConnectivity,
  visitor: (index: number) => void,
): void {
  if (x > 0) {
    visitor(y * width + x - 1)
  }
  if (x + 1 < width) {
    visitor(y * width + x + 1)
  }
  if (y > 0) {
    visitor((y - 1) * width + x)
  }
  if (y + 1 < height) {
    visitor((y + 1) * width + x)
  }
  if (connectivity === 4) {
    return
  }
  if (x > 0 && y > 0) {
    visitor((y - 1) * width + x - 1)
  }
  if (x + 1 < width && y > 0) {
    visitor((y - 1) * width + x + 1)
  }
  if (x > 0 && y + 1 < height) {
    visitor((y + 1) * width + x - 1)
  }
  if (x + 1 < width && y + 1 < height) {
    visitor((y + 1) * width + x + 1)
  }
}

function validateDimensions(binary: Uint8Array, width: number, height: number): void {
  if (!Number.isInteger(width) || width < 1 || !Number.isInteger(height) || height < 1) {
    throw new RangeError('Connected-component dimensions must be positive integers.')
  }
  if (binary.length !== width * height) {
    throw new RangeError('Connected-component input length does not match its dimensions.')
  }
}
