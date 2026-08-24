/**
 * Development. Empty base means same-origin, which the dev server turns into a proxy to the API
 * on 5172 - see proxy.conf.json. Keeping it relative means no CORS in the common case and no
 * hard-coded port in the built output.
 */
export const environment = {
  production: false,
  apiBase: '',
};
