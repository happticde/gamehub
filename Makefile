# Documentation commands only. Keep product build/release automation in its existing workflows.
.DEFAULT_GOAL := help

DOCS_PORT ?= 3001
DOCS_VERSION ?=
DOCS_CONTAINER ?= gamehub_devcontainer-app-1
DOCS_WORKSPACE ?= /workspaces/gamehub

MAKEFILE_ROOT := $(dir $(abspath $(lastword $(MAKEFILE_LIST))))
IN_CONTAINER := $(shell test -f /.dockerenv && printf yes)

# npm always runs in the devcontainer, including when make is called from the host.
ifeq ($(IN_CONTAINER),yes)
DOCS_NPM = cd "$(MAKEFILE_ROOT)docs/site" && env DOCS_CANDIDATE_VERSION="$(DOCS_VERSION)" npm
else
# A TTY lets Ctrl+C stop an interactive preview; noninteractive checks need no TTY.
DOCKER_TTY := $(shell test -t 0 && test -t 1 && printf '%s' '-it')
DOCS_NPM = docker exec $(DOCKER_TTY) -e DOCS_CANDIDATE_VERSION="$(DOCS_VERSION)" -w "$(DOCS_WORKSPACE)/docs/site" "$(DOCS_CONTAINER)" npm
endif

.PHONY: help docs-install docs-test docs-check docs-build docs-preview docs-dev

help:
	@printf '%s\n' \
	  'GameHub documentation (run from the repository root)' \
	  '' \
	  '  make docs-install                 Install locked documentation dependencies' \
	  '  make docs-test                    Run documentation tooling tests' \
	  '  make docs-check                   Run tests and the complete production build' \
	  '  make docs-build                   Build the static documentation' \
	  '  make docs-preview                 Build and serve the production site, including search' \
	  '  make docs-dev                     Start the development preview' \
	  '' \
	  '  make docs-preview DOCS_VERSION=0.1.0  Also preview a local numbered candidate' \
	  '  make docs-preview DOCS_PORT=3002      Use a different port' \
	  '' \
	  'Forward DOCS_PORT from the devcontainer, then open http://localhost:3001/gamehub/' \
	  '(replace 3001 when overriding DOCS_PORT). Stop the preview with Ctrl+C.' \
	  'No target publishes documentation, creates a release, or starts application services.'

docs-install:
	$(DOCS_NPM) ci --no-fund

docs-test:
	$(DOCS_NPM) test

docs-check:
	$(DOCS_NPM) run check

docs-build:
	$(DOCS_NPM) run build

docs-preview: docs-build
	@printf 'Forward container port %s, then open http://localhost:%s/gamehub/\n' "$(DOCS_PORT)" "$(DOCS_PORT)"
	$(DOCS_NPM) run serve -- --port "$(DOCS_PORT)"

docs-dev:
	@printf 'Forward container port %s, then open http://localhost:%s/gamehub/\n' "$(DOCS_PORT)" "$(DOCS_PORT)"
	$(DOCS_NPM) start -- --port "$(DOCS_PORT)"
