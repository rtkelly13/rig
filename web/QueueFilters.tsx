import { Button, Input, Popover, Select } from '@rtkelly-labs/design-system';
import { useEffect, useState } from 'react';
import { Icon } from './Icon';
import { readiness, when } from './model';
import { navigate, updateQuery } from './router';
import type { Route } from './router';
import type { QueueState } from './useQueue';
import { actionNames } from './attention';

export function QueueFilters({
  route,
  state,
  repos,
  count,
  total,
  readinessCounts,
}: {
  route: Route;
  state: QueueState;
  repos: string[];
  count: number;
  total: number;
  readinessCounts: number[];
}) {
  const [open, setOpen] = useState(false);
  const focused = route.query.get('layout') === 'focus';
  useEffect(() => setOpen(false), [route.page, focused]);
  const filters = [
    route.query.get('repo'),
    route.query.get('action') ? actionNames[route.query.get('action') || ''] : null,
    route.query.has('status') ? readiness[Number(route.query.get('status'))] : null,
    route.query.get('read') === 'unread' ? 'Unread' : null,
  ].filter(Boolean);
  const selectedRepo = route.query.get('repo') || '';
  const repositoryOptions =
    selectedRepo && !repos.includes(selectedRepo) ? [selectedRepo, ...repos] : repos;
  const reset = () => {
    const query = new URLSearchParams(route.query);
    for (const name of ['repo', 'status', 'read', 'action']) query.delete(name);
    navigate(`${location.pathname}${query.size ? `?${query}` : ''}`);
  };
  return (
    <>
      <div className="queue-filter-row">
        <div className="search-field">
          <Icon name="search" />
          <Input
            id="queue-search"
            aria-label="Search queue"
            placeholder="Search queue…"
            value={route.query.get('q') || ''}
            onChange={(event) => updateQuery('q', event.target.value, true)}
          />
          <kbd>/</kbd>
        </div>
        <Popover
          title="Queue filters"
          className="queue-filters-popup"
          open={open}
          onOpenChange={setOpen}
          trigger={
            <Button size="sm" variant="inverse" className="quiet-button" aria-label="Queue filters">
              Filters
              {filters.length ? <span className="filter-count">{filters.length}</span> : null}
            </Button>
          }
        >
          <div className="queue-filter-fields">
            {!route.repository ? (
              <Select
                label="Repository"
                className="filter-select"
                value={route.query.get('repo') || 'all'}
                onValueChange={(value) => updateQuery('repo', value === 'all' ? '' : value)}
                options={[
                  { value: 'all', label: 'All repositories' },
                  ...repositoryOptions.map((repo) => ({ value: repo, label: repo })),
                ]}
              />
            ) : null}
            {['inbox', 'pulls'].includes(route.page) ? (
              <Select
                label="Readiness"
                className="filter-select"
                value={route.query.get('status') || 'all'}
                onValueChange={(value) => updateQuery('status', value === 'all' ? '' : value)}
                options={[
                  { value: 'all', label: 'Any readiness' },
                  ...readiness.map((label, index) => ({
                    value: String(index),
                    label: `${label} (${readinessCounts[index] || 0})`,
                  })),
                ]}
              />
            ) : null}
            <Select
              label="Needs action"
              className="filter-select"
              value={route.query.get('action') || 'all'}
              onValueChange={(value) => updateQuery('action', value === 'all' ? '' : value)}
              options={[
                { value: 'all', label: 'Any action' },
                ...Object.entries(actionNames).map(([value, label]) => ({ value, label })),
              ]}
            />
            <Select
              label="Group by"
              className="filter-select"
              value={route.query.get('group') || 'stacks'}
              onValueChange={(value) => updateQuery('group', value === 'stacks' ? '' : value)}
              options={[
                { value: 'stacks', label: 'PR stacks & branch chains' },
                { value: 'repository', label: 'Repository' },
                { value: 'action', label: 'Next action' },
                { value: 'flat', label: 'No grouping' },
              ]}
            />
            <label className="unread-filter">
              <input
                type="checkbox"
                checked={route.query.get('read') === 'unread'}
                onChange={(event) => updateQuery('read', event.target.checked ? 'unread' : '')}
              />
              Unread only
            </label>
            <div className="filter-actions">
              <Button
                size="sm"
                variant="inverse"
                className="quiet-button"
                disabled={!filters.length}
                onClick={reset}
              >
                Reset filters
              </Button>
              <Button size="sm" variant="primary" onClick={() => setOpen(false)}>
                Done
              </Button>
            </div>
            <p className="filter-context">Priority → readiness → size → age</p>
            <p className="filter-context">
              {state.pending === '/watch'
                ? 'Checking GitHub…'
                : state.checked
                  ? `${state.checkedScope === 'estate' ? 'Estate refreshed' : 'Selected checked'} ${when(state.checked)}`
                  : 'Saved queue'}
            </p>
          </div>
        </Popover>
      </div>
      <div className="queue-summary" aria-live="polite">
        <span>{count === total ? `${count} items` : `${count} of ${total} items`}</span>
        {filters.length ? (
          <>
            <span className="filter-summary" title={filters.join(', ')}>
              {filters.join(', ')}
            </span>
            <button className="text-button" aria-label="Clear queue filters" onClick={reset}>
              Clear
            </button>
          </>
        ) : route.query.get('q') ? (
          <span className="filter-summary">Matching search</span>
        ) : null}
      </div>
    </>
  );
}
