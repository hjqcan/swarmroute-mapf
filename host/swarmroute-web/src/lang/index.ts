import zh_CN from '@/lang/zh-cn.json'
import en_US from '@/lang/en.json'
import type { Lang } from '@/store/system'

export const languageList: { label: string; value: Lang }[] = [
  { label: '简体中文', value: 'zh-CN' },
  { label: 'English', value: 'en-US' },
]

export const languageMessage: Record<Lang, Record<string, string>> = {
  'zh-CN': zh_CN,
  'en-US': en_US,
}
