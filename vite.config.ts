import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    strictPort: true,
    watch: {
      ignored: ['**/.local/**', '**/playwright-report/**', '**/test-results/**']
    }
  },
  build: {
    outDir: 'wwwroot/vite/trip-editor',
    emptyOutDir: true,
    manifest: 'manifest.json',
    rollupOptions: {
      input: 'ClientApps/trip-editor/src/main.ts'
    }
  }
});
