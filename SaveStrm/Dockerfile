FROM node:24-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM node:24-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3001

COPY package*.json ./
RUN npm ci --omit=dev

COPY --from=build /app/dist ./dist
COPY server ./server
COPY src ./src

RUN mkdir -p /app/data

EXPOSE 3001
CMD ["node", "server/index.js"]
