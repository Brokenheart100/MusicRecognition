# syntax=docker/dockerfile:1
FROM node:24.15.0-bookworm-slim AS build
WORKDIR /src
COPY MusicRecognition.Web/package*.json ./
COPY MusicRecognition.Web/scripts/ scripts/
RUN npm ci --ignore-scripts
COPY MusicRecognition.Web/ ./
RUN npm run build
FROM node:24.15.0-bookworm-slim
ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
WORKDIR /app
COPY --from=build --chown=node:node /src/.output ./
USER node
EXPOSE 3000
CMD ["node", "server/index.mjs"]
