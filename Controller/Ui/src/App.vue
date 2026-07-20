<template>
    <v-app>
        <AppBar />
        <v-main>
            <router-view />
        </v-main>
        <v-btn class="ma-2"
               icon="mdi-theme-light-dark"
               location="bottom right"
               position="fixed"
               @click="$vuetify.theme.cycle()" />
    </v-app>
</template>

<script lang="ts" setup>
import AppBar from '@/presentation/components/AppBar.vue'
</script>