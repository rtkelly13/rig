import {
  Badge,
  Button,
  ResourceState,
  Spinner,
  type ResourceStateValue,
} from '@rtkelly-labs/design-system';
import type { ReactNode } from 'react';
import type { TraceLinks } from './opsApi';

// HyperDX first; Aspire only while it is answering, since it keeps live data only.
export function TraceLinkButtons({ links }: { links: TraceLinks }) {
  if (!links.hyperdx && !links.aspire) return <span className="text-content-muted">—</span>;
  return (
    <span className="flex flex-wrap gap-2">
      {links.hyperdx ? (
        <Button
          href={links.hyperdx}
          target="_blank"
          rel="noreferrer"
          size="sm"
          variant={links.primary === links.hyperdx ? 'primary' : 'tertiary'}
        >
          HyperDX
        </Button>
      ) : null}
      {links.aspire ? (
        <Button
          href={links.aspire}
          target="_blank"
          rel="noreferrer"
          size="sm"
          variant={links.primary === links.aspire ? 'primary' : 'tertiary'}
        >
          Aspire
        </Button>
      ) : null}
    </span>
  );
}

export function Outcome({ ok, label }: { ok: boolean; label?: string }) {
  return <Badge accent={ok ? 'success' : 'danger'}>{label ?? (ok ? 'ok' : 'failed')}</Badge>;
}

export function outcomeLabel(run: { ok: boolean; error: string | null; returncode: number | null }) {
  return run.ok ? 'ok' : run.error ? 'error' : `exit ${run.returncode ?? '?'}`;
}

// The loading, failed and stale states every ops page shares around its body.
export function PanelState<T>({
  state,
  children,
}: {
  state: ResourceStateValue<T>;
  children: (data: T) => ReactNode;
}) {
  return (
    <ResourceState
      state={state}
      loading={<Spinner label="Loading" />}
      errorTitle="The rig server did not answer"
      staleTitle="Showing the last good answer"
    >
      {children}
    </ResourceState>
  );
}

export function Toolbar({
  children,
  onRefresh,
  loading,
}: {
  children?: ReactNode;
  onRefresh: () => void;
  loading: boolean;
}) {
  return (
    <div className="mb-4 flex flex-wrap items-center gap-3">
      {children}
      <span className="grow" />
      {loading ? <Spinner size="sm" label="Refreshing" /> : null}
      <Button size="sm" onClick={onRefresh}>
        Refresh
      </Button>
    </div>
  );
}

export function OpsPage({ title, lede, children }: { title: string; lede: string; children: ReactNode }) {
  return (
    <section className="ops-page flex flex-col gap-4 p-6" aria-labelledby="ops-title">
      <header>
        <h1 id="ops-title" className="text-2xl font-semibold">
          {title}
        </h1>
        <p className="text-content-muted">{lede}</p>
      </header>
      {children}
    </section>
  );
}
