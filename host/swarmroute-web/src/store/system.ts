import { create } from 'zustand'
import { devtools, persist } from 'zustand/middleware'

export type Lang = 'zh-CN' | 'en-US'

/** System / UI preferences (language). Mirrors gurkirbshmi's system store. */
export interface SystemState {
  lang: Lang
  setLang: (lang: Lang) => void
}

export const useSystemState = create<SystemState>()(
  devtools(
    persist(
      (set) => ({
        lang:
          ((typeof window !== 'undefined' ? localStorage.getItem('lang') : null) as Lang | null) ||
          'zh-CN',
        setLang: (lang: Lang) => {
          if (typeof window !== 'undefined') localStorage.setItem('lang', lang)
          set({ lang })
        },
      }),
      {
        name: 'system',
        partialize: (state) => ({ lang: state.lang }),
      }
    )
  )
)
