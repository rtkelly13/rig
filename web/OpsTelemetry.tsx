import {
  Badge,
  Button,
  DataTable,
  EmptyState,
  NoteBlock,
  Select,
  usePolledResource,
} from '@rtkelly-labs/design-system';
import { opsApi, traceLinks, type ExplorerStatus, type TraceView } from './opsApi';
import { fromNanos } from './opsFormat';
import { OpsPage, PanelState, Toolbar, TraceLinkButtons } from './OpsCommon';
import { updateQuery } from './router';
import type { Route } from './router';

const DAY_OPTIONS = [1, 3, 7, 14].map((d) => ({
  label: d === 1 ? 'Last day' : `Last ${d} days`,
  value: String(d),
}));

function explorerSentence(explorers: ExplorerStatus): string {
  const hyperdx = explorers.hyperdx_url
    ? explorers.hyperdx_reachable
      ? 'Trace links open HyperDX.'
      : 'HyperDX is configured but not answering.'
    : 'HyperDX is not configured (RIG_HYPERDX_URL).';
  const aspire = explorers.aspire_reachable ? ' Aspire holds live data only, since it last started.' : '';
  return hyperdx + aspire;
}

export function OpsTelemetry({ route }: { route: Route }) {
  const days = DAY_OPTIONS.some((option) => option.value === route.query.get('days'))
    ? (route.query.get('days') ?? '3')
    : '3';
  const status = usePolledResource(opsApi.telemetry);
  const traces = usePolledResource(() => opsApi.traces(Number(days)), {
    intervalMs: 60_000,
    key: days,
  });
  const explorers = status.data?.explorers;

  return (
    <OpsPage title="Telemetry" lede="Recent traces from every rig tool, with links into the trace explorers.">
      <Toolbar
        onRefresh={() => {
          status.reload();
          traces.reload();
        }}
        loading={traces.loading}
      >
        <div className="w-48">
          <Select
            label="Window"
            options={DAY_OPTIONS}
            value={days}
            onValueChange={(next) => updateQuery('days', next === '3' ? '' : next, true)}
          />
        </div>
        {explorers?.hyperdx_url ? (
          <Button href={explorers.hyperdx_url} target="_blank" rel="noreferrer" variant="primary" size="sm">
            Open HyperDX
          </Button>
        ) : null}
        {explorers?.aspire_url ? (
          <Button href={explorers.aspire_url} target="_blank" rel="noreferrer" size="sm">
            Open Aspire
          </Button>
        ) : null}
      </Toolbar>
      {status.data ? (
        <NoteBlock
          type={status.data.store.clickhouse_reachable ? 'tip' : 'note'}
          title={`Source: ${status.data.store.source === 'clickhouse' ? 'ClickHouse' : 'local trace files'}`}
        >
          {status.data.store.detail} {explorerSentence(status.data.explorers)}
        </NoteBlock>
      ) : null}
      <PanelState state={traces}>
        {(list) => (
          <>
            {list.note ? <span className="text-content-muted">{list.note}</span> : null}
            {list.traces.length === 0 ? (
              <EmptyState title="No traces in this window" />
            ) : (
              <DataTable<TraceView>
                caption={`Recent traces, from ${list.source === 'clickhouse' ? 'ClickHouse' : 'local trace files'}`}
                data={list.traces}
                pageSize={25}
                keyExtractor={(trace) => trace.trace_id}
                columns={[
                  { header: 'Started', accessor: (trace) => fromNanos(trace.start_ns), rowHeader: true },
                  {
                    header: 'Service',
                    accessor: (trace) => <Badge accent="secondary">{trace.service || '?'}</Badge>,
                  },
                  { header: 'Root span', accessor: (trace) => <span className="font-mono">{trace.root}</span> },
                  {
                    header: 'Took',
                    accessor: (trace) => `${(trace.duration_ms / 1000).toFixed(2)} s`,
                    enableSorting: true,
                    sortValue: (trace) => trace.duration_ms,
                  },
                  { header: 'Spans', accessor: 'spans' },
                  {
                    header: 'Errors',
                    accessor: (trace) =>
                      trace.errors ? (
                        <Badge accent="danger">{trace.errors}</Badge>
                      ) : (
                        <span className="text-content-muted">0</span>
                      ),
                  },
                  {
                    header: 'Explore',
                    accessor: (trace) => <TraceLinkButtons links={traceLinks(explorers, trace.trace_id)} />,
                  },
                ]}
              />
            )}
          </>
        )}
      </PanelState>
    </OpsPage>
  );
}
