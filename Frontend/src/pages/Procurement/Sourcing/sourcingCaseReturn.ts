/**
 * The handshake between a sourcing case and the supplier form it opens.
 *
 * The case sends the rep to `/suppliers?new=1&tags=<part>&returnTo=<case>`; on save the
 * suppliers page sends them back to `returnTo` with this flag appended, and the case runs the
 * candidate search itself so the new supplier is listed without a further click. The flag is
 * stripped from the address as soon as it has been acted on, so a reload does not search again.
 */
export const REFRESH_ON_RETURN_PARAM = 'refresh';

export const withRefreshOnReturn = (path: string): string =>
  `${path}${path.includes('?') ? '&' : '?'}${REFRESH_ON_RETURN_PARAM}=1`;
