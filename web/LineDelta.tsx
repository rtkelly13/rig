import type { Item } from './model';

/**
 * A PR's size as GitHub shows it: green additions, red deletions. Snapshots taken
 * before the split was recorded only know the total, so they keep showing that.
 */
export function LineDelta({ item }: { item: Item }) {
  if (item.additions == null || item.deletions == null)
    return <small className="line-delta">{item.lines.toLocaleString()} lines</small>;
  return (
    <small
      className="line-delta"
      aria-label={`${item.additions.toLocaleString()} lines added, ${item.deletions.toLocaleString()} lines deleted`}
    >
      <span className="line-delta-added">+{item.additions.toLocaleString()}</span>
      <span className="line-delta-deleted">−{item.deletions.toLocaleString()}</span>
    </small>
  );
}
