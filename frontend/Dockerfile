FROM node:26-alpine AS base

# Compile a dependency-free notice collector for the native build platform.
FROM golang:1.26.9-bookworm AS notice-builder
COPY --from=helper-source tools/license-notices/main.go /license-notices.go
RUN CGO_ENABLED=0 go build -o /license-notices /license-notices.go

# The named context is restricted by services/cli/.dockerignore.
FROM golang:1.26.9-bookworm AS helper-builder
WORKDIR /src
COPY --from=helper-source go.mod go.sum ./
RUN go mod download
COPY --from=helper-source . .
ARG VERSION=dev
ARG REVISION=unknown
ARG SOURCE_REPOSITORY=jfk1987/gamehub
COPY --from=license-source . /licensing
RUN GAMEHUB_LICENSE_DIR=/licensing GAMEHUB_SOURCE_REVISION="$REVISION" \
    GAMEHUB_SOURCE_REPOSITORY="$SOURCE_REPOSITORY" sh scripts/package-macos-helper.sh /downloads "$VERSION"

# Install dependencies only when needed
FROM base AS deps
RUN apk add --no-cache libc6-compat
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

# Rebuild the source code only when needed
FROM base AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
# Source references are public build metadata, never application credentials.
ARG VERSION=dev
ARG REVISION=unknown
ARG SOURCE_REPOSITORY=jfk1987/gamehub
COPY --from=license-source . /licensing
COPY --from=notice-builder /license-notices /usr/local/bin/license-notices
ENV GAMEHUB_NOTICE_GENERATOR=/usr/local/bin/license-notices
RUN GAMEHUB_LICENSE_DIR=/licensing GAMEHUB_BUILD_VERSION="$VERSION" \
    GAMEHUB_SOURCE_REVISION="$REVISION" GAMEHUB_SOURCE_REPOSITORY="$SOURCE_REPOSITORY" npm run build

# Production image, copy all the files and run next
FROM base AS runner
# The standalone server runs directly with Node. Package managers are build tools;
# remove their bundled dependencies from the shipped runtime image.
RUN rm -rf /usr/local/lib/node_modules/npm /opt/yarn-v* \
    && rm -f /usr/local/bin/npm /usr/local/bin/npx \
       /usr/local/bin/yarn /usr/local/bin/yarnpkg
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.source="https://github.com/jfk1987/gamehub" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
WORKDIR /app

ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1

RUN addgroup --system --gid 1001 nodejs
RUN adduser --system --uid 1001 nextjs

# Set the correct permission for prerender cache
RUN mkdir .next
RUN chown nextjs:nodejs .next

# Copy necessary files from the builder
COPY --from=builder /app/public ./public
# Runtime-only downloads let BuildKit build Next.js and the Go helper independently.
COPY --from=helper-builder /downloads ./public/downloads/helper
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs

EXPOSE 3000
ENV PORT=3000
ENV HOSTNAME="0.0.0.0"

# server.js is created by next build from the standalone output
CMD ["node", "server.js"]
