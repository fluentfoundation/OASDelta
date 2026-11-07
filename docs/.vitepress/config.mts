import { defineConfig } from 'vitepress'

const repo = 'https://github.com/fluentfoundation/OASDelta'

export default defineConfig({
  title: 'OasDelta',
  description:
    'OpenAPI diff and breaking-change detection for .NET: a byte-compatible C# port of oasdiff, compiled to a single NativeAOT executable.',
  base: '/OASDelta/',
  cleanUrls: true,
  lastUpdated: true,
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: '/OASDelta/logo.svg' }],
    ['meta', { name: 'theme-color', content: '#512bd4' }],
  ],
  themeConfig: {
    logo: '/logo.svg',
    nav: [
      { text: 'Guide', link: '/guide/getting-started', activeMatch: '/guide/' },
      { text: 'Benchmarks', link: '/benchmarks' },
      { text: 'Why .NET', link: '/why-dotnet' },
    ],
    sidebar: [
      {
        text: 'Guide',
        items: [
          { text: 'Getting started', link: '/guide/getting-started' },
          { text: 'Commands & flags', link: '/guide/usage' },
          { text: 'Compatibility with oasdiff', link: '/guide/compatibility' },
        ],
      },
      {
        text: 'Performance',
        items: [
          { text: 'Benchmarks', link: '/benchmarks' },
          { text: 'Why C# and .NET', link: '/why-dotnet' },
        ],
      },
    ],
    socialLinks: [{ icon: 'github', link: repo }],
    editLink: {
      pattern: `${repo}/edit/main/docs/:path`,
      text: 'Edit this page on GitHub',
    },
    search: { provider: 'local' },
    footer: {
      message:
        'Apache License 2.0. OasDelta is a derivative work of <a href="https://github.com/oasdiff/oasdiff">oasdiff</a> and is not affiliated with or endorsed by the oasdiff project.',
      copyright: '© Fluent Foundation',
    },
  },
})
