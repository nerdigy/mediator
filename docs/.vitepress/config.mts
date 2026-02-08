import { defineConfig } from 'vitepress';

const repositoryName = process.env.GITHUB_REPOSITORY?.split('/')[1] ?? '';
const isUserOrOrgSite = repositoryName.endsWith('.github.io');
const base = process.env.GITHUB_ACTIONS
  ? isUserOrOrgSite
    ? '/'
    : `/${repositoryName}/`
  : '/';

export default defineConfig({
  base,
  title: 'Nerdigy.Mediator',
  description: 'Mediator library for .NET applications',
  lang: 'en-US',
  lastUpdated: true,
  appearance: 'force-dark',
  head: [
    ['link', { rel: 'preconnect', href: 'https://fonts.googleapis.com' }],
    ['link', { rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: '' }]
  ],
  markdown: {
    theme: 'tokyo-night'
  },
  themeConfig: {
    siteTitle: 'Nerdigy.Mediator',
    nav: [],
    sidebar: [
      {
        text: 'Start Here',
        items: [
          { text: 'Getting Started', link: '/guide/getting-started' },
          { text: 'Dependency Injection', link: '/guide/dependency-injection' }
        ]
      },
      {
        text: 'Core Messaging',
        items: [
          { text: 'Requests', link: '/guide/requests' },
          { text: 'Notifications', link: '/guide/notifications' },
          { text: 'Streaming', link: '/guide/streaming' }
        ]
      },
      {
        text: 'Pipelines And Errors',
        items: [
          { text: 'Pipelines And Processors', link: '/guide/pipelines' },
          { text: 'Exception Handling', link: '/guide/exception-handling' }
        ]
      },
      {
        text: 'Operations',
        items: [
          { text: 'Performance', link: '/guide/performance' },
          { text: 'Troubleshooting', link: '/guide/troubleshooting' }
        ]
      },
      {
        text: 'Reference',
        items: [
          { text: 'Contracts', link: '/api/contracts' },
          { text: 'Runtime Behavior', link: '/api/runtime-behavior' }
        ]
      }
    ],
    footer: {
      message: 'Mediator runtime for .NET',
      copyright: 'Nerdigy'
    },
    outline: {
      level: [2, 3]
    }
  }
});
