/**
 * Active ingestion polling backs off enough to avoid a tight loop, but never far enough for a
 * completed document to look stuck for another 30 seconds. This query only runs while the user is
 * watching one in-flight batch, so a five-second ceiling is bounded and keeps the progress honest.
 */
export const pollIntervalFor = (completedFetches: number): number =>
  Math.min(2000 * 2 ** Math.floor(Math.max(completedFetches, 0) / 3), 5000);
