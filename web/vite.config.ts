import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: {
    // 直接产出到宿主的 wwwroot，dotnet build 会把它拷到 exe 旁边
    outDir: fileURLToPath(new URL('../src/DiskLens/wwwroot', import.meta.url)),
    emptyOutDir: true,
    target: 'es2022',
    chunkSizeWarningLimit: 1500,
  },
  server: { port: 5173, strictPort: true },
});
