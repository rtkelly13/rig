import { Select } from '@rtkelly-labs/design-system';
import type { Entry } from './model';
import { navigate, queueUrl } from './router';
import type { QueueView, Route } from './router';
export function QueueScope({ route, entries }: { route: Route; entries: Entry[] }) {
  const repos = [
    ...new Set([
      ...entries.map((entry) => entry.item.repo),
      ...(route.repository ? [route.repository] : []),
    ]),
  ].sort();
  return (
    <div className="queue-scope">
      <Select
        aria-label="Queue scope"
        value={route.repository || 'all'}
        options={[
          { value: 'all', label: 'All repositories' },
          ...repos.map((repo) => ({ value: repo, label: repo })),
        ]}
        onValueChange={(value) => {
          const query = new URLSearchParams(route.query);
          query.delete('repo');
          navigate(queueUrl(route.page as QueueView, value === 'all' ? '' : value, query));
        }}
      />
    </div>
  );
}
