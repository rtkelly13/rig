import {
  Button,
  Input,
  Select,
  layoutServiceGraph,
  serviceEdgePath,
} from '@rtkelly-labs/design-system';
import { useEffect, useMemo, useState } from 'react';
import { Icon } from './Icon';
import { failedStates } from './attention';
import { inView } from './model';
import { navigate, queueUrl, updateQuery } from './router';
import type { Route } from './router';
import type { QueueState } from './useQueue';
import type { EstateGraph, EstateProject } from './estateModel';

export function Estate({ route, state }: { route: Route; state: QueueState }) {
  const [graph, setGraph] = useState<EstateGraph | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [zoom, setZoom] = useState(1);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    setLoading(true);
    setError('');
    void fetch(`/api/estate/graph${revision ? '/refresh' : ''}`, { signal: abort.signal })
      .then(async (response) => {
        const data: EstateGraph & { error?: string } = await response.json();
        if (!response.ok || data.error)
          throw new Error(data.error || `Graph request failed (${response.status})`);
        setGraph(data);
      })
      .catch((reason: Error) => {
        if (reason.name !== 'AbortError') setError(reason.message);
      })
      .finally(() => {
        if (!abort.signal.aborted) setLoading(false);
      });
    return () => abort.abort();
  }, [revision]);
  const kind = route.query.get('kind') || 'all';
  const search = (route.query.get('q') || '').toLowerCase();
  const selectedId = route.query.get('repo') || '';
  const selection = graph?.projects.find((project) => project.id === selectedId);
  const dependencies = useMemo(
    () => graph?.dependencies.filter((edge) => kind === 'all' || edge.registry === kind) || [],
    [graph, kind],
  );
  const connected = new Set(dependencies.flatMap((edge) => [edge.source, edge.target]));
  const components =
    graph?.components.filter((component) =>
      component.members.some((member) => connected.has(member)),
    ) || [];
  const owners = new Map(
    graph?.components.flatMap((component) =>
      component.members.map((member) => [member, component.id] as const),
    ),
  );
  const edges = [
    ...new Map(
      dependencies.map((edge) => {
        const source = owners.get(edge.source) || edge.source,
          target = owners.get(edge.target) || edge.target;
        return [`${source}:${target}`, { source, target }];
      }),
    ).values(),
  ].filter((edge) => edge.source !== edge.target);
  const layout = layoutServiceGraph(
    {
      nodes: components.map((component) => ({
        id: component.id,
        requests: 0,
        errors: 0,
        avgDuration: 0,
      })),
      edges: edges.map((edge) => ({
        from: edge.source,
        to: edge.target,
        calls: 0,
        errors: 0,
        avgDuration: 0,
      })),
    },
    { nodeWidth: 240, nodeHeight: 100, columnGap: 80, rowGap: 28 },
  );
  const projectById = new Map(graph?.projects.map((project) => [project.id, project]));
  const componentById = new Map(components.map((component) => [component.id, component]));
  const stats = (ids: string[]) => {
    const entries = state.entries.filter((entry) => ids.includes(entry.item.repo));
    const inbox = entries.filter((entry) => inView(entry, 'inbox'));
    return {
      prs: inbox.length,
      issues: entries.filter((entry) => inView(entry, 'issues')).length,
      failed: inbox.filter((entry) => failedStates.has(entry.item.facts?.checks_state || ''))
        .length,
      waiting: entries.filter((entry) => inView(entry, 'waiting')).length,
    };
  };
  const matches = (project: EstateProject) =>
    !search ||
    `${project.id} ${project.description} ${project.packages.join(' ')}`
      .toLowerCase()
      .includes(search) ||
    dependencies.some(
      (edge) =>
        (edge.source === project.id || edge.target === project.id) &&
        edge.package.toLowerCase().includes(search),
    );
  const choose = (repo: string) => updateQuery('repo', repo);
  const isolated =
    graph?.projects.filter((project) => !connected.has(project.id) && matches(project)) || [];
  const outgoing = graph?.dependencies.filter((edge) => edge.source === selectedId) || [];
  const incoming = graph?.dependencies.filter((edge) => edge.target === selectedId) || [];
  const triageLink = (label: string, view: 'inbox' | 'issues' | 'waiting', repo: string) => (
    <a
      className="estate-triage-link"
      href={queueUrl(view, repo)}
      onClick={(event) => {
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        navigate(queueUrl(view, repo));
      }}
    >
      {label}
      <Icon name="arrow" />
    </a>
  );
  return (
    <section className="estate-page" aria-labelledby="estate-title">
      <div className="estate-toolbar">
        <div>
          <h1 id="estate-title">Estate Managed Repositories</h1>
          <p>
            {graph
              ? `${graph.projects.length} repositories · ${graph.dependencies.length} dependency records`
              : 'Loading estate connectivity…'}
          </p>
        </div>
        <Button
          size="sm"
          variant="inverse"
          className="quiet-button"
          href="/estate/operations"
          onClick={(event) => {
            if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            navigate('/estate/operations');
          }}
        >
          Operations
        </Button>
        <Button
          size="sm"
          variant="inverse"
          className="quiet-button"
          href="/triage/inbox"
          onClick={(event) => {
            if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            navigate('/triage/inbox');
          }}
        >
          All-repository inbox
          <Icon name="queue" />
        </Button>
        <Button
          size="sm"
          variant="inverse"
          className="quiet-button"
          disabled={loading}
          onClick={() => setRevision((value) => value + 1)}
        >
          <Icon name="refresh" />
          {loading ? 'Scanning…' : 'Rescan connections'}
        </Button>
        <Button
          size="sm"
          variant="inverse"
          className="quiet-button"
          pending={state.pending === '/refresh'}
          pendingLabel="Refreshing triage"
          disabled={Boolean(state.pending && state.pending !== '/refresh')}
          onClick={() => state.perform('/refresh', {}, 'Managed queues refreshed.')}
        >
          <Icon name="refresh" />
          Refresh triage
        </Button>
      </div>
      {state.error ? (
        <p role="alert" className="operation-message error-message">
          {state.error}
        </p>
      ) : null}
      {state.notice ? (
        <p role="status" className="operation-message success-message">
          {state.notice}
        </p>
      ) : null}
      {error ? (
        <p role="alert" className="operation-message error-message">
          {error}
        </p>
      ) : null}
      <div className="estate-controls">
        <Input
          aria-label="Find repository or package"
          placeholder="Find repository or package…"
          value={route.query.get('q') || ''}
          onChange={(event) => updateQuery('q', event.target.value, true)}
        />
        <Select
          aria-label="Connection type"
          value={kind}
          options={[
            { value: 'all', label: 'All connections' },
            { value: 'npm', label: 'npm packages' },
            { value: 'nuget', label: 'NuGet packages' },
            { value: 'pypi', label: 'Python packages' },
            { value: 'project', label: 'Project references' },
            { value: 'declared', label: 'Declared dependencies' },
          ]}
          onValueChange={(value) => updateQuery('kind', value === 'all' ? '' : value)}
        />
        <div className="graph-zoom">
          <Button
            variant="inverse"
            className="quiet-button"
            size="sm"
            aria-label="Zoom graph out"
            onClick={() => setZoom((value) => Math.max(0.4, value - 0.2))}
          >
            −
          </Button>
          <span>{Math.round(zoom * 100)}%</span>
          <Button
            variant="inverse"
            className="quiet-button"
            size="sm"
            aria-label="Zoom graph in"
            onClick={() => setZoom((value) => Math.min(1.8, value + 0.2))}
          >
            +
          </Button>
          <Button variant="inverse" className="quiet-button" size="sm" onClick={() => setZoom(1)}>
            Reset
          </Button>
        </div>
      </div>
      <div className="estate-layout">
        <div className="estate-graph-panel" aria-label="Estate dependency graph">
          <div className="graph-legend">
            <span>
              <Icon name="link" />
              Provider → consumer
            </span>
            <span>
              <Icon name="alert" />
              Failed checks in triage
            </span>
            <span>Cycles are grouped into DAG nodes</span>
          </div>
          {loading && !graph ? (
            <p className="empty-state">Reading managed repositories and local package manifests…</p>
          ) : null}
          {components.length ? (
            <div
              className="estate-canvas"
              style={{ width: (layout.width + 40) * zoom, height: (layout.height + 40) * zoom }}
            >
              <div
                className="estate-canvas-content"
                style={{
                  width: layout.width + 40,
                  height: layout.height + 40,
                  transform: `scale(${zoom})`,
                }}
              >
                <svg
                  aria-hidden="true"
                  width={layout.width + 40}
                  height={layout.height + 40}
                  className="estate-edges"
                >
                  <defs>
                    <marker
                      id="dependency-arrow"
                      markerWidth="8"
                      markerHeight="8"
                      refX="7"
                      refY="4"
                      orient="auto"
                    >
                      <path d="M0 0 8 4 0 8z" />
                    </marker>
                  </defs>
                  <g transform="translate(20,20)">
                    {layout.edges.map((edge) => (
                      <path
                        key={`${edge.from}:${edge.to}`}
                        d={serviceEdgePath(edge)}
                        markerEnd="url(#dependency-arrow)"
                        data-selected={
                          componentById.get(edge.from)?.members.includes(selectedId) ||
                          componentById.get(edge.to)?.members.includes(selectedId)
                        }
                      />
                    ))}
                  </g>
                </svg>
                {layout.nodes.map((node) => {
                  const component = componentById.get(node.id);
                  if (!component) return null;
                  const project = projectById.get(component.members[0] || '');
                  if (!project) return null;
                  const counts = stats(component.members),
                    match = component.members.some((id) => {
                      const item = projectById.get(id);
                      return item && matches(item);
                    });
                  return (
                    <button
                      key={node.id}
                      className="estate-node"
                      data-selected={component.members.includes(selectedId)}
                      data-failed={counts.failed > 0}
                      data-enabled={component.members.some((id) => projectById.get(id)?.enabled)}
                      data-match={match}
                      style={{ left: node.x + 20, top: node.y + 20, width: 240, height: 100 }}
                      aria-label={`Inspect ${component.cyclic ? `cycle group: ${component.members.join(', ')}` : project.id}`}
                      onClick={() => choose(project.id)}
                    >
                      <span className="estate-node-heading">
                        <Icon name={component.cyclic ? 'stack' : 'folder'} />
                        <strong>
                          {component.cyclic
                            ? `${component.members.length} repositories in cycle`
                            : project.name}
                        </strong>
                      </span>
                      <span className="estate-node-repo" title={project.id}>
                        {component.cyclic
                          ? component.members.map((id) => id.split('/').at(-1)).join(', ')
                          : project.id}
                      </span>
                      <span className="estate-node-counts">
                        <span>
                          <Icon name="pull" />
                          {counts.prs} PRs
                        </span>
                        <span>
                          <Icon name="issue" />
                          {counts.issues} issues
                        </span>
                        {counts.failed ? (
                          <span className="estate-failures">
                            <Icon name="alert" />
                            {counts.failed} failed
                          </span>
                        ) : null}
                      </span>
                    </button>
                  );
                })}
              </div>
            </div>
          ) : graph ? (
            <p className="empty-state">No matching internal connections are recorded.</p>
          ) : null}
          {isolated.length ? (
            <section className="unconnected-repositories">
              <h2>
                {kind === 'all'
                  ? 'Repositories without recorded internal links'
                  : 'Repositories without matching links'}{' '}
                <span>{isolated.length}</span>
              </h2>
              <div>
                {isolated.map((project) => (
                  <button
                    key={project.id}
                    data-selected={selectedId === project.id}
                    onClick={() => choose(project.id)}
                  >
                    <Icon name="folder" />
                    <span>
                      {project.name}
                      <small>
                        {project.scanned
                          ? `${project.manifests} supported manifests scanned`
                          : 'Not scanned'}
                      </small>
                    </span>
                  </button>
                ))}
              </div>
            </section>
          ) : null}
        </div>
        <aside className="estate-inspector" aria-label="Repository details">
          {selection ? (
            <>
              <div className="estate-inspector-header">
                <Icon name="folder" />
                <div>
                  <h2>{selection.name}</h2>
                  <p>{selection.id}</p>
                </div>
              </div>
              {!selection.enabled ? (
                <p className="estate-warning">
                  Retained repository · excluded from active managed triage.
                </p>
              ) : null}
              <p>{selection.description}</p>
              <p className="context-note">
                {selection.stack} · {selection.domain}
              </p>
              <div className="estate-triage-links">
                {triageLink(`${stats([selection.id]).prs} PRs to triage`, 'inbox', selection.id)}
                {triageLink(
                  `${stats([selection.id]).issues} issues to triage`,
                  'issues',
                  selection.id,
                )}
                {triageLink(`${stats([selection.id]).waiting} waiting`, 'waiting', selection.id)}
              </div>
              <h3>
                Consumes from {new Set(incoming.map((edge) => edge.source)).size}{' '}
                {new Set(incoming.map((edge) => edge.source)).size === 1
                  ? 'repository'
                  : 'repositories'}
              </h3>
              {incoming.map((edge, index) => (
                <div
                  className="dependency-evidence"
                  key={`${edge.source}:${edge.package}:${index}`}
                >
                  <button onClick={() => choose(edge.source)}>
                    <Icon name="link" />
                    {edge.source}
                  </button>
                  <strong>{edge.package}</strong>
                  <code>{edge.requirement}</code>
                  <small>
                    {edge.scope} · {edge.registry} · {edge.evidence}
                  </small>
                </div>
              ))}
              <h3>
                Used by {new Set(outgoing.map((edge) => edge.target)).size}{' '}
                {new Set(outgoing.map((edge) => edge.target)).size === 1
                  ? 'repository'
                  : 'repositories'}
              </h3>
              {outgoing.map((edge, index) => (
                <div
                  className="dependency-evidence"
                  key={`${edge.target}:${edge.package}:${index}`}
                >
                  <button onClick={() => choose(edge.target)}>
                    <Icon name="arrow" />
                    {edge.target}
                  </button>
                  <strong>{edge.package}</strong>
                  <small>
                    {edge.scope} · {edge.evidence}
                  </small>
                </div>
              ))}
              {selection.packages.length ? (
                <>
                  <h3>Declared packages</h3>
                  <ul className="estate-package-list">
                    {selection.packages.map((name) => (
                      <li key={name}>
                        <Icon name="tag" />
                        {name}
                      </li>
                    ))}
                  </ul>
                </>
              ) : null}
              <p className="context-note">
                {selection.scanned
                  ? `${selection.manifests} tracked manifests scanned locally.`
                  : 'Local consumption has not been scanned.'}
              </p>
              {selection.warnings.map((warning) => (
                <p className="estate-warning" key={warning}>
                  {warning}
                </p>
              ))}
              {graph?.components.find((component) => component.members.includes(selection.id))
                ?.cyclic ? (
                <p className="estate-warning">
                  This repository participates in a dependency cycle. The graph groups its members
                  into one DAG node; the original connections remain listed here.
                </p>
              ) : null}
            </>
          ) : (
            <div className="empty-state">
              <Icon name="grid" />
              <h2>Select a repository</h2>
              <p>
                Inspect package evidence, upstream providers, downstream consumers and triage
                queues.
              </p>
            </div>
          )}
          {graph ? (
            <p className="context-note">
              Connections scanned {new Date(graph.scanned_at).toLocaleString()}. Package and triage
              snapshots refresh independently.
            </p>
          ) : null}
          {graph?.warnings.map((warning) => (
            <p className="estate-warning" key={warning}>
              {warning}
            </p>
          ))}
        </aside>
      </div>
    </section>
  );
}
