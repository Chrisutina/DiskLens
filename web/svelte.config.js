import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

export default {
  preprocess: vitePreprocess(),
  // 自己的组件强制 runes 模式；第三方库按它们自己的来
  vitePlugin: {
    dynamicCompileOptions({ filename }) {
      if (!filename.includes('node_modules')) return { runes: true };
    },
  },
};
