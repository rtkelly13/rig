import {
  AlertDialog,
  Badge,
  Button,
  CodeBlock,
  DataTable,
  EmptyState,
  formatBytes,
  NoteBlock,
  Progress,
  StatCard,
  ToastProvider,
  usePolledResource,
  useToast,
} from '@rtkelly-labs/design-system';
import { useRef, useState } from 'react';
import { opsApi, type Decision, type TrimCandidate } from './opsApi';
import { ago } from './opsFormat';
import { OpsPage, PanelState, Toolbar } from './OpsCommon';

export function OpsDecisions() {
  const state = usePolledResource(opsApi.decisions, { intervalMs: 120_000 });
  return (
    <OpsPage
      title="Decisions"
      lede="Worktree questions the estate tooling will not answer on its own."
    >
      <Toolbar onRefresh={state.reload} loading={state.loading}>
        {state.data?.written ? (
          <span className="text-content-muted">feed written {ago(state.data.written)}</span>
        ) : null}
      </Toolbar>
      <PanelState state={state}>
        {(feed) => (
          <div className="flex flex-col gap-4">
            {feed.note ? <NoteBlock type="note">{feed.note}</NoteBlock> : null}
            {feed.decisions.length === 0 ? (
              feed.note ? null : (
                <EmptyState title="Nothing to decide" description="No idle worktree needs a person's call." />
              )
            ) : (
              <DataTable<Decision>
                caption="Worktree questions only a person should answer"
                data={feed.decisions}
                keyExtractor={(d) => d.path}
                columns={[
                  { header: 'Kind', accessor: (d) => <Badge accent="warning">{d.kind}</Badge> },
                  { header: 'Repo', accessor: 'repo', enableSorting: true, sortValue: (d) => d.repo },
                  {
                    header: 'Path',
                    accessor: (d) => <span className="font-mono break-all">{d.path}</span>,
                    rowHeader: true,
                  },
                  { header: 'Why', accessor: 'reason' },
                  {
                    header: 'Size',
                    accessor: (d) => formatBytes(d.size_bytes),
                    enableSorting: true,
                    sortValue: (d) => d.size_bytes,
                  },
                  {
                    header: 'Suggested',
                    accessor: (d) => (
                      <div className="flex flex-col gap-1">
                        <span>{d.suggested_action}</span>
                        {d.command ? <CodeBlock copyable>{d.command}</CodeBlock> : null}
                      </div>
                    ),
                  },
                ]}
              />
            )}
          </div>
        )}
      </PanelState>
    </OpsPage>
  );
}

export function OpsTrim() {
  return (
    <ToastProvider>
      <TrimPage />
    </ToastProvider>
  );
}

function TrimPage() {
  const disk = usePolledResource(opsApi.disk, { intervalMs: 60_000 });
  // A rescan is asked for once, by the next load after Refresh; the polls after it read the cached scan.
  const rescan = useRef(false);
  const scan = usePolledResource(
    () => {
      const refresh = rescan.current;
      rescan.current = false;
      return opsApi.trim(refresh);
    },
    { intervalMs: 10_000 },
  );
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [confirming, setConfirming] = useState(false);
  const toast = useToast();

  const candidates = scan.data?.candidates ?? [];
  const chosen = candidates.filter((c) => selected.has(c.path));
  const chosenBytes = chosen.reduce((sum, c) => sum + c.freeable, 0);

  const apply = () => {
    setConfirming(false);
    opsApi
      .applyTrim(chosen.map((c) => c.path))
      .then((results) => {
        const removed = results.filter((r) => r.status === 'removed');
        const freed = removed.reduce((sum, r) => sum + r.freeable, 0);
        const other = results.filter((r) => r.status !== 'removed');
        toast.show({
          title: `Removed ${removed.length} of ${results.length}, freeing ${formatBytes(freed)}`,
          description: other.length ? other.map((r) => `${r.path}: ${r.status}`).join('\n') : undefined,
          intent: other.length ? 'warning' : 'success',
        });
        setSelected(new Set());
        disk.reload();
        scan.reload();
      })
      .catch((reason: unknown) =>
        toast.show({
          title: 'Trim failed',
          description: reason instanceof Error ? reason.message : String(reason),
          intent: 'danger',
        }),
      );
  };

  return (
    <OpsPage title="Trim" lede="Free disk by removing regenerable output from idle worktrees.">
      <PanelState state={disk}>
        {(usage) => (
          <div className="grid gap-4 sm:grid-cols-3">
            <StatCard
              title="Free"
              value={formatBytes(usage.free)}
              subtitle={usage.path}
              accent={usage.free / usage.total < 0.1 ? 'danger' : 'success'}
            />
            <StatCard
              title="Used"
              value={formatBytes(usage.used)}
              subtitle={`of ${formatBytes(usage.total)}`}
              accent="quiet"
            />
            <div className="flex flex-col justify-center gap-2">
              <Progress value={Math.round((usage.used / usage.total) * 100)} label="Disk used" />
            </div>
          </div>
        )}
      </PanelState>
      <Toolbar
        onRefresh={() => {
          rescan.current = true;
          scan.reload();
        }}
        loading={scan.loading || scan.data?.scanning === true}
      >
        <span className="text-content-muted">
          {scan.data?.scanned_at ? `scanned ${ago(scan.data.scanned_at)}` : 'first scan running'}
          {scan.data?.scanning ? ' · rescanning' : ''}
        </span>
        <Button variant="primary" size="sm" disabled={chosen.length === 0} onClick={() => setConfirming(true)}>
          Trim {chosen.length} selected ({formatBytes(chosenBytes)})
        </Button>
      </Toolbar>
      <NoteBlock type="note" title="What trim removes">
        Regenerable output (node_modules, virtual environments, build output) inside idle worktrees: the
        same set <code>rig estate trim --apply</code> removes. The worktree, its branch and its temp/ stay.
        Applying checks each path again and skips anything no longer safe to remove.
      </NoteBlock>
      {scan.data?.error ? <NoteBlock type="warning">{scan.data.error}</NoteBlock> : null}
      <PanelState state={scan}>
        {(result) =>
          result.candidates.length === 0 ? (
            <EmptyState title={result.scanned_at ? 'Nothing to trim' : 'Scanning idle worktrees'} />
          ) : (
            <DataTable<TrimCandidate>
              caption="Regenerable output in idle worktrees, biggest first"
              data={result.candidates}
              pageSize={50}
              keyExtractor={(c) => c.path}
              selectedKeys={selected}
              onSelectionChange={setSelected}
              isRowSelectable={(c) => (c.refused ? `Refused: ${c.refused}` : true)}
              selectionLabel={(c) => `Trim ${c.path}`}
              columns={[
                {
                  header: 'Worktree',
                  accessor: (c) => <span className="font-mono">{c.worktree.split(/[\\/]/).pop()}</span>,
                },
                {
                  header: 'Path',
                  accessor: (c) => <span className="font-mono break-all">{c.path}</span>,
                  rowHeader: true,
                },
                { header: 'Category', accessor: 'category' },
                {
                  header: 'Frees',
                  accessor: (c) => formatBytes(c.freeable),
                  enableSorting: true,
                  sortValue: (c) => c.freeable,
                },
                { header: 'Refused because', accessor: (c) => c.refused || '—' },
              ]}
            />
          )
        }
      </PanelState>
      <AlertDialog
        isOpen={confirming}
        onClose={() => setConfirming(false)}
        onConfirm={apply}
        title={`Delete ${chosen.length} directories?`}
        confirmLabel="Trim"
      >
        They regenerate on the next install or build. {formatBytes(chosenBytes)} would be freed.
      </AlertDialog>
    </OpsPage>
  );
}
