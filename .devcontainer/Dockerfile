FROM mcr.microsoft.com/devcontainers/dotnet:10.0-preview-trixie-slim

RUN curl -sS https://dl.yarnpkg.com/debian/pubkey.gpg | gpg --dearmor > /usr/share/keyrings/yarn-archive-keyring.gpg   

# Features for Node.js and Go are configured in devcontainer.json
# [Optional] Uncomment this line to install global node packages.
# RUN su vscode -c "source /usr/local/share/nvm/nvm.sh && npm install -g <your-package-here>" 2>&1