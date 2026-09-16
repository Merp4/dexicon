/**
 * The API token, and only that.
 *
 * Split out of api.ts because the generated client's runtime config needs it, and
 * importing api.ts from there would be a cycle: api.ts imports the generated client,
 * which imports the config, which would import api.ts.
 *
 * sessionStorage rather than a cookie: no cookie means no CSRF surface, and the token
 * dies with the tab rather than outliving the person using it.
 */
const TOKEN_KEY = 'dexicon.token';

export function getToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null; // private mode, blocked storage
  }
}

export function setToken(token: string | null) {
  try {
    if (token) sessionStorage.setItem(TOKEN_KEY, token);
    else sessionStorage.removeItem(TOKEN_KEY);
  } catch {
    /* non-fatal: the app still works for this page load */
  }
}
