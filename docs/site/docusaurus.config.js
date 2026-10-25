const {readFileSync} = require('node:fs');
const path = require('node:path');

const versions = JSON.parse(readFileSync(path.join(__dirname, 'versions.json'), 'utf8'));
const repository = process.env.GITHUB_REPOSITORY || 'happticde/gamehub';
const [owner, project] = repository.split('/');
const baseUrl = process.env.DOCS_BASE_URL || `/${project}/`;
const siteUrl = process.env.DOCS_URL || `https://${owner}.github.io`;
const previewCommit = process.env.DOCS_PREVIEW_SHA || '';
if (previewCommit && !/^[a-f0-9]{40}$/.test(previewCommit)) throw new Error('Invalid preview commit');
if (previewCommit && !baseUrl.endsWith('/dev/')) throw new Error('Dev preview needs its own /dev/ base path');

/** @type {import('@docusaurus/types').Config} */
module.exports = {
  title: 'GameHub handbook',
  tagline: 'Connect your libraries. Understand your collection.',
  url: siteUrl,
  baseUrl,
  customFields: {previewCommit},
  ...(previewCommit ? {headTags: [{tagName: 'meta', attributes: {name: 'robots', content: 'noindex, nofollow'}}]} : {}),
  favicon: 'img/gamehub.svg',
  organizationName: owner,
  projectName: project,
  trailingSlash: true,
  onBrokenLinks: 'throw',
  markdown: {format: 'detect', mermaid: true, hooks: {onBrokenMarkdownLinks: 'throw', onBrokenMarkdownImages: 'throw'}},
  i18n: {defaultLocale: 'en', locales: ['en']},
  themes: ['@docusaurus/theme-mermaid', ['@easyops-cn/docusaurus-search-local', {
    hashed: true, language: 'en', indexBlog: false, indexPages: false, docsRouteBasePath: '/',
    forceIgnoreNoIndex: true,
    docsDir: ['.generated/current', ...versions.map(version => `versioned_docs/version-${version}`)],
    highlightSearchTermsOnTargetPage: true, explicitSearchResultPath: true,
  }]],
  presets: [['classic', {
    docs: {
      path: '.generated/current', routeBasePath: '/', sidebarPath: './.generated/sidebar.json',
      lastVersion: versions[0] || 'current',
      versions: {
        current: {label: 'Next (unreleased)', path: 'next', banner: 'unreleased', noIndex: true},
        ...Object.fromEntries(versions.map((version, index) => [version, {
          label: `v${version}`, path: version, banner: index === 0 ? 'none' : 'unmaintained',
        }])),
      },
    },
    blog: false,
    theme: {customCss: './src/css/custom.css'},
  }]],
  themeConfig: {
    ...(previewCommit ? {announcementBar: {
      id: 'dev-preview', isCloseable: false,
      content: `Dev preview · ${previewCommit.slice(0, 7)} · Unreleased documentation`,
    }} : {}),
    colorMode: {defaultMode: 'light', respectPrefersColorScheme: true},
    navbar: {
      title: 'GameHub', logo: {alt: '', src: 'img/gamehub.svg'},
      items: [
        ...(previewCommit ? [{href: new URL(baseUrl.slice(0, -4), siteUrl).href, label: 'Published handbook', position: 'left'}] : []),
        {type: 'docSidebar', sidebarId: 'handbook', position: 'left', label: 'Handbook'},
        {type: 'docsVersionDropdown', position: 'right', dropdownActiveClassDisabled: true},
        {href: `https://github.com/${repository}`, label: 'GitHub', position: 'right'},
      ],
    },
    footer: {style: 'dark', copyright: 'GameHub · Handbook content: AGPL-3.0-only. Website tooling: MIT.',
      links: [{title: 'Licensing', items: [
        {label: 'Licenses and source', href: `https://github.com/${repository}/blob/main/licensing/README.md`},
        {label: 'Third-party notices', href: 'pathname:///legal/THIRD_PARTY_NOTICES.txt'},
        {label: 'Handbook license (AGPLv3)', href: 'pathname:///legal/AGPL-3.0-only.txt'},
        {label: 'Website license (MIT)', href: 'pathname:///legal/MIT.txt'},
      ]}]},
    tableOfContents: {minHeadingLevel: 2, maxHeadingLevel: 3},
    prism: {additionalLanguages: ['bash', 'csharp', 'go', 'json', 'powershell', 'yaml']},
  },
};
