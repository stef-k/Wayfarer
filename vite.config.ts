import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

const frontendApps = {
  'trip-editor': 'ClientApps/trip-editor/src/main.ts',
  'trip-viewer': 'ClientApps/trip-viewer/src/main.ts'
} as const;

type FrontendAppName = keyof typeof frontendApps;

export default defineConfig(({ mode }) => {
  const appName: FrontendAppName = mode === 'trip-viewer' ? 'trip-viewer' : 'trip-editor';

  return {
    plugins: [vue()],
    server: {
      port: 5173,
      strictPort: true,
      watch: {
        ignored: ['**/.local/**', '**/playwright-report/**', '**/test-results/**']
      }
    },
    build: {
      outDir: `wwwroot/vite/${appName}`,
      emptyOutDir: true,
      manifest: 'manifest.json',
      rollupOptions: {
        input: frontendApps[appName]
      }
    }
  };
});
