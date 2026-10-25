import React from 'react';
import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import versions from '../../versions.json';

export default function Home() {
  const current = versions[0] || 'next';
  const handbook = useBaseUrl(`/${current}/`);
  const install = useBaseUrl(`/${current}/guides/installation/`);
  return (
    <Layout title="Your collection, understood" description="Install, use, and operate GameHub with a handbook matched to your release.">
      <main className="handbook-home">
        <div className="handbook-intro">
          <p className="handbook-kicker">THE GAMEHUB HANDBOOK</p>
          <h1>Every library.<br />One place to call home.</h1>
          <p className="handbook-lead">Bring Steam, PlayStation, GOG, Epic, and your physical games together. Start with your first import, or find the details to run your own GameHub.</p>
          <div className="handbook-actions">
            <Link className="button button--primary button--lg" to={install}>Set up GameHub</Link>
            <Link className="button button--outline button--secondary button--lg" to={handbook}>Browse the handbook</Link>
          </div>
          <p className="handbook-version">{versions.length ? `Reading v${current}. Use the version menu for earlier releases or Next.` : 'Next · Unreleased documentation. Published releases will appear in the version menu.'}</p>
        </div>
        <nav className="handbook-shelves" aria-label="Explore the handbook">
          {[
            ['Your first library', 'Installation, account setup, and the first successful sync.', 'guides/first-library'],
            ['Your collection', 'Find games, fix matches, track progress, and protect your library.', 'guides/library'],
            ['Your installation', 'Deployment, authentication, backups, upgrades, and troubleshooting.', 'guides/deployment'],
            ['Find help', 'Fix login, connection helpers, missing games, and import failures.', 'guides/troubleshooting'],
          ].map(([title, description, route]) => (
            <Link key={route} className="handbook-shelf" to={`${handbook}${route}/`}>
              <h2>{title}</h2><p>{description}</p><span aria-hidden="true">↗</span>
            </Link>
          ))}
        </nav>
      </main>
    </Layout>
  );
}
