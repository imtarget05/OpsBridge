import { onRequest as __api_v1___path___ts_onRequest } from "/Users/mainguyenbinhtan/Downloads/OpsBridge/src/OpsBridge.Web/functions/api/v1/[[path]].ts"

export const routes = [
    {
      routePath: "/api/v1/:path*",
      mountPath: "/api/v1",
      method: "",
      middlewares: [],
      modules: [__api_v1___path___ts_onRequest],
    },
  ]