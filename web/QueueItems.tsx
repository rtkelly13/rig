import { Badge } from '@rtkelly-labs/design-system';
import { Icon } from './Icon';
import { SignalChip } from './Signals';
import { failedStates, visibleSignals } from './attention';
import type { QueueGroup } from './attention';
import { age, isUnread, ref } from './model';
import type { Entry } from './model';
import { itemUrl } from './router';
import type { QueueView, Route } from './router';

function GroupStatus({ group }: { group: QueueGroup }) {
  const failed = group.entries.filter((entry) =>
    failedStates.has(entry.item.facts?.checks_state || ''),
  ).length;
  return failed ? (
    <span className="group-failure-count">
      <Icon name="alert" />
      {failed} failed
    </span>
  ) : null;
}
export function QueueItems({
  groups,
  collapsed,
  toggle,
  route,
  select,
  order,
}: {
  groups: QueueGroup[];
  collapsed: Set<string>;
  toggle: (key: string) => void;
  route: Route;
  select: (entry: Entry) => void;
  order: Entry[];
}) {
  const positions = new Map(order.map((entry, index) => [ref(entry.item), index + 1]));
  const row = (entry: Entry) => (
    <li key={ref(entry.item)}>
      <a
        href={itemUrl(
          route.page as QueueView,
          entry.item.repo,
          entry.item.number,
          route.query,
          route.repository,
        )}
        aria-current={route.selected === ref(entry.item) ? 'true' : undefined}
        onClick={(event) => {
          if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
          event.preventDefault();
          select(entry);
        }}
        className={`queue-row ${isUnread(entry) ? 'is-unread' : 'is-read'}`}
      >
        <span className="queue-rank">
          {isUnread(entry) ? (
            <span className="unread-dot" aria-label="Unread" />
          ) : (
            positions.get(ref(entry.item))
          )}
        </span>
        <div className="queue-row-content">
          <div className="queue-row-title">
            <span
              className="item-kind"
              data-kind={entry.item.kind}
              data-draft={entry.item.facts?.draft}
            >
              <Icon name={entry.item.kind === 'pr' ? 'pull' : 'issue'} />
            </span>
            <strong>{entry.item.title}</strong>
            {entry.item.priority < 4 ? (
              <Badge accent={entry.item.priority === 0 ? 'danger' : 'quiet'}>
                P{entry.item.priority}
              </Badge>
            ) : null}
          </div>
          <div className="queue-row-repo">
            {entry.item.repo}
            <span>#{entry.item.number}</span>
            <time dateTime={entry.item.created}>{age(entry.item.created)}</time>
          </div>
          <div className="queue-signals">
            {visibleSignals(entry).map((signal) => (
              <SignalChip key={signal.key} signal={signal} />
            ))}
            {entry.item.kind === 'pr' ? (
              <small>{entry.item.lines.toLocaleString()} lines</small>
            ) : null}
          </div>
        </div>
      </a>
    </li>
  );
  return (
    <ol className="queue-groups">
      {groups.map((group) =>
        group.grouped ? (
          <li key={group.key} className="queue-group">
            <button
              className="queue-group-toggle"
              aria-expanded={!collapsed.has(group.key)}
              onClick={() => toggle(group.key)}
            >
              <Icon name={group.icon} />
              <strong>{group.title}</strong>
              <GroupStatus group={group} />
              <span>{group.entries.length}</span>
              <span className="group-chevron">{collapsed.has(group.key) ? '+' : '−'}</span>
            </button>
            {!collapsed.has(group.key) ? (
              <>
                {group.description ? (
                  <p className="queue-group-description">{group.description}</p>
                ) : null}
                <ol>{group.entries.map(row)}</ol>
              </>
            ) : null}
          </li>
        ) : (
          group.entries.map(row)
        ),
      )}
    </ol>
  );
}
