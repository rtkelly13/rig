export interface Reference {
  name: string;
  category: 'Inbox' | 'Review' | 'Notifications';
  url: string;
  evidence: string;
  pattern: string;
  adaptation: string;
  boundary: string;
  related?: string;
  screenshot?: string;
  layout: 'sections' | 'split' | 'workflow' | 'compact';
}
export const references: Reference[] = [
  {
    name: 'Gmail reading pane',
    category: 'Inbox',
    url: 'https://support.google.com/mail/answer/9259769?hl=en',
    evidence:
      'A persistent inbox supports a reading pane beside or below the list, with the same navigation and search context.',
    pattern: 'An inbox and a conversation, not a dashboard and a form',
    adaptation:
      'Keep the queue beside the selected PR, preserve navigation in the URL and mark opened work as read.',
    boundary: 'PR creation age and deterministic priority replace email recency as the ordering rule.',
    layout: 'split',
  },
  {
    name: 'Gmail archive & snooze',
    category: 'Inbox',
    url: 'https://support.google.com/mail/answer/6576?hl=en-GB',
    evidence:
      'Archive clears an item from the inbox without deleting it; a reply returns it. Snooze temporarily sets work aside in its own view.',
    related: 'https://support.google.com/mail/answer/7622010',
    pattern: 'Inbox is attention, not every unfinished item',
    adaptation:
      'Separate Inbox, Waiting, Snoozed and Done. A new head or human reply can return reviewed or archived work to attention.',
    boundary:
      'Rig snooze currently lasts until explicit resumption, not a scheduled date. Archive never closes or merges a PR.',
    layout: 'workflow',
  },
  {
    name: 'GitHub pull requests',
    category: 'Inbox',
    url: 'https://github.blog/changelog/2026-03-26-new-pull-requests-dashboard-is-in-public-preview/',
    evidence:
      'The PR inbox separates review requests, items needing action and items ready to merge. Saved views preserve search queries.',
    pattern: 'Stable views, compact rows, repository filters',
    adaptation: 'Use URL-addressable queue views and filters, with status visible in each row.',
    boundary: 'Keep the agreed deterministic ranking within each filtered view.',
    layout: 'sections',
  },
  {
    name: 'Graphite PR inbox',
    category: 'Inbox',
    url: 'https://graphite.com/docs/use-pr-inbox',
    evidence:
      'The inbox groups PRs into review, approved, returned, merging, draft and waiting sections, with customizable filters.',
    pattern: 'Workflow sections with clear ownership',
    adaptation: 'Distinguish needs-review, waiting-on-changes and reviewed-current-head in the queue.',
    boundary: 'Rig currently has snapshot and local-decision data, not Graphite stack metadata.',
    layout: 'sections',
  },
  {
    name: 'Graphite review page',
    category: 'Review',
    url: 'https://graphite.com/docs/review-pull-requests',
    evidence:
      'A side tray switches between timeline and file tree. Focus mode hides the tray, and review outcomes update the inbox.',
    pattern: 'A stable reading pane beside navigation',
    adaptation: 'Keep the selected commit, decision and interaction history together beside the queue.',
    boundary: 'Diff reading and submitted GitHub reviews remain on GitHub.',
    layout: 'split',
  },
  {
    name: 'GitKraken Launchpad',
    screenshot: 'https://gitkraken.com/wp-content/uploads/2025/09/Group-21947.png',
    category: 'Inbox',
    url: 'https://gitkraken.com/features/launchpad',
    evidence:
      'A multi-repository dashboard groups work by status and supports pinning, snoozing and filtering alongside build state.',
    pattern: 'Actionable status at a glance',
    adaptation: 'Add readiness filters, a compact status overview and a dedicated deferred/completed view.',
    boundary: 'Do not add an unexplained second ordering through pins.',
    layout: 'workflow',
  },
  {
    name: 'GitLab merge requests',
    category: 'Review',
    url: 'https://docs.gitlab.com/user/project/merge_requests/homepage/',
    evidence:
      'The homepage offers workflow and role views, separates active work from waiting states, and shows checks and changed-line counts.',
    pattern: 'Separate your next action from waiting',
    adaptation: 'Make the local review state and the GitHub readiness state distinct in the detail pane.',
    boundary: 'Do not label clean checks as approval or completed review.',
    layout: 'workflow',
  },
  {
    name: 'Octobox',
    screenshot:
      'https://octobox.io/assets/screenshot-a4e7bd51e1d76ce5555c80b76328811802d2d14251f1d1b11c9f698ccdbee597.png',
    category: 'Notifications',
    url: 'https://octobox.io/',
    evidence:
      'Archived notifications return to the inbox when the thread changes. Filters include repository, type, state and CI status.',
    pattern: 'Return attention when something changes',
    adaptation: 'Call out a new head after a previous decision, and retain deferred work for resumption.',
    boundary:
      'Snoozed work stays snoozed until resumed; archived work returns when its head or human discussion changes.',
    layout: 'split',
  },
  {
    name: 'Gitify',
    screenshot: 'https://gitify.io/_astro/notifications.BNb_XWro_Z1M6Jqi.webp',
    category: 'Notifications',
    url: 'https://gitify.io/',
    evidence:
      'A compact desktop notification inbox provides repository notifications and filters, with distinct all-read and preferences views.',
    pattern: 'Compact, low-interruption scanning',
    adaptation: 'Use readable dense rows and quiet background polling with explicit errors.',
    boundary: 'Avoid notification counts or live indicators that the available data cannot support.',
    layout: 'compact',
  },
];
