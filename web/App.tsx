import { Badge, Button, Tooltip } from '@rtkelly-labs/design-system';
import { createRoot } from 'react-dom/client';
import { useEffect, useRef, useState } from 'react';
import { Icon } from './Icon';
import type { IconName } from './Icon';
import { Queue } from './Queue';
import { Estate } from './Estate';
import { References } from './References';
import { inView } from './model';
import { OpsAttention } from './OpsAttention';
import { OpsDecisions, OpsTrim } from './OpsEstate';
import { OpsJobs, OpsRuns } from './OpsJobs';
import { OpsTelemetry } from './OpsTelemetry';
import { isOpsPage, navigate, OPS_TITLES, queueUrl, useRoute } from './router';
import { useQueue } from './useQueue';

let legacyLoaded: Promise<void> | undefined;
function loadLegacy() {
  legacyLoaded ??= new Promise<void>((resolve, reject) => {
    const script = document.createElement('script');
    script.type = 'module';
    script.src = '/static/studio.js';
    script.onload = () => resolve();
    script.onerror = () => reject(new Error('Could not load the studio. Reload to try again.'));
    document.body.append(script);
  });
  return legacyLoaded;
}
function App() {
  const route = useRoute();
  const state = useQueue();
  const [environment, setEnvironment] = useState('Local');
  const [legacyError, setLegacyError] = useState('');
  const [theme, setTheme] = useState(() => {
    try {
      return localStorage.getItem('rig:theme') === 'sketch' ? 'sketch' : 'midnight';
    } catch {
      return 'midnight';
    }
  });
  const launcher = useRef<HTMLDialogElement>(null);
  const [compactNavigation, setCompactNavigation] = useState(
    () => window.matchMedia('(max-width: 900px)').matches,
  );
  useEffect(() => {
    const media = window.matchMedia('(max-width: 900px)');
    const update = () => setCompactNavigation(media.matches);
    media.addEventListener('change', update);
    update();
    return () => media.removeEventListener('change', update);
  }, []);
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem('rig:sidebar') !== 'expanded';
    } catch {
      return true;
    }
  });
  useEffect(() => {
    document.documentElement.dataset.sidebar = collapsed ? 'collapsed' : 'expanded';
    try {
      localStorage.setItem('rig:sidebar', collapsed ? 'collapsed' : 'expanded');
    } catch {
      /* Storage may be disabled. */
    }
  }, [collapsed]);
  const legacy = ['studio', 'optical', 'operations'].includes(route.page);
  useEffect(() => {
    const abort = new AbortController();
    void fetch('/api/status', { signal: abort.signal })
      .then((response) => response.json())
      .then((data: { is_preview: boolean }) =>
        setEnvironment(data.is_preview ? 'Preview' : 'Production'),
      )
      .catch(() => {
        /* Keep local label. */
      });
    return () => abort.abort();
  }, []);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try {
      localStorage.setItem('rig:theme', theme);
    } catch {
      /* Storage may be disabled. */
    }
  }, [theme]);
  useEffect(() => {
    document.title = `Rig / ${route.page === 'references' ? 'UI references' : route.page === 'estate' ? 'Estate' : isOpsPage(route.page) ? `Operations / ${OPS_TITLES[route.page]}` : legacy ? route.page : 'Triage'}`;
    const workspace = document.getElementById('legacy-workspace');
    if (workspace) {
      workspace.hidden = !legacy;
      workspace.dataset.view = route.page === 'operations' ? 'estate' : route.page;
    }
    document.body.classList.toggle('legacy-route', legacy);
    let cancelled = false;
    if (legacy)
      void loadLegacy()
        .then(() => {
          if (!cancelled)
            window.dispatchEvent(
              new CustomEvent('rig:view', {
                detail:
                  route.page === 'studio'
                    ? 'workbench'
                    : route.page === 'operations'
                      ? 'estate'
                      : route.page,
              }),
            );
        })
        .catch((error: Error) => {
          if (!cancelled) setLegacyError(error.message);
        });
    return () => {
      cancelled = true;
    };
  }, [route.page, legacy]);
  const tool = isOpsPage(route.page)
    ? 'Operations'
    : route.page === 'estate' || route.page === 'operations'
      ? 'Estate'
      : route.page === 'studio' || route.page === 'optical' || route.page === 'references'
        ? 'Design library'
        : 'Triage';
  const count = (view: Parameters<typeof inView>[1]) =>
    state.entries.filter(
      (entry) => (!route.repository || entry.item.repo === route.repository) && inView(entry, view),
    ).length;
  const nav = (href: string, label: string, icon: IconName, active: boolean, count?: number) => (
    <a
      key={href}
      href={href}
      aria-label={label}
      aria-current={active ? 'page' : undefined}
      onClick={(event) => {
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        navigate(href);
      }}
    >
      <Icon name={icon} />
      <span>{label}</span>
      {count !== undefined ? <small>{count}</small> : null}
    </a>
  );
  const sidebarNav = (
    href: string,
    label: string,
    icon: IconName,
    active: boolean,
    count?: number,
  ) => (
    <Tooltip key={href} content={label} side="right" disabled={!collapsed && !compactNavigation}>
      {nav(href, label, icon, active, count)}
    </Tooltip>
  );
  return (
    <>
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>
      <header className="app-masthead">
        <button
          className="sidebar-toggle"
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-expanded={!collapsed}
          aria-controls="rig-sidebar"
          onClick={() => setCollapsed(!collapsed)}
        >
          <Icon name="menu" />
        </button>
        <a
          className="app-brand"
          aria-label="Rig inbox"
          href="/triage/inbox"
          onClick={(event) => {
            if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            navigate('/triage/inbox');
          }}
        >
          <span className="brand-mark">
            r<span>_</span>
          </span>
          <span>rig</span>
        </a>
        <div className="app-breadcrumb">
          Workspace<span>/</span>
          {route.page === 'estate'
            ? 'Estate'
            : isOpsPage(route.page)
              ? 'Operations'
              : legacy
              ? route.page === 'studio'
                ? 'Asset studio'
                : route.page
              : 'Triage'}
          {route.repository ? (
            <>
              <span>/</span>
              {route.repository}
            </>
          ) : null}
          {route.page === 'references' ? (
            <>
              <span>/</span>References
            </>
          ) : null}
          {isOpsPage(route.page) ? (
            <>
              <span>/</span>
              {OPS_TITLES[route.page]}
            </>
          ) : null}
        </div>
        <div className="app-header-end">
          <button
            className="tool-launcher"
            aria-label="Rig tools"
            onClick={() => launcher.current?.showModal()}
          >
            <Icon name="grid" />
            <span>Tools</span>
          </button>
          <Badge accent="quiet">{environment}</Badge>
          <button
            className="text-button theme-toggle"
            onClick={() => setTheme(theme === 'midnight' ? 'sketch' : 'midnight')}
          >
            {theme === 'midnight' ? 'Sketch theme' : 'Midnight theme'}
          </button>
        </div>
      </header>
      <aside id="rig-sidebar" className="app-sidebar" data-compact={collapsed || compactNavigation}>
        <div className="workspace-label">
          <span className="workspace-avatar">RK</span>
          <div>
            Personal estate<small>Development workspace</small>
          </div>
        </div>
        {/* The sidebar is the current tool's pages; switching tools is the launcher's job. */}
        <nav aria-label="Rig navigation">
          <div className="nav-section-label">{tool}</div>
          {tool === 'Triage' ? (
            <>
              {route.repository && !collapsed && !compactNavigation ? (
                <div className="mx-3 mb-2.5 border-b border-edge-default pt-2 pb-2.5 text-xs text-content-primary">
                  <span className="block truncate font-semibold" title={route.repository}>
                    {route.repository}
                  </span>
                  <a
                    href="/triage/inbox"
                    className="mt-1 flex items-center gap-1.5 text-caption text-content-secondary no-underline hover:text-content-primary [&_svg]:size-3"
                    onClick={(event) => {
                      if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
                      event.preventDefault();
                      navigate('/triage/inbox');
                    }}
                  >
                    <Icon name="folder" />
                    All repositories
                  </a>
                </div>
              ) : null}
              {sidebarNav(
                queueUrl('inbox', route.repository),
                'Inbox',
                'queue',
                ['inbox', 'pulls'].includes(route.page),
                count('inbox'),
              )}
              {sidebarNav(
                queueUrl('issues', route.repository),
                'Issues',
                'issue',
                route.page === 'issues',
                count('issues'),
              )}
              {sidebarNav(
                queueUrl('waiting', route.repository),
                'Waiting',
                'history',
                route.page === 'waiting',
                count('waiting'),
              )}
              {sidebarNav(
                queueUrl('snoozed', route.repository),
                'Snoozed',
                'clock',
                route.page === 'snoozed',
                count('snoozed'),
              )}
              {sidebarNav(
                queueUrl('done', route.repository),
                'Done',
                'check',
                ['done', 'history'].includes(route.page),
                count('done'),
              )}
            </>
          ) : tool === 'Estate' ? (
            <>
              {sidebarNav('/estate', 'Repositories', 'folder', route.page === 'estate')}
              {sidebarNav('/estate/operations', 'Operations', 'clock', route.page === 'operations')}
            </>
          ) : tool === 'Operations' ? (
            <>
              {sidebarNav('/ops', 'Attention', 'alert', route.page === 'ops')}
              {sidebarNav('/ops/jobs', 'Jobs', 'clock', route.page === 'ops-jobs')}
              {sidebarNav('/ops/runs', 'Runs', 'history', route.page === 'ops-runs')}
              {sidebarNav('/ops/telemetry', 'Telemetry', 'search', route.page === 'ops-telemetry')}
              {sidebarNav('/ops/decisions', 'Decisions', 'review', route.page === 'ops-decisions')}
              {sidebarNav('/ops/trim', 'Trim', 'folder', route.page === 'ops-trim')}
            </>
          ) : route.page === 'studio' ? (
            sidebarNav('/studio', 'Asset studio', 'grid', true)
          ) : route.page === 'optical' ? (
            sidebarNav('/optical', 'Optical studio', 'grid', true)
          ) : (
            sidebarNav('/triage/references', 'UI references', 'book', route.page === 'references')
          )}
        </nav>
        <div className="sidebar-footer">
          <span className="status-dot status-0" />
          Local workspace<small>Decisions stay in rig.</small>
        </div>
      </aside>
      <dialog
        ref={launcher}
        className="rig-tools-dialog"
        aria-labelledby="tools-title"
        onClick={(event) => {
          const bounds = event.currentTarget.getBoundingClientRect();
          if (
            event.target === event.currentTarget &&
            (event.clientX < bounds.left ||
              event.clientX > bounds.right ||
              event.clientY < bounds.top ||
              event.clientY > bounds.bottom)
          )
            launcher.current?.close();
        }}
      >
        <div className="tools-heading">
          <h2 id="tools-title">Rig tools</h2>
          <Button
            className="quiet-button"
            variant="inverse"
            size="sm"
            onClick={() => launcher.current?.close()}
          >
            Close
          </Button>
        </div>
        <nav
          aria-label="Rig tools"
          onClick={(event) => {
            if (event.target instanceof Element && event.target.closest('a'))
              launcher.current?.close();
          }}
        >
          {nav('/triage/inbox', 'Triage', 'queue', !legacy && !isOpsPage(route.page))}
          {nav('/ops', 'Operations', 'alert', isOpsPage(route.page))}
          {nav('/estate', 'Estate', 'grid', route.page === 'estate')}
          {nav('/estate/operations', 'Estate operations', 'clock', route.page === 'operations')}
          {nav('/studio', 'Asset studio', 'grid', route.page === 'studio')}
          {nav('/optical', 'Optical studio', 'grid', route.page === 'optical')}
          {nav('/triage/references', 'UI references', 'book', route.page === 'references')}
          {/* The C4 viewer is its own page, served by the Python handler, so this is a full load. */}
          <a href="/c4/" aria-label="C4 architecture">
            <Icon name="stack" />
            <span>C4 architecture</span>
          </a>
        </nav>
      </dialog>
      <main
        id="main-content"
        className={`app-content ${legacy ? 'legacy-active' : ''}`}
        tabIndex={-1}
      >
        {legacy ? (
          legacyError ? (
            <div role="alert" className="operation-message error-message">
              {legacyError}
            </div>
          ) : null
        ) : route.page === 'estate' ? (
          <Estate route={route} state={state} />
        ) : route.page === 'references' ? (
          <References route={route} />
        ) : route.page === 'ops' ? (
          <OpsAttention />
        ) : route.page === 'ops-jobs' ? (
          <OpsJobs />
        ) : route.page === 'ops-runs' ? (
          <OpsRuns route={route} />
        ) : route.page === 'ops-telemetry' ? (
          <OpsTelemetry route={route} />
        ) : route.page === 'ops-decisions' ? (
          <OpsDecisions />
        ) : route.page === 'ops-trim' ? (
          <OpsTrim />
        ) : route.page === 'missing' ? (
          <div className="empty-state">
            <h1>Page not found</h1>
            <Button
              href="/triage/inbox"
              variant="primary"
              onClick={(event) => {
                if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
                event.preventDefault();
                navigate('/triage/inbox');
              }}
            >
              Return to triage
            </Button>
          </div>
        ) : (
          <Queue route={route} state={state} />
        )}
      </main>
    </>
  );
}
const root = document.getElementById('rig-app');
if (root) createRoot(root).render(<App />);
