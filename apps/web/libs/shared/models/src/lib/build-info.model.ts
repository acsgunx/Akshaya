/**
 * What GET /api/version returns — the identity of the API build that is
 * actually running, stamped at publish time.
 *
 * `commit` is the full git SHA, present whenever the build knew it (a git
 * checkout, or the Dockerfile's GIT_SHA build arg). It is the field that
 * answers "did that deploy land"; `version` alone only changes on a release
 * bump.
 */
export interface BuildInfo {
  readonly version: string;
  readonly commit?: string | null;
}

/** "v0.1.0+abc1234" — the short sha is enough to recognise a deploy. */
export function formatBuildInfo(info: BuildInfo): string {
  return `v${info.version}${info.commit ? `+${info.commit.slice(0, 7)}` : ''}`;
}
