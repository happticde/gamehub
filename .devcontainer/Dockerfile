FROM node:24.15.0-bookworm-slim AS node
FROM golang:1.27.3-bookworm AS go

FROM mcr.microsoft.com/devcontainers/dotnet:10.0-preview-trixie-slim

COPY --from=node /usr/local/bin/ /usr/local/bin/
COPY --from=node /usr/local/lib/node_modules/ /usr/local/lib/node_modules/
COPY --from=go /usr/local/go/ /usr/local/go/

COPY Directory.Packages.props /tmp/Directory.Packages.props

RUN DOTNET_EF_VERSION="$(dotnet msbuild /tmp/Directory.Packages.props -getItem:PackageVersion -nologo \
      | node -p 'JSON.parse(require("fs").readFileSync(0, "utf8")).Items.PackageVersion.find(p => p.Identity === "Microsoft.EntityFrameworkCore.Design").Version')" \
    && dotnet tool install --tool-path /usr/local/bin dotnet-ef --version "${DOTNET_EF_VERSION}" \
    && rm /tmp/Directory.Packages.props \
    && ln -s /usr/local/go/bin/go /usr/local/bin/go \
    && ln -s /usr/local/go/bin/gofmt /usr/local/bin/gofmt
