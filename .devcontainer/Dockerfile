FROM node:24.15.0-bookworm-slim AS node
FROM golang:1.26.4-bookworm AS go

FROM mcr.microsoft.com/devcontainers/dotnet:10.0-preview-trixie-slim

COPY --from=node /usr/local/bin/ /usr/local/bin/
COPY --from=node /usr/local/lib/node_modules/ /usr/local/lib/node_modules/
COPY --from=go /usr/local/go/ /usr/local/go/

RUN ln -s /usr/local/go/bin/go /usr/local/bin/go \
    && ln -s /usr/local/go/bin/gofmt /usr/local/bin/gofmt
