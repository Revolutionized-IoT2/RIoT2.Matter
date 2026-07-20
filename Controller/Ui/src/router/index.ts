/**
 * router/index.ts
 *
 * Application routing. Registered as a plugin in `src/plugins`.
 * Views live under `src/presentation/views` and are lazy-loaded.
 */

import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'

const routes: readonly RouteRecordRaw[] = [
  {
    path: '/',
    name: 'home',
    component: () => import('@/presentation/views/HomeView.vue'),
  },
  {
    path: '/devices/add',
    name: 'add-device',
    component: () => import('@/presentation/views/AddDeviceView.vue'),
  },
  {
    path: '/devices/:nodeId',
    name: 'device-detail',
    component: () => import('@/presentation/views/DeviceDetailView.vue'),
  },
  {
    path: '/rooms',
    name: 'rooms',
    component: () => import('@/presentation/views/RoomsView.vue'),
  },
  {
    path: '/fabric',
    name: 'fabric',
    component: () => import('@/presentation/views/FabricView.vue'),
  },
]

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [...routes],
})