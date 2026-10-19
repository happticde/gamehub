# Security policy

## Supported versions

Security fixes target `main` and the latest published GameHub release. Older
releases have no dedicated security maintenance. Before the first release,
`main` is the development version; use its latest commit when reporting a problem.
Security maintenance and responses are provided on a best-effort basis.

## Report a vulnerability privately

Open [GameHub security advisories](https://github.com/jfk1987/gamehub/security/advisories)
and choose **Report a vulnerability** to submit a private report to the maintainers.
This requires GitHub's private vulnerability reporting to be enabled for the repository.

If that button is unavailable, open an issue containing **only a request for a
private security contact channel**. Wait for a private channel before sharing
technical details. Do not disclose vulnerabilities in public issues, pull
requests, discussions, or their attachments.

Include, where possible:

- The affected release or full commit SHA and component (Core, Ingest, frontend,
  CLI/helper, or deployment tooling).
- The deployment mode and conditions required to reproduce the issue.
- Reproduction steps or a minimal proof of concept using synthetic data.
- The expected and observed behavior, impact, and any proposed mitigation.

Redact credentials, tokens, cookies, connection strings, personal information and
library data. Do not attach deployment environment files or real library backups.
Test only instances and data you own or have permission to assess.

The maintainers will assess the report, discuss mitigation and coordinate a fix
and disclosure through the private channel. Response and remediation times depend
on availability and the issue's severity; no fixed response time is promised.

## Dependency vulnerabilities

Dependabot alerts and the CI vulnerability gates complement private reports.
Temporary CI exceptions are recorded with their scope, reason and expiry in
[the security exception policy](.ci/security-exceptions.json); an exception does
not mean a vulnerability has been fixed. See the
[Dependabot maintenance guide](docs/dependabot.md) for setup and review steps.
