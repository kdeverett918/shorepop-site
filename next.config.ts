import type { NextConfig } from 'next'

const nextConfig: NextConfig = {
  // Marketing site: fully static, published from ./out by Render.
  output: 'export',
  images: { unoptimized: true },
  reactStrictMode: true,
  poweredByHeader: false,
  // Keep module resolution inside this repo (the home directory has its own lockfile).
  turbopack: { root: __dirname },
}

export default nextConfig
