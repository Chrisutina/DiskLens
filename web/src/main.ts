import { mount } from 'svelte';
import '@fontsource-variable/geist';
import '@fontsource-variable/geist-mono';
import App from './App.svelte';
import { applyCategoryCssVars } from './lib/categories.ts';
import { cleanupTasks, startCleanupTasks } from './lib/cleanupTasks.svelte.ts';
import { useTaskRunner } from './lib/state.svelte.ts';
import './styles/theme.css';

applyCategoryCssVars();
useTaskRunner({ get active() { return cleanupTasks.active; }, start: startCleanupTasks });
mount(App, { target: document.getElementById('app')! });
