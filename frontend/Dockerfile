FROM node:24-alpine AS base

# The named context is restricted by services/cli/.dockerignore.
FROM golang:1.26.9-bookworm AS helper-builder
WORKDIR /src
COPY --from=helper-source . .
ARG VERSION=dev
RUN sh scripts/package-macos-helper.sh /downloads "$VERSION"

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
COPY --from=helper-builder /downloads ./public/downloads/helper
# Environment variables needed during build can be passed as ARG
RUN npm run build

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
      org.opencontainers.image.source="https://github.com/happticde/gamehub"
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
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs

EXPOSE 3000
ENV PORT=3000
ENV HOSTNAME="0.0.0.0"

# server.js is created by next build from the standalone output
CMD ["node", "server.js"]
