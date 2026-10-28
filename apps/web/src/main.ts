import { createApp } from 'vue'
import Antd from 'ant-design-vue'
import 'ant-design-vue/dist/reset.css'
import './styles/global.css'
import App from './App.vue'
import router from './router'
import { configureApi } from './api/client'
import { markUnauthenticated } from './composables/useAuthState'

// Token source defaults to the dev provider (DEV builds only). SEAM: install an OIDC TokenProvider here in production.
configureApi({ onUnauthorized: () => markUnauthenticated() })

createApp(App).use(Antd).use(router).mount('#app')
