import {
  Badge,
  Button,
  CodeBlock,
  DataTable,
  EmptyState,
  InlineAction,
  Select,
  Spinner,
  ToastProvider,
  usePolledResource,
  useToast,
} from '@rtkelly-labs/design-system';
import { useEffect, useState } from 'react';
import { opsApi, traceLinks, type JobView, type RunOutput, type RunView } from './opsApi';
import { ago, seconds, when } from './opsFormat';
import { OpsPage, Outcome, outcomeLabel, PanelState, Toolbar, TraceLinkButtons } from './OpsCommon';
import { navigate, updateQuery } from './router';
import type { Route } from './router';

export function runsUrl(job = '', runId = ''): string {
  const query = new URLSearchParams();
  if (job) query.set('job', job);
  if (runId) query.set('run', runId);
  return `/ops/runs${query.size ? `?${query}` : ''}`;
}

// The "run every due job" request, as opposed to one named job.
const DUE = '';

export function OpsJobs() {
  return (
    <ToastProvider>
      <JobsPage />
    </ToastProvider>
  );
}

function JobsPage() {
  const state = usePolledResource(opsApi.jobs);
  const toast = useToast();
  // A run starts in the background, so a button waits only for the server to accept it.
  const [starting, setStarting] = useState<string | null>(null);

  const start = (job: string) => {
    setStarting(job);
    opsApi
      .startRun(job === DUE ? null : job)
      .then(() =>
        toast.show({
          title: job === DUE ? 'Started a run of the due jobs' : `Started ${job}`,
          description:
            'It shows in Runs when it finishes. If a scheduled run is in progress, this one skips.',
          intent: 'success',
          action: { label: 'VIEW RUNS', onClick: () => navigate(runsUrl(job)) },
        }),
      )
      .catch((reason: unknown) =>
        toast.show({
          title: 'Could not start the run',
          description: reason instanceof Error ? reason.message : String(reason),
          intent: 'danger',
        }),
      )
      .finally(() => setStarting(null));
  };

  return (
    <OpsPage title="Jobs" lede="The desktop coordinator's jobs and how each one last went.">
      <Toolbar onRefresh={state.reload} loading={state.loading}>
        <Button size="sm" onClick={() => start(DUE)} disabled={starting !== null}>
          Run due jobs
        </Button>
      </Toolbar>
      <PanelState state={state}>
        {(jobs) => (
          <DataTable<JobView>
            caption="Coordinator jobs, in the order a run takes them"
            data={jobs}
            keyExtractor={(job) => job.name}
            columns={[
              {
                header: 'Job',
                accessor: (job) => (
                  <InlineAction className="font-mono" onClick={() => navigate(runsUrl(job.name))}>
                    {job.name}
                  </InlineAction>
                ),
                rowHeader: true,
              },
              { header: 'What it does', accessor: 'description' },
              {
                header: 'When',
                accessor: (job) => (
                  <span className="flex flex-wrap gap-1">
                    <Badge accent="quiet">{job.cadence}</Badge>
                    {job.triggers.map((trigger) => (
                      <Badge key={trigger} accent="secondary">
                        {trigger}
                      </Badge>
                    ))}
                  </span>
                ),
              },
              {
                header: 'Last run',
                accessor: (job) =>
                  job.last === null ? (
                    <span className="text-content-muted">never</span>
                  ) : (
                    <span className="flex flex-wrap items-center gap-2">
                      <Outcome ok={job.last.ok} label={outcomeLabel(job.last)} />
                      <span>{ago(job.last.started)}</span>
                      <span className="text-content-muted">{seconds(job.last.seconds)}</span>
                    </span>
                  ),
                enableSorting: true,
                sortValue: (job) => job.last?.started ?? '',
              },
              {
                header: 'Run',
                accessor: (job) => (
                  <Button
                    size="sm"
                    aria-label={`Run ${job.name} now`}
                    onClick={() => start(job.name)}
                    disabled={starting !== null}
                  >
                    Run now
                  </Button>
                ),
              },
            ]}
          />
        )}
      </PanelState>
    </OpsPage>
  );
}

const ALL = '*';

export function OpsRuns({ route }: { route: Route }) {
  const job = route.query.get('job') ?? '';
  const runId = route.query.get('run') ?? '';
  const jobs = usePolledResource(opsApi.jobs, { intervalMs: 0 });
  const telemetry = usePolledResource(opsApi.telemetry, { intervalMs: 60_000 });
  const runs = usePolledResource(() => opsApi.runs(job || undefined), {
    intervalMs: 30_000,
    key: job,
  });
  const options = [
    { label: 'All jobs', value: ALL },
    ...(jobs.data ?? []).map((j) => ({ label: j.name, value: j.name })),
  ];
  const explorers = telemetry.data?.explorers;

  return (
    <OpsPage title="Runs" lede="Every recorded job run, with its output and its trace.">
      <Toolbar onRefresh={runs.reload} loading={runs.loading}>
        <div className="w-64">
          <Select
            label="Job"
            options={options}
            value={job || ALL}
            onValueChange={(next) => navigate(runsUrl(next === ALL ? '' : next))}
          />
        </div>
      </Toolbar>
      {job && runId ? (
        <RunOutputView job={job} runId={runId} onClose={() => updateQuery('run', '')} />
      ) : null}
      <PanelState state={runs}>
        {(data) =>
          data.length === 0 ? (
            <EmptyState
              title="No runs recorded yet"
              description="The coordinator records every job run from its next tick on."
            />
          ) : (
            <DataTable<RunView>
              caption="Job runs, newest first"
              data={data}
              pageSize={25}
              keyExtractor={(run) => `${run.job}/${run.run_id}`}
              columns={[
                { header: 'Started', accessor: (run) => when(run.started), rowHeader: true },
                { header: 'Job', accessor: (run) => <span className="font-mono">{run.job}</span> },
                { header: 'Trigger', accessor: 'trigger' },
                {
                  header: 'Outcome',
                  accessor: (run) => <Outcome ok={run.ok} label={outcomeLabel(run)} />,
                },
                { header: 'Took', accessor: (run) => seconds(run.seconds) },
                {
                  header: 'Output',
                  accessor: (run) =>
                    run.has_output ? (
                      <Button size="sm" onClick={() => navigate(runsUrl(job || run.job, run.run_id))}>
                        View
                      </Button>
                    ) : (
                      <span className="text-content-muted">{run.error ?? 'none'}</span>
                    ),
                },
                {
                  header: 'Trace',
                  accessor: (run) => <TraceLinkButtons links={traceLinks(explorers, run.trace_id)} />,
                },
              ]}
            />
          )
        }
      </PanelState>
    </OpsPage>
  );
}

function RunOutputView({ job, runId, onClose }: { job: string; runId: string; onClose: () => void }) {
  const [output, setOutput] = useState<RunOutput>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let live = true;
    setOutput(undefined);
    setError(undefined);
    opsApi
      .runOutput(job, runId)
      .then((value) => live && setOutput(value))
      .catch(
        (reason: unknown) => live && setError(reason instanceof Error ? reason.message : String(reason)),
      );
    return () => {
      live = false;
    };
  }, [job, runId]);

  const title = `${job} · ${runId}`;
  return (
    <div className="flex flex-col gap-2" aria-label="Run output">
      <div className="flex items-center gap-3">
        <span className="font-mono text-content-secondary">{title}</span>
        <span className="grow" />
        <Button size="sm" onClick={onClose}>
          Close
        </Button>
      </div>
      {error ? (
        <span role="alert" className="text-intent-danger">
          {error}
        </span>
      ) : null}
      {output === undefined && !error ? <Spinner label="Loading output" /> : null}
      {output ? (
        <CodeBlock title={title} copyable className="max-h-[32rem] overflow-auto">
          {output.output ?? 'This run saved no output, or it has since been pruned.'}
        </CodeBlock>
      ) : null}
    </div>
  );
}
