export type IconName =
  | 'stack'
  | 'message'
  | 'alert'
  | 'clock'
  | 'review'
  | 'merge'
  | 'user'
  | 'tag'
  | 'link'
  | 'folder'
  | 'menu'
  | 'pull'
  | 'issue'
  | 'queue'
  | 'grid'
  | 'history'
  | 'book'
  | 'arrow'
  | 'refresh'
  | 'check'
  | 'search'
  | 'external'
  | 'copy';
const paths: Record<IconName, string> = {
  stack: 'm3 7 9-4 9 4-9 4zm0 5 9 4 9-4M3 17l9 4 9-4',
  message: 'M3 4h18v13H9l-6 4zM7 8h10M7 12h7',
  alert: 'm12 3 10 18H2zm0 6v5m0 3h.01',
  clock: 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0m-9-5v6l4 2',
  review: 'M5 3h14v18H5zm4 5h6m-6 4h6m-7 4 2 2 5-4',
  merge: 'M6 3v12a3 3 0 0 0 3 3h9M18 3v15m-4-4 4 4 4-4',
  user: 'M17 7a5 5 0 1 1-10 0 5 5 0 0 1 10 0M3 22v-3a6 6 0 0 1 6-6h6a6 6 0 0 1 6 6v3',
  tag: 'M3 3h8l10 10-8 8L3 11zm4 4h.01',
  link: 'm9 15 6-6m-7 3H6a4 4 0 0 1 0-8h4a4 4 0 0 1 4 4m-4 8a4 4 0 0 0 4 4h4a4 4 0 0 0 0-8h-2',
  folder: 'M3 6h7l2 3h9v12H3z',

  menu: 'M3 6h18M3 12h18M3 18h18',
  pull: 'M6 3v12m0-12a2 2 0 1 0 0 .1M6 15a2 2 0 1 0 0 .1M18 15a2 2 0 1 0 0 .1M18 15V8a3 3 0 0 0-3-3h-3m0 0 3-3m-3 3 3 3',
  issue: 'M12 8v5m0 3h.01M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0',
  queue: 'M8 6h12M8 12h12M8 18h12M3 6h.01M3 12h.01M3 18h.01',
  grid: 'M3 3h7v7H3zm11 0h7v7h-7zM3 14h7v7H3zm11 0h7v7h-7z',
  history: 'M3 11a9 9 0 1 1 2 7M3 4v7h7m2-4v5l3 2',
  book: 'M3 4h7l2 2 2-2h7v15h-7l-2 2-2-2H3zm9 2v15',
  arrow: 'M5 12h14m-6-6 6 6-6 6',
  refresh: 'M20 7a9 9 0 1 0 1 9M20 2v6h-6',
  check: 'm5 12 4 4L19 6',
  search: 'm16 16 5 5M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0',
  external: 'M14 3h7v7m0-7L10 14M10 3H3v18h18v-7',
  copy: 'M8 8h12v13H8zM16 8V3H3v13h5',
};
export function Icon({ name }: { name: IconName }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      width="18"
      height="18"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d={paths[name]} />
    </svg>
  );
}
