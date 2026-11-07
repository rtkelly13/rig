import {
  Button,
  Card,
  CodeBlock,
  EmptyState,
  NoteBlock,
  StatCard,
  usePolledResource,
} from '@rtkelly-labs/design-system';
import { attentionItems, opsApi } from './opsApi';
import { OpsPage, PanelState, Toolbar } from './OpsCommon';
import { runsUrl } from './OpsJobs';
import { navigate } from './router';

// One poll for the whole page, so the counts and the list always describe the same moment.
async function loadOverview() {
  const [jobs, runs, feed, telemetry] = await Promise.all([
    opsApi.jobs(),
    opsApi.runs(undefined, 100),
    opsApi.decisions(),
    opsApi.telemetry(),
  ]);
  return { jobs, feed, telemetry, attention: attentionItems(jobs, runs, feed) };
}

export function OpsAttention() {
  const state = usePolledResource(loadOverview);
  return (
    <OpsPage title="Attention" lede="What needs you: failed jobs and worktree decisions only a person can make.">
      <Toolbar onRefresh={state.reload} loading={state.loading} />
      <PanelState state={state}>
        {({ jobs, feed, telemetry, attention }) => {
          const failing = jobs.filter((job) => job.last && !job.last.ok).length;
          const { store, explorers } = telemetry;
          return (
            <div className="flex flex-col gap-6">
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                <StatCard
                  title="Needs attention"
                  value={attention.length}
                  accent={attention.length ? 'warning' : 'success'}
                />
                <StatCard
                  title="Failing jobs"
                  value={failing}
                  subtitle={`of ${jobs.length} coordinator jobs`}
                  accent={failing ? 'danger' : 'success'}
                />
                <StatCard
                  title="Decisions"
                  value={feed.decisions.length}
                  subtitle="worktree questions for a person"
                  accent="info"
                />
                <StatCard
                  title="Telemetry"
                  value={store.clickhouse_reachable ? 'ClickHouse' : 'Local files'}
                  subtitle={
                    explorers.hyperdx_reachable
                      ? 'HyperDX answering'
                      : explorers.aspire_reachable
                        ? 'Aspire answering (live only)'
                        : 'no explorer answering'
                  }
                  accent={store.clickhouse_reachable ? 'success' : 'quiet'}
                />
              </div>
              {feed.note ? <NoteBlock type="note">{feed.note}</NoteBlock> : null}
              {attention.length === 0 ? (
                <EmptyState
                  title="Nothing needs you"
                  description="Every job's last run passed and the decisions feed is empty."
                />
              ) : (
                <div className="flex flex-col gap-3">
                  {attention.map((item) => (
                    <Card
                      key={`${item.kind}:${item.title}:${item.detail}`}
                      title={item.title}
                      description={item.detail}
                      accent={item.kind === 'job-failed' ? 'danger' : 'warning'}
                    >
                      {item.kind === 'job-failed' ? (
                        <Button size="sm" onClick={() => navigate(runsUrl(item.job, item.runId))}>
                          {item.runId ? "Open the run's output" : 'Open its runs'}
                        </Button>
                      ) : null}
                      {item.command ? <CodeBlock copyable>{item.command}</CodeBlock> : null}
                    </Card>
                  ))}
                </div>
              )}
            </div>
          );
        }}
      </PanelState>
    </OpsPage>
  );
}
