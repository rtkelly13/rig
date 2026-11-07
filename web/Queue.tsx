import { Button } from '@rtkelly-labs/design-system';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { filterEntries, inView, readiness, ref } from './model';
import type { Entry } from './model';
import { itemUrl, navigate, queueUrl as queuePath, updateQuery } from './router';
import type { QueueView, Route } from './router';
import type { QueueState } from './useQueue';
import { Detail } from './Detail';
import { Icon } from './Icon';
import { QueueFilters } from './QueueFilters';
import { QueueItems } from './QueueItems';
import { QueueScope } from './QueueScope';
import { groupEntries, signals } from './attention';

const titles: Record<QueueView, string> = {
  inbox: 'Review inbox',
  pulls: 'Review inbox',
  issues: 'Issues',
  waiting: 'Waiting',
  snoozed: 'Snoozed',
  done: 'Done',
  history: 'History',
};
export function Queue({ route, state }: { route: Route; state: QueueState }) {
  const columns = useRef<HTMLDivElement>(null);
  const [actualWidth, setActualWidth] = useState(320);
  useEffect(() => {
    const column = columns.current?.querySelector('.queue-column');
    if (!column) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry && entry.contentRect.width > 0) setActualWidth(Math.round(entry.contentRect.width));
    });
    observer.observe(column);
    return () => observer.disconnect();
  }, []);
  const [listWidth, setListWidth] = useState<number | null>(() => {
    try {
      const saved = Number(localStorage.getItem('rig:queue-width-px'));
      return Number.isFinite(saved) && saved >= 260 && saved <= 560 ? saved : null;
    } catch {
      return null;
    }
  });
  const resize = (width: number | null) => {
    const next = width === null ? null : Math.max(260, Math.min(560, width));
    setListWidth(next);
    try {
      if (next === null) localStorage.removeItem('rig:queue-width-px');
      else localStorage.setItem('rig:queue-width-px', String(next));
    } catch {
      /* Storage may be disabled. */
    }
  };
  const [collapsedGroups, setCollapsedGroups] = useState(() => new Set<string>());
  const view = route.page as QueueView;
  const scoped = useMemo(
    () =>
      state.entries.filter((entry) => !route.repository || entry.item.repo === route.repository),
    [state.entries, route.repository],
  );
  const selected = scoped.find((entry) => ref(entry.item) === route.selected);
  const selectedKey = selected ? ref(selected.item) : '';
  const selectedItem = selected?.item;
  const selectedRef = useRef(selectedItem);
  useEffect(() => {
    selectedRef.current = selectedItem;
  }, [selectedItem]);
  const logged = useRef('');
  const entries = useMemo(
    () =>
      filterEntries(scoped, view, route.query).filter(
        (entry) =>
          !route.query.get('action') ||
          signals(entry).some((signal) => signal.key === route.query.get('action')),
      ),
    [scoped, view, route.query],
  );
  const all = scoped.filter((entry) => inView(entry, view));
  const repos = [...new Set(all.map((entry) => entry.item.repo))].sort();
  const focused = Boolean(selected && route.query.get('layout') === 'focus');
  const groupMode = route.query.get('group') || 'stacks';
  const groups = useMemo(
    () => groupEntries(entries, groupMode, state.entries),
    [entries, groupMode, state.entries],
  );
  const ordered = useMemo(
    () => groups.flatMap((group) => (collapsedGroups.has(group.key) ? [] : group.entries)),
    [groups, collapsedGroups],
  );
  const position = ordered.findIndex((entry) => ref(entry.item) === selectedKey);
  const queueUrl = queuePath(view, route.repository, route.query);
  const move = (direction: number) => {
    const next = ordered[position + direction];
    if (next)
      navigate(itemUrl(view, next.item.repo, next.item.number, route.query, route.repository));
  };
  const activePrs = scoped.filter((entry) => inView(entry, 'inbox'));
  const { perform } = state;

  useEffect(() => {
    if (!selectedKey || state.pending) return;
    const current = selectedRef.current;
    if (!current || logged.current === selectedKey) return;
    logged.current = selectedKey;
    void perform('/interact', {
      repo: current.repo,
      number: current.number,
      head: current.head,
      action: 'opened',
      note: '',
    }).then((saved) => {
      if (saved && selectedRef.current && ref(selectedRef.current) === selectedKey) {
        void perform('/watch', { repo: current.repo, number: current.number });
      }
    });
  }, [selectedKey, perform, state.pending]);
  useEffect(() => {
    const timer = setInterval(() => {
      const current = selectedRef.current;
      if (!document.hidden && current && current.state === 'OPEN')
        void perform('/watch', { repo: current.repo, number: current.number });
    }, 30000);
    return () => clearInterval(timer);
  }, [perform]);

  const select = useCallback(
    (entry: Entry) =>
      navigate(itemUrl(view, entry.item.repo, entry.item.number, route.query, route.repository)),
    [view, route.query, route.repository],
  );
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target;
      if (
        (target instanceof Element &&
          target.closest('dialog, [role="dialog"], [role="listbox"]')) ||
        event.ctrlKey ||
        event.metaKey ||
        event.altKey ||
        target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target instanceof HTMLSelectElement ||
        (target instanceof HTMLElement && target.isContentEditable)
      )
        return;
      if (event.key === '/') {
        event.preventDefault();
        if (window.matchMedia('(max-width: 900px)').matches && selected) navigate(queueUrl);
        else if (focused) updateQuery('layout', '');
        requestAnimationFrame(() => document.getElementById('queue-search')?.focus());
      }
      if (!['j', 'k'].includes(event.key) || !ordered.length) return;
      event.preventDefault();
      const index = ordered.findIndex((entry) => ref(entry.item) === route.selected);
      const next =
        ordered[Math.min(ordered.length - 1, Math.max(0, index + (event.key === 'j' ? 1 : -1)))];
      if (next) select(next);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [ordered, route.selected, select, focused, selected, queueUrl]);

  return (
    <section
      className={`triage-page ${selected ? 'has-selection' : ''}`}
      data-layout={focused ? 'focus' : 'split'}
      aria-labelledby="queue-title"
    >
      <div className="workspace-toolbar">
        <h1 id="queue-title">
          {titles[view]} <span>{all.length}</span>
        </h1>
        <QueueScope route={route} entries={state.entries} />
        {selected ? (
          <div className="reading-controls">
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button back-to-queue"
              href={queueUrl}
              onClick={(event) => {
                if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
                event.preventDefault();
                navigate(queueUrl);
              }}
            >
              <Icon name="queue" />
              Back to inbox
            </Button>
            <span className="item-position">
              {position >= 0 ? `${position + 1} of ${ordered.length}` : 'Outside this view'}
            </span>
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button previous-item"
              aria-label="Previous item"
              disabled={position <= 0}
              onClick={() => move(-1)}
            >
              <Icon name="arrow" />
            </Button>
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button"
              aria-label="Next item"
              disabled={!ordered.length || position >= ordered.length - 1}
              onClick={() => move(1)}
            >
              <Icon name="arrow" />
            </Button>
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button layout-toggle"
              aria-pressed={focused}
              onClick={() => updateQuery('layout', focused ? '' : 'focus')}
            >
              {focused ? 'Split view' : 'Focus view'}
            </Button>
          </div>
        ) : null}
        <Button
          size="sm"
          variant="inverse"
          className="quiet-button workspace-refresh"
          pending={state.pending === '/refresh'}
          pendingLabel="Refreshing managed repositories"
          disabled={Boolean(state.pending && state.pending !== '/refresh')}
          onClick={() => state.perform('/refresh', {}, 'All managed repositories refreshed.')}
        >
          <Icon name="refresh" />
          Refresh estate
        </Button>
      </div>
      {state.error ? (
        <div role="alert" className="operation-message error-message">
          <strong>Could not complete the operation.</strong> {state.error} Your saved history is
          retained.
        </div>
      ) : null}
      {state.notice ? (
        <p role="status" className="operation-message success-message">
          {state.notice}
        </p>
      ) : null}
      <div
        ref={columns}
        className="triage-columns"
        style={
          !focused && listWidth !== null
            ? { gridTemplateColumns: `${listWidth}px 8px minmax(0, 1fr)` }
            : undefined
        }
      >
        <div className="queue-column">
          <QueueFilters
            route={route}
            state={state}
            repos={repos}
            count={entries.length}
            total={all.length}
            readinessCounts={readiness.map(
              (_, index) => activePrs.filter((entry) => entry.item.readiness === index).length,
            )}
          />
          <div className="queue-list" aria-label="Triage queue" aria-busy={state.loading}>
            {state.loading ? (
              <div className="empty-state">Loading your saved queue…</div>
            ) : (
              <QueueItems
                groups={groups}
                collapsed={collapsedGroups}
                route={route}
                select={select}
                order={ordered}
                toggle={(key) =>
                  setCollapsedGroups((previous) => {
                    const next = new Set(previous);
                    if (next.has(key)) next.delete(key);
                    else next.add(key);
                    return next;
                  })
                }
              />
            )}
            {!state.loading && !entries.length ? (
              <div className="empty-state">
                <Icon name="queue" />
                <h2>{all.length ? 'No matching items' : 'This queue is clear'}</h2>
                <p>
                  {all.length
                    ? 'Try a different search or repository filter.'
                    : 'Refresh the estate to discover new work.'}
                </p>
                {all.length ? (
                  <button
                    className="text-button"
                    onClick={() => navigate(queuePath(view, route.repository))}
                  >
                    Clear filters
                  </button>
                ) : null}
              </div>
            ) : null}
          </div>
        </div>
        <div
          className="column-resizer"
          role="separator"
          tabIndex={0}
          aria-label="Resize item list"
          aria-orientation="vertical"
          aria-valuemin={260}
          aria-valuemax={560}
          aria-valuenow={actualWidth}
          aria-valuetext={`${actualWidth} pixels`}
          onDoubleClick={() => resize(null)}
          onKeyDown={(event) => {
            if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
              event.preventDefault();
              resize(
                event.key === 'Home'
                  ? 260
                  : event.key === 'End'
                    ? 560
                    : (columns.current?.querySelector('.queue-column')?.getBoundingClientRect()
                        .width ?? 320) + (event.key === 'ArrowLeft' ? -20 : 20),
              );
            }
          }}
          onPointerDown={(event) => {
            event.currentTarget.setPointerCapture(event.pointerId);
            event.preventDefault();
          }}
          onPointerMove={(event) => {
            if (!event.currentTarget.hasPointerCapture(event.pointerId)) return;
            const bounds = columns.current?.getBoundingClientRect();
            if (bounds) resize(event.clientX - bounds.left);
          }}
          onPointerUp={(event) => {
            if (event.currentTarget.hasPointerCapture(event.pointerId))
              event.currentTarget.releasePointerCapture(event.pointerId);
          }}
        />
        {selected ? (
          <Detail
            key={ref(selected.item)}
            entry={selected}
            state={state}
            allActivity={route.query.get('activity') === 'all'}
            route={route}
          />
        ) : (
          <div className="detail-placeholder">
            <Icon name="pull" />
            <h2>
              {state.loading
                ? 'Loading saved item…'
                : route.selected
                  ? 'Item not in the saved queue'
                  : 'Choose your next review'}
            </h2>
            <p>
              {route.selected
                ? 'Refresh the estate to fetch this item, or select another from the queue.'
                : 'Select a PR to see the current head, record a decision and follow what changes.'}
            </p>
            {entries[0] ? (
              <Button variant="primary" size="sm" onClick={() => select(entries[0]!)}>
                Start with the first item <Icon name="arrow" />
              </Button>
            ) : null}
          </div>
        )}
      </div>
    </section>
  );
}
