/**
 * Deployment base path support. The app can be served from a site root ("/") or from a sub-path such as an IIS
 * virtual application ("/followup/"). The server writes the right `<base href>` into index.html, and every
 * app-absolute URL ("/api/…", "/hubs/…") is resolved against it here.
 */
export function appBasePath(): string {
  try {
    const path = new URL(document.baseURI).pathname;
    return path.endsWith('/') ? path : `${path}/`;
  } catch {
    return '/';
  }
}

/** Resolves an app-absolute path such as "/api/tasks" against the deployment base path ("/followup/api/tasks"). */
export function appUrl(path: string): string {
  if (!path.startsWith('/')) return path;
  return appBasePath() + path.slice(1);
}
