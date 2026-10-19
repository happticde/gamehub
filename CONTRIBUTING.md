# Contributing to GameHub

Open a pull request against `main`; do not push directly to it. Explain the
problem, the change, and the verification performed. Follow the development
guide in [docs/guides/development.md](docs/guides/development.md).

## Licensing contributions

Unless a file already has a different license notice, contributions to GameHub
are submitted under **AGPL-3.0-only**. Contributions to the independent website
implementation/tooling in `docs/site` are submitted under **MIT**; handbook
content sourced from the rest of `docs` remains AGPL-3.0-only.

Only submit work you have the right to contribute under the applicable terms.
Identify copied or adapted code and preserve its copyright and license notices.
Include the source and exact version for newly introduced third-party material.
Review both direct and transitive dependency licenses when changing dependencies.

Contributors retain their rights. This policy does not assign copyright or grant
the maintainer unrestricted relicensing rights. A future proprietary license or
incompatible relicensing may require separate permission from contributors.

## Source availability and releases

Keep the license, generated third-party notices, dependency license checks, and source references
current. See [licensing/README.md](licensing/README.md) for the review procedure.
Modified deployments must offer the corresponding source for the version they
actually run where the AGPL requires it. A link to an unmodified upstream build
does not cover unpublished local changes.

Never include credentials, personal library exports, databases, or local
environment configuration in a contribution or published source archive.
