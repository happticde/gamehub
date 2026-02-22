FROM mcr.microsoft.com/devcontainers/dotnet:10.0-preview-trixie-slim

RUN curl -sS https://dl.yarnpkg.com/debian/pubkey.gpg | gpg --dearmor > /usr/share/keyrings/yarn-archive-keyring.gpg   

# [Optional] Uncomment this section to install additional OS packages.
RUN apt-get update && apt-get install -y --no-install-recommends \
    golang-go \
    && rm -rf /var/lib/apt/lists/*
# [Optional] Uncomment this line to install global node packages.
# RUN su vscode -c "source /usr/local/share/nvm/nvm.sh && npm install -g <your-package-here>" 2>&1