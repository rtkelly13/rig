// Response shapes of the /api/ops endpoints, mirroring the dataclasses in rig.otel_stack.web,
// rig.estate.web and desktop_manager.jobs.web. Field names are the wire format.

export interface LastRun {
  started: string;
  seconds: number;
  returncode: number | null;
  trigger: string;
  error: string | null;
  ok: boolean;
}

export interface JobView {
  name: string;
  description: string;
  cadence: string;
  triggers: string[];
  timeout_minutes: number;
  last: LastRun | null;
}

export interface RunView {
  job: string;
  run_id: string;
  started: string;
  seconds: number;
  returncode: number | null;
  trigger: string;
  error: string | null;
  ok: boolean;
  has_output: boolean;
  trace_id: string;
}

export interface RunOutput {
  job: string;
  run_id: string;
  output: string | null;
}

export interface TelemetryStatus {
  source: 'clickhouse' | 'ndjson';
  clickhouse_reachable: boolean;
  detail: string;
}

export interface ExplorerStatus {
  hyperdx_url: string;
  hyperdx_reachable: boolean;
  hyperdx_trace_url: string;
  aspire_url: string;
  aspire_reachable: boolean;
  aspire_trace_url: string;
}

export interface TelemetryView {
  store: TelemetryStatus;
  explorers: ExplorerStatus;
}

export interface TraceView {
  trace_id: string;
  service: string;
  root: string;
  start_ns: number;
  duration_ms: number;
  spans: number;
  errors: number;
}

export interface TraceListView {
  source: 'clickhouse' | 'ndjson';
  note: string;
  traces: TraceView[];
}

export interface Decision {
  kind: string;
  repo: string;
  path: string;
  reason: string;
  size_bytes: number;
  suggested_action: string;
  command: string;
}

export interface DecisionFeed {
  decisions: Decision[];
  written: string;
  note: string;
}

export interface DiskUsage {
  path: string;
  total: number;
  used: number;
  free: number;
}

export interface TrimCandidate {
  worktree: string;
  path: string;
  category: string;
  freeable: number;
  refused: string;
}

export interface TrimScan {
  scanned_at: string;
  candidates: TrimCandidate[];
  scanning: boolean;
  error: string;
}

export interface TrimResult {
  path: string;
  status: string;
  freeable: number;
}

export interface RunStarted {
  job: string | null;
  pid: number;
}

export class OpsApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init);
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      /* Not the JSON error envelope; keep the status line. */
    }
    throw new OpsApiError(response.status, message);
  }
  return (await response.json()) as T;
}

const get = <T>(path: string): Promise<T> => request<T>(path);

// The server accepts only same-origin JSON writes, so a form post from another site cannot trigger one.
const post = <T>(path: string, body: unknown): Promise<T> =>
  request<T>(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

const query = (params: Record<string, string | number | undefined>): string => {
  const search = new URLSearchParams();
  for (const [name, value] of Object.entries(params))
    if (value !== undefined && value !== '') search.set(name, String(value));
  return search.size ? `?${search}` : '';
};

export const opsApi = {
  jobs: () => get<JobView[]>('/api/ops/jobs'),
  runs: (job?: string, limit = 200) => get<RunView[]>(`/api/ops/runs${query({ job, limit })}`),
  startRun: (job: string | null) => post<RunStarted>('/api/ops/jobs/run', { job }),
  runOutput: (job: string, runId: string) =>
    get<RunOutput>(`/api/ops/runs/${encodeURIComponent(job)}/${encodeURIComponent(runId)}/output`),
  telemetry: () => get<TelemetryView>('/api/ops/telemetry/status'),
  traces: (days: number, service?: string) =>
    get<TraceListView>(`/api/ops/telemetry/traces${query({ days, service })}`),
  decisions: () => get<DecisionFeed>('/api/ops/decisions'),
  disk: () => get<DiskUsage>('/api/ops/estate/disk'),
  trim: (refresh: boolean) =>
    get<TrimScan>(`/api/ops/estate/trim${query({ refresh: refresh ? 'true' : undefined })}`),
  applyTrim: (paths: string[]) => post<TrimResult[]>('/api/ops/estate/trim/apply', { paths }),
};

export interface TraceLinks {
  primary: string;
  hyperdx: string;
  aspire: string;
}

// The server hands out URL templates, so only the browser combines a trace id with an explorer.
// HyperDX is preferred because it keeps history; Aspire is offered only while it answers, since it holds live data only.
export function traceLinks(explorers: ExplorerStatus | undefined, traceId: string): TraceLinks {
  if (!explorers || !traceId) return { primary: '', hyperdx: '', aspire: '' };
  const fill = (template: string) => template.replaceAll('{trace_id}', encodeURIComponent(traceId));
  const hyperdx = explorers.hyperdx_trace_url ? fill(explorers.hyperdx_trace_url) : '';
  const aspire =
    explorers.aspire_trace_url && explorers.aspire_reachable ? fill(explorers.aspire_trace_url) : '';
  const primary = explorers.hyperdx_reachable && hyperdx ? hyperdx : aspire || hyperdx;
  return { primary, hyperdx, aspire };
}

export interface AttentionItem {
  kind: 'job-failed' | 'decision';
  title: string;
  detail: string;
  job: string;
  runId: string;
  command: string;
}

// What needs a person: every job whose last run failed, then every open decision.
export function attentionItems(
  jobs: JobView[],
  runs: RunView[],
  feed: DecisionFeed,
): AttentionItem[] {
  const latest = new Map<string, RunView>();
  for (const run of runs) if (!latest.has(run.job)) latest.set(run.job, run);
  const failed = jobs.flatMap((job): AttentionItem[] =>
    job.last && !job.last.ok
      ? [
          {
            kind: 'job-failed',
            title: `${job.name} failed`,
            detail:
              job.last.error ?? `exit ${job.last.returncode ?? '?'} on the ${job.last.trigger} run`,
            job: job.name,
            runId: latest.get(job.name)?.run_id ?? '',
            command: '',
          },
        ]
      : [],
  );
  const decisions = feed.decisions.map(
    (decision): AttentionItem => ({
      kind: 'decision',
      title: `${decision.kind}: ${decision.repo}`,
      detail: `${decision.reason} (${decision.path})`,
      job: '',
      runId: '',
      command: decision.command,
    }),
  );
  return [...failed, ...decisions];
}
